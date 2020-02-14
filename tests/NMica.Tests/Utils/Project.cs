using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using Nuke.Common.IO;

namespace NMica.Tests.Utils
{
    public class Project
    {
        [XmlIgnore]
        public string Name { get; set; } = "app1"; 
        [XmlIgnore]
        public NugetConfiguration NugetConfig { get; set; }
        [XmlAttribute]
        public string Sdk {get;set;}
        public PropertyGroup PropertyGroup {get;set;} = new PropertyGroup();
        [XmlArray]
        [XmlArrayItem(typeof(PackageReference))]
        [XmlArrayItem(typeof(PackageDownload))]
        [XmlArrayItem(typeof(ProjectReference))]
        public List<object> ItemGroup {get;set;} = new List<object>();
        public Project AddPackageReference(string name, string version)
        {
            ItemGroup.Add(new PackageReference{Include = name, Version = version});
            return this;
        }
        public Project AddPackageDownload(string name, string version)
        {
            ItemGroup.Add(new PackageDownload{ Include = name, Version = version });
            return this;
        }
        
        public AbsolutePath Generate(AbsolutePath dir)
        {
            FileSystemTasks.EnsureExistingDirectory(dir);
            var fileName = dir / $"{Name}.csproj";
            NugetConfig?.Generate(dir);
            File.WriteAllText(fileName, this.ToXml());
            return fileName;
        }

        public AbsolutePath GenerateProgram(AbsolutePath dir)
        {
            FileSystemTasks.EnsureExistingDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "program.cs"), TestUtils.AssertProgram);
            return Generate(dir);
        }
    }
}