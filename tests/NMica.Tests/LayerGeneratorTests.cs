using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NMica.Tests.Utils;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Utilities.Collections;
using Xunit;
using Xunit.Abstractions;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Docker.DockerTasks;
using PackageReference = NMica.Tests.Utils.PackageReference;


namespace NMica.Tests
{
    public class LayerGeneratorTests : IDisposable, IClassFixture<TestsSetup>
    {
        private readonly ITestOutputHelper _output;
        private readonly TestsSetup _setup;
        private readonly AbsolutePath _testDir;
        private readonly string _testName;
        private string TagName => _testName.Replace(".", "_").ToLower();


        public LayerGeneratorTests(ITestOutputHelper output, TestsSetup setup)
        {
            _output = output;
            _setup = setup;
            _testName = Guid.NewGuid().ToString("N");
            _testDir = (AbsolutePath) Directory.CreateDirectory(_testName).FullName;
            DeleteDirectory(_testDir);
            CopyDirectoryRecursively(NukeBuild.RootDirectory / "artifacts", _testDir / "artifacts");

        }

        [Theory]
        [MemberData(nameof(GetBasicSupportedProjects))]
        public void BuildSolution_SupportedProjects_ContainerizesAndRuns(SolutionConfiguration solution)
        {
            ExecuteProcess(() =>
            {
                var appProject = solution.Projects.First(x => x.Name == "app1");
                solution.Generate(_testDir);
                
                DotNetBuild(_ => _
                    .SetProjectFile(_testDir / "testapp.sln"));
                DockerBuild(_ => _
                    .SetFile(_testDir /  appProject.SlnRelativeDir / "Dockerfile")
                    .SetPath(_testDir)
                    .SetTag(TagName));
                var result = DockerRun(_ => _
                        .SetRm(true)
                        .SetImage(TagName))
                    .EnsureOnlyStd()
                    .First()
                    .Text;
                result.Should().Be("PASSED");
            });
        }


        [Theory]
        [MemberData(nameof(GetBasicUnsupportedProjects))]
        public void BuildSolution_UnsupportedProjects_SkipDockerfileGeneration(SolutionConfiguration solution)
        {
            solution.Generate(_testDir);
            ExecuteProcess(() =>
            {
                var output = DotNetBuild(_ => _
                        .SetVerbosity(DotNetVerbosity.Normal)
                        .SetProjectFile(_testDir / "testapp.sln"))
                    .Flatten();
                output.Should().NotContain("GenerateDockerfile:", "GenerateDockerfile target ran on build, but should have been skipped");
                var dockerFilesGenerated = solution.Projects
                    .Select(x => _testDir / x.Name / "Dockerfile")
                    .Where(FileExists)
                    .ToList();
                dockerFilesGenerated.Should().BeEmpty();
            });
        }

        [Fact]
        public void PublishLayer_ComplexSolution_LayersGenerated()
        {
            var projects = new SolutionConfiguration
            {
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        // SlnRelativeDir = ".",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = {OutputType = "exe", TargetFramework = "netcoreapp3.1"},
                    },
                    new Project()
                    {
                        Name = "classlib",
                        // SlnRelativeDir = ".",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = {TargetFramework = "netcoreapp3.1"},
                    }
                }
            }.Generate(_testDir);
            var projectFile = projects
                .Where(x => x.Value.Name == "app1")
                .Select(x => x.Key)
                .First();
            DotNet($@"add {projectFile} reference ..{Path.DirectorySeparatorChar}classlib{Path.DirectorySeparatorChar}classlib.csproj", projectFile.Parent);
            DotNet($@"add {projectFile} package NMica -v {TestsSetup.NMicaVersion.NuGetPackageVersion} ");
            DotNet($@"add {projectFile} package Serilog -v 2.9.1-dev-01154 ");
            DotNet($@"add {projectFile} package Newtonsoft.Json -v 12.0.1 ");
            DotNetRestore(_ => _
                .SetProjectFile(projectFile));
            
            var cliPublishDir = _testDir / "cli-layers";
            DotNet($"msbuild /t:PublishLayer /p:PublishDir={cliPublishDir} /p:DockerLayer=All {projectFile} /p:GenerateDockerfile=False");
            AssertLayers(cliPublishDir);
            
            var msbuildPublishDir = _testDir / "msbuild-layers";
            MSBuildTasks.MSBuild(_ => _
                .SetProjectFile(projectFile)
                .SetTargets("PublishLayer")
                .AddProperty("PublishDir", msbuildPublishDir)
                .AddProperty("DockerLayer", "All")
                .AddProperty("GenerateDockerfile", false));
            AssertLayers(msbuildPublishDir);

            var cliMultiLayerPublishDir = _testDir / "cli-layers";
            DotNet($"msbuild /t:PublishLayer /p:PublishDir={cliMultiLayerPublishDir} /p:DockerLayer=App,Project {projectFile} /p:GenerateDockerfile=False");
            AssertLayers(cliMultiLayerPublishDir, true);

