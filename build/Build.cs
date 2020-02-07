using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
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

    [Solution] readonly Solution Solution;
    // [GitRepository] readonly GitRepository GitRepository;
    // [GitVersion] readonly GitVersion GitVersion;


    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";

    string NMicaProject => Solution.GetProject("NMica").Path;
    string Version { get; set; } = "local";
    
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
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target Publish => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            
            EnsureCleanDirectory(TemporaryDirectory);
            EnsureCleanDirectory(ArtifactsDirectory);
            var dockerProject = Solution.GetProject("NMica").Directory;
            var dockerCompileDir = dockerProject / "bin" / Configuration;
            
            CopyDirectoryRecursively(dockerProject / "nuget", TemporaryDirectory, DirectoryExistsPolicy.Merge);
            CopyDirectoryRecursively(dockerCompileDir, TemporaryDirectory / "tasks");

            // var buildName = $"NMica-dev{DateTime.Now:yyyyMMddhhmmss}";
            NuGetTasks.NuGetPack(c => c
                .SetTargetPath(TemporaryDirectory / "NMica.nuspec")
                .SetNoPackageAnalysis(true)
                .SetVersion("1.0.0-local")
                .SetOutputDirectory(ArtifactsDirectory));
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
        .DependsOn(Publish, CleanNugetCache)
        .Executes(() =>
        {
            var samplesSolution = Solution.Directory / "Samples" / "MultiProjectWebApp" / "MultiProjectWebApp.sln";
            var output = DotNetBuild(_ => _
                .SetProjectFile(samplesSolution)
                .SetConfiguration(Configuration));
            Assert(output.Select(x => x.Text).Any(x => x.Contains("Generated Dockerfile")), "Building with `dotnet build` didn't executed expected target");
            MSBuild(_ => _
                .SetProjectFile(samplesSolution)
                .SetConfiguration(Configuration));
            Assert(output.Select(x => x.Text).Any(x => x.Contains("Generated Dockerfile")), "Building with `MSBuild` didn't executed expected target");
        });

}
