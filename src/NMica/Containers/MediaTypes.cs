namespace NMica.Containers;

/// <summary>
/// Media-type constants for OCI and Docker v2 schemas. Kept in one place so the rest of the code
/// can pick the right type based on which schema family the base image uses (you must round-trip
/// the base's schema to avoid mixed-schema manifests, which some registries reject).
/// </summary>
/// <remarks>
/// Mirrors <c>Microsoft.NET.Build.Containers.SchemaTypes</c> from the .NET SDK. We keep the same
/// constant names to minimise friction when upstreaming.
/// </remarks>
public static class MediaTypes
{
    // OCI
    public const string OciManifestV1 = "application/vnd.oci.image.manifest.v1+json";
    public const string OciImageIndexV1 = "application/vnd.oci.image.index.v1+json";
    public const string OciImageConfigV1 = "application/vnd.oci.image.config.v1+json";
    public const string OciLayerGzipV1 = "application/vnd.oci.image.layer.v1.tar+gzip";

    // Docker v2
    public const string DockerManifestV2 = "application/vnd.docker.distribution.manifest.v2+json";
    public const string DockerManifestListV2 = "application/vnd.docker.distribution.manifest.list.v2+json";
    public const string DockerContainerV1 = "application/vnd.docker.container.image.v1+json";
    public const string DockerLayerGzip = "application/vnd.docker.image.rootfs.diff.tar.gzip";

    /// <summary>Picks the layer media type that matches a given manifest media type.</summary>
    public static string LayerMediaTypeFor(string manifestMediaType) =>
        manifestMediaType switch
        {
            OciManifestV1 => OciLayerGzipV1,
            _ => DockerLayerGzip,
        };

    /// <summary>Picks the image-config media type that matches a given manifest media type.</summary>
    public static string ConfigMediaTypeFor(string manifestMediaType) =>
        manifestMediaType switch
        {
            OciManifestV1 => OciImageConfigV1,
            _ => DockerContainerV1,
        };

    public static bool IsOci(string manifestMediaType) => manifestMediaType == OciManifestV1;
}
