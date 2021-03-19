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
    public class DockerfileUnsupportedTests : BaseTests
    {
        public DockerfileUnsupportedTests(ITestOutputHelper output, TestsSetup setup) : base(output, setup)
        {
        }
        public static IEnumerable<object[]> GetBasicUnsupportedProjects()
        {
            yield return MakeSolution("net472", Sdks.Microsoft_NET_Sdk, "net472", directRef: false);
            yield return MakeSolution("netcoreapp2.1", Sdks.Microsoft_NET_Sdk, "netcoreapp2.1", directRef: false); 
            yield return MakeSolution("netcoreapp3.1, outputType=Library", Sdks.Microsoft_NET_Sdk, "netcoreapp3.1", outputType: "Library", directRef: false);
        }
        
        [Theory]
        [MemberData(nameof(GetBasicUnsupportedProjects))]
        public void BuildSolution_UnsupportedProjects_SkipDockerfileGeneration(SolutionConfiguration solution)
        {
            solution.Generate(_testDir);
            ExecuteProcess(() =>
            {
                var output =  DockerRun(_ => _
                        .EnableRm()
                        .AddVolume(ContainerAppDir)
                        .SetImage(_setup.TestContainerSDKImage)
                        .SetCommand($"dotnet build --verbosity normal {ContainerAppDir / "testapp.sln"}"))
                    .Flatten();
                output.Should().NotContain("GenerateDockerfile:", "GenerateDockerfile target ran on build, but should have been skipped");
                var dockerFilesGenerated = solution.Projects
                    .Select(x => _testDir / x.Name / "Dockerfile")
                    .Where(FileExists)
                    .ToList();
                dockerFilesGenerated.Should().BeEmpty();
            });
        }
    }
}