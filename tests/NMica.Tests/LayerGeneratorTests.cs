using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Ductus.FluentDocker.Builders;
using Ductus.FluentDocker.Model.Builders;
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
        [MemberData(nameof(GetBasicSupportedProjectsNuget))]
        public void BuildSolution_SupportedProjects_ContainerizesAndRuns(SolutionConfiguration solution)
        {
            ExecuteProcess(() =>
            {
                var appProject = solution.Projects.First(x => x.Name == "app1");
                var frameworks = appProject.PropertyGroup.TargetFrameworks?.Split(';') ?? new [] {appProject.PropertyGroup.TargetFramework };
                var isMultiTarget = frameworks.Length > 1;
                foreach (var framework in frameworks)
                {
                    var version = Regex.Replace(framework, @"[a-z\.]", string.Empty);
                    var dockerFile = !isMultiTarget ? "Dockerfile" : $"Dockerfile{version}";
                    solution.Generate(_testDir);

                    DotNetBuild(_ => _
                        .SetProjectFile(_testDir / "testapp.sln"));
                    DockerBuild(_ => _
                        .SetFile(_testDir / appProject.SlnRelativeDir / dockerFile)
                        .SetPath(_testDir)
                        .SetTag(TagName));
                    var result = DockerRun(_ => _
                            .SetRm(true)
                            .SetImage(TagName))
                        .EnsureOnlyStd()
                        .First()
                        .Text;
                    result.Should().Be("PASSED");
                }
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

        public static IEnumerable<object[]> GetSupportedFrameworks()
        {
            yield return new[] {"netcoreapp3.1", "mcr.microsoft.com/dotnet/sdk:3.1"};
            yield return new[] {"net5.0", "mcr.microsoft.com/dotnet/sdk:5.0"};
        }

        [Theory]
        [MemberData(nameof(GetSupportedFrameworks))]
        public void PublishLayer_ComplexSolution_LayersGenerated(string framework, string sdkImage)
        {
            var root = NukeBuild.RootDirectory;
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
                        PropertyGroup = {OutputType = "exe", TargetFramework = framework},
                    },
                    new Project()
                    {
                        Name = "classlib",
                        // SlnRelativeDir = ".",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = {TargetFramework = framework},
                    }
                }
            }.Generate(_testDir);
            using var container = new Builder()
                .UseContainer()
                .UseImage(sdkImage)
                .Mount(_testDir, "/app", MountType.ReadWrite)
                .Build()
                .Start();
            
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
            
            void AssertLayers(AbsolutePath publishDir)
            {
                FileExists(publishDir / "package" / "Newtonsoft.Json.dll").Should().BeTrue();
                FileExists(publishDir / "earlypackage" / "Serilog.dll").Should().BeTrue();
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

        public static IEnumerable<object[]> GetBasicSupportedProjectsNuget() => GetBasicSupportedProjects(false);
        public static IEnumerable<object[]> GetBasicSupportedProjectsTarget() => GetBasicSupportedProjects(true);
        public static IEnumerable<object[]> GetBasicSupportedProjects(bool isDirect)
        {
            yield return MakeSolution("net5.0 Microsoft_NET_Sdk", Sdks.Microsoft_NET_Sdk, "net5.0", isDirect);
            yield return MakeSolution("netcoreapp3.1 Microsoft_NET_Sdk", Sdks.Microsoft_NET_Sdk, "netcoreapp3.1", isDirect);
            yield return MakeSolution("net5.0 Microsoft_NET_Sdk_Web", Sdks.Microsoft_NET_Sdk_Web, "net5.0", isDirect);
            yield return MakeSolution("netcoreapp3.1 Microsoft_NET_Sdk_Web", Sdks.Microsoft_NET_Sdk_Web, "netcoreapp3.1", isDirect);
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
            yield return MakeSolution("netcoreapp2.1", Sdks.Microsoft_NET_Sdk, "netcoreapp2.1"); 
            yield return MakeSolution("netcoreapp2.2", Sdks.Microsoft_NET_Sdk, "netcoreapp2.2");
            yield return MakeSolution("netcoreapp3.0", Sdks.Microsoft_NET_Sdk, "netcoreapp3.0");
            yield return MakeSolution("netcoreapp3.1, outputType=Library", Sdks.Microsoft_NET_Sdk, "netcoreapp3.1", outputType: "Library");
        }

        static object[] MakeSolution(string description, string sdk, string targetFramework, bool directRef = true, string outputType = "exe")
        {
            var isMultiFramework = targetFramework.Split(";").Length > 1;
            var targetFrameworks = string.Empty;
            if (isMultiFramework)
            {
                targetFrameworks = targetFramework;
                targetFramework = String.Empty;
            }

            var itemGroup = new List<object>();
            var imports = new List<Import>();
            var propertyGroup = new PropertyGroup {OutputType = outputType, TargetFramework = targetFramework, TargetFrameworks = targetFrameworks};
            if (directRef)
            {
                var nmicaFramework = targetFramework == "net472" ? targetFramework : "netstandard2.0";
                imports = new List<Import> {Import.NmicaProps, Import.NmicaTargets};
                propertyGroup.NMicaToolsPath = NukeBuild.RootDirectory / "src" / "NMica" / "bin" / "Debug" / nmicaFramework / "NMica.dll";
            }
            else
            {
                itemGroup = new List<object> {PackageReference.NMica};
            }
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
                            PropertyGroup = propertyGroup,
                            ItemGroup = itemGroup,
                            Imports = imports
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