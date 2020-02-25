using System;
using System.Diagnostics;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NerdbankGitVersioning;
using static Nuke.Common.Tools.NerdbankGitVersioning.NerdbankGitVersioningTasks;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.ControlFlow;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("Framework to build against - netstandard2.0 or net472")]
    readonly string Framework;
    
    [Solution] readonly Solution Solution;
    
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath SamplesSolutionDir => Solution.Directory / "Samples" / "MultiProjectWebApp";

    [Parameter("Determines if release branch will have pre-release tags applied to it. Default is false, meaning when cutting new version it is considered final (stable) package")]
    readonly bool IsPreRelease = false;
    [Parameter("Nuget ApiKey required in order to push packages")]
    string NugetApiKey;

    string NMicaProject => Solution.GetProject("NMica").Path;

    [NerdbankGitVersioning] readonly NerdbankGitVersioning GitVersion;

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            TestsDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(_ => _
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(Solution)
                .SetFramework(Framework)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Publish => _ => _
        .DependsOn(Clean,Compile)
        .Description("Creates nuget package in artifacts directory")
        .Executes(() =>
        {
            
            EnsureCleanDirectory(TemporaryDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
            var dockerProject = Solution.GetProject("NMica").Directory;
            var dockerCompileDir = dockerProject / "bin" / Configuration;
            
            CopyDirectoryRecursively(dockerProject / "nuget", TemporaryDirectory, DirectoryExistsPolicy.Merge);
            CopyDirectoryRecursively(dockerCompileDir, TemporaryDirectory / "tasks");

            DotNetPack(_ => _
                    .SetProject(Solution.Path)
                    .DisableRunCodeAnalysis()
                    .AddProperty("NuspecFile", TemporaryDirectory / "NMica.nuspec")
                    .AddProperty("NoPackageAnalysis", true)
                    .AddProperty("NuspecProperties", $"version={GitVersion.NuGetPackageVersion}")
                    .SetOutputDirectory(ArtifactsDirectory));
        });

    Target Release => _ => _
        .After(Publish,Test)
        .OnlyWhenDynamic(() => string.IsNullOrEmpty(GitVersion.PrereleaseVersion)) // we don't publish non final releases to nuget.org - prerelease builds are available on azure artifacts feed
        .Executes(() =>
        {
            DotNetNuGetPush(_ => _
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetTargetPath(ArtifactsDirectory / $"NMica.{GitVersion.NuGetPackageVersion}.nupkg")
                .SetApiKey(NugetApiKey));
        });

    Target CleanNugetCache => _ => _
        .DependsOn(Publish)
        .Unlisted()
        .Executes(() =>
        {
            // for some reason dotnet is leaving locks on nuget dll after tests even after existing
            // this is a dirty hack that kills every dotnet process except current one to release lock
            foreach (var process in Process.GetProcesses()
                .Where(x => x.ProcessName == "dotnet" && x.Id != Process.GetCurrentProcess().Id))
            {
                process.Kill(true);
            }
                
            var userFolder = (AbsolutePath) Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var nugetCacheFolder = userFolder / ".nuget" / "packages";
            DeleteDirectory(nugetCacheFolder / "NMica");
            
        });
    

    Target Test => _ => _
        .After(Publish)
        .Description("Executes test suite. Requires Docker")
        .Executes(() =>
        {
            var testProject = RootDirectory / "tests" / "NMica.Tests" / "NMica.Tests.csproj";
            DotNetTest(_ => _
                .SetProjectFile(testProject)
                .SetWorkingDirectory(testProject.Parent));
        });

    Target CutReleaseBranch => _ => _
        .Executes(() => NerdbankGitVersioningPrepareRelease(_ => _
            .SetWorkingDirectory(RootDirectory)
            .SetTag(IsPreRelease ? "beta" : null)));

    Target CI => _ => _
        .Unlisted()
        .Triggers(Publish, Test, Release)
        .Executes(() => NerdbankGitVersioningCloud());
}
