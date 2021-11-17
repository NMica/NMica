using System;
using Nuke.Common;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.NerdbankGitVersioning;
using DockerImagePruneSettingsExtensions = Nuke.Common.Tools.Docker.DockerImagePruneSettingsExtensions;

namespace NMica.Tests.Utils
{
    public class TestsSetup : IDisposable
    {
        public readonly string TestContainerSDKImage;
        private readonly object _lock = new();
        public TestsSetup ()
        {
            ToolPathResolver.NuGetPackagesConfigFile = NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "NMica.Tests.csproj";

            var version = NerdbankGitVersioningTasks.NerdbankGitVersioningGetVersion(s => s
                    .SetProcessWorkingDirectory(NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "BuilderImage")
                    .DisableProcessLogOutput()
                    .SetFormat(NerdbankGitVersioningFormat.Json))
                .Result;
            TestContainerSDKImage = $"macsux/nmica-test-container:{version.NuGetPackageVersion}";

            // lock (_lock)
            // {
            //     ToolPathResolver.NuGetPackagesConfigFile = NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "NMica.Tests.csproj";
            //     var imageExists = DockerTasks.DockerImages(c => c
            //             .SetFormat("'{{json .}}'")
            //             .SetRepository(TestContainerSDKImage))
            //         .Any();
            //     if (!imageExists)
            //     {
            //         var imageBuilder = NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "BuilderImage";
            //         DockerTasks.DockerBuild(c => c
            //                 .SetPath(imageBuilder)
            //                 .SetProcessWorkingDirectory(imageBuilder)
            //                 .SetTag(TestContainerSDKImage))
            //             .EnsureNoErrors();
            //     }
            // }
        }

        public static string NMicaVersion => "1.0.0-test"; 
 
        public void Dispose()
        {
            DockerTasks.DockerImagePrune(_ => DockerImagePruneSettingsExtensions.EnableForce(_));
        }
    }
}