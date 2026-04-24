using System.Collections.Generic;
using NMica.Containers.Models;

namespace NMica.Containers;

/// <summary>
/// Frozen output of <see cref="ImageBuilder.Build"/>: manifest + config + every layer blob path.
/// Passed to a sink (archive writer, registry push, docker load) which moves the bytes where
/// they need to go.
/// </summary>
public sealed record BuiltImage(
    Manifest Manifest,
    string ManifestJson,
    string ManifestDigest,
    long ManifestSize,
    string ImageConfigJson,
    string ImageConfigDigest,
    long ImageConfigSize,
    IReadOnlyList<Layer> AddedLayers,
    IReadOnlyList<ManifestLayer> AllLayerDescriptors);
