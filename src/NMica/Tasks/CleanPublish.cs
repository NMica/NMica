using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;
using MSBuildExtensionTask;
using NMica.Tasks.Base;

namespace NMica.Tasks
{
    public class CleanPublishDir  : ContextAwareTask
    {
        private const int MaxRetry = 10;
        public string PublishDir { get; set; }

        protected override bool ExecuteInner()
        {
            Log.LogMessage(MessageImportance.High, "Cleaning publish folder");
            if (Directory.Exists(PublishDir) && Directory.EnumerateFileSystemEntries(PublishDir).Any())
            {
                DeletePublishDir();
                Directory.CreateDirectory(PublishDir);
            }

            return true;
        }

        private void DeletePublishDir() {
            for (var retry = 1; retry <= MaxRetry; retry++) {
                try {
                    Directory.Delete(PublishDir, true);
                } catch (DirectoryNotFoundException) {
                    return;
                } catch (Exception e) {
                    if (!(e is IOException) && !(e is UnauthorizedAccessException)) throw;
                    System.Diagnostics.Debug.WriteLine("Prevented from deletion of {0}! Attempt #{1}.", PublishDir, retry);

                    // see http://stackoverflow.com/questions/329355/cannot-delete-directory-with-directory-deletepath-true for more magic
                    Thread.Sleep(50);
                    continue;
                }
                return;
            }
        }
    }
}
