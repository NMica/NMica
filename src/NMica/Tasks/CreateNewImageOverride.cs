using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using NMica.Containers;
using NMica.Containers.Sinks;

// Intentional namespace-shadow: NMica.targets declares a <UsingTask> for
// Microsoft.NET.Build.Containers.Tasks.CreateNewImage pointing at NMica.dll. That registration
// (imported after the SDK's) wins via MSBuild's last-UsingTask-wins rule, so _PublishSingleContainer
// routes here.
//
// Property names MUST match the SDK's CreateNewImage call site verbatim (see
// Microsoft.NET.Build.Containers.targets → _PublishSingleContainer). If the SDK adds a property
// in a newer version, MSBuild fails with MSB4064 until we add it here too.
namespace Microsoft.NET.Build.Containers.Tasks
{
    public class CreateNewImage : Microsoft.Build.Utilities.Task
    {
        // --- Inputs the SDK's _PublishSingleContainer passes. ---

        [Required]
        public string PublishDirectory { get; set; } = "";
        public string ContainerizeDirectory { get; set; } = "";
        public string ToolPath { get; set; } = "";
        public string ToolExe { get; set; } = "";
        public string WorkingDirectory { get; set; } = "/app";

        public string BaseRegistry { get; set; } = "";
        public string BaseImageName { get; set; } = "";
        public string BaseImageTag { get; set; } = "";
        public string BaseImageDigest { get; set; } = "";
        public string ImageFormat { get; set; } = "";

        public string Repository { get; set; } = "";
        public ITaskItem[] ImageTags { get; set; } = Array.Empty<ITaskItem>();

        public string OutputRegistry { get; set; } = "";
        public string LocalRegistry { get; set; } = "";
        public string ArchiveOutputPath { get; set; } = "";

        public ITaskItem[] Entrypoint { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] EntrypointArgs { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] AppCommand { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] AppCommandArgs { get; set; } = Array.Empty<ITaskItem>();
        public string AppCommandInstruction { get; set; } = "";
        public ITaskItem[] DefaultArgs { get; set; } = Array.Empty<ITaskItem>();

        public ITaskItem[] Labels { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] ExposedPorts { get; set; } = Array.Empty<ITaskItem>();
        public ITaskItem[] ContainerEnvironmentVariables { get; set; } = Array.Empty<ITaskItem>();
        public string ContainerUser { get; set; } = "";
        public string ContainerRuntimeIdentifier { get; set; } = "";
        public string RuntimeIdentifierGraphPath { get; set; } = "";
        public bool SkipPublishing { get; set; }
        public bool GenerateLabels { get; set; }
        public bool GenerateDigestLabel { get; set; }

        // --- Outputs ---

        [Output] public string GeneratedContainerDigest { get; set; } = "";
        [Output] public string GeneratedContainerManifest { get; set; } = "";
        [Output] public string GeneratedContainerConfiguration { get; set; } = "";
        [Output] public string GeneratedContainerMediaType { get; set; } = "";
        [Output] public ITaskItem[] GeneratedContainerNames { get; set; } = Array.Empty<ITaskItem>();
        [Output] public string GeneratedArchiveOutputPath { get; set; } = "";
        [Output] public ITaskItem[] GeneratedDigestLabel { get; set; } = Array.Empty<ITaskItem>();

        private static readonly string[] LayerOrder = { "package", "earlypackage", "project", "app" };

        public override bool Execute()
        {
            if (SkipPublishing) return true;

            // The companion _NMicaPreparePublishLayers target runs PublishLayer before us, which
            // rearranges $(PublishDir) into package/earlypackage/project/app subdirs.
            var existingLayers = LayerOrder
                .Where(l => Directory.Exists(Path.Combine(PublishDirectory, l)))
                .ToList();
            if (existingLayers.Count == 0)
            {
                Log.LogError(
                    "NMica's CreateNewImage expected a layered publish directory at '{0}' (with " +
                    "package/, earlypackage/, project/, or app/ subdirectories) but found none. " +
                    "Set <NMicaOverridePublishContainer>false</NMicaOverridePublishContainer> to fall " +
                    "back to the SDK's single-layer behaviour.",
                    PublishDirectory);
                return false;
            }

            // Daemonless OCI archive output — the primary Route A path.
            if (!string.IsNullOrEmpty(ArchiveOutputPath))
            {
                try
                {
                    return ExecuteOciArchiveAsync(existingLayers).GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    Log.LogError("NMica OCI archive write failed: {0}", e.Message);
                    Log.LogMessage(MessageImportance.Low, e.ToString());
                    return false;
                }
            }

            // Other output modes (local daemon, remote registry) still use the docker-build
            // fallback. Phase 2 will replace these with direct registry push + docker-save format
            // tarball + docker load.
            return ExecuteDockerBuildFallback(existingLayers);
        }

