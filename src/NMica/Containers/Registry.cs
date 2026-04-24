using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NMica.Containers.Models;

namespace NMica.Containers;

/// <summary>
/// Minimal Distribution API client: anonymous Bearer-challenge pull. Enough to fetch manifests,
/// configs, and layer blobs from public registries (MCR, Docker Hub's anonymous tier, most
/// read-only public registries). Push and credentialed pull are not yet implemented.
/// </summary>
/// <remarks>
/// Mirrors the shape of <c>Microsoft.NET.Build.Containers.Registry</c> at a much smaller scope.
/// When upstreaming to the SDK, this would be replaced by the SDK's full <c>Registry</c> +
/// <c>DefaultRegistryAPI</c> chain (auth + ECR + HTTP-fallback handlers).
/// </remarks>
public sealed class Registry : IDisposable
{
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, AuthenticationHeaderValue?> _authCache = new();

    public Registry()
    {
        _client = new HttpClient(new AuthHandler(this))
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("NMica/1.0");
    }

    /// <summary>
    /// Pull the manifest for <paramref name="reference"/>. If the response is a multi-arch index,
    /// resolve to a single-platform manifest matching <paramref name="targetRid"/> and return
    /// that; otherwise return the manifest directly.
    /// </summary>
    public async Task<(Manifest manifest, string rawJson, string mediaType, string digest)> GetManifestAsync(
        ImageReference reference, string? targetRid, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, reference.ManifestUri);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd(MediaTypes.OciManifestV1);
        req.Headers.Accept.ParseAdd(MediaTypes.OciImageIndexV1);
        req.Headers.Accept.ParseAdd(MediaTypes.DockerManifestV2);
        req.Headers.Accept.ParseAdd(MediaTypes.DockerManifestListV2);

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        resp.EnsureSuccessStatusCode();

        var mediaType = resp.Content.Headers.ContentType?.MediaType ?? MediaTypes.OciManifestV1;
        var rawJson = await resp.Content.ReadAsStringAsync(ct);
        var digest = resp.Headers.TryGetValues("Docker-Content-Digest", out var d) ? d.First() : string.Empty;

        // Multi-arch: pick a platform-specific manifest.
        if (mediaType is MediaTypes.OciImageIndexV1 or MediaTypes.DockerManifestListV2)
        {
            var innerDigest = PickPlatformDigest(rawJson, targetRid);
            var inner = reference with { Reference = innerDigest, IsDigest = true };
            return await GetManifestAsync(inner, targetRid: null, ct);
        }

