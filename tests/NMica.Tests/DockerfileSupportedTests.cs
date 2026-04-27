using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using NMica.Tests.Utils;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace NMica.Tests
{
    /// <summary>
    /// Each case here invokes <c>docker build</c> against the Dockerfile NMica generated.
    /// Running them in parallel against the same Docker daemon hits a race in the daemon's
    /// layer-export path on GHA-style containerd setups — serialising avoids the flake.
    /// </summary>
    [NotInParallel(nameof(DockerfileSupportedTests))]
    public class DockerfileSupportedTests : BaseTests
    {
        [Test]
        [MethodDataSource(nameof(GetBasicSupportedProjectsNuget))]
        public async Task BuildSolution_SupportedProjects_ContainerizesAndRuns(SolutionConfiguration solution)
        {
            var appProject = solution.Projects.First(x => x.Name == "app1");
            var frameworks = appProject.PropertyGroup.TargetFrameworks?.Split(';') ?? new[] { appProject.PropertyGroup.TargetFramework };
            var isMultiTarget = frameworks.Length > 1;

            foreach (var framework in frameworks)
            {
                var version = Regex.Replace(framework, "[a-z.]", string.Empty);
                var dockerFile = !isMultiTarget ? "Dockerfile" : $"Dockerfile{version}";

                solution.Generate(TestDir);

                // build inside SDK container so no host caches contaminate results. Explicitly
                // opt into Dockerfile generation — it's off by default as of the PublishContainer
                // integration (where NMica intercepts image creation directly instead).
                await using var sdk = await DockerHelper.StartSdkAsync(Setup.SdkImage, TestDir);
                var buildResult = await sdk.ExecAsync(
                    $"dotnet build -p:GenerateDockerfile=true {DockerHelper.ContainerMount}/{solution.FileName}",
                    Output);
                await Assert.That(buildResult.ExitCode).IsEqualTo(0);

                var tag = TagName;
                var dockerfileRelative = Path.Combine(appProject.SlnRelativeDir, dockerFile);
                await DockerHelper.BuildImageAsync(TestDir, dockerfileRelative, tag);

                var runResult = await DockerHelper.RunImageOnceAsync(tag, Output);
                await Assert.That(runResult.Stdout.Trim()).IsEqualTo("PASSED");
            }
        }

        public static IEnumerable<Func<SolutionConfiguration>> GetBasicSupportedProjectsNuget() => GetBasicSupportedProjects(isDirect: false);

        public static IEnumerable<Func<SolutionConfiguration>> GetBasicSupportedProjects(bool isDirect)
        {
            yield return () => MakeSolution("net8.0 Microsoft_NET_Sdk", Sdks.Microsoft_NET_Sdk, "net8.0", isDirect);
            yield return () => MakeSolution("net8.0 Microsoft_NET_Sdk_Web", Sdks.Microsoft_NET_Sdk_Web, "net8.0", isDirect);
            yield return () => MakeSolution("net10.0 Microsoft_NET_Sdk", Sdks.Microsoft_NET_Sdk, "net10.0", isDirect);
            yield return () => MakeSolution("net10.0 Microsoft_NET_Sdk_Web", Sdks.Microsoft_NET_Sdk_Web, "net10.0", isDirect);

            yield return () => new SolutionConfiguration
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
                        PropertyGroup = { OutputType = "exe", TargetFramework = "net10.0" },
                        ItemGroup = { PackageReference.NMica }
                    }
                }
            };

            yield return () => new SolutionConfiguration
            {
                Description = "solution /w unrelated project",
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = { OutputType = "exe", TargetFramework = "net10.0" },
                        ItemGroup = { PackageReference.NMica, new ProjectReference("..\\common\\common.csproj") }
                    },
                    new Project
                    {
                        Name = "app2",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = { OutputType = "exe", TargetFramework = "net10.0" },
                        ItemGroup = { PackageReference.NMica }
                    },
                    new Project
                    {
                        Name = "common",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = { TargetFramework = "net10.0" }
                    }
                }
            };
        }
    }
}