        private async Task<bool> ExecuteOciArchiveAsync(IList<string> existingLayers)
        {
            var tagValues = ResolveTags();
            var baseRef = ImageReference.Parse(BaseRegistry, BaseImageName, BaseImageTag);

            Log.LogMessage(MessageImportance.High,
                "NMica: pulling base image manifest {0}", baseRef);

            using var registry = new Registry();
            var (baseManifest, _, _, _) = await registry.GetManifestAsync(baseRef, ContainerRuntimeIdentifier);
            var baseConfigJson = await registry.GetBlobAsStringAsync(baseRef, baseManifest.Config.Digest);

            var builder = new ImageBuilder(baseManifest, baseConfigJson);
            ConfigureImage(builder);

            // Build and add one layer per populated subdir. Each directory becomes one Docker
            // layer — that's the whole point.
            foreach (var layerName in existingLayers)
            {
                var layerDir = Path.Combine(PublishDirectory, layerName);
                Log.LogMessage(MessageImportance.High,
                    "NMica: tarring layer '{0}' from {1}", layerName, layerDir);
                var layer = Layer.FromDirectory(layerDir, WorkingDirectory, builder.ManifestMediaType);
                builder.AddLayer(layer);
                Log.LogMessage(MessageImportance.High,
                    "NMica:   → {0} ({1} bytes)", layer.Descriptor.Digest, layer.Descriptor.Size);
            }

            var built = builder.Build();

            Log.LogMessage(MessageImportance.High,
                "NMica: writing OCI archive with {0} layers to {1}",
                built.AllLayerDescriptors.Count, ArchiveOutputPath);

            await OciArchiveWriter.WriteAsync(new OciArchiveWriter.WriteRequest(
                Image: built,
                BaseImageRef: baseRef,
                Registry: registry,
                OutputPath: ArchiveOutputPath,
                Tags: tagValues));

            GeneratedArchiveOutputPath = ArchiveOutputPath;
            GeneratedContainerManifest = built.ManifestJson;
            GeneratedContainerConfiguration = built.ImageConfigJson;
            GeneratedContainerDigest = built.ManifestDigest;
            GeneratedContainerMediaType = built.Manifest.MediaType!;
            GeneratedContainerNames = tagValues
                .Select(t => (ITaskItem)new TaskItem($"{Repository}:{t}"))
                .ToArray();
            return true;
        }

        private void ConfigureImage(ImageBuilder builder)
        {
            if (!string.IsNullOrEmpty(WorkingDirectory))
                builder.SetWorkingDirectory(WorkingDirectory);
            if (!string.IsNullOrEmpty(ContainerUser))
                builder.SetUser(ContainerUser);
            foreach (var env in ContainerEnvironmentVariables)
                builder.AddEnvironmentVariable($"{env.ItemSpec}={env.GetMetadata("Value")}");
            foreach (var port in ExposedPorts)
            {
                var proto = port.GetMetadata("Type");
                var spec = string.IsNullOrEmpty(proto) ? $"{port.ItemSpec}/tcp" : $"{port.ItemSpec}/{proto}";
                builder.ExposePort(spec);
            }
            foreach (var label in Labels)
                builder.AddLabel(label.ItemSpec, label.GetMetadata("Value"));

            var (entrypoint, cmd) = ResolveEntrypointAndCmd();
            builder.SetEntrypointAndCmd(entrypoint, cmd);
        }

        private (IEnumerable<string>? entrypoint, IEnumerable<string>? cmd) ResolveEntrypointAndCmd()
        {
            var entryParts = Entrypoint.Select(i => i.ItemSpec).Concat(EntrypointArgs.Select(i => i.ItemSpec)).ToList();
            var cmdParts = AppCommand.Select(i => i.ItemSpec).Concat(AppCommandArgs.Select(i => i.ItemSpec)).ToList();
            return (
                entryParts.Count > 0 ? entryParts : null,
                cmdParts.Count > 0 ? cmdParts : null
            );
        }

