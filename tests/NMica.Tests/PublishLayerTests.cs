using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using NMica.Tests.Utils;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace NMica.Tests
{
    public class PublishLayerTests : BaseTests
    {
        public static IEnumerable<Func<(string layer, string[] expectedFiles)>> GetPublishLayers()
        {
            yield return () => ("App", new[] { "app/app1.dll" });
            yield return () => ("Project%2cPackage", new[] { "package/Newtonsoft.Json.dll", "project/classlib.dll" });
        }

        [Test]
        [MethodDataSource(nameof(GetPublishLayers))]
        public async Task PublishLayer_IndividualLayers_LayersGenerated(string layer, string[] expectedFilesAfterPublish)
        {
            var libProject = new Project
            {
                Name = "classlib",
                Sdk = Sdks.Microsoft_NET_Sdk,
                PropertyGroup = { TargetFramework = "net10.0" }
            };
            new SolutionConfiguration
            {
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = { OutputType = "exe", TargetFramework = "net10.0" }
                    }
                        .AddPackageReference("NMica", TestsSetup.NMicaVersion)
                        .AddPackageReference("Newtonsoft.Json", "13.0.3")
                        .AddProjectReference(libProject),
                    libProject
                }
            }.Generate(TestDir);

            var publishDir = $"{DockerHelper.ContainerMount}/publish";

            await using var sdk = await DockerHelper.StartSdkAsync(Setup.SdkImage, TestDir);
            (await sdk.ExecAsync($"dotnet build {DockerHelper.ContainerMount}/testapp.sln", Output)).EnsureSuccess();
            (await sdk.ExecAsync(
                $"dotnet msbuild /t:PublishLayer /p:PublishDir={publishDir} /p:DockerLayer={layer} /p:GenerateDockerfile=False {DockerHelper.ContainerMount}/app1/app1.csproj",
                Output)).EnsureSuccess();

            foreach (var relative in expectedFilesAfterPublish)
            {
                var hostFile = Path.Combine(TestDir, "publish", relative.Replace('/', Path.DirectorySeparatorChar));
                await Assert.That(File.Exists(hostFile)).IsTrue();
            }
        }

        public static IEnumerable<string> GetSupportedFrameworks()
        {
            yield return "net8.0";
            yield return "net10.0";
        }

        [Test]
        [MethodDataSource(nameof(GetSupportedFrameworks))]
        public async Task PublishLayer_SupportedFrameworks_LayersGenerated(string framework)
        {
            var classLib = new Project
            {
                Name = "classlib",
                Sdk = Sdks.Microsoft_NET_Sdk,
                PropertyGroup = { TargetFramework = framework }
            };
            new SolutionConfiguration
            {
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = { OutputType = "exe", TargetFramework = framework }
                    }
                        .AddProjectReference(classLib)
                        .AddPackageReference("NMica", TestsSetup.NMicaVersion)
                        .AddPackageReference("Serilog", "2.9.1-dev-01154")
                        .AddPackageReference("Newtonsoft.Json", "13.0.3"),
                    classLib
                }
            }.Generate(TestDir);

            var containerProjectFile = $"{DockerHelper.ContainerMount}/app1/app1.csproj";
            var containerPublishDir = $"{DockerHelper.ContainerMount}/publish-layers";

            await using var sdk = await DockerHelper.StartSdkAsync(Setup.SdkImage, TestDir);
            (await sdk.ExecAsync($"dotnet build {containerProjectFile}", Output)).EnsureSuccess();
            (await sdk.ExecAsync(
                $"dotnet msbuild /t:PublishLayer /p:PublishDir={containerPublishDir} /p:DockerLayer=All /p:GenerateDockerfile=False {containerProjectFile}",
                Output)).EnsureSuccess();

            var publishDir = Path.Combine(TestDir, "publish-layers");
            await Assert.That(File.Exists(Path.Combine(publishDir, "package", "Newtonsoft.Json.dll"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(publishDir, "earlypackage", "Serilog.dll"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(publishDir, "project", "classlib.dll"))).IsTrue();
            await Assert.That(File.Exists(Path.Combine(publishDir, "app", "app1.dll"))).IsTrue();
        }

        /// <summary>
        /// When publishing for a specific RID, native binaries land in the publish root (not under
        /// runtimes/&lt;rid&gt;/native/). Make sure PublishLayer still routes them to the package layer.
        /// </summary>
        [Test]
        public async Task PublishLayer_WithNativeDependency_NativeDllsInCorrectLayer()
        {
            new SolutionConfiguration
            {
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = { OutputType = "exe", TargetFramework = "net10.0" }
                    }
                        .AddPackageReference("NMica", TestsSetup.NMicaVersion)
                        .AddPackageReference("SQLitePCLRaw.lib.e_sqlite3", "2.0.4")
                }
            }.Generate(TestDir);

            var containerProjectFile = $"{DockerHelper.ContainerMount}/app1/app1.csproj";
            var containerPublishDir = $"{DockerHelper.ContainerMount}/publish-layers";

            await using var sdk = await DockerHelper.StartSdkAsync(Setup.SdkImage, TestDir);
            (await sdk.ExecAsync($"dotnet restore -r linux-x64 {containerProjectFile}", Output)).EnsureSuccess();
            (await sdk.ExecAsync(
                $"dotnet msbuild /t:PublishLayer /p:PublishDir={containerPublishDir} /p:DockerLayer=All /p:RuntimeIdentifier=linux-x64 /p:SelfContained=False /p:GenerateDockerfile=False {containerProjectFile}",
                Output)).EnsureSuccess();

            var hostNativeLib = Path.Combine(TestDir, "publish-layers", "package", "libe_sqlite3.so");
            await Assert.That(File.Exists(hostNativeLib)).IsTrue();
        }
    }
}
