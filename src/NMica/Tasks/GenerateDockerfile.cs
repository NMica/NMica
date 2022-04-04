using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using Microsoft.Build.Framework;
using MSBuildExtensionTask;
using Newtonsoft.Json.Linq;
using NMica.Tasks.Base;
using NMica.Utils;

namespace NMica.Tasks
{
    public class GenerateDockerfile  : ContextAwareTask
    {
        public bool UsingMicrosoftNETSdkWeb { get; set; }
        public string TargetFrameworkVersion { get; set; }
        public string TargetFrameworkIdentifier { get; set; }
        public string AssemblyName { get; set; }
        public string MSBuildProjectFullPath { get; set; }
        public string BaseIntermediateOutputPath { get; set; }
        public string SolutionPath { get; set; }
        public bool IsExecutable { get; set; }

        protected override bool ExecuteInner()
        {
            // technically all these checks are redundant as they exist in targets file as conditions,
            // but if we get called SOMEHOW accidentally let's not fail the whole build (return true) but give reason why task failed
            if (!IsExecutable)
            {
                Log.LogError("Can only generate Dockerfile for executable projects");
                return true;
            }
            if(string.IsNullOrEmpty(SolutionPath) || SolutionPath == "*Undefined*")
            {
                Log.LogError("Can only generate Dockerfile if building from solution");
                return true;
            }
            
            var imageVersion = TargetFrameworkVersion.Trim('v');

            if (TargetFrameworkIdentifier != ".NETCoreApp")
            {
                Log.LogMessage("Only .NET Core projects are supported");
                return true;
            }
            if (!decimal.TryParse(imageVersion, out var imageVersionNum))
            {
                Log.LogWarning("Unsupported .NET Core version");
                return true;
            }
            
            if (imageVersionNum < 2.1m || imageVersionNum == 2.2m || imageVersionNum == 3.0m)
            {
                Log.LogWarning($"Unsupported version: project is targeting .NET Core {TargetFrameworkVersion} which is end of life.");
                return true;
            }
            var solutionFullPath = (AbsolutePath)SolutionPath;
			var solutionFullDir = solutionFullPath.Parent;
			var currentProjectFullPath = (AbsolutePath)MSBuildProjectFullPath;
			var currentProjectFullDir = currentProjectFullPath.Parent;

			var assetsFile = Path.Combine(BaseIntermediateOutputPath, "project.assets.json");
			var assets = JObject.Parse(File.ReadAllText(assetsFile));
			var projects = assets
				.SelectTokens("$..msbuildProject")
				.OfType<JValue>()
				.Select(x => currentProjectFullDir / (string)x)
				.ToList();
			projects.Add(currentProjectFullPath);
			
			string runImageName = UsingMicrosoftNETSdkWeb ? "mcr.microsoft.com/dotnet/aspnet" : "mcr.microsoft.com/dotnet/runtime";

			var sb = new StringBuilder();
			sb.AppendLine($"FROM mcr.microsoft.com/dotnet/sdk:{imageVersion} AS build");
			sb.AppendLine("WORKDIR src");
			var projectNugetConfigs = projects
				.Union(new[] { solutionFullPath })
				.SelectMany(projFile =>
				{
					var projFullFolder = projFile.Parent;
			
					var nugetConfig = projFullFolder / "nuget.config";
					if (!File.Exists(nugetConfig))
						return Enumerable.Empty<NugetSource>();
					using (var nugetConfigStream = File.OpenRead(nugetConfig))
					{
						var doc = new XPathDocument(nugetConfigStream);
						var nav = doc.CreateNavigator();
						return nav.Select("/configuration/packageSources/add")
							.Cast<XPathNavigator>()
							.Select(x => x.GetAttribute("value", string.Empty))
							.Where(x => !Path.IsPathRooted(x))
							.Select(pkgSource => nugetConfig.Parent / pkgSource) // get absolute path to source
							.Select(x => new NugetSource
							{ 
								NugetFile = nugetConfig,
								SourceFolder = x
							})
							.ToList();
					}
				})
				.ToArray();
			var nugetConfigs = projectNugetConfigs.Select(x => x.NugetFile).Distinct();
			sb.AppendLine("# copy nuget.config files at solution and project levels");
			foreach (var nugetConfig in nugetConfigs.Select(x => solutionFullDir.GetRelativePathTo(x)))
			{
				sb.AppendLine($"COPY [\"{nugetConfig}\", \"{nugetConfig}\"]");
			}

			sb.AppendLine("# copy any local nuget sources that are subfolders of the solution");
			foreach (var nugetSource in projectNugetConfigs.Select(x => solutionFullDir.GetRelativePathTo(x.SourceFolder)))
			{
				sb.AppendLine($"COPY [\"{nugetSource}\", \"{nugetSource}\"]");
			}
			foreach (var project in projects)
			{
				var osFixedProject = solutionFullDir.GetUnixRelativePathTo(project);
				sb.AppendLine($"COPY [\"{osFixedProject}\", \"{osFixedProject}\"]");
			}
			sb.AppendLine($"RUN dotnet restore \"{solutionFullDir.GetRelativePathTo(currentProjectFullPath)}\"");
			sb.AppendLine($"COPY . .");
			//var projectPath = currentProject.Replace('\\', '/');
			sb.AppendLine($"RUN dotnet msbuild /p:RestorePackages=false /t:PublishLayer /p:PublishDir=/layer/ /p:DockerLayer=All \"{solutionFullDir.GetUnixRelativePathTo(currentProjectFullPath)}\"");

			sb.AppendLine($"FROM {runImageName}:{imageVersion} AS run");
			sb.AppendLine($"WORKDIR /app");
			foreach (var layer in KnownLayers.AllLayers)
			{
				var layerName = layer.ToString().ToLower();
				sb.AppendLine($"COPY --from=build /layer/{layerName} ./");
			}

			sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"{AssemblyName}.dll\"]");

            var dockerfileName = Path.Combine(currentProjectFullDir, "Dockerfile");
            File.WriteAllText(dockerfileName, sb.ToString());
            Log.LogMessage(MessageImportance.High, $"Generated {dockerfileName}");
            
            var dockerIgnoreFile = Path.Combine(solutionFullDir, ".dockerignore");

            if (!File.Exists(dockerIgnoreFile))
            {
                File.WriteAllText(dockerIgnoreFile, "**/bin/\n**/obj/\n**/out/\n**/layer/\n**Dockerfile*\n*/*.md");
                Log.LogMessage(MessageImportance.High, "No existing .dockerignore file found");
                Log.LogMessage(MessageImportance.High, $"Generated {dockerIgnoreFile}");
            }

            return true;

            
        }
        struct NugetSource
        {
	        public AbsolutePath NugetFile;
	        public AbsolutePath SourceFolder;
        }
        
    }
}