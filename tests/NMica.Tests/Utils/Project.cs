using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    public class Project
    {
        private string _slnRelativeDir;

        [XmlElement("Import")]
        public List<Import> Imports { get; set; } = new();

        [XmlIgnore]
        public string Name { get; set; } = "app1";

        [XmlIgnore]
        public string SlnRelativeDir
        {
            get => _slnRelativeDir ?? Name;
            set => _slnRelativeDir = value;
        }

        [XmlIgnore]
        public NugetConfiguration NugetConfig { get; set; }

        [XmlAttribute]
        public string Sdk { get; set; }

        public PropertyGroup PropertyGroup { get; set; } = new();

        [XmlArray]
        [XmlArrayItem(typeof(PackageReference))]
        [XmlArrayItem(typeof(PackageDownload))]
        [XmlArrayItem(typeof(ProjectReference))]
        public List<object> ItemGroup { get; set; } = new();

        public Project AddProjectReference(Project project)
        {
            var up = string.Join(Path.DirectorySeparatorChar.ToString(),
                System.Linq.Enumerable.Repeat("..", SlnRelativeDir.Split('/', '\\').Length));
            var reference = Path.Combine(up, project.SlnRelativeDir, project.Name + ".csproj");
            ItemGroup.Add(new ProjectReference(reference));
            return this;
        }

        public Project AddPackageReference(string name, string version)
        {
            ItemGroup.Add(new PackageReference { Include = name, Version = version });
            return this;
        }

        public Project AddPackageDownload(string name, string version)
        {
            ItemGroup.Add(new PackageDownload { Include = name, Version = version });
            return this;
        }

        public string Generate(string dir)
        {
            Directory.CreateDirectory(dir);
            var fileName = Path.Combine(dir, Name + ".csproj");
            NugetConfig?.Generate(dir);
            File.WriteAllText(fileName, this.ToXml());
            return fileName;
        }

        public string GenerateProgram(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "program.cs"), TestUtils.AssertProgram);
            return Generate(dir);
        }
    }
}
