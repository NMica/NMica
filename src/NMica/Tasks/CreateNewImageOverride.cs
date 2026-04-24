using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.NET.Build.Containers;
using Microsoft.NET.Build.Containers.Resources;
using NMica.Vendor;
using ILogger = Microsoft.Extensions.Logging.ILogger;

// Intentional namespace-shadow. NMica.targets registers a <UsingTask> for
// Microsoft.NET.Build.Containers.Tasks.CreateNewImage that points at NMica.dll. MSBuild's
// last-registered-wins rule (NuGet .targets import after SDK targets) routes the SDK's
// _PublishSingleContainer call into THIS class instead of the SDK's own implementation.
//
// The flow is a near-verbatim copy of src/Containers/Microsoft.NET.Build.Containers/Tasks/
// CreateNewImage.cs (upstream), except for one critical change: instead of making a single
// Layer.FromDirectory() call on $(PublishDir), we enumerate the package/earlypackage/project/app
// subdirectories (prepared by _NMicaPreparePublishLayers) and call Layer.FromDirectory() +
// imageBuilder.AddLayer() once per populated bucket. Everything else — base image pull, image
// config assembly, manifest construction, and publish to the chosen sink — reuses the SDK
// machinery unchanged.
namespace Microsoft.NET.Build.Containers.Tasks;

