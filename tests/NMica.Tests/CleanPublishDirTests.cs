using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using NMica.Tasks;
using Xunit;

namespace NMica.Tests
{
    public class CleanPublishDirTests
    {
        [Fact]
        public void FileLocked_DirectoryNotDeleted()
        {
            var publishPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            var subdirectoryPath = Path.Combine(publishPath, "Subdirectory");
            var filePath = Path.Combine(subdirectoryPath, "File.txt");

            CleanPublishDir task = new() { PublishDir = publishPath };
            task.BuildEngine = new MockBuildEngine();
            void Execute()
            {
                task.Execute();
            }
            try
            {
                Directory.CreateDirectory(publishPath);
                Directory.CreateDirectory(subdirectoryPath);
                
                using (FileStream _ = new(filePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    _.WriteByte(1);
                    _.Flush();
                    File.SetAttributes(publishPath, FileAttributes.ReadOnly);
                    Thread t = new(Execute);
                    t.Start();
                    Assert.True(Directory.EnumerateFileSystemEntries(publishPath).Any());
                    Thread.Sleep(100);
                    Assert.True(Directory.EnumerateFileSystemEntries(publishPath).Any());
                }
                File.SetAttributes(publishPath, FileAttributes.Normal);
                Thread.Sleep(100);
                Assert.False(Directory.EnumerateFileSystemEntries(publishPath).Any());
                
            }
            finally
            {
                if (Directory.Exists(publishPath))
                {
                    Directory.Delete(publishPath, true);
                }
            }
        }
        
        
        private class MockBuildEngine : IBuildEngine
        {
            public void LogErrorEvent(BuildErrorEventArgs e)
            {
                
            }

            public void LogWarningEvent(BuildWarningEventArgs e)
            {
                
            }

            public void LogMessageEvent(BuildMessageEventArgs e)
            {
                
            }

            public void LogCustomEvent(CustomBuildEventArgs e)
            {
                
            }

            public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties,
                IDictionary targetOutputs)
            {
                return true;
            }

            public bool ContinueOnError { get; }
            public int LineNumberOfTaskNode { get; }
            public int ColumnNumberOfTaskNode { get; }
            public string ProjectFileOfTaskNode { get; }
        }
    }
}