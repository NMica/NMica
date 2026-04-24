using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace NMica.Tasks.Base
{
    /// <summary>
    /// Runs the task body inside an isolated <see cref="AssemblyLoadContext"/> so private
    /// dependencies (e.g. Newtonsoft.Json) don't clash with assemblies MSBuild already loaded.
    /// </summary>
    public abstract class ContextAwareTask : Microsoft.Build.Utilities.Task
    {
        public override bool Execute()
        {
            string taskAssemblyPath = new Uri(GetType().GetTypeInfo().Assembly.CodeBase).LocalPath;
            var loadContext = new TaskLoadContext(Path.GetDirectoryName(taskAssemblyPath));

            Assembly inContextAssembly = loadContext.LoadFromAssemblyPath(taskAssemblyPath);
            Type innerTaskType = inContextAssembly.GetType(GetType().FullName);
            object innerTask = Activator.CreateInstance(innerTaskType);

            var outerProperties = GetType().GetRuntimeProperties().ToDictionary(i => i.Name);
            var innerProperties = innerTaskType.GetRuntimeProperties().ToDictionary(i => i.Name);
            var propertiesMap = (from outerProperty in outerProperties.Values
                                 where outerProperty.SetMethod is not null && outerProperty.GetMethod is not null
                                 let innerProperty = innerProperties[outerProperty.Name]
                                 select new { outerProperty, innerProperty }).ToArray();
            var outputPropertiesMap = propertiesMap
                .Where(p => p.outerProperty.GetCustomAttribute<OutputAttribute>() is not null)
                .ToArray();

            foreach (var pair in propertiesMap)
            {
                pair.innerProperty.SetValue(innerTask, pair.outerProperty.GetValue(this));
            }

            var executeInner = innerTaskType.GetMethod(nameof(ExecuteInner), BindingFlags.Instance | BindingFlags.NonPublic);
            bool result = (bool)executeInner.Invoke(innerTask, Array.Empty<object>());

            foreach (var pair in outputPropertiesMap)
            {
                pair.outerProperty.SetValue(this, pair.innerProperty.GetValue(innerTask));
            }

            return result;
        }

        protected abstract bool ExecuteInner();

        private sealed class TaskLoadContext : AssemblyLoadContext
        {
            private readonly string _taskDirectory;

            public TaskLoadContext(string taskDirectory)
            {
                _taskDirectory = taskDirectory;
            }

            protected override Assembly Load(AssemblyName assemblyName)
            {
                // MSBuild and System.* form our exchange surface — let the default context supply them
                if (assemblyName.Name.StartsWith("Microsoft.Build", StringComparison.OrdinalIgnoreCase) ||
                    assemblyName.Name.StartsWith("System.", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                string candidate = Path.Combine(_taskDirectory, assemblyName.Name + ".dll");
                return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
            }
        }
    }
}
