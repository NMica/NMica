using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    public class PackageReference
    {
        [XmlAttribute]
        public string Include {get;set;}
        [XmlAttribute]
        public string Version {get;set;}
        public static PackageReference NMica => new PackageReference {Include = "Nmica", Version = TestsSetup.NMicaVersion.NuGetPackageVersion};
    }
}