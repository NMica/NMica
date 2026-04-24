using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.XPath;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace NMica.Tasks;

/// <summary>
/// Writes a multi-stage Dockerfile next to the current project's .csproj. Uses NMica's layer
/// partitioning scheme on the runtime side (four COPY lines, one per layer bucket). The list of
/// projects to <c>COPY</c> into the build stage is supplied as an MSBuild item — no more
/// <c>project.assets.json</c> parsing.
/// </summary>
public class GenerateDockerfile : Microsoft.Build.Utilities.Task
{
    public bool UsingMicrosoftNETSdkWeb { get; set; }
    [Required] public string TargetFrameworkVersion { get; set; } = "";
    [Required] public string TargetFrameworkIdentifier { get; set; } = "";
    [Required] public string AssemblyName { get; set; } = "";
    [Required] public string MSBuildProjectFullPath { get; set; } = "";
    [Required] public string SolutionPath { get; set; } = "";
    public bool IsExecutable { get; set; }

    /// <summary>
    /// Full paths to every csproj in the dependency graph. Caller typically passes
    /// <c>@(_MSBuildProjectReferenceExistent)</c> or an enumeration of the solution's projects.
    /// The current project is added automatically.
    /// </summary>
    public ITaskItem[] ProjectReferences { get; set; } = Array.Empty<ITaskItem>();

    public override bool Execute()
    {
        if (!IsExecutable)
        {
            Log.LogError("Can only generate Dockerfile for executable projects");
            return false;
        }
        if (string.IsNullOrEmpty(SolutionPath) || SolutionPath == "*Undefined*")
        {
            Log.LogError("Can only generate Dockerfile when building from a solution");
            return false;
        }
        if (TargetFrameworkIdentifier != ".NETCoreApp")
        {
            Log.LogMessage("Only .NET Core projects are supported");
            return true;
        }

        var runtimeVersion = TargetFrameworkVersion.Trim('v');
        if (!decimal.TryParse(runtimeVersion, out var runtimeVersionNum))
        {
            Log.LogWarning("Unsupported .NET Core version");
            return true;
        }

        // Build stage must be at least SDK 10 because NMica's MSBuild task targets .NET 10 and
        // is loaded during `dotnet msbuild /t:PublishLayer` inside the build stage. For newer
        // target TFMs we pick the matching SDK so the runtime pack is already there.
        const decimal MinBuildSdk = 10.0m;
        var buildSdkNum = runtimeVersionNum > MinBuildSdk ? runtimeVersionNum : MinBuildSdk;
        var buildSdkVersion = $"{buildSdkNum:0.0}";

        var solutionDir = Path.GetDirectoryName(SolutionPath)!;
        var projectDir = Path.GetDirectoryName(MSBuildProjectFullPath)!;

        // Collect every csproj to COPY into the build stage: all project references, plus the
        // current project. Dedupe by full path.
        var allProjects = ProjectReferences
            .Select(i => Path.GetFullPath(i.ItemSpec))
            .Append(MSBuildProjectFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var runImageName = UsingMicrosoftNETSdkWeb
            ? "mcr.microsoft.com/dotnet/aspnet"
            : "mcr.microsoft.com/dotnet/runtime";

        var sb = new StringBuilder();
        sb.AppendLine($"FROM mcr.microsoft.com/dotnet/sdk:{buildSdkVersion} AS build");
        sb.AppendLine("WORKDIR src");

        // Walk the build-context tree to find every nuget.config in or above project dirs. Any
        // relative `<add value="..." />` entries in those files become COPY-for-context entries
        // so the restore inside the build stage can see the local feed folders.
        var nugetConfigs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nugetSources = new List<string>();
        foreach (var projFile in allProjects.Append(SolutionPath))
        {
            var dir = Path.GetDirectoryName(projFile)!;
            var cfg = Path.Combine(dir, "nuget.config");
            if (!File.Exists(cfg)) continue;
            if (!nugetConfigs.Add(cfg)) continue;

            using var fs = File.OpenRead(cfg);
            var doc = new XPathDocument(fs);
            var nav = doc.CreateNavigator();
            foreach (XPathNavigator add in nav.Select("/configuration/packageSources/add"))
            {
                var value = add.GetAttribute("value", string.Empty);
                if (string.IsNullOrEmpty(value) || Path.IsPathRooted(value)) continue;
                var src = Path.GetFullPath(Path.Combine(dir, value));
                nugetSources.Add(src);
            }
        }

        if (nugetConfigs.Count > 0)
            sb.AppendLine("# copy nuget.config files at solution and project levels");
        foreach (var cfg in nugetConfigs)
        {
            var rel = GetRelative(solutionDir, cfg);
            sb.AppendLine($"COPY [\"{rel}\", \"{rel}\"]");
        }

        if (nugetSources.Count > 0)
            sb.AppendLine("# copy any local nuget sources that are subfolders of the solution");
        foreach (var src in nugetSources)
        {
            var rel = GetRelative(solutionDir, src);
            sb.AppendLine($"COPY [\"{rel}\", \"{rel}\"]");
        }

        foreach (var proj in allProjects)
        {
            var rel = GetRelative(solutionDir, proj).Replace('\\', '/');
            sb.AppendLine($"COPY [\"{rel}\", \"{rel}\"]");
        }

        var currentProjRel = GetRelative(solutionDir, MSBuildProjectFullPath);
        sb.AppendLine($"RUN dotnet restore \"{currentProjRel}\"");
        sb.AppendLine("COPY . .");

        var currentProjUnix = GetRelative(solutionDir, MSBuildProjectFullPath).Replace('\\', '/');
        sb.AppendLine($"RUN dotnet msbuild /p:RestorePackages=false /t:PublishLayer /p:PublishDir=/layer/ /p:DockerLayer=All \"{currentProjUnix}\"");

        sb.AppendLine();
        sb.AppendLine($"FROM {runImageName}:{runtimeVersion} AS run");
        sb.AppendLine("WORKDIR /app");
        foreach (var layer in KnownLayers.AllLayers)
        {
            sb.AppendLine($"COPY --from=build /layer/{layer.ToString().ToLowerInvariant()} ./");
        }
        sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"{AssemblyName}.dll\"]");

        var dockerfileName = Path.Combine(projectDir, "Dockerfile");
        File.WriteAllText(dockerfileName, sb.ToString());
        Log.LogMessage(MessageImportance.High, $"Generated {dockerfileName}");

        var dockerIgnore = Path.Combine(solutionDir, ".dockerignore");
        if (!File.Exists(dockerIgnore))
        {
            File.WriteAllText(dockerIgnore, "**/bin/\n**/obj/\n**/out/\n**/layer/\n**Dockerfile*\n*/*.md");
            Log.LogMessage(MessageImportance.High, $"Generated {dockerIgnore}");
        }

        return !Log.HasLoggedErrors;
    }

    private static string GetRelative(string basePath, string fullPath)
    {
        var rel = Path.GetRelativePath(basePath, fullPath);
        return rel;
    }
}