        var manifest = JsonSerializer.Deserialize<Manifest>(rawJson)
            ?? throw new InvalidDataException("Manifest JSON was null");
        manifest.MediaType ??= mediaType;
        return (manifest, rawJson, mediaType, digest);
    }

    public async Task<string> GetBlobAsStringAsync(ImageReference reference, string digest, CancellationToken ct = default)
    {
        using var resp = await _client.GetAsync(reference.BlobUri(digest), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Stream a blob into a file, returning the total bytes written. The caller is expected to
    /// verify the digest matches expectations.
    /// </summary>
    public async Task<long> DownloadBlobAsync(ImageReference reference, string digest, string destFile, CancellationToken ct = default)
    {
        using var resp = await _client.GetAsync(reference.BlobUri(digest), HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destFile);
        await src.CopyToAsync(dst, ct);
        return dst.Length;
    }

    private static string PickPlatformDigest(string indexJson, string? targetRid)
    {
        // Parse manifests[] and pick one matching os/architecture for the target RID.
        using var doc = JsonDocument.Parse(indexJson);
        var manifests = doc.RootElement.GetProperty("manifests");

        var (os, arch) = RidToPlatform(targetRid);

        foreach (var m in manifests.EnumerateArray())
        {
            var platform = m.GetProperty("platform");
            if (platform.GetProperty("os").GetString() == os &&
                platform.GetProperty("architecture").GetString() == arch)
            {
                return m.GetProperty("digest").GetString()!;
            }
        }
        // Fallback: first entry.
        return manifests.EnumerateArray().First().GetProperty("digest").GetString()!;
    }

    private static (string os, string arch) RidToPlatform(string? rid)
    {
        // Minimal RID → (os, arch) map. Covers the common cases; SDK handles many more.
        if (string.IsNullOrEmpty(rid))
            return ("linux", System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "amd64");

        var dash = rid.IndexOf('-');
        var os = dash < 0 ? rid : rid[..dash];
        var arch = dash < 0 ? "amd64" : rid[(dash + 1)..];
        if (os is "win" or "win10") os = "windows";
        if (arch is "x64") arch = "amd64";
        if (arch is "arm") arch = "arm";
        return (os, arch);
    }

    public void Dispose() => _client.Dispose();

    // -------- Auth handler --------

    /// <summary>
    /// DelegatingHandler that does the Bearer challenge dance: on 401, parse the
    /// <c>WWW-Authenticate</c> header, fetch a token, cache it per-registry, replay the request.
    /// Anonymous tokens only (no credentials). Follows the Docker v2 token spec.
    /// </summary>
    private sealed class AuthHandler : DelegatingHandler
    {
        private readonly Registry _owner;

        public AuthHandler(Registry owner) : base(new HttpClientHandler()) => _owner = owner;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var registryKey = request.RequestUri?.Host ?? string.Empty;

            if (_owner._authCache.TryGetValue(registryKey, out var cached) && cached is not null)
            {
                request.Headers.Authorization = cached;
            }

            var resp = await base.SendAsync(request, ct);
            if (resp.StatusCode != HttpStatusCode.Unauthorized) return resp;

            var challenge = resp.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme == "Bearer");
            if (challenge?.Parameter is null) return resp;

            var token = await FetchAnonymousTokenAsync(challenge.Parameter, ct);
            if (token is null) return resp;

            var header = new AuthenticationHeaderValue("Bearer", token);
            _owner._authCache[registryKey] = header;

            resp.Dispose();
            using var retry = await CloneAsync(request);
            retry.Headers.Authorization = header;
            return await base.SendAsync(retry, ct);
        }

        private async Task<string?> FetchAnonymousTokenAsync(string challengeParams, CancellationToken ct)
        {
            // challengeParams looks like: realm="https://...",service="...",scope="..."
            var parts = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Regex.Matches(challengeParams, @"(\w+)=""([^""]*)"""))
            {
                parts[m.Groups[1].Value] = m.Groups[2].Value;
            }

            if (!parts.TryGetValue("realm", out var realm)) return null;

            var url = new UriBuilder(realm);
            var qs = new System.Collections.Generic.List<string>();
            if (parts.TryGetValue("service", out var service))
                qs.Add("service=" + Uri.EscapeDataString(service));
            if (parts.TryGetValue("scope", out var scope))
                qs.Add("scope=" + Uri.EscapeDataString(scope));
            // Preserve any params that were already on the realm URL (some registries put them there).
            if (!string.IsNullOrEmpty(url.Query))
            {
                var existing = url.Query.TrimStart('?');
                if (!string.IsNullOrEmpty(existing)) qs.Insert(0, existing);
            }
            url.Query = string.Join("&", qs);

            using var inner = new HttpClient();
            using var tokResp = await inner.GetAsync(url.Uri, ct);
            if (!tokResp.IsSuccessStatusCode) return null;

            var body = await tokResp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            foreach (var key in new[] { "access_token", "token" })
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            return null;
        }

        private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage req)
        {
            var clone = new HttpRequestMessage(req.Method, req.RequestUri) { Version = req.Version };
            foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (req.Content is not null)
            {
                var ms = new MemoryStream();
                await req.Content.CopyToAsync(ms);
                ms.Position = 0;
                clone.Content = new StreamContent(ms);
                foreach (var h in req.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return clone;
        }
    }
}
