using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using FluentAssertions;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.Docker;

namespace NMica.Tests.Utils
{
    public static class Extensions
    {
        
        public static DockerRunSettings AddVolume(this DockerRunSettings settings, MountedPath path) => settings.AddVolume($"{path.HostPath.ToDockerfilePath()}:{path.ToDockerfilePath()}");
        public static MountedPath ToMountedPath(this AbsolutePath path, AbsolutePath mountPoint) => new MountedPath(mountPoint, path);
        public static string ToDockerfilePath(this AbsolutePath path) => path.ToString().Replace("\\", "/");
        public static string ToDockerfilePath(this MountedPath path) => path.ToString().Replace("\\", "/");
        public static IReadOnlyCollection<Output> EnsureNoErrors(
            this IReadOnlyCollection<Output> output)
        {
            foreach (Output output1 in (IEnumerable<Output>) output)
                ControlFlow.Assert(output1.Type == OutputType.Std, output1.Text);
            return output;
        }
        public static string ToXml(this object obj)
        {
		
            var stringWriter = new StringWriter();
            var xmlWriter = XmlWriter.Create(stringWriter, new XmlWriterSettings { OmitXmlDeclaration = true, Indent=true });
            var serializer = new XmlSerializer(obj.GetType());
            var ns = new XmlSerializerNamespaces();
            ns.Add("", "");
            serializer.Serialize(xmlWriter, obj, ns);
            return stringWriter.ToString();
        }

        public static string Flatten(this IReadOnlyCollection<Output> outputs)
        {
            return outputs.Aggregate(new StringBuilder(), (sb, x) => sb.AppendLine(x.Text)).ToString();
            
        }
        public static FileAssertion Should(this FileToTest file) => new FileAssertion {File = file};
        public class FileAssertion
        {
            public FileToTest File;
            public void NotExist(string because = "",params object[] reasonArgs)
                => System.IO.File.Exists(File.Path).Should().BeFalse(because,reasonArgs);
            public void Exist(string because = "", params object[] reasonArgs)
                => System.IO.File.Exists(File.Path).Should().BeTrue(because, reasonArgs);
        }
        public static FileToTest FileName(string fileName) => new FileToTest { Path = fileName };
        public class FileToTest {public string Path;}
    }
}