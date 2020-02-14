using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.XPath;
using Microsoft.Build.Framework;
using NMica.Tasks.Base;

namespace NMica.Tasks
{
    public class GenerateDockerfile  : ContextIsolatedTask
    {
        public bool UsingMicrosoftNETSdkWeb { get; set; }
        public string TargetFrameworkVersion { get; set; }
        public string TargetFrameworkIdentifier { get; set; }
        public string AssemblyName { get; set; }
        public string MSBuildProjectName { get; set; }
        public string MSBuildProjectFile { get; set; }
        public string SolutionPath { get; set; }
        public bool IsExecutable { get; set; }

        protected override bool ExecuteIsolated()
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
            var solution = File.ReadAllText(SolutionPath);
            var solutionFilename = Path.GetFileName(SolutionPath);
            var solutionFullDir = Path.GetDirectoryName(SolutionPath);
            var projects = Regex.Matches(solution, @"^Project\(.+\)\s*=\s*\"".+?\"",\s*""(?<project>[^\""]+)", RegexOptions.Multiline).Cast<Match>()
                .Select(x => x.Groups["project"].Value)
                .ToList();
            
            var currentProject = projects.First(x => Path.GetFileName(x) == MSBuildProjectFile);
            
            string runImageName = UsingMicrosoftNETSdkWeb ? "mcr.microsoft.com/dotnet/core/aspnet" : "mcr.microsoft.com/dotnet/core/runtime";

            var sb = new StringBuilder();
            sb.AppendLine($"FROM mcr.microsoft.com/dotnet/core/sdk:{imageVersion} AS build");
            sb.AppendLine("WORKDIR src");
            var projectNugetConfigs = projects.Union(new[] {SolutionPath})
                .SelectMany(projFile =>
            {
                var projFullFolder = Path.Combine(solutionFullDir, Path.GetDirectoryName(projFile));

                // Log.LogMessage( MessageImportance.High,  $"ProjFolder: {projFullFolder}");
                var nugetConfig = Path.Combine(projFullFolder, "nuget.config");
                // Log.LogMessage( MessageImportance.High,  $"NugetConfig: {nugetConfig}");
                // Log.LogMessage( MessageImportance.High,  $"NugetConfigPath: {Path.GetDirectoryName(nugetConfig)}");
                if (!File.Exists(nugetConfig))
                    return Enumerable.Empty<Tuple<string,string>>();
                using (var nugetConfigStream = File.OpenRead(nugetConfig))
                {
                    var doc = new XPathDocument(nugetConfigStream);
                    var nav = doc.CreateNavigator();
                    return nav.Select("/configuration/packageSources/add")
                        .Cast<XPathNavigator>()
                        .Select(x => x.GetAttribute("value", string.Empty))
                        .Where(x => !Path.IsPathRooted(x) && !x.StartsWith(".."))
                        .Select(x => Path.Combine(Path.GetDirectoryName(nugetConfig), x))
                        .Select(x => MakeRelativePath(solutionFullDir, x))
                        .Select(x => Tuple.Create(MakeRelativePath(solutionFullDir,nugetConfig), x));
                }
            }).ToArray();
            var nugetConfigs = projectNugetConfigs.Select(x => x.Item1).Distinct();
            sb.AppendLine("# copy nuget.config files at solution and project levels");
            foreach (var nugetConfig in nugetConfigs)
            {
                sb.AppendLine($"COPY {nugetConfig} {nugetConfig}");
            }

            sb.AppendLine("# copy any local nuget sources that are subfolders of the solution");
            foreach (var nugetSource in projectNugetConfigs)
            {
                sb.AppendLine($"COPY {nugetSource.Item2} {nugetSource.Item2}");
            }
            sb.AppendLine($"COPY {solutionFilename} .");
            foreach (var project in projects)
            {
                var osFixedProject = project.Replace('\\', '/');
                sb.AppendLine($"COPY {osFixedProject} {osFixedProject}");
            }
            sb.AppendLine($"RUN dotnet restore {solutionFilename}");
            
            sb.AppendLine($"COPY . .");
            var projectPath = currentProject.Replace('\\', '/');
            sb.AppendLine($"RUN dotnet msbuild /p:RestorePackages=false /t:PublishLayer /p:PublishDir=/layer/ /p:DockerLayer=All {projectPath}");

            sb.AppendLine($"FROM {runImageName}:{imageVersion} AS run");
            sb.AppendLine($"WORKDIR /app");
            foreach (var layer in KnownLayers.AllLayers)
            {
                var layerName = layer.ToString().ToLower();
                sb.AppendLine($"COPY --from=build /layer/{layerName} ./");
            }

            sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"{AssemblyName}.dll\"]");

            var dockerfileName = $"{MSBuildProjectName}.Dockerfile";
            File.WriteAllText(Path.Combine(solutionFullDir, dockerfileName), sb.ToString());
            Log.LogMessage(MessageImportance.High, $"Generated {dockerfileName}");
            
            var dockerIgnoreFile = Path.Combine(solutionFullDir, ".dockerignore");

            if (!File.Exists(dockerIgnoreFile))
            {
                File.WriteAllText(dockerIgnoreFile, "**/bin/\n**/obj/\n**/out/\n**/layer/\n**Dockerfile*\n*/*.md");
                Log.LogMessage(MessageImportance.High, "No existing .dockerignore file found");
                Log.LogMessage(MessageImportance.High, $"Generated {dockerIgnoreFile}");
            }

            return true;

            String MakeRelativePath(String fromPath, String toPath)
            {
                if (String.IsNullOrEmpty(fromPath)) throw new ArgumentNullException(nameof(fromPath));
                if (String.IsNullOrEmpty(toPath)) throw new ArgumentNullException(nameof(toPath));

                if(!fromPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    fromPath += Path.DirectorySeparatorChar;
                if (!fromPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                    toPath += Path.DirectorySeparatorChar;

                var fromUri = new Uri(fromPath);
                Log.LogMessage(MessageImportance.High, toPath);
                var toUri = new Uri(toPath);

                if (fromUri.Scheme != toUri.Scheme) { return toPath; } // path can't be made relative.

                Uri relativeUri = fromUri.MakeRelativeUri(toUri);
                String relativePath = Uri.UnescapeDataString(relativeUri.ToString());

                if (toUri.Scheme.Equals("file", StringComparison.InvariantCultureIgnoreCase))
                {
                    relativePath = relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
                }

                return relativePath;
            }   
        }
        
        
    }
}