            var msbuildMultiLayerPublishDir = _testDir / "msbuild-layers";
            MSBuildTasks.MSBuild(_ => _
                .SetProjectFile(projectFile)
                .SetTargets("PublishLayer")
                .AddProperty("PublishDir", msbuildMultiLayerPublishDir)
                .AddProperty("DockerLayer", "App,Project")
                .AddProperty("GenerateDockerfile", false));
            AssertLayers(msbuildMultiLayerPublishDir, true);


            void AssertLayers(AbsolutePath publishDir, bool multiLayer = false)
            {
                if (multiLayer)
                {
                    FileExists(publishDir / "package" / "Newtonsoft.Json.dll").Should().BeFalse();
                    FileExists(publishDir / "earlypackage" / "Serilog.dll").Should().BeFalse();
                }
                else
                {
                    FileExists(publishDir / "package" / "Newtonsoft.Json.dll").Should().BeTrue();
                    FileExists(publishDir / "earlypackage" / "Serilog.dll").Should().BeTrue();
                }

                FileExists(publishDir / "project" / "classlib.dll").Should().BeTrue();
                FileExists(publishDir / "app" / "app1.dll").Should().BeTrue();
            }
        }

        private void ExecuteProcess(Action action)
        {
            try
            {
                action();
            }
            catch (ProcessException e)
            {
                // many details of errors from builds are actually sent to stdout which current ProcessException doesn't include in msg
                var process = (IProcess) typeof(ProcessException).GetProperty("Process", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(e);
                process.Output.ForEach(x => _output.WriteLine(x.Text));
                throw;
            }
        }

        public static IEnumerable<object[]> GetBasicSupportedProjects()
        {
            yield return MakeSolution("netcoreapp2.1 Microsoft_NET_Sdk", Sdks.Microsoft_NET_Sdk, "netcoreapp2.1");
            yield return MakeSolution("netcoreapp3.1 Microsoft_NET_Sdk", Sdks.Microsoft_NET_Sdk, "netcoreapp3.1");
            yield return MakeSolution("netcoreapp2.1 Microsoft_NET_Sdk_Web", Sdks.Microsoft_NET_Sdk_Web, "netcoreapp2.1");
            yield return MakeSolution("netcoreapp3.1 Microsoft_NET_Sdk_Web", Sdks.Microsoft_NET_Sdk_Web, "netcoreapp3.1");
            yield return new[]
            {
                new SolutionConfiguration
                {
                    Description = "project folder has spaces",
                    NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                    Projects =
                    {
                        new Project
                        {
                            Name = "app1",
                            SlnRelativeDir = "app 1",
                            Sdk = Sdks.Microsoft_NET_Sdk,
                            PropertyGroup = {OutputType = "exe", TargetFramework = "netcoreapp3.1"},
                            ItemGroup = {PackageReference.NMica}
                        }
                    }
                }
            };
            yield return new[]
            {
                new SolutionConfiguration
                {
                    Description = "solution /w unrelated project",
                    NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                    Projects =
                    {
                        new Project
                        {
                            Name = "app1",
                            Sdk = Sdks.Microsoft_NET_Sdk,
                            PropertyGroup = {OutputType = "exe", TargetFramework = "netcoreapp3.1"},
                            ItemGroup = {PackageReference.NMica, new ProjectReference("..\\common\\common.csproj")},
                        },
                        new Project
                        {
                            Name = "app2",
                            Sdk = Sdks.Microsoft_NET_Sdk,
                            PropertyGroup = {OutputType = "exe", TargetFramework = "netcoreapp3.1"},
                            ItemGroup = {PackageReference.NMica}
                        },
                        new Project
                        {
                            Name = "common",
                            Sdk = Sdks.Microsoft_NET_Sdk,
                            PropertyGroup = {TargetFramework = "netcoreapp3.1"},
                            
                        }
                    }
                }
            };
        }

  

        public static IEnumerable<object[]> GetBasicUnsupportedProjects()
        {
            yield return MakeSolution("net472", Sdks.Microsoft_NET_Sdk, "net472");
            yield return MakeSolution("netcoreapp2.0", Sdks.Microsoft_NET_Sdk, "netcoreapp2.0");
            yield return MakeSolution("netcoreapp2.2", Sdks.Microsoft_NET_Sdk, "netcoreapp2.2");
            yield return MakeSolution("netcoreapp3.0", Sdks.Microsoft_NET_Sdk, "netcoreapp3.0");
            yield return MakeSolution("netcoreapp3.1, outputType=Library", Sdks.Microsoft_NET_Sdk, "netcoreapp3.1", outputType: "Library");
        }

        static object[] MakeSolution(string description, string sdk, string targetFramework, string outputType = "exe")
        {
            return new object[]
            {
                new SolutionConfiguration
                {
                    Description = description,
                    NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                    Projects =
                    {
                        new Project
                        {
                            Sdk = sdk,
                            PropertyGroup = {OutputType = outputType, TargetFramework = targetFramework},
                            ItemGroup = {PackageReference.NMica}
                        }
                    }
                }

            };
        }

        public void Dispose()
        {
            // Directory.Delete(_testDir, true);
        }
    }
}