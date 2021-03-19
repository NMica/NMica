using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using NMica.Tests.Utils;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Utilities.Collections;
using Xunit;
using Xunit.Abstractions;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Docker.DockerTasks;
using PackageReference = NMica.Tests.Utils.PackageReference;

namespace NMica.Tests
{
    [Collection("Docker")]
    public class DockerfileSupportedTests : BaseTests
    {
        public DockerfileSupportedTests(ITestOutputHelper output, TestsSetup setup) : base(output, setup)
        {
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
                    solution.Generate(ContainerAppDir.HostPath);
                    
                    // compile in docker
                    DockerRun(_ => _
                        .EnableRm()
                        .AddVolume(ContainerAppDir)
                        .SetImage(_setup.TestContainerSDKImage)
                        .SetCommand($"dotnet build {ContainerAppDir / "testapp.sln"}"));

                    
                    DockerBuild(_ => _
                        .SetFile(_testDir / appProject.SlnRelativeDir / dockerFile)
                        .SetPath(_testDir)
                        .SetTag(TagName));
                    var result = DockerRun(_ => _
                            .SetRm(true)
                            .SetImage(TagName));
                    var stdOut = result
                        .FirstOrDefault(x => x.Type == OutputType.Std)
                        .Text;
                    var stdErr = string.Join("\n", result.Where(x => x.Type == OutputType.Err).Select(x => x.Text));
                    if (string.IsNullOrWhiteSpace(stdErr))
                        stdErr = null;
                    stdOut.Should().Be("PASSED", $"{stdErr} \n {stdOut}");
                    if(stdErr != null)
                        Logger.Warn($"Container emitted the following to error output \n {stdErr}");
                }
            });
        }

        public static IEnumerable<object[]> GetBasicSupportedProjectsNuget() => GetBasicSupportedProjects(false);
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
    }
}