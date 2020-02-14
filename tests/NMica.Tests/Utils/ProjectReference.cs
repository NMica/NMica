using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    public class ProjectReference
    {
        public ProjectReference(string projectPath)
        {
            Include = projectPath;
        }

        public ProjectReference()
        {
        }

        [XmlAttribute]
        public string Include { get; set; }
    }
}