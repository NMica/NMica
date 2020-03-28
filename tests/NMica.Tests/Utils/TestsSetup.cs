using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;
using Nuke.Common.Tools.NerdbankGitVersioning;
using DockerImagePruneSettingsExtensions = Nuke.Common.Tools.Docker.DockerImagePruneSettingsExtensions;
using LogLevel = Nuke.Common.LogLevel;

namespace NMica.Tests.Utils
{
    public class TestsSetup : IDisposable
    {
        public TestsSetup ()
        {
            ToolPathResolver.NuGetPackagesConfigFile = NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "NMica.Tests.csproj";
            //
            var userFolder = (AbsolutePath) Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var nugetCacheFolder = userFolder / ".nuget" / "packages";
            return;
            // var nbgvDll = nugetCacheFolder / "nbgv" / "3.0.50" / "tools" / "netcoreapp2.1" / "any" / "nbgv.dll";
            //
            // Console.WriteLine(ToolPathResolver.NuGetPackagesConfigFile);
            // Console.WriteLine(NukeBuild.RootDirectory);
            // if (File.Exists(nbgvDll)) // this is faster then letting tool resolver figure out where it lives
            // {
            //     Console.WriteLine(nbgvDll);
            //
            //     Environment.SetEnvironmentVariable("NERDBANKGITVERSIONING_EXE",nbgvDll);
            // }
            try
            {
                FileSystemTasks.DeleteDirectory(nugetCacheFolder / "NMica" / NMicaVersion.NuGetPackageVersion);
            }
            catch (UnauthorizedAccessException)
            {
                // something dumb is keeping locks on nuget cache between runs. this is a DIRTY hack to try to release any locks on that folder
                foreach (var process in Process.GetProcesses()
                    .Where(x => x.ProcessName == "dotnet" && x.Id != Process.GetCurrentProcess().Id))
                {
                    process.Kill(true);
                }
                FileSystemTasks.DeleteDirectory(nugetCacheFolder / "NMica" / NMicaVersion.NuGetPackageVersion);
            }
            
        }

        public static NerdbankGitVersioning NMicaVersion => _nMicaVersion.Value; 
            
        private static Lazy<NerdbankGitVersioning> _nMicaVersion = 
            new Lazy<NerdbankGitVersioning>(() =>
            {
                ToolPathResolver.NuGetPackagesConfigFile = NukeBuild.RootDirectory / "tests" / "NMica.Tests" / "NMica.Tests.csproj";
            
                var userFolder = (AbsolutePath) Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var nugetCacheFolder = userFolder / ".nuget" / "packages";
                var nbgvDll = nugetCacheFolder / "nbgv" / "3.0.50" / "tools" / "netcoreapp2.1" / "any" / "nbgv.dll";
            
                Console.WriteLine(ToolPathResolver.NuGetPackagesConfigFile);
                Console.WriteLine(NukeBuild.RootDirectory);
                if (File.Exists(nbgvDll)) // this is faster then letting tool resolver figure out where it lives
                {
                    Console.WriteLine(nbgvDll);

                    Environment.SetEnvironmentVariable("NERDBANKGITVERSIONING_EXE",nbgvDll);
                }
                Console.WriteLine($"NERDBANKGITVERSIONING_EXE: {Environment.GetEnvironmentVariable("NERDBANKGITVERSIONING_EXE")}");
                return (NerdbankGitVersioning) new NerdbankGitVersioningAttribute().GetValue(null, null);
            });
        
        public void Dispose()
        {
            DockerTasks.DockerImagePrune(_ => DockerImagePruneSettingsExtensions.EnableForce(_));
        }
    }
}