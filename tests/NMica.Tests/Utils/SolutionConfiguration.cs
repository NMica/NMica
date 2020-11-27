using System.Collections.Generic;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using static Nuke.Common.Tools.DotNet.DotNetTasks;

namespace NMica.Tests.Utils
{
    public class SolutionConfiguration
    {
        public string Description { get; set; }
        public string Name { get; set; } = "testapp";
        public string FileName => $"{Name}.sln";
        public List<Project> Projects { get; set; } = new List<Project>();
        public NugetConfiguration NugetConfig { get; set; } =  new NugetConfiguration();

        public Dictionary<AbsolutePath, Project> Generate(AbsolutePath dir)
        {
            NugetConfig.Generate(dir);
            var projects =  Projects
                .ToDictionary(x => x.GenerateProgram(x.SlnRelativeDir == null ? dir / x.Name : dir / x.SlnRelativeDir), x => x);
            DotNet("new sln -n testapp", dir).EnsureNoErrors();
            foreach (var projectPath in projects.Keys.Select(x => dir.GetRelativePathTo(x)))
            {
                DotNet($"sln testapp.sln add \"{projectPath}\"", dir).EnsureNoErrors();    
            }

            return projects;
        }

        public override string ToString()
        {
            return Description ?? base.ToString();
        }
    }
}