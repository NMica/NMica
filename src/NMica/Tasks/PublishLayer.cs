using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Build.Framework;
using Newtonsoft.Json.Linq;
using NMica.Tasks.Base;
using NMica.Utils; // using System.Text.Json;

namespace NMica.Tasks
{
    public class PublishLayer : ContextIsolatedTask
    {
        public string RuntimeIdentifier { get; set; } = "";
        public string TargetFrameworkMoniker { get; set; } = "";
        public string TargetFramework { get; set; } = "";
        public string BaseIntermediateOutputPath { get; set; } = "";
        public string PublishDir { get; set; } = "";

        private JObject _projectAssetsJson;
        private List<string> _originalFiles;

        protected string PublishPath => Path.GetFullPath(PublishDir);

        public string DockerLayer
        {
            get => _layersToPublish.ToString(); 
            set => _layersToPublish = string.IsNullOrEmpty(value) ? Layer.All : (Layer)Enum.Parse(typeof(Layer), value, true);
        }

        private Layer _layersToPublish = Layer.All;

        protected override bool ExecuteIsolated()
        {
            Initialize();
            // var publishPath = Path.GetFullPath(PublishDir);
            foreach (var layer in _layersToPublish.ToValuesArray())
            {
                PublishLayer(layer);
            }
            foreach (var file in _originalFiles.Where(File.Exists))
            {
                File.Delete(file);
            }
            LogToConsole(Directory.EnumerateFiles(PublishPath, string.Empty, SearchOption.AllDirectories));
            
            void PublishLayer(Layer layer)
            {
                var layerFiles = GetFilesForLayer(layer);
            
                var layerDir = Path.Combine(PublishPath, layer.ToString().ToLower());
                Directory.CreateDirectory(layerDir);
                    
                foreach (var layerFile in layerFiles)
                {
                    Move(layerFile, PublishPath, layerDir);
                }
            }
            
            
            void LogToConsole<T>(IEnumerable<T> items)
            {
                if (Log == null)
                {
                    return;
                }

                foreach (var item in items)
                {
                    Log.LogMessage(MessageImportance.High, item.ToString());
                }
            }
            
            void Move(string file, string basePath, string toFolder)
            {
                var relativePath = file.Remove(0, basePath.Length).Trim('\\');
                var relativeFolder = Path.GetDirectoryName(relativePath);
                Directory.CreateDirectory(Path.Combine(toFolder, relativeFolder));
                var to = Path.Combine(toFolder, relativePath);
                if(File.Exists(to))
                    File.Delete(to);
                if(File.Exists(file))
                    File.Move(file, to);
            }

            return true;
        }

        internal void Initialize()
        {
            var assetsFile = Path.Combine(BaseIntermediateOutputPath, "project.assets.json");
            
            _projectAssetsJson = JObject.Parse(File.ReadAllText(assetsFile));
            
            // stores the output of publish command - these get sorted into individual layers
            _originalFiles = Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), PublishDir), "*", SearchOption.AllDirectories).ToList();
        }

        internal IEnumerable<string> GetFilesForLayer(Layer layer)
        {
            if (layer != Layer.App)
            {
                return GetReferences(layer)
                    .Select(x => Path.Combine(PublishPath, x));
            }
            return _originalFiles
                .Except(KnownLayers.DependencyLayers
                    .SelectMany(GetReferences)
                    .Select(x => Path.Combine(PublishDir, x)))
                .Select(Path.GetFullPath).ToList();
        }
        /// <summary>
        /// Returns absolute list of files to be placed for a give layer
        /// </summary>
        internal List<string> GetReferences(Layer layer)
        {
            var targets = _projectAssetsJson["targets"];
            var framework = targets[TargetFrameworkMoniker] ?? targets[TargetFramework];
            var early = false;
            if (layer == Layer.EarlyPackage)
            {
                layer = Layer.Package;
                early = true;
            }
            
            var dependency = framework.AsJEnumerable().Cast<JProperty>()
                .Where(x => ((JValue)x.Value["type"]).Value.ToString() == layer.ToString().ToLower()
                            && (x.Name.Split('/')[1].Contains("-") == early)).ToList();
            
            var runtimeDependency = dependency
                .SelectMany(x => Optional(x, "runtime"))
                .SelectMany(x => x.AsJEnumerable().Cast<JProperty>())
                .Select(x => x.Name)
                .Select(Path.GetFileName)
                .Where(x => x != "_._").ToList();
                
            var runtimeTargetDependency = dependency
                .SelectMany(x => Optional(x, "runtimeTargets"))
                .SelectMany(x => x.AsJEnumerable().Cast<JProperty>())
                .Select(property =>
                {
                    var localPath = property.Name;
                    if(RuntimeIdentifier != "" && property.Value["assetType"]?.Value<string>() == "native")
                    {
                        // when publishing for specific RID, native assets are outputted into the root of publish folder instead of under /runtimes/<rid>/native/
                        localPath = Regex.Replace(localPath, "^runtimes/.+?/native/", "");
                    }
                    return localPath.Replace('/', Path.DirectorySeparatorChar);
                });
            var result = runtimeDependency
                .Union(runtimeTargetDependency)
                .ToList();
            return result;
            
        }
        static IEnumerable<JObject> Optional(JProperty x, string property)
        {
            if (x.Value is JObject val && val.TryGetValue(property, StringComparison.InvariantCulture, out var runtime))
            {
                yield return (JObject)runtime;
            }
        }
    }
}