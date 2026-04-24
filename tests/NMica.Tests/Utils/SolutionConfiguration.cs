using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace NMica.Tests.Utils
{
    public class SolutionConfiguration
    {
        public string Description { get; set; }
        public string Name { get; set; } = "testapp";
        public string FileName => $"{Name}.sln";
        public List<Project> Projects { get; set; } = new();
        public NugetConfiguration NugetConfig { get; set; } = new();

        public Dictionary<string, Project> Generate(string dir)
        {
            Directory.CreateDirectory(dir);
            NugetConfig.Generate(dir);

            var projects = Projects.ToDictionary(
                x => x.GenerateProgram(x.SlnRelativeDir == null ? Path.Combine(dir, x.Name) : Path.Combine(dir, x.SlnRelativeDir)),
                x => x);

            RunDotnet(dir, "new", "sln", "-n", Name, "--format", "sln");
            foreach (var projectPath in projects.Keys)
            {
                var relative = Path.GetRelativePath(dir, projectPath);
                RunDotnet(dir, "sln", FileName, "add", relative);
            }

            return projects;
        }

        public override string ToString() => Description ?? base.ToString();

        private static void RunDotnet(string workingDir, params string[] args)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p!.WaitForExit();
            if (p.ExitCode != 0)
            {
                throw new System.InvalidOperationException(
                    $"dotnet {string.Join(' ', args)} failed with {p.ExitCode}:\n{p.StandardOutput.ReadToEnd()}\n{p.StandardError.ReadToEnd()}");
            }
        }
    }
}
