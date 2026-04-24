using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    public class Import
    {
        [XmlAttribute]
        public string Project { get; set; }

        public static Import NmicaProps => new Import { Project = TestPaths.NMicaPropsPath };
        public static Import NmicaTargets => new Import { Project = TestPaths.NMicaTargetsPath };
    }
}
