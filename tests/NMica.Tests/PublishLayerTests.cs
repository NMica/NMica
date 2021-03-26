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
    public class PublishLayerTests : BaseTests
    {
        public PublishLayerTests(ITestOutputHelper output, TestsSetup setup) : base(output, setup)
        {
            
        }
        public static IEnumerable<object[]> GetPublishLayers()
        {
            yield return new object[] {"App", new []
            {
                (RelativePath)"app/app1.dll",
            }};
            yield return new object[] {"Project%2cPackage", new []
            {
                (RelativePath)"package/Newtonsoft.Json.dll",
                (RelativePath)"project/classlib.dll",
            }};
        }

        [Theory]
        [MemberData(nameof(GetPublishLayers))]
        public void PublishLayer_IndividualLayers_LayersGenerated(string layer, RelativePath[] expectedFilesAfterPublish)
        {
            var libProject = new Project()
            {
                Name = "classlib",
                Sdk = Sdks.Microsoft_NET_Sdk,
                PropertyGroup = {TargetFramework = "net5.0"},
            };
            var projects = new SolutionConfiguration
            {
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = {OutputType = "exe", TargetFramework = "net5.0"},
                    }
                        .AddPackageReference("NMica",TestsSetup.NMicaVersion)
                        .AddPackageReference("Newtonsoft.Json", "12.0.3")
                        .AddProjectReference(libProject),
                    libProject
                }
            }.Generate(_testDir);

            var publishDir = ContainerAppDir / "publish";

            DockerRun(_ => _
                .SetProcessEnvironmentVariable("DOCKER_SCAN_SUGGEST","false")
                .EnableRm()
                .AddVolume(ContainerAppDir)
                .SetImage(_setup.TestContainerSDKImage)
                .SetCommand(
                    Batch(
                        $"dotnet build {ContainerAppDir / "testapp.sln"}", // build it first to restore our addon targets
                        $@"dotnet msbuild /t:PublishLayer /p:PublishDir={publishDir} /p:DockerLayer={layer} /p:GenerateDockerfile=False {ContainerAppDir / "app1" / "app1.csproj"}" // build using dotnet cli
                    )));

            var hostExpectedFiles = expectedFilesAfterPublish.Select(x => publishDir.HostPath / x).ToList();
            foreach (var file in hostExpectedFiles)
            {
                FileExists(file).Should().BeTrue();
            }
        }
        
        public static IEnumerable<object[]> GetSupportedFrameworks()
        {
            yield return new[] {"netcoreapp3.1"};
            yield return new[] {"net5.0"};
            yield return new[] {"net6.0"};
        }
        
        [Theory]
        [MemberData(nameof(GetSupportedFrameworks))]
        public void PublishLayer_SupportedFrameworks_LayersGenerated(string framework)
        {
            // Console.WriteLine(sdkImage);
            var root = NukeBuild.RootDirectory;
            var classLib = new Project()
            {
                Name = "classlib",
                // SlnRelativeDir = ".",
                Sdk = Sdks.Microsoft_NET_Sdk,
                PropertyGroup = {TargetFramework = framework},
            };
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
                    }
                        .AddProjectReference(classLib)
                        .AddPackageReference("Nmica", TestsSetup.NMicaVersion)
                        .AddPackageReference("Serilog","2.9.1-dev-01154")
                        .AddPackageReference("Newtonsoft.Json","12.0.3"),
                    classLib
                }
            }.Generate(_testDir);
            

            var containerProjectFile = ContainerAppDir / "app1"  / "app1.csproj";
            var dotnetPublishDir = ContainerAppDir / "dotnet-cli-layers";
            var msbuildPublishDir = ContainerAppDir / "msbuild-cli-layers";
            DockerRun(_ => _
                .EnableRm()
                .SetVolume(ContainerAppDir)
                .SetImage(_setup.TestContainerSDKImage)
                .SetCommand(
                    Batch(
                        $"dotnet build {containerProjectFile}", // build it first to restore our addon targets
                        $@"dotnet msbuild /t:PublishLayer /p:PublishDir={dotnetPublishDir} /p:DockerLayer=All /p:GenerateDockerfile=False {containerProjectFile}", // build using dotnet cli
                        $"msbuild /t:PublishLayer /p:PublishDir={msbuildPublishDir} /p:DockerLayer=All {containerProjectFile} /p:GenerateDockerfile=False"))); // build using msbuild

            AssertLayers(dotnetPublishDir.HostPath);
            AssertLayers(msbuildPublishDir.HostPath);

            void AssertLayers(AbsolutePath publishDir)
            {
                FileExists(publishDir / "package" / "Newtonsoft.Json.dll").Should().BeTrue();
                FileExists(publishDir / "earlypackage" / "Serilog.dll").Should().BeTrue();
                FileExists(publishDir / "project" / "classlib.dll").Should().BeTrue();
                FileExists(publishDir / "app" / "app1.dll").Should().BeTrue();
            }
        }
        
        
        /// <summary>
        /// Build for specific RID and check that native binaries are put into package layer (as they end up in different folder then what's in assets)
        /// </summary>
        [Fact]
        public void PublishLayer_WithNativeDependency_NativeDllsInCorrectLayer()
        {
            new SolutionConfiguration
            {
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        // SlnRelativeDir = ".",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = {OutputType = "exe", TargetFramework = "net6.0"},
                    }
                        .AddPackageReference("Nmica", TestsSetup.NMicaVersion)
                        .AddPackageReference("SQLitePCLRaw.lib.e_sqlite3","2.0.4")
                }
            }.Generate(_testDir);
            

            var containerProjectFile = ContainerAppDir / "app1"  / "app1.csproj"; 
            var dotnetPublishDir = ContainerAppDir / "dotnet-cli-layers";
            DockerRun(_ => _
                .EnableRm()
                .SetVolume(ContainerAppDir)
                .SetImage(_setup.TestContainerSDKImage)
                .SetCommand(
                    Batch(
                        $"dotnet restore -r linux-x64 {containerProjectFile}", // build it first to restore our addon targets
                        $@"dotnet msbuild /t:PublishLayer /p:PublishDir={dotnetPublishDir} /p:DockerLayer=All /p:RuntimeIdentifier=linux-x64 /p:SelfContained=False /p:GenerateDockerfile=False {containerProjectFile}")));

            FileExists(dotnetPublishDir.HostPath / "package" / "libe_sqlite3.so").Should().BeTrue();
        }
    }
}