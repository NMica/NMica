using System;

namespace NMica.Containers;

/// <summary>
/// Parses and normalises a container image reference: <c>[registry/]repo[:tag|@digest]</c>.
/// Defaults match Docker's conventions: no registry → <c>registry-1.docker.io</c>, no tag or
/// digest → <c>latest</c>, single-segment repo → prefixed with <c>library/</c>.
/// </summary>
public sealed record ImageReference(string Registry, string Repository, string Reference, bool IsDigest)
{
    public const string DockerHubRegistry = "registry-1.docker.io";

    public static ImageReference Parse(string registry, string repository, string tag)
    {
        registry = NormaliseRegistry(registry);
        repository = NormaliseRepository(registry, repository);

        // tag can be "sha256:..." (digest) or a plain tag; default to "latest"
        if (string.IsNullOrEmpty(tag)) tag = "latest";
        var isDigest = tag.StartsWith("sha256:", StringComparison.Ordinal);
        return new ImageReference(registry, repository, tag, isDigest);
    }

    public Uri ManifestUri => new($"https://{Registry}/v2/{Repository}/manifests/{Reference}");
    public Uri BlobUri(string digest) => new($"https://{Registry}/v2/{Repository}/blobs/{digest}");

    public override string ToString() => IsDigest
        ? $"{Registry}/{Repository}@{Reference}"
        : $"{Registry}/{Repository}:{Reference}";

    private static string NormaliseRegistry(string registry)
    {
        if (string.IsNullOrEmpty(registry)) return DockerHubRegistry;
        // SDK's convention: users who set "docker.io" expect to hit Docker Hub
        return registry == "docker.io" ? DockerHubRegistry : registry;
    }

    private static string NormaliseRepository(string registry, string repository)
    {
        if (registry == DockerHubRegistry && !repository.Contains('/'))
        {
            // Docker Hub bare names live in the "library" org: "alpine" → "library/alpine"
            return "library/" + repository;
        }
        return repository;
    }
}
