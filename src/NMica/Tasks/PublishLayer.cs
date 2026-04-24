using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace NMica.Tasks;

/// <summary>
/// Partitions the freshly-published output in <see cref="PublishDir"/> into
/// <c>package/</c>, <c>earlypackage/</c>, <c>project/</c>, and <c>app/</c> subdirectories —
/// one "layer bucket" per bullet. The classification is driven entirely by MSBuild item
/// metadata that the SDK already computes during restore; we no longer parse
/// <c>project.assets.json</c>.
/// </summary>
/// <remarks>
/// <para><b>Inputs.</b> Every file in the publish output (<see cref="ResolvedFilesToPublish"/>,
/// usually <c>@(ResolvedFileToPublish)</c>) carries <c>%(NuGetPackageId)</c> /
/// <c>%(NuGetPackageVersion)</c> metadata set by the SDK's <c>ResolvePackageAssets</c> task
/// (see <c>ResolvePackageAssets.cs:1838</c>) for every asset that originated in a NuGet
/// package. Items from project references and from the project itself carry no such metadata,
/// so we tell project-origin from app-origin via
/// <see cref="ProjectReferenceAssemblies"/> (usually
/// <c>@(_ResolvedProjectReferencePaths)</c>).</para>
///
/// <para><b>Classification.</b></para>
/// <list type="bullet">
///   <item><description>Pre-release package (version contains <c>-</c>) → <c>earlypackage/</c></description></item>
///   <item><description>Stable package (version with no <c>-</c>) → <c>package/</c></description></item>
///   <item><description>No package metadata, filename matches a project reference → <c>project/</c></description></item>
///   <item><description>Everything else → <c>app/</c></description></item>
/// </list>
/// </remarks>
public class PublishLayer : Microsoft.Build.Utilities.Task
{
    [Required]
    public string PublishDir { get; set; } = "";

    /// <summary><c>@(ResolvedFileToPublish)</c>. Every file the SDK copies to the publish dir,
    /// with package-origin metadata intact.</summary>
    [Required]
    public ITaskItem[] ResolvedFilesToPublish { get; set; } = Array.Empty<ITaskItem>();

    /// <summary><c>@(_ResolvedProjectReferencePaths)</c>. The resolved DLLs of direct
    /// ProjectReferences; their filenames tell us which <see cref="ResolvedFilesToPublish"/>
    /// entries came from project-references rather than the app's own code.</summary>
    public ITaskItem[] ProjectReferenceAssemblies { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>Comma-separated layers to emit. Default <c>All</c>.</summary>
    public string DockerLayer
    {
        get => _layersToPublish.ToString();
        set => _layersToPublish = string.IsNullOrEmpty(value) ? Layer.All : (Layer)Enum.Parse(typeof(Layer), value, true);
    }
    private Layer _layersToPublish = Layer.All;

    public override bool Execute()
    {
        var publishPath = Path.GetFullPath(PublishDir);
        var requestedLayers = _layersToPublish.ToValuesArray().ToHashSet();

        var projectRefFilenames = ProjectReferenceAssemblies
            .Select(i => Path.GetFileName(i.ItemSpec))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Group each published file by its classification. %(RelativePath) is the SDK's
        // authoritative path inside the publish dir; fall back to the item spec's basename if
        // it's absent (shouldn't happen for items flowing from ComputeFilesToPublish, but we're
        // defensive).
        var byLayer = new Dictionary<Layer, List<string>>();
        foreach (var item in ResolvedFilesToPublish)
        {
            if (item.GetMetadata("CopyToPublishDirectory") == "Never")
                continue;

            var layer = Classify(item, projectRefFilenames);
            if (!requestedLayers.Contains(layer)) continue;

            var relative = FindRelativePath(item);
            if (string.IsNullOrEmpty(relative)) continue;

            if (!byLayer.TryGetValue(layer, out var list))
                byLayer[layer] = list = new List<string>();
            list.Add(relative);
        }

        // Always materialise every requested layer as a directory, even when classification
        // produced no files for it. Generated Dockerfiles COPY each layer dir unconditionally
        // (`COPY --from=build /layer/package ./`), so a missing dir breaks `docker build` for
        // projects that e.g. have zero runtime NuGet dependencies.
        foreach (var layer in requestedLayers)
        {
            var layerDir = Path.Combine(publishPath, layer.ToString().ToLowerInvariant());
            Directory.CreateDirectory(layerDir);
        }

        foreach (var (layer, files) in byLayer)
        {
            var layerDir = Path.Combine(publishPath, layer.ToString().ToLowerInvariant());
            foreach (var relative in files)
            {
                var src = Path.Combine(publishPath, relative);
                var dst = Path.Combine(layerDir, relative);
                if (!File.Exists(src)) continue;

                var dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);

                if (File.Exists(dst)) File.Delete(dst);
                File.Move(src, dst);
                Log.LogMessage(MessageImportance.Low, "NMica layer '{0}' ← {1}", layer, relative);
            }
        }

        return !Log.HasLoggedErrors;
    }

    private static Layer Classify(ITaskItem item, HashSet<string> projectRefFilenames)
    {
        var version = item.GetMetadata("NuGetPackageVersion");
        if (!string.IsNullOrEmpty(version))
        {
            return version.Contains('-') ? Layer.EarlyPackage : Layer.Package;
        }

        var name = Path.GetFileName(item.ItemSpec);
        if (projectRefFilenames.Contains(name))
            return Layer.Project;

        return Layer.App;
    }

    /// <summary>
    /// Return the item's path relative to <see cref="PublishDir"/>. The SDK sets
    /// <c>%(RelativePath)</c> / <c>%(DestinationSubPath)</c> on publish items for this purpose;
    /// we prefer DestinationSubPath (set on <c>ResolvedFileToPublish</c>) and fall back to
    /// RelativePath, then to the item spec's filename.
    /// </summary>
    private static string FindRelativePath(ITaskItem item)
    {
        var sub = item.GetMetadata("DestinationSubPath");
        if (!string.IsNullOrEmpty(sub)) return sub;

        var rel = item.GetMetadata("RelativePath");
        if (!string.IsNullOrEmpty(rel)) return rel;

        return Path.GetFileName(item.ItemSpec);
    }
}
