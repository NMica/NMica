#if NETCOREAPP

using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Nerdbank.GitVersioning
{
    public class GitLoaderContext : AssemblyLoadContext
    {
        public static readonly GitLoaderContext Instance = new GitLoaderContext();

        public const string RuntimePath = "./runtimes";

        protected override Assembly Load(AssemblyName assemblyName)
        {
            var path = Path.Combine(Path.GetDirectoryName(typeof(GitLoaderContext).Assembly.Location), assemblyName.Name + ".dll");
            return File.Exists(path)
                ? this.LoadFromAssemblyPath(path)
                : Default.LoadFromAssemblyName(assemblyName);
        }
    }
}
#endif