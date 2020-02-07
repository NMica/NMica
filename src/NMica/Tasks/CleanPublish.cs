using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using NMica.Tasks.Base;

namespace NMica.Tasks
{
    public class CleanPublishDir  : ContextIsolatedTask
    {
        public string PublishDir { get; set; }

        protected override bool ExecuteIsolated()
        {
            Log.LogMessage(MessageImportance.High, "Cleaning publish folder");
            if (Directory.Exists(PublishDir) && Directory.EnumerateFileSystemEntries(PublishDir).Any())
            {
                Directory.Delete(PublishDir, true);
                Directory.CreateDirectory(PublishDir);
            }

            return true;
        }
    }
}