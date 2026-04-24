using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NMica.Tests.Utils;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace NMica.Tests
{
    public class DockerfileUnsupportedTests : BaseTests
    {
        public static IEnumerable<Func<SolutionConfiguration>> GetBasicUnsupportedProjects()
        {
            // Library outputType never gets a Dockerfile generated regardless of framework.
            yield return () => MakeSolution("net10.0, outputType=Library", Sdks.Microsoft_NET_Sdk, "net10.0", outputType: "Library");
        }

        [Test]
        [MethodDataSource(nameof(GetBasicUnsupportedProjects))]
        public async Task BuildSolution_UnsupportedProjects_SkipDockerfileGeneration(SolutionConfiguration solution)
        {
            solution.Generate(TestDir);

            // Pass GenerateDockerfile=true explicitly: otherwise the target is skipped by its own
            // property condition and the test becomes trivial. We want to verify that NMica's
            // NMicaSupportedProject gate keeps the target off *even when the user opts in*.
            await using var sdk = await DockerHelper.StartSdkAsync(Setup.SdkImage, TestDir);
            var result = await sdk.ExecAsync(
                $"dotnet build -p:GenerateDockerfile=true --verbosity normal {DockerHelper.ContainerMount}/{solution.FileName}",
                Output);

            await Assert.That(result.Stdout).DoesNotContain("GenerateDockerfile:");

            var dockerfilesGenerated = solution.Projects
                .Select(x => Path.Combine(TestDir, x.Name, "Dockerfile"))
                .Where(File.Exists)
                .ToList();
            await Assert.That(dockerfilesGenerated).IsEmpty();
        }
    }
}
