using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NMica.Containers.Sinks;

/// <summary>
/// Writes an OCI-layout archive (tarball) containing every blob needed to reconstruct the image:
/// base image blobs (pulled from the source registry), our appended layer blobs, the new image
/// config, and the new manifest. Output is a plain tar (not gzipped) — <c>docker load</c>,
/// <c>podman load</c>, and <c>skopeo</c> all accept this format.
/// </summary>
/// <remarks>
/// Mirrors <c>DockerCli.WriteOciImageToStreamAsync</c> from the .NET SDK.
///
/// Layout inside the tarball:
/// <code>
///   oci-layout               (JSON, declares imageLayoutVersion)
///   index.json               (top-level manifest list)
///   blobs/sha256/&lt;digest&gt;   (all blobs — manifest, config, layers, base layers)
/// </code>
/// </remarks>
public static class OciArchiveWriter
{
    private const string OciLayoutJson = """{"imageLayoutVersion":"1.0.0"}""";

    public sealed record WriteRequest(
        BuiltImage Image,
        ImageReference BaseImageRef,
        Registry Registry,
        string OutputPath,
        IReadOnlyList<string> Tags);

    public static async Task WriteAsync(WriteRequest req, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(req.OutputPath)) ?? ".");

        // Stage all blobs on disk first so we can tar them in order. Pulls base-image layer blobs
        // from the source registry into a temp dir keyed by digest.
        var staging = Path.Combine(Path.GetTempPath(), $"nmica-oci-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var blobFiles = await StageBlobsAsync(req, staging, ct);

            await using var fs = File.Create(req.OutputPath);
            await using var tar = new TarWriter(fs, TarEntryFormat.Pax, leaveOpen: false);

            // 1. oci-layout file (top-level marker)
            WriteTarEntryFromBytes(tar, "oci-layout", Encoding.UTF8.GetBytes(OciLayoutJson));

            // 2. index.json — a manifest list that references our new manifest by digest.
            var indexJson = BuildIndexJson(req);
            WriteTarEntryFromBytes(tar, "index.json", Encoding.UTF8.GetBytes(indexJson));

            // 3. blobs/sha256/<digest> — all our blobs, plus pulled base-image blobs.
            WriteTarEntryFromBytes(tar, BlobPath(req.Image.ManifestDigest), Encoding.UTF8.GetBytes(req.Image.ManifestJson));
            WriteTarEntryFromBytes(tar, BlobPath(req.Image.ImageConfigDigest), Encoding.UTF8.GetBytes(req.Image.ImageConfigJson));

            foreach (var layer in req.Image.AddedLayers)
            {
                await WriteTarEntryFromFileAsync(tar, BlobPath(layer.Descriptor.Digest), layer.BackingFile, ct);
            }

            foreach (var (digest, path) in blobFiles)
            {
                await WriteTarEntryFromFileAsync(tar, BlobPath(digest), path, ct);
            }
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { /* best-effort */ }
        }
    }

    private static async Task<Dictionary<string, string>> StageBlobsAsync(
        WriteRequest req, string staging, CancellationToken ct)
    {
        // Our added layers are already on disk (Layer.BackingFile). Base image layers need to be
        // pulled from the source registry so the archive is self-contained. We rely on the fact
        // that every base layer appears in req.Image.AllLayerDescriptors BEFORE the appended ones
        // (ImageBuilder preserves base layers at the front of the list).
        var addedDigests = new HashSet<string>();
        foreach (var l in req.Image.AddedLayers) addedDigests.Add(l.Descriptor.Digest);

        var toDownload = new List<ManifestLayerDescriptor>();
        foreach (var d in req.Image.AllLayerDescriptors)
        {
            if (!addedDigests.Contains(d.Digest))
                toDownload.Add(new ManifestLayerDescriptor(d.Digest, d.MediaType));
        }

        var staged = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in toDownload)
        {
            var path = Path.Combine(staging, Sanitise(d.Digest));
            await req.Registry.DownloadBlobAsync(req.BaseImageRef, d.Digest, path, ct);
            staged[d.Digest] = path;
        }
        return staged;
    }

    private static string BuildIndexJson(WriteRequest req)
    {
        var annotations = new Dictionary<string, string>();
        if (req.Tags is { Count: > 0 })
        {
            // org.opencontainers.image.ref.name is the standard annotation tools look for
            annotations["org.opencontainers.image.ref.name"] = req.Tags[0];
        }

        var index = new
        {
            schemaVersion = 2,
            mediaType = MediaTypes.OciImageIndexV1,
            manifests = new[]
            {
                new
                {
                    mediaType = req.Image.Manifest.MediaType ?? MediaTypes.OciManifestV1,
                    digest = req.Image.ManifestDigest,
                    size = req.Image.ManifestSize,
                    annotations,
                }
            }
        };
        return JsonSerializer.Serialize(index);
    }

    private static string BlobPath(string digest) => $"blobs/{digest.Replace(':', '/')}";

    private static string Sanitise(string digest) => digest.Replace(':', '_');

    private static void WriteTarEntryFromBytes(TarWriter tar, string name, byte[] data)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = (UnixFileMode)0b110_100_100,  // 0644
            DataStream = new MemoryStream(data),
        };
        tar.WriteEntry(entry);
    }

    private static async Task WriteTarEntryFromFileAsync(TarWriter tar, string name, string hostPath, CancellationToken ct)
    {
        var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
        {
            Mode = (UnixFileMode)0b110_100_100,
            DataStream = File.OpenRead(hostPath),
        };
        await tar.WriteEntryAsync(entry, ct);
    }

    private readonly record struct ManifestLayerDescriptor(string Digest, string MediaType);
}
