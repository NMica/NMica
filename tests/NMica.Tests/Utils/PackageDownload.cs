using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    public class PackageDownload
    {
        [XmlAttribute]
        public string Include { get; set; }
        [XmlAttribute]
        public string Version { get; set; }
    }
}