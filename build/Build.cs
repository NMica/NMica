using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.NerdbankGitVersioning;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

/// <summary>
/// Deployment orchestration only. Compilation and testing are driven by vanilla
/// <c>dotnet build</c> / <c>dotnet test</c> — Nuke stays out of that path.
/// </summary>
class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Pack);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";

    [Parameter("NuGet version to use. Default to value provided by Nerdbank GitVersion")]
    string Version;

    [Parameter("Determines if release branch will have pre-release tags applied to it.")]
    readonly bool IsPreRelease = false;

    [Parameter("NuGet ApiKey required in order to push packages")]
    string NugetApiKey;

    AbsolutePath NMicaProject => RootDirectory / "src" / "NMica" / "NMica.csproj";
    AbsolutePath TestProject => RootDirectory / "tests" / "NMica.Tests" / "NMica.Tests.csproj";

    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath TestResultDirectory => ArtifactsDirectory / "test-results";

    [NerdbankGitVersioning(UpdateBuildNumber = true)] readonly NerdbankGitVersioning GitVersion;
    protected override void OnBuildInitialized() => Version ??= GitVersion.NuGetPackageVersion;

    Target Clean => _ => _
        .Executes(() =>
        {
            foreach (var dir in Directory.EnumerateDirectories(RootDirectory / "src", "bin", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateDirectories(RootDirectory / "src", "obj", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateDirectories(RootDirectory / "tests", "bin", SearchOption.AllDirectories))
                .Concat(Directory.EnumerateDirectories(RootDirectory / "tests", "obj", SearchOption.AllDirectories)))
            {
                Directory.Delete(dir, recursive: true);
            }
            if (Directory.Exists(ArtifactsDirectory)) Directory.Delete(ArtifactsDirectory, recursive: true);
            Directory.CreateDirectory(ArtifactsDirectory);
        });

    /// <summary>
    /// Builds NMica and emits a release-versioned .nupkg into <c>artifacts/</c>.
    /// </summary>
    Target Pack => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(NMicaProject)
                .SetConfiguration(Configuration)
                .SetVersion(Version));
        });

    Target Test => _ => _
        .Description("Executes test suite. Requires Docker")
        .Produces(TestResultDirectory / "*.trx")
        .Executes(() =>
        {
            // Using MTP's `dotnet test` (see global.json) — requires --project and uses
            // --results-directory / --report-trx instead of VSTest --logger.
            DotNet($"test --project \"{TestProject}\" --configuration {Configuration} " +
                   $"-- --results-directory \"{TestResultDirectory}\" --report-trx");
        });

    Target Release => _ => _
        .DependsOn(Pack, Test)
        .Requires(() => NugetApiKey)
        .OnlyWhenDynamic(() => string.IsNullOrEmpty(GitVersion.PrereleaseVersion))
        .Executes(() =>
        {
            DotNetNuGetPush(_ => _
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetTargetPath(ArtifactsDirectory / $"NMica.{GitVersion.NuGetPackageVersion}.nupkg")
                .SetApiKey(NugetApiKey));
        });

    Target CutReleaseBranch => _ => _
        .Executes(() => NerdbankGitVersioningTasks.NerdbankGitVersioningPrepareRelease(_ => _
            .SetTag(IsPreRelease ? "beta" : null)));
}
