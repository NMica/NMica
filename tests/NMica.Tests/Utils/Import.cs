using System.Xml.Serialization;
using Nuke.Common;

namespace NMica.Tests.Utils
{
    public class Import
    {
        [XmlAttribute]
        public string Project { get; set; }

        public static Import NmicaProps => new Import {Project = NukeBuild.RootDirectory / "src" / "NMica" / "nuget" / "build" / "NMica.props"};
        public static Import NmicaTargets => new Import {Project = NukeBuild.RootDirectory / "src" / "NMica" / "nuget" / "build" / "NMica.targets"};
    }
}