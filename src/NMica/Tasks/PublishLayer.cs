using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Newtonsoft.Json.Linq;
using NMica.Tasks.Base; // using System.Text.Json;

namespace NMica.Tasks
{
    public class PublishLayer : ContextIsolatedTask
    {
        public string TargetFrameworkMoniker { get; set; }
        public string TargetFramework { get; set; }
        public string BaseIntermediateOutputPath { get; set; }
        public string PublishDir { get; set; }

        public string DockerLayer
        {
            get => _layer.ToString(); 
            set => _layer = (Layer)Enum.Parse(typeof(Layer), value, true);
        }

        private Layer _layer;
        protected override bool ExecuteIsolated()
        {
            // return true;
            var assetsFile = Path.Combine(BaseIntermediateOutputPath, "project.assets.json");
            
            var doc = JObject.Parse(File.ReadAllText(assetsFile));
            
            // var root = doc.RootElement;
            var targets = doc["targets"];
            var framework = targets[TargetFrameworkMoniker] ?? targets[TargetFramework];

            var originalFiles = Directory.EnumerateFiles(Path.Combine(Directory.GetCurrentDirectory(), PublishDir), "*", SearchOption.AllDirectories)
                .ToList();
            var publishPath = Path.GetFullPath(PublishDir);
            if (_layer == Layer.All)
            {
                foreach (var layer in KnownLayers.AllLayers)
                {
                    PublishLayer(layer);
                }
            }
            else
            {
                PublishLayer(_layer);
                foreach (var file in originalFiles.Where(File.Exists))
                {
                    File.Delete(file);
                }
            }
            LogToConsole(Directory.EnumerateFiles(publishPath, string.Empty, SearchOption.AllDirectories));
            
            void PublishLayer(Layer layer)
            {
                var layerFiles = GetFilesForLayer(layer);
            
                var layerDir = Path.Combine(publishPath, layer.ToString().ToLower());
                Directory.CreateDirectory(layerDir);
                    
                foreach (var layerFile in layerFiles)
                {
                    Move(layerFile, publishPath, layerDir);
                }
            }
            
            IEnumerable<string> GetFilesForLayer(Layer layer)
            {
                if (layer != Layer.App)
                {
                    return GetReferences(layer)
                        .Select(x => Path.Combine(publishPath, x));
                }
                return originalFiles
                    .Except(KnownLayers.DependencyLayers
                        .SelectMany(GetReferences)
                        .Select(x => Path.Combine(PublishDir, x)))
                        .Select(Path.GetFullPath).ToList();
            }
            void LogToConsole<T>(IEnumerable<T> items)
            {
                foreach (var item in items)
                {
                    Log.LogMessage(MessageImportance.High, item.ToString());
                }
            }
            List<string> GetReferences(Layer layer)
            {
            
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
                
                var runtimeTargetDependecy = dependency
                    .SelectMany(x => Optional(x, "runtimeTargets"))
                    .SelectMany(x => x.AsJEnumerable().Cast<JProperty>())
                    .Select(x => x.Name.Replace('/', Path.DirectorySeparatorChar));
                var result = runtimeDependency
                    .Union(runtimeTargetDependecy).ToList();
                return result;
            
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
            IEnumerable<JObject> Optional(JProperty x, string property)
            {
                if (x.Value is JObject)
                {
                    var val = (JObject)x.Value;
                    if(val.TryGetValue(property, StringComparison.InvariantCulture, out var runtime))
                        yield return (JObject)runtime;
                }
            }
            return true;
        }
        
    }
}