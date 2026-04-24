using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace NMica.Containers.Models;

/// <summary>
/// An image manifest (OCI or Docker v2) — the document at <c>/v2/{repo}/manifests/{ref}</c>.
/// Layers + config descriptor. Media type switches the serialisation between the two families
/// but the shape is identical.
/// </summary>
/// <remarks>Mirrors <c>ManifestV2</c> in the .NET SDK.</remarks>
public sealed class Manifest
{
    [JsonPropertyName("schemaVersion")] public int SchemaVersion { get; set; } = 2;
    [JsonPropertyName("mediaType")] public string? MediaType { get; set; }
    [JsonPropertyName("config")] public ManifestConfig Config { get; set; }
    [JsonPropertyName("layers")] public List<ManifestLayer> Layers { get; set; } = new();

    public Manifest() { }

    public Manifest(Manifest copyFrom)
    {
        SchemaVersion = copyFrom.SchemaVersion;
        MediaType = copyFrom.MediaType;
        Config = copyFrom.Config;
        Layers = new List<ManifestLayer>(copyFrom.Layers);
    }
}

public record struct ManifestConfig(
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string Digest);

public record struct ManifestLayer(
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string Digest)
{
    /// <summary>OCI "foreign layer" URLs. Omitted when null.</summary>
    [JsonPropertyName("urls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Urls { get; init; }
}
