using System;
using System.IO;
using System.Linq;

namespace NMica.Tests.Utils
{
    public static class TestPaths
    {
        public static string RepoRoot { get; } = FindRepoRoot();
        public static string ArtifactsDir => Path.Combine(RepoRoot, "artifacts");
        public static string NMicaPropsPath => Path.Combine(RepoRoot, "src", "NMica", "build", "NMica.props");
        public static string NMicaTargetsPath => Path.Combine(RepoRoot, "src", "NMica", "build", "NMica.targets");
        public static string NMicaTaskDll => Path.Combine(RepoRoot, "src", "NMica", "bin", "Debug", "net10.0", "NMica.dll");

        /// <summary>
        /// Full path to the NMica nupkg produced by the most recent build. Version is computed
        /// by Nerdbank.GitVersioning from version.json + git history, so it's not predictable
        /// at compile time — we resolve it by enumeration.
        /// </summary>
        public static string NMicaNupkg
        {
            get
            {
                if (!Directory.Exists(ArtifactsDir))
                    throw new DirectoryNotFoundException($"Artifacts directory {ArtifactsDir} does not exist. Build NMica.csproj first.");

                var packages = Directory.GetFiles(ArtifactsDir, "NMica.*.nupkg")
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToArray();

                return packages.Length > 0
                    ? packages[0]
                    : throw new FileNotFoundException($"No NMica package found in {ArtifactsDir}.");
            }
        }

        /// <summary>Version of the NMica package, extracted from the nupkg filename.</summary>
        public static string NMicaVersion
        {
            get
            {
                // "NMica.10.0.0-alpha-gabcdef.nupkg" → "10.0.0-alpha-gabcdef"
                var name = Path.GetFileNameWithoutExtension(NMicaNupkg);
                const string prefix = "NMica.";
                return name.StartsWith(prefix) ? name[prefix.Length..] : name;
            }
        }

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
