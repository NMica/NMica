namespace NMica.Tasks.Base
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.Loader;
    using Microsoft.Build.Framework;
    using Microsoft.Build.Utilities;

    partial class ContextIsolatedTask : Task
    {
        /// <summary>
        /// The context the inner task is loaded within.
        /// </summary>
        private AssemblyLoadContext ctxt;

        /// <inheritdoc />
        public sealed override bool Execute()
        {
            try
            {
                string taskAssemblyPath = new Uri(this.GetType().GetTypeInfo().Assembly.CodeBase).LocalPath;
                this.ctxt = new CustomAssemblyLoader(this);
                Assembly inContextAssembly = this.ctxt.LoadFromAssemblyPath(taskAssemblyPath);
                Type innerTaskType = inContextAssembly.GetType(this.GetType().FullName);

                object innerTask = Activator.CreateInstance(innerTaskType);
                return this.ExecuteInnerTask(innerTask);
            }
            catch (OperationCanceledException)
            {
                this.Log.LogMessage(MessageImportance.High, "Canceled.");
                return false;
            }
        }

        /// <summary>
        /// Loads the assembly at the specified path within the isolated context.
        /// </summary>
        /// <param name="assemblyPath">The path to the assembly to be loaded.</param>
        /// <returns>The loaded assembly.</returns>
        protected Assembly LoadAssemblyByPath(string assemblyPath)
        {
            if (this.ctxt == null)
            {
                throw new InvalidOperationException("AssemblyLoadContext must be set first.");
            }

            return this.ctxt.LoadFromAssemblyPath(assemblyPath);
        }

        private bool ExecuteInnerTask(object innerTask)
        {
            Type innerTaskType = innerTask.GetType();
            Type innerTaskBaseType = innerTaskType;
            while (innerTaskBaseType.FullName != typeof(ContextIsolatedTask).FullName)
            {
                innerTaskBaseType = innerTaskBaseType.GetTypeInfo().BaseType;
            }

            var outerProperties = this.GetType().GetRuntimeProperties().ToDictionary(i => i.Name);
            var innerProperties = innerTaskType.GetRuntimeProperties().ToDictionary(i => i.Name);
            var propertiesDiscovery = from outerProperty in outerProperties.Values
                                      where outerProperty.SetMethod != null && outerProperty.GetMethod != null
                                      let innerProperty = innerProperties[outerProperty.Name]
                                      select new { outerProperty, innerProperty };
            var propertiesMap = propertiesDiscovery.ToArray();
            var outputPropertiesMap = propertiesMap.Where(pair => pair.outerProperty.GetCustomAttribute<OutputAttribute>() != null).ToArray();

            foreach (var propertyPair in propertiesMap)
            {
                object outerPropertyValue = propertyPair.outerProperty.GetValue(this);
                propertyPair.innerProperty.SetValue(innerTask, outerPropertyValue);
            }

            // Forward any cancellation requests
            MethodInfo innerCancelMethod = innerTaskType.GetMethod(nameof(Cancel));
            using (this.CancellationToken.Register(() => innerCancelMethod.Invoke(innerTask, new object[0])))
            {
                this.CancellationToken.ThrowIfCancellationRequested();

                // Execute the inner task.
                var executeInnerMethod = innerTaskType.GetMethod(nameof(ExecuteIsolated), BindingFlags.Instance | BindingFlags.NonPublic);
                bool result = (bool)executeInnerMethod.Invoke(innerTask, new object[0]);

                // Retrieve any output properties.
                foreach (var propertyPair in outputPropertiesMap)
                {
                    propertyPair.outerProperty.SetValue(this, propertyPair.innerProperty.GetValue(innerTask));
                }

                return result;
            }
        }

        private class CustomAssemblyLoader : AssemblyLoadContext
        {
            private readonly ContextIsolatedTask loaderTask;

            internal CustomAssemblyLoader(ContextIsolatedTask loaderTask)
            {
                this.loaderTask = loaderTask;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                return this.loaderTask.LoadAssemblyByName(assemblyName)
                    ?? Default.LoadFromAssemblyName(assemblyName);
            }

            protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
            {
                string unmanagedDllPath = Directory.EnumerateFiles(
                    this.loaderTask.UnmanagedDllDirectory,
                    $"{unmanagedDllName}.*").Concat(
                        Directory.EnumerateFiles(
                            this.loaderTask.UnmanagedDllDirectory,
                            $"lib{unmanagedDllName}.*"))
                    .FirstOrDefault();
                if (unmanagedDllPath != null)
                {
                    return this.LoadUnmanagedDllFromPath(unmanagedDllPath);
                }

                return base.LoadUnmanagedDll(unmanagedDllName);
            }
        }
    }
}
