using System;
using System.IO;

namespace NMica.Tests.Utils
{
    public static class TestPaths
    {
        public static string RepoRoot { get; } = FindRepoRoot();
        public static string ArtifactsDir => Path.Combine(RepoRoot, "artifacts");
        public static string NMicaPropsPath => Path.Combine(RepoRoot, "src", "NMica", "build", "NMica.props");
        public static string NMicaTargetsPath => Path.Combine(RepoRoot, "src", "NMica", "build", "NMica.targets");
        public static string NMicaTaskDll => Path.Combine(RepoRoot, "src", "NMica", "bin", "Debug", "net9.0", "NMica.dll");

        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "NMica.sln")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException("Could not locate repository root (NMica.sln) from " + AppContext.BaseDirectory);
        }
    }
}
