using Microsoft.Build.Utilities;
using NMica.Tasks.Base;

namespace NMica.Tasks.Base
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Threading;
    // using Microsoft.Build.Framework;
    // using Microsoft.Build.Utilities;

    partial class ContextIsolatedTask : AppDomainIsolatedTask // We need MarshalByRefObject -- we don't care for MSBuild's AppDomain though.
    {
        /// <summary>
        /// A guard against stack overflows in the assembly resolver.
        /// </summary>
        private readonly ThreadLocal<bool> alreadyInAssemblyResolve = new ThreadLocal<bool>();

        /// <summary>
        /// Initializes a new instance of the <see cref="Tasks.Base.ContextIsolatedTask"/> class.
        /// </summary>
        public ContextIsolatedTask()
        {
        }

        /// <inheritdoc />
        public sealed override bool Execute()
        {
            try
            {
                // We have to hook our own AppDomain so that the TransparentProxy works properly.
                AppDomain.CurrentDomain.AssemblyResolve += this.CurrentDomain_AssemblyResolve;

                // On .NET Framework (on Windows), we find native binaries by adding them to our PATH.
                if (this.UnmanagedDllDirectory != null)
                {
                    string pathEnvVar = Environment.GetEnvironmentVariable("PATH");
                    string[] searchPaths = pathEnvVar.Split(Path.PathSeparator);
                    if (!searchPaths.Contains(this.UnmanagedDllDirectory, StringComparer.OrdinalIgnoreCase))
                    {
                        pathEnvVar += Path.PathSeparator + this.UnmanagedDllDirectory;
                        Environment.SetEnvironmentVariable("PATH", pathEnvVar);
                    }
                }

                // Run under our own AppDomain so we can apply the .config file of the MSBuild Task we're hosting.
                // This gives the owner the control over binding redirects to be applied.
                var appDomainSetup = new AppDomainSetup();
                string pathToTaskAssembly = this.GetType().Assembly.Location;
                appDomainSetup.ApplicationBase = Path.GetDirectoryName(pathToTaskAssembly);
                appDomainSetup.ConfigurationFile = pathToTaskAssembly + ".config";
                var appDomain = AppDomain.CreateDomain("ContextIsolatedTask: " + this.GetType().Name, AppDomain.CurrentDomain.Evidence, appDomainSetup);
                string taskAssemblyFullName = this.GetType().Assembly.GetName().FullName;
                string taskFullName = this.GetType().FullName;
                var isolatedTask = (Tasks.Base.ContextIsolatedTask)appDomain.CreateInstanceAndUnwrap(taskAssemblyFullName, taskFullName);

                return this.ExecuteInnerTask(isolatedTask, this.GetType());
            }
            catch (OperationCanceledException)
            {
                // this.Log.LogMessage(MessageImportance.High, "Canceled.");
                return false;
            }
            finally
            {
                AppDomain.CurrentDomain.AssemblyResolve -= this.CurrentDomain_AssemblyResolve;
            }
        }

        /// <summary>
        /// Loads the assembly at the specified path within the isolated context.
        /// </summary>
        /// <param name="assemblyPath">The path to the assembly to be loaded.</param>
        /// <returns>The loaded assembly.</returns>
        protected Assembly LoadAssemblyByPath(string assemblyPath)
        {
            return Assembly.LoadFile(assemblyPath);
        }

        private bool ExecuteInnerTask(Tasks.Base.ContextIsolatedTask innerTask, Type innerTaskType)
        {
            if (innerTask == null)
            {
                throw new ArgumentNullException(nameof(innerTask));
            }

            try
            {
                Type innerTaskBaseType = innerTaskType;
                while (innerTaskBaseType.FullName != typeof(Tasks.Base.ContextIsolatedTask).FullName)
                {
                    innerTaskBaseType = innerTaskBaseType.GetTypeInfo().BaseType;
                    if (innerTaskBaseType == null)
                    {
                        throw new ArgumentException($"Unable to find {nameof(Tasks.Base.ContextIsolatedTask)} in type hierarchy.");
                    }
                }

                var properties = this.GetType().GetRuntimeProperties()
                    .Where(property => property.GetMethod != null && property.SetMethod != null);

                foreach (var property in properties)
                {
                    object value = property.GetValue(this);
                    property.SetValue(innerTask, value);
                }

                // Forward any cancellation requests
                using (this.CancellationToken.Register(innerTask.Cancel))
                {
                    this.CancellationToken.ThrowIfCancellationRequested();

                    // Execute the inner task.
                    bool result = innerTask.ExecuteIsolated();

                    // Retrieve any output properties.
                    foreach (var property in properties)
                    {
                        object value = property.GetValue(innerTask);
                        property.SetValue(this, value);
                    }

                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                // this.Log.LogMessage(MessageImportance.High, "Canceled.");
                return false;
            }
        }

        private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
        {
            if (alreadyInAssemblyResolve.Value)
            {
                // Guard against stack overflow exceptions.
                return null;
            }

            alreadyInAssemblyResolve.Value = true;
            try
            {
                return Assembly.Load(args.Name);
            }
            catch
            {
                return null;
            }
            finally
            {
                alreadyInAssemblyResolve.Value = false;
            }
        }
    }
}