public sealed class CreateNewImage : Microsoft.Build.Utilities.Task, ICancelableTask, IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private static readonly string[] LayerOrder = { "package", "earlypackage", "project", "app" };

    // ---- Inputs (match the SDK's CreateNewImage.Interface.cs property-by-property so the
    // SDK's _PublishSingleContainer call site binds without MSB4064 errors) ----

    public string ContainerizeDirectory { get; set; } = "";
    public string ToolPath { get; set; } = "";
    public string ToolExe { get; set; } = "";

    public string BaseRegistry { get; set; } = "";
    [Required] public string BaseImageName { get; set; } = "";
    public string BaseImageTag { get; set; } = "";
    public string BaseImageDigest { get; set; } = "";
    public string ImageFormat { get; set; } = "";

    public string OutputRegistry { get; set; } = "";
    public string LocalRegistry { get; set; } = "";
    public string ArchiveOutputPath { get; set; } = "";

    [Required] public string Repository { get; set; } = "";
    [Required] public string[] ImageTags { get; set; } = Array.Empty<string>();

    [Required] public string PublishDirectory { get; set; } = "";
    public string WorkingDirectory { get; set; } = "/app";

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

    // ---- Outputs ----

    [Output] public string GeneratedContainerDigest { get; set; } = "";
    [Output] public string GeneratedContainerManifest { get; set; } = "";
    [Output] public string GeneratedContainerConfiguration { get; set; } = "";
    [Output] public string GeneratedContainerMediaType { get; set; } = "";
    [Output] public ITaskItem[] GeneratedContainerNames { get; set; } = Array.Empty<ITaskItem>();
    [Output] public string GeneratedArchiveOutputPath { get; set; } = "";
    [Output] public ITaskItem? GeneratedDigestLabel { get; set; }

    public void Cancel() => _cts.Cancel();
    public void Dispose() => _cts.Dispose();

    public override bool Execute()
    {
        try
        {
            System.Threading.Tasks.Task.Run(() => ExecuteAsync(_cts.Token)).GetAwaiter().GetResult();
        }
        catch (TaskCanceledException ex) { Log.LogWarningFromException(ex); }
        catch (OperationCanceledException ex) { Log.LogWarningFromException(ex); }
        return !Log.HasLoggedErrors;
    }

    private async System.Threading.Tasks.Task<bool> ExecuteAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var loggerProvider = new MSBuildLoggerProvider(Log);
        ILoggerFactory loggerFactory = new NMicaLoggerFactory(loggerProvider);
        ILogger logger = loggerFactory.CreateLogger<CreateNewImage>();

        if (!Directory.Exists(PublishDirectory))
        {
            Log.LogErrorWithCodeFromResources(nameof(Strings.PublishDirectoryDoesntExist), nameof(PublishDirectory), PublishDirectory);
            return false;
        }

        // _NMicaPreparePublishLayers (a BeforeTarget of _PublishSingleContainer) restructures
        // $(PublishDir) into package/earlypackage/project/app subdirectories. If none exist
        // something went wrong — fail rather than silently emitting a single-layer image.
        var layerDirs = LayerOrder
            .Select(n => Path.Combine(PublishDirectory, n))
            .Where(Directory.Exists)
            .ToList();
        if (layerDirs.Count == 0)
        {
            Log.LogError(
                "NMica expected a layered publish directory at '{0}' with package/earlypackage/project/app " +
                "subdirectories but found none. Set <NMicaOverridePublishContainer>false</NMicaOverridePublishContainer> " +
                "to fall back to the SDK's single-layer CreateNewImage.", PublishDirectory);
            return false;
        }

        // --- Pull base image manifest + config (same as SDK CreateNewImage) ---
        var sourceMode = BaseRegistry.Equals(OutputRegistry, StringComparison.InvariantCultureIgnoreCase)
            ? RegistryMode.PullFromOutput
            : RegistryMode.Pull;
        Registry? sourceRegistry = string.IsNullOrWhiteSpace(BaseRegistry) ? null : new Registry(BaseRegistry, logger, sourceMode);
        if (sourceRegistry is null)
        {
            throw new NotSupportedException(Resource.GetString(nameof(Strings.ImagePullNotSupported)));
        }

        var sourceRef = new SourceImageReference(sourceRegistry, BaseImageName, BaseImageTag, BaseImageDigest);
        var destRef = DestinationImageReference.CreateFromSettings(
            Repository, ImageTags, loggerFactory, ArchiveOutputPath, OutputRegistry, LocalRegistry);

        var telemetry = new Telemetry(sourceRef, destRef, Log);

        ImageBuilder? imageBuilder;
        try
        {
            var picker = new RidGraphManifestPicker(RuntimeIdentifierGraphPath);
            imageBuilder = await sourceRegistry.GetImageManifestAsync(
                BaseImageName, sourceRef.Reference, ContainerRuntimeIdentifier, picker, ct).ConfigureAwait(false);
        }
        catch (RepositoryNotFoundException)
        {
            Log.LogErrorWithCodeFromResources(nameof(Strings.RepositoryNotFound), BaseImageName, BaseImageTag, BaseImageDigest, sourceRegistry.RegistryName);
            return false;
        }
        catch (UnableToAccessRepositoryException)
        {
            Log.LogErrorWithCodeFromResources(nameof(Strings.UnableToAccessRepository), BaseImageName, sourceRegistry.RegistryName);
            return false;
        }
        catch (ContainerHttpException e)
        {
            Log.LogErrorFromException(e, showStackTrace: false, showDetail: true, file: null);
            return false;
        }

        if (imageBuilder is null)
        {
            Log.LogErrorWithCodeFromResources(nameof(Strings.BaseImageNotFound), sourceRef, ContainerRuntimeIdentifier);
            return false;
        }

        // Honor ContainerImageFormat if set
        if (Enum.TryParse<KnownImageFormats>(ImageFormat, out var fmt))
        {
            imageBuilder.ManifestMediaType = fmt switch
            {
                KnownImageFormats.Docker => SchemaTypes.DockerManifestV2,
                KnownImageFormats.OCI => SchemaTypes.OciManifestV1,
                _ => imageBuilder.ManifestMediaType,
            };
        }

        // --- THE ONE INTERESTING CHANGE vs the SDK's CreateNewImage ---
        // SDK does: Layer.FromDirectory(PublishDirectory, ...); imageBuilder.AddLayer(newLayer);
        // We iterate the layer subdirectories instead, giving the final image N separate layers
        // ordered by change frequency.
        var userId = imageBuilder.IsWindows ? null : ContainerBuilder.TryParseUserId(ContainerUser);
        foreach (var layerDir in layerDirs)
        {
            var layer = Layer.FromDirectory(layerDir, WorkingDirectory, imageBuilder.IsWindows, imageBuilder.ManifestMediaType, userId);
            imageBuilder.AddLayer(layer);
            Log.LogMessage(MessageImportance.Normal,
                "NMica: added layer {0} → {1} ({2} bytes)",
                Path.GetFileName(layerDir), layer.Descriptor.Digest, layer.Descriptor.Size);
        }
        imageBuilder.SetWorkingDirectory(WorkingDirectory);

        var (entry, cmd) = DetermineEntrypointAndCmd(imageBuilder.BaseImageConfig.GetEntrypoint());
        imageBuilder.SetEntrypointAndCmd(entry, cmd);

        if (GenerateLabels)
        {
            foreach (var label in Labels) imageBuilder.AddLabel(label.ItemSpec, label.GetMetadata("Value"));
            if (GenerateDigestLabel)
            {
                var (l, d) = imageBuilder.AddBaseImageDigestLabel();
                var item = new TaskItem(l);
                item.SetMetadata("Value", d);
                GeneratedDigestLabel = item;
            }
        }

        foreach (var env in ContainerEnvironmentVariables)
            imageBuilder.AddEnvironmentVariable(env.ItemSpec, env.GetMetadata("Value"));

        foreach (var port in ExposedPorts)
        {
            if (ContainerHelpers.TryParsePort(port.ItemSpec, port.GetMetadata("Type"), out var parsed, out _))
                imageBuilder.ExposePort(parsed.Value.Number, parsed.Value.Type);
        }

        if (!string.IsNullOrEmpty(ContainerUser)) imageBuilder.SetUser(ContainerUser);

        if (Log.HasLoggedErrors) return false;

        var builtImage = imageBuilder.Build();
        ct.ThrowIfCancellationRequested();

        GeneratedContainerManifest = builtImage.Manifest;
        GeneratedContainerConfiguration = builtImage.Config;
        GeneratedContainerDigest = builtImage.ManifestDigest;
        GeneratedArchiveOutputPath = ArchiveOutputPath;
        GeneratedContainerMediaType = builtImage.ManifestMediaType;
        GeneratedContainerNames = destRef.FullyQualifiedImageNames()
            .Select(n => (ITaskItem)new TaskItem(n))
            .ToArray();

        if (!SkipPublishing)
        {
            await ImagePublisher.PublishImageAsync(builtImage, sourceRef, destRef, Log, telemetry, ct).ConfigureAwait(false);
        }

        return !Log.HasLoggedErrors;
    }

    private (string[] entrypoint, string[] cmd) DetermineEntrypointAndCmd(string[]? baseEntrypoint)
    {
        return ImageBuilder.DetermineEntrypointAndCmd(
            entrypoint: Entrypoint.Select(i => i.ItemSpec).ToArray(),
            entrypointArgs: EntrypointArgs.Select(i => i.ItemSpec).ToArray(),
            cmd: DefaultArgs.Select(i => i.ItemSpec).ToArray(),
            appCommand: AppCommand.Select(i => i.ItemSpec).ToArray(),
            appCommandArgs: AppCommandArgs.Select(i => i.ItemSpec).ToArray(),
            appCommandInstruction: AppCommandInstruction,
            baseImageEntrypoint: baseEntrypoint,
            logWarning: s => Log.LogWarningWithCodeFromResources(s),
            logError: (s, a) => { if (a is null) Log.LogErrorWithCodeFromResources(s); else Log.LogErrorWithCodeFromResources(s, a); });
    }
}
