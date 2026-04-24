using System;
using System.Collections.Generic;
using System.IO;
using NMica.Tests.Utils;
using TUnit.Core;

namespace NMica.Tests
{
    public abstract class BaseTests
    {
        private const string TestNupkgName = "NMica.1.0.0-test.nupkg";

        [ClassDataSource<TestsSetup>(Shared = SharedType.PerAssembly)]
        public required TestsSetup Setup { get; init; }

        protected string TestDir { get; private set; } = null!;
        protected string TestName { get; private set; } = null!;
        protected string TagName => TestName.Replace(".", "_").ToLower();
        protected string ContainerAppDir => DockerHelper.ContainerMount;

        /// <summary>Stdout writer for the currently running test — safe for concurrent writes.</summary>
        protected TextWriter Output => TestContext.Current!.Output.StandardOutput;

        [Before(HookType.Test)]
        public void SetupTestDir()
        {
            TestName = Guid.NewGuid().ToString("N");
            TestDir = Path.Combine(Path.GetTempPath(), "nmica-tests", TestName);
            Directory.CreateDirectory(TestDir);
            Output.WriteLine($"Test project: {TestDir}");

            // Stage local NMica nupkg so test solutions can restore it from nuget.config -> ./artifacts
            var artifactsDir = Path.Combine(TestDir, "artifacts");
            Directory.CreateDirectory(artifactsDir);
            File.Copy(Path.Combine(TestPaths.ArtifactsDir, TestNupkgName),
                      Path.Combine(artifactsDir, TestNupkgName), overwrite: true);
        }

        [After(HookType.Test)]
        public void CleanupTestDir()
        {
            try { Directory.Delete(TestDir, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// Standard solution factory. Default assumes nuget-package consumption of NMica; pass
        /// <c>directRef: true</c> to import NMica.props/targets from the built output directly
        /// instead of going through a nuget package.
        /// </summary>
        protected static SolutionConfiguration MakeSolution(string description, string sdk, string targetFramework,
                                                            bool directRef = false, string outputType = "exe")
        {
            var isMultiFramework = targetFramework.Split(';').Length > 1;
            var targetFrameworks = string.Empty;
            if (isMultiFramework)
            {
                targetFrameworks = targetFramework;
                targetFramework = string.Empty;
            }

            var itemGroup = new List<object>();
            var imports = new List<Import>();
            var propertyGroup = new PropertyGroup
            {
                OutputType = outputType,
                TargetFramework = targetFramework,
                TargetFrameworks = targetFrameworks
            };

            if (directRef)
            {
                imports.Add(Import.NmicaProps);
                imports.Add(Import.NmicaTargets);
                propertyGroup.NMicaToolsPath = TestPaths.NMicaTaskDll;
            }
            else
            {
                itemGroup.Add(PackageReference.NMica);
            }

            return new SolutionConfiguration
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
            };
        }
    }
}
