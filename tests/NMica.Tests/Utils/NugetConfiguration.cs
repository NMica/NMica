using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;
using Nuke.Common.IO;

namespace NMica.Tests.Utils
{
    [XmlRoot("configuration")]
    public class NugetConfiguration
    {
        [XmlArray("packageSources")]
        [XmlArrayItem("add")]
        public List<NugetPackageSource> PackageSources { get; set; } = new List<NugetPackageSource>();

        public NugetConfiguration Add(string name, string url)
        {
            PackageSources.Add(new NugetPackageSource { Key = name, Value = url});
            return this;
        } 
        public static NugetConfiguration FromDictionary(Dictionary<string, string> values)
        {
            var config = new NugetConfiguration()
            {
                PackageSources = values.Select(x => new NugetPackageSource
                {
                    Key = x.Key,
                    Value = x.Value
                }).ToList()
            };
            return config;
        }

        public void Generate(AbsolutePath dir)
        {
            FileSystemTasks.EnsureExistingDirectory(dir);
            File.WriteAllText(dir / "nuget.config", this.ToXml());
        }
    }
}