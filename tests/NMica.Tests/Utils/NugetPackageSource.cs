using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    public class NugetPackageSource
    {
        [XmlAttribute("key")]
        public string Key { get; set; }
        [XmlAttribute("value")]
        public string Value { get; set; }
    }
}