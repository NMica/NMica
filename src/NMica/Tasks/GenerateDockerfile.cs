using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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
            if (!IsExecutable || string.IsNullOrEmpty(SolutionPath) || SolutionPath == "*Undefined*" || Environment.GetEnvironmentVariable("NODOCKERFILE") != null)
            {
                return true;
            }

            if (TargetFrameworkIdentifier != ".NETCoreApp")
            {
                this.Log.LogError("Only .NET Core projects are supported");
                return false;
            }
            var solution = File.ReadAllText(SolutionPath);
            var solutionFilename = Path.GetFileName(SolutionPath);
            var solutionDir = Path.GetDirectoryName(SolutionPath);
            var projects = Regex.Matches(solution, @"^Project\(.+\)\s*=\s*\"".+?\"",\s*""(?<project>[^\""]+)", RegexOptions.Multiline).Cast<Match>()
                .Select(x => x.Groups["project"].Value)
                .ToList();
            var currentProject = projects.First(x => Path.GetFileName(x) == MSBuildProjectFile);
            
            
            string runImageName = UsingMicrosoftNETSdkWeb ? "mcr.microsoft.com/dotnet/core/aspnet" : "mcr.microsoft.com/dotnet/core/runtime";
            var imageVersion = TargetFrameworkVersion.Trim('v');

            var sb = new StringBuilder();
            sb.AppendLine($"FROM mcr.microsoft.com/dotnet/core/sdk:{imageVersion} AS build");
            sb.AppendLine("WORKDIR src");
            sb.AppendLine($"COPY {solutionFilename} .");
            foreach (var project in projects)
            {
                var osFixedProject = project.Replace('\\', '/');
                sb.AppendLine($"COPY {osFixedProject} {osFixedProject}");
            }
            sb.AppendLine($"RUN dotnet restore");
            
            sb.AppendLine($"COPY . .");
            var projectPath = currentProject.Replace('\\', '/');
            sb.AppendLine($"RUN dotnet msbuild /p:RestorePackages=false /t:PublishLayer /p:PublishDir=/layer/ /p:DockerLayer=All {projectPath}");

            sb.AppendLine($"FROM {runImageName}:{imageVersion}");
            sb.AppendLine($"WORKDIR /app");
            foreach (var layer in KnownLayers.AllLayers)
            {
                var layerName = layer.ToString().ToLower();
                sb.AppendLine($"COPY --from=build /layer/{layerName} ./");
            }

            sb.AppendLine($"ENTRYPOINT [\"dotnet\", \"{AssemblyName}.dll\"]");

            var dockerfileName = $"Dockerfile-{MSBuildProjectName}";
            File.WriteAllText(Path.Combine(solutionDir, dockerfileName), sb.ToString());
            Log.LogMessage(MessageImportance.High, $"Generated {dockerfileName}");
            
            var dockerIgnoreFile = Path.Combine(solutionDir, ".dockerignore");
            if(!File.Exists(dockerIgnoreFile))
                File.WriteAllText(dockerIgnoreFile, "**/bin/\n**/obj/\n**/out/\n**/layer/\n**Dockerfile*\n*/*.md");
            return true;
        }
    }
}