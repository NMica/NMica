using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Build.Framework;

namespace NMica.Tasks;

/// <summary>
/// Empties <see cref="PublishDir"/> before a partial <see cref="PublishLayer"/> run. Retries on
/// transient <see cref="IOException"/> / <see cref="UnauthorizedAccessException"/> because
/// Windows / antivirus / indexers sometimes hold files briefly after a prior build.
/// </summary>
public class CleanPublishDir : Microsoft.Build.Utilities.Task
{
    private const int MaxRetry = 10;

    [Required]
    public string PublishDir { get; set; } = "";

    public override bool Execute()
    {
        Log.LogMessage(MessageImportance.High, "Cleaning publish folder");
        if (Directory.Exists(PublishDir) && Directory.EnumerateFileSystemEntries(PublishDir).Any())
        {
            DeletePublishDir();
            Directory.CreateDirectory(PublishDir);
        }
        return true;
    }

    private void DeletePublishDir()
    {
        for (var retry = 1; retry <= MaxRetry; retry++)
        {
            try
            {
                Directory.Delete(PublishDir, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Debug.WriteLine("Prevented from deletion of {0}! Attempt #{1}.", PublishDir, retry);
                Thread.Sleep(50);
            }
        }
    }
}
