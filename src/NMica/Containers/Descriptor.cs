using System.Text.Json.Serialization;

namespace NMica.Containers;

/// <summary>
/// OCI <see href="https://github.com/opencontainers/image-spec/blob/main/descriptor.md">content descriptor</see>:
/// the (mediaType, digest, size) triple used to reference any blob in an image. We add one non-serialised
/// field — <see cref="UncompressedDigest"/> — to carry the layer's diff-id (SHA of the uncompressed
/// tar stream) alongside its transport digest (SHA of the gzipped blob). That pairing is the invariant
/// an image manifest + config must preserve in lockstep; see <see cref="Layer"/>.
/// </summary>
/// <remarks>Mirrors <c>Microsoft.NET.Build.Containers.Descriptor</c>.</remarks>
public readonly record struct Descriptor(
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("size")] long Size)
{
    /// <summary>SHA of the uncompressed tar (the layer's diff-id). Only populated for layers.</summary>
    [JsonIgnore]
    public string? UncompressedDigest { get; init; }
}
