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
    public abstract class BaseTests : IDisposable, IClassFixture<TestsSetup>
    {
        protected readonly ITestOutputHelper _output;
        protected readonly TestsSetup _setup;
        protected readonly AbsolutePath _testDir;
        const string TestNupkgName = "NMica.1.0.0-test.nupkg";
        protected MountedPath ContainerAppDir { get; }
        protected readonly string _testName;
        protected string TagName => _testName.Replace(".", "_").ToLower();
        // static readonly AbsolutePath ContainerAppRoot = (AbsolutePath) @"c:\app";


        public BaseTests(ITestOutputHelper output, TestsSetup setup)
        {
            _output = output;
            _setup = setup;
            _testName = Guid.NewGuid().ToString("N");
            _testDir = (AbsolutePath) Directory.CreateDirectory(_testName).FullName;
            _output.WriteLine($"Test project: {_testDir}");
            ContainerAppDir = _testDir.ToMountedPath((AbsolutePath) @"c:\app");
            DeleteDirectory(_testDir);
            CopyFile(NukeBuild.RootDirectory / "artifacts" / TestNupkgName, _testDir / "artifacts" / TestNupkgName);
            // CopyDirectoryRecursively(NukeBuild.RootDirectory / "artifacts", _testDir / "artifacts");

        }
        
        public void Dispose()
        {
            // Directory.Delete(_testDir, true);
        }
        
        protected void ExecuteProcess(Action action)
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

        protected static object[] MakeSolution(string description, string sdk, string targetFramework, bool directRef = true, string outputType = "exe")
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
        protected string Batch(params string[] commands)
        {
            return $"powershell '&'('{string.Join(" ; ", commands)}')";
        }
        
        // IReadOnlyCollection<Output> ExecuteInDocker<T>(string image, string tool, Configure<T> configurator, MountedPath volume) where T : ToolSettings
        // {
        //     var command = $"{tool} {configurator(Activator.CreateInstance<T>()).GetArguments().RenderForExecution()}";
        //     DockerRun(_ => _
        //         .EnableRm()
        //         .SetVolume($"{volume.HostPath.ToDockerfilePath()}:{volume.ToDockerfilePath()}")
        //         .SetImage(TestsSetup.TestContainerSDKImage)
        //         .SetCommand(command));
        // }
    }
}