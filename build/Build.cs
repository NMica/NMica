using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.CI.AzurePipelines;
using Nuke.Common.Execution;
using Nuke.Common.IO;
using Nuke.Common.OutputSinks;
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
// using static Nuke.Common.Tools.Docker.DockerTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using static Nuke.Common.ControlFlow;

[CheckBuildProjectConfigurations]
[UnsetVisualStudioEnvironmentVariables]
// [AzurePipelines(
//     image: AzurePipelinesImage.WindowsLatest,
//     AutoGenerate = true,
//     NonEntryTargets = new[]{nameof(Clean)},
//     InvokedTargets = new []{nameof(CI)})]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly string Configuration = IsLocalBuild ? "Debug" : "Release";
    [Parameter("Nuget version to use. Default to value provided by Nerdbank GitVersion")] 
    string Version;

    [Parameter("Determines if release branch will have pre-release tags applied to it. Default is false, meaning when cutting new version it is considered final (stable) package")]
    readonly bool IsPreRelease = false;

    [Parameter("Nuget ApiKey required in order to push packages")]
    string NugetApiKey;
    
    [Parameter] string DockerUsername;
    [Parameter] string DockerPassword;
    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath TestsDirectory => RootDirectory / "tests";
    AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
    AbsolutePath CacheDirectory => RootDirectory / "cache";
    
    
    
    [Solution] readonly Solution Solution;
    Project NMicaProject => Solution.GetProject("NMica")!;
    Project TestProject => Solution.GetProject("NMica.Tests")!;
    
    [Partition(2)] readonly Partition TestPartition;
    IEnumerable<Project> TestProjects => TestPartition.GetCurrent(Solution.GetProjects("*.Tests"));
    AbsolutePath TestResultDirectory => ArtifactsDirectory / "test-results";

    [NerdbankGitVersioning(UpdateBuildNumber = true)] readonly NerdbankGitVersioning GitVersion;
    [NerdbankGitVersioning(Project = "tests/NMica.Tests/BuilderImage")] readonly NerdbankGitVersioning TestBuilderVersion;
    const string TestPackageNugetVersion = "1.0.0-test";
    const string TestBuilderImageName = "nmica-test-container";
    static readonly string TestBuilderDockerRepository = $"macsux/{TestBuilderImageName}";
    string TestBuilderDockerImageWithTag => $"{TestBuilderDockerRepository}:{TestBuilderVersion.NuGetPackageVersion}";
    protected override void OnBuildInitialized() => Version ??= GitVersion.NuGetPackageVersion;
    

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


    Target CompileSource => _ => _
        .DependsOn(Restore)
        .Unlisted()
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(NMicaProject.Path)
                .SetConfiguration(Configuration)
                .EnableNoRestore());
        });

    Target CompileTests => _ => _
        .DependsOn(Restore)
        .Unlisted()
        .Executes(() =>
        {
            DotNetBuild(_ => _
                .SetProjectFile(TestProject.Path)
                .SetConfiguration(Configuration)
                .SetProperty("NoDependencyBuild", true)
                .EnableNoRestore());
        });

    Target Compile => _ => _
        .DependsOn(Restore, CompileSource, CompileTests);
    
    Target Publish => _ => _
        .DependsOn(Clean, Compile)
        .Description("Creates nuget package in artifacts directory")
        .Executes(() =>
        {
            DoPublish(Version);
            // if (!GitVersion.PublicRelease)
            // {
                ArtifactsDirectory.GlobFiles("*.nupkg").ForEach(package =>
                    AzurePipelines.Instance?.UploadArtifacts("", "nuget", package));
            // }
        });

    void DoPublish(string packageVersion)
    {
        packageVersion ??= Version;

        EnsureCleanDirectory(TemporaryDirectory);
        var dockerProject = NMicaProject.Directory;
        var dockerCompileDir = dockerProject / "bin" / Configuration;
            
        CopyDirectoryRecursively(dockerProject / "nuget", TemporaryDirectory, DirectoryExistsPolicy.Merge);
        CopyDirectoryRecursively(dockerCompileDir, TemporaryDirectory / "tasks");

        DotNetPack(_ => _
            .SetProject(NMicaProject.Path)
            .DisableRunCodeAnalysis()
            .EnableNoBuild()
            .AddProperty("NuspecFile", TemporaryDirectory / "NMica.nuspec")
            .AddProperty("NoPackageAnalysis", true)
            .AddProperty("NuspecProperties", $"version={packageVersion}")
            .SetOutputDirectory(ArtifactsDirectory));
            
        
    }


    Target PublishTest => _ => _
        .Unlisted()
        .DependsOn(Clean, Compile)
        .Executes(() =>
        {
            DoPublish(TestPackageNugetVersion);
        });

    Target Release => _ => _
        .DependsOn(Publish,Test)
        .Requires(() => NugetApiKey)
        .OnlyWhenDynamic(() => string.IsNullOrEmpty(GitVersion.PrereleaseVersion)) // we don't publish non final releases to nuget.org - prerelease builds are available on azure artifacts feed
        .Executes(() =>
        {
            DotNetNuGetPush(_ => _
                .SetSource("https://api.nuget.org/v3/index.json")
                .SetTargetPath(ArtifactsDirectory / $"NMica.{GitVersion.NuGetPackageVersion}.nupkg")
                .SetApiKey(NugetApiKey));
        });

    Target LoginDocker => _ => _
        .Requires(() => DockerUsername, () => DockerPassword)
        .Unlisted()
        .Executes(() =>
        {
            DockerTasks.DockerLogin(_ => _
                .SetUsername(DockerUsername)
                .SetPassword(DockerPassword)
                .DisableProcessLogOutput());
        });
    Target PullBuilderImage => _ => _
        .OnlyWhenDynamic(() => !IsDockerImageInLocalRepo(TestBuilderDockerImageWithTag))
        .Executes(() =>
        {
            try
            {
                DockerTasks.DockerPull(_ => _
                    .SetName(TestBuilderDockerImageWithTag));
            }
            catch (ProcessException e) when(e.Message.Contains("not found"))
            {
                
            }
        });

    Target MakeBuilderImage => _ => _
        .After(PullBuilderImage)
        .OnlyWhenDynamic(() => !IsDockerImageInLocalRepo(TestBuilderDockerImageWithTag))
        .Executes(() =>
        {
            var builderImageDir = TestProject.Directory / "BuilderImage";
            DockerTasks.DockerBuild(_ => _
                .SetPath(builderImageDir)
                .SetProcessWorkingDirectory(builderImageDir)
                .SetTag(TestBuilderDockerImageWithTag)
                .SetProcessEnvironmentVariable("DOCKER_SCAN_SUGGEST","false"));
        });

    Target EnsureLatestBuilderImage => _ => _
        .Before(Test)
        .DependsOn(PullBuilderImage, MakeBuilderImage, PublishLatestBuilder);

    Target PublishLatestBuilder => _ => _
        .DependsOn(MakeBuilderImage, LoginDocker)
        // .OnlyWhenDynamic(() => !SkippedTargets.Any(x => x.Factory == MakeBuilderImage))
        .Executes(() =>
        {
            DockerTasks.DockerPush(_ => _
                .SetName(TestBuilderDockerImageWithTag));
        });

    Target Test => _ => _
        .DependsOn(PublishTest)
        .Produces(TestResultDirectory / "*.trx")
        .Produces(TestResultDirectory / "*.xml")
        .Description("Executes test suite. Requires Docker")
        .Executes(() =>
        {
            
            try
            {
                DotNetTest(_ => _
                    .SetConfiguration(Configuration)
                    .SetNoBuild(InvokedTargets.Contains(Compile))
                    .ResetVerbosity()
                    .AddProperty("NoDependencyBuild", true)
                    .SetResultsDirectory(TestResultDirectory)
                    .SetProjectFile(TestProject.Path)
                    .CombineWith(TestProjects, (_, v) => _
                        .SetProjectFile(v)
                        .SetProcessWorkingDirectory(v.Directory)
                        .SetLogger($"trx;LogFileName={v.Name}.trx")),
                    completeOnFailure: true);
                
            }
            finally
            {
                ReportTestResults();
            }
        });

    bool IsDockerImageInLocalRepo(string image) =>
        DockerTasks.DockerImages(c => c
            .SetFormat("'{{json .}}'")
            .SetRepository(image)
            .DisableProcessLogOutput())
        .Any();
    
    void ReportTestResults()
    {
        TestResultDirectory.GlobFiles("*.trx").ForEach(x =>
            AzurePipelines.Instance?.PublishTestResults(
                type: AzurePipelinesTestResultsType.VSTest,
                title: $"{Path.GetFileNameWithoutExtension(x)} ({AzurePipelines.Instance.StageDisplayName})",
                files: new string[] { x }));
    }

    Target CutReleaseBranch => _ => _
        .Executes(() => NerdbankGitVersioningPrepareRelease(_ => _
            .SetProcessWorkingDirectory(RootDirectory)
            .SetTag(IsPreRelease ? "beta" : null)));


    Target CI => _ => _
        .Unlisted()
        .Triggers(Publish, EnsureLatestBuilderImage, Test, Release)
        .Executes(() =>
        {
            AzurePipelines.Instance?.UpdateBuildNumber(GitVersion.CloudBuildNumber);
        });
}
