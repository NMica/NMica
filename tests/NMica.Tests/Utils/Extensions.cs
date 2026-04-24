using System.IO;
using System.Xml;
using System.Xml.Serialization;

namespace NMica.Tests.Utils
{
    public static class Extensions
    {
        public static string ToXml(this object obj)
        {
            var stringWriter = new StringWriter();
            using var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { OmitXmlDeclaration = true, Indent = true });
            var serializer = new XmlSerializer(obj.GetType());
            var ns = new XmlSerializerNamespaces();
            ns.Add(string.Empty, string.Empty);
            serializer.Serialize(xmlWriter, obj, ns);
            return stringWriter.ToString();
        }
    }
}
