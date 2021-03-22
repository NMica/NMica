using System;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using DockerImagePruneSettingsExtensions = Nuke.Common.Tools.Docker.DockerImagePruneSettingsExtensions;

namespace NMica.Tests.Utils
{
    public class TestsSetup : IDisposable
    {
        public const string TestContainerSDKImage = "nmica-test-container";
        public TestsSetup ()
        {
            ToolPathResolver.NuGetPackagesConfigFile = NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "NMica.Tests.csproj";
            var imageExists = DockerTasks.DockerImages(c => c
                    .SetFormat("'{{json .}}'")
                    .SetRepository(TestContainerSDKImage))
                .Any();
            if (!imageExists)
            {
                var builderImageSpec = NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "BuilderImage"; 
                DockerTasks.DockerBuild(c => c
                        .SetPath(builderImageSpec)
                        .SetWorkingDirectory(builderImageSpec)
                        .SetTag(TestContainerSDKImage))
                    .EnsureNoErrors();
            }
            
        }

        public static string NMicaVersion => "1.0.0-test"; 
 
        public void Dispose()
        {
            DockerTasks.DockerImagePrune(_ => DockerImagePruneSettingsExtensions.EnableForce(_));
        }
    }
}