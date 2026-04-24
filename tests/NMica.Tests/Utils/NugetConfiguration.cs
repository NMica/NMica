using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    [XmlRoot("configuration")]
    public class NugetConfiguration
    {
        [XmlArray("packageSources")]
        [XmlArrayItem("add")]
        public List<NugetPackageSource> PackageSources { get; set; } = new();

        public NugetConfiguration Add(string name, string url)
        {
            PackageSources.Add(new NugetPackageSource { Key = name, Value = url });
            return this;
        }

        public static NugetConfiguration FromDictionary(Dictionary<string, string> values)
        {
            return new NugetConfiguration
            {
                PackageSources = values.Select(x => new NugetPackageSource { Key = x.Key, Value = x.Value }).ToList()
            };
        }

        public void Generate(string dir)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "nuget.config"), this.ToXml());
        }
    }
}