        private string[] ResolveTags()
        {
            var tags = ImageTags
                .Select(t => t.ItemSpec)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
            return tags.Length > 0 ? tags : new[] { "latest" };
        }

        // -------------- Legacy docker-build fallback (for local daemon / remote push) --------------

        private bool ExecuteDockerBuildFallback(IList<string> existingLayers)
        {
            var dockerfilePath = Path.Combine(PublishDirectory, "Dockerfile.nmica");
            File.WriteAllText(dockerfilePath, BuildDockerfile(existingLayers));
            Log.LogMessage(MessageImportance.High, "NMica wrote layered Dockerfile -> {0}", dockerfilePath);

            var repoRef = string.IsNullOrEmpty(OutputRegistry)
                ? Repository
                : $"{OutputRegistry.TrimEnd('/')}/{Repository}";
            var tagValues = ResolveTags();
            var primaryTag = $"{repoRef}:{tagValues[0]}";

            var buildArgs = new StringBuilder();
            buildArgs.Append($"build -t \"{primaryTag}\"");
            foreach (var t in tagValues.Skip(1))
            {
                buildArgs.Append($" -t \"{repoRef}:{t}\"");
            }
            buildArgs.Append($" -f \"{dockerfilePath}\" \"{PublishDirectory}\"");

            if (!RunDocker(buildArgs.ToString())) return false;

            if (!string.IsNullOrEmpty(OutputRegistry))
            {
                foreach (var t in tagValues)
                {
                    if (!RunDocker($"push \"{repoRef}:{t}\"")) return false;
                }
            }

            GeneratedContainerNames = tagValues
                .Select(t => (ITaskItem)new TaskItem($"{repoRef}:{t}"))
                .ToArray();
            return true;
        }

        private string BuildDockerfile(IList<string> layers)
        {
            var sb = new StringBuilder();

            var baseRef = string.IsNullOrEmpty(BaseRegistry)
                ? $"{BaseImageName}:{BaseImageTag}"
                : $"{BaseRegistry.TrimEnd('/')}/{BaseImageName}:{BaseImageTag}";
            sb.AppendLine($"FROM {baseRef}");

            if (!string.IsNullOrEmpty(WorkingDirectory))
                sb.AppendLine($"WORKDIR {WorkingDirectory}");
            if (!string.IsNullOrEmpty(ContainerUser))
                sb.AppendLine($"USER {ContainerUser}");

            foreach (var layer in layers)
                sb.AppendLine($"COPY {layer}/ ./");

            foreach (var port in ExposedPorts)
            {
                var proto = port.GetMetadata("Type");
                sb.Append("EXPOSE ").Append(port.ItemSpec);
                if (!string.IsNullOrEmpty(proto)) sb.Append('/').Append(proto);
                sb.AppendLine();
            }

            foreach (var env in ContainerEnvironmentVariables)
                sb.AppendLine($"ENV {env.ItemSpec}={Quote(env.GetMetadata("Value"))}");

            foreach (var label in Labels)
                sb.AppendLine($"LABEL {label.ItemSpec}={Quote(label.GetMetadata("Value"))}");

            var (entrypoint, _) = ResolveEntrypointAndCmd();
            if (entrypoint is not null)
            {
                var parts = entrypoint.ToList();
                sb.AppendLine("ENTRYPOINT [" + string.Join(", ", parts.Select(p => $"\"{p}\"")) + "]");
            }

            return sb.ToString();
        }

        private bool RunDocker(string args)
        {
            Log.LogMessage(MessageImportance.High, "$ docker {0}", args);
            var psi = new ProcessStartInfo("docker", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            Process proc;
            try
            {
                proc = Process.Start(psi)!;
            }
            catch (Exception e)
            {
                Log.LogError("Could not start docker (is it installed and on PATH?): {0}", e.Message);
                return false;
            }

            using (proc)
            {
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                proc.WaitForExit();
                if (!string.IsNullOrEmpty(stdout.Result))
                    Log.LogMessage(MessageImportance.Normal, stdout.Result);
                if (!string.IsNullOrEmpty(stderr.Result))
                    Log.LogMessage(MessageImportance.High, stderr.Result);
                if (proc.ExitCode != 0)
                {
                    Log.LogError("docker exited with code {0}", proc.ExitCode);
                    return false;
                }
            }
            return true;
        }

        private static string Quote(string s) => "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
