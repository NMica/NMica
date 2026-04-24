using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NMica.Containers.Models;

namespace NMica.Containers;

/// <summary>
/// Assembles a multi-layer image from a base manifest + config and a sequence of
/// <see cref="Layer"/> additions. The base image's layer descriptors are carried forward verbatim
/// in the output manifest; new layers are appended. The image config's <c>rootfs.diff_ids</c> array
/// is kept in lockstep via <see cref="ImageConfig"/>.
/// </summary>
/// <remarks>Mirrors <c>Microsoft.NET.Build.Containers.ImageBuilder</c>.</remarks>
public sealed class ImageBuilder
{
    private readonly Manifest _manifest;
    private readonly ImageConfig _config;
    private readonly List<Layer> _addedLayers = new();

    public string ManifestMediaType => _manifest.MediaType ?? MediaTypes.OciManifestV1;

    /// <param name="baseManifest">Manifest the base image came in with (we clone + extend it).</param>
    /// <param name="baseConfigJson">Raw config JSON of the base image.</param>
    public ImageBuilder(Manifest baseManifest, string baseConfigJson)
    {
        _manifest = new Manifest(baseManifest);
        _config = new ImageConfig(baseConfigJson);
    }

    public void AddLayer(Layer layer)
    {
        _manifest.Layers.Add(new ManifestLayer(
            MediaType: layer.Descriptor.MediaType,
            Size: layer.Descriptor.Size,
            Digest: layer.Descriptor.Digest));
        _config.AddLayer(layer);
        _addedLayers.Add(layer);
    }

    public void SetWorkingDirectory(string wd) => _config.SetWorkingDirectory(wd);
    public void SetUser(string user) => _config.SetUser(user);
    public void SetEntrypointAndCmd(IEnumerable<string>? entrypoint, IEnumerable<string>? cmd) =>
        _config.SetEntrypointAndCmd(entrypoint, cmd);
    public void AddEnvironmentVariable(string kv) => _config.AddEnvironmentVariable(kv);
    public void AddLabel(string key, string value) => _config.AddLabel(key, value);
    public void ExposePort(string portSpec) => _config.ExposePort(portSpec);

    /// <summary>
    /// Finalise the image: emit the config JSON, hash it, rewrite the manifest's config
    /// descriptor to point at the new config blob, then serialise the manifest. Returns a frozen
    /// <see cref="BuiltImage"/> ready for a sink.
    /// </summary>
    public BuiltImage Build()
    {
        // 1. Emit + hash config.
        var configJson = _config.Build();
        var configBytes = Encoding.UTF8.GetBytes(configJson);
        var configDigest = "sha256:" + HexLower(SHA256.HashData(configBytes));

        // 2. Swap the manifest's config descriptor.
        _manifest.Config = new ManifestConfig(
            MediaType: MediaTypes.ConfigMediaTypeFor(ManifestMediaType),
            Size: configBytes.LongLength,
            Digest: configDigest);
        _manifest.MediaType ??= MediaTypes.OciManifestV1;

        // 3. Serialise + hash manifest.
        var manifestJson = JsonSerializer.Serialize(_manifest, SerializerOptions);
        var manifestBytes = Encoding.UTF8.GetBytes(manifestJson);
        var manifestDigest = "sha256:" + HexLower(SHA256.HashData(manifestBytes));

        return new BuiltImage(
            Manifest: _manifest,
            ManifestJson: manifestJson,
            ManifestDigest: manifestDigest,
            ManifestSize: manifestBytes.LongLength,
            ImageConfigJson: configJson,
            ImageConfigDigest: configDigest,
            ImageConfigSize: configBytes.LongLength,
            AddedLayers: _addedLayers.AsReadOnly(),
            AllLayerDescriptors: _manifest.Layers.AsReadOnly());
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static string HexLower(byte[] bytes)
    {
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
