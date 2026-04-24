using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using NMica.Tests.Utils;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;
using TUnit.Core;

namespace NMica.Tests
{
    /// <summary>
    /// Verifies NMica's override of the SDK's <c>PublishContainer</c> target emits a genuine
    /// multi-layer image. Runs <c>dotnet publish</c> on the HOST because the Docker-daemon
    /// fallback needs host Docker, and the OCI-archive path needs host-side HTTP to pull base
    /// image blobs from MCR.
    /// </summary>
    public class PublishContainerTests : BaseTests
    {
        /// <summary>
        /// Local-daemon output path: `dotnet publish /t:PublishContainer` → SDK + NMica's
        /// multi-layer override produce an image that lands in the local Docker daemon via
        /// <c>docker load</c> (of a docker-save tarball the SDK emits internally). We verify
        /// via `docker inspect` that the resulting image has more layers than the base.
        /// </summary>
        [Test]
        public async Task PublishContainer_LocalDaemon_ProducesMultiLayerImage()
        {
            var imageName = $"nmica-test-{TestName[..16].ToLowerInvariant()}";
            const string imageTag = "latest";
            var imageRef = $"{imageName}:{imageTag}";

            GenerateSampleProject();
            var csproj = Path.Combine(TestDir, "app1", "app1.csproj");

            try
            {
                var publish = await RunAsync("dotnet",
                    "publish", "/t:PublishContainer", "-c", "Release",
                    $"-p:ContainerImageName={imageName}",
                    $"-p:ContainerImageTag={imageTag}",
                    csproj);
                await Assert.That(publish.ExitCode).IsEqualTo(0);

                var publishDir = Path.Combine(TestDir, "app1", "bin", "Release", "net10.0", "publish");

                // Count layers on the final image vs the base to confirm NMica added ≥N.
                var (totalLayers, baseLayers) = await GetLayerCountsAsync(imageRef, "mcr.microsoft.com/dotnet/runtime:10.0");
                var layerDirs = CountLayerSubdirs(publishDir);
                Output.WriteLine($"Total layers={totalLayers}, base={baseLayers}, NMica dirs={layerDirs}");
                await Assert.That(totalLayers - baseLayers).IsGreaterThanOrEqualTo(layerDirs);
            }
            finally
            {
                await RunAsync("docker", "rmi", "-f", imageRef);
            }
        }

        /// <summary>
        /// Daemonless OCI-archive path: `dotnet publish /t:PublishContainer
        /// -p:ContainerArchiveOutputPath=./img.tar` → NMica pulls base manifest+blobs via HTTPS
        /// and writes a self-contained oci-layout tarball. No Docker build required. Verifies the
        /// tarball contains the expected layer count by parsing its index.json + manifest blob.
        /// </summary>
        [Test]
        public async Task PublishContainer_OciArchive_ProducesMultiLayerOciTarball()
        {
            var imageName = $"nmica-test-{TestName[..16].ToLowerInvariant()}";
            const string imageTag = "latest";
            var archivePath = Path.Combine(TestDir, "out.tar");

            GenerateSampleProject();
            var csproj = Path.Combine(TestDir, "app1", "app1.csproj");

            var publish = await RunAsync("dotnet",
                "publish", "/t:PublishContainer", "-c", "Release",
                $"-p:ContainerImageName={imageName}",
                $"-p:ContainerImageTag={imageTag}",
                $"-p:ContainerArchiveOutputPath={archivePath}",
                "-p:ContainerImageFormat=OCI",       // force oci-layout archive
                csproj);
            await Assert.That(publish.ExitCode).IsEqualTo(0);
            await Assert.That(File.Exists(archivePath)).IsTrue();

            // Inspect the OCI archive: extract index.json, follow the manifest digest, parse the
            // manifest's layers array. Expected count = base image layer count + NMica layer dirs.
            var extractDir = Path.Combine(TestDir, "extracted");
            Directory.CreateDirectory(extractDir);
            (await RunAsync("tar", "-xf", archivePath, "-C", extractDir)).EnsureSuccess();

            var indexJson = await File.ReadAllTextAsync(Path.Combine(extractDir, "index.json"));
            using var indexDoc = JsonDocument.Parse(indexJson);
            var manifestDigest = indexDoc.RootElement
                .GetProperty("manifests")[0].GetProperty("digest").GetString()!;

            var manifestPath = Path.Combine(extractDir, "blobs", "sha256", manifestDigest.Split(':')[1]);
            await Assert.That(File.Exists(manifestPath)).IsTrue();

            var manifestJson = await File.ReadAllTextAsync(manifestPath);
            using var manifestDoc = JsonDocument.Parse(manifestJson);
            var layerCount = manifestDoc.RootElement.GetProperty("layers").GetArrayLength();

            var publishDir = Path.Combine(TestDir, "app1", "bin", "Release", "net10.0", "publish");
            var nmicaLayers = CountLayerSubdirs(publishDir);
            Output.WriteLine($"OCI archive layers: {layerCount}, NMica added: {nmicaLayers}");
            // Base image typically ships ≥1 layer; our additions bring the total up.
            await Assert.That(layerCount).IsGreaterThanOrEqualTo(nmicaLayers + 1);

            // Every layer blob the manifest references must be present in the archive (self-contained).
            foreach (var layer in manifestDoc.RootElement.GetProperty("layers").EnumerateArray())
            {
                var digest = layer.GetProperty("digest").GetString()!;
                var blob = Path.Combine(extractDir, "blobs", "sha256", digest.Split(':')[1]);
                await Assert.That(File.Exists(blob)).IsTrue();
            }
        }

        private void GenerateSampleProject()
        {
            new SolutionConfiguration
            {
                NugetConfig = new NugetConfiguration().Add("artifacts", "artifacts"),
                Projects =
                {
                    new Project
                    {
                        Name = "app1",
                        Sdk = Sdks.Microsoft_NET_Sdk,
                        PropertyGroup = { OutputType = "exe", TargetFramework = "net10.0" }
                    }
                        .AddPackageReference("NMica", TestsSetup.NMicaVersion)
                        .AddPackageReference("Newtonsoft.Json", "13.0.3")
                }
            }.Generate(TestDir);
        }

        private static int CountLayerSubdirs(string publishDir) =>
            Directory.EnumerateDirectories(publishDir)
                .Select(Path.GetFileName)
                .Count(n => n is "package" or "earlypackage" or "project" or "app");

        private async Task<(int total, int baseLayers)> GetLayerCountsAsync(string imageRef, string baseRef)
        {
            var totalInspect = await RunAsync("docker", "inspect", "--format", "{{len .RootFS.Layers}}", imageRef);
            var baseInspect = await RunAsync("docker", "inspect", "--format", "{{len .RootFS.Layers}}", baseRef);
            return (int.Parse(totalInspect.Stdout.Trim()),
                    baseInspect.ExitCode == 0 ? int.Parse(baseInspect.Stdout.Trim()) : 0);
        }

        private async Task<ProcessResult> RunAsync(string exe, params string[] args)
        {
            var psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = TestDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Output.WriteLine($"$ {exe} {string.Join(' ', args)}");
            using var proc = Process.Start(psi)!;
            var stdoutTask = proc.StandardOutput.ReadToEndAsync();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (!string.IsNullOrEmpty(stdout)) Output.WriteLine(stdout);
            if (!string.IsNullOrEmpty(stderr)) Output.WriteLine("stderr: " + stderr);
            return new ProcessResult(proc.ExitCode, stdout, stderr);
        }

        private record ProcessResult(int ExitCode, string Stdout, string Stderr)
        {
            public ProcessResult EnsureSuccess()
            {
                if (ExitCode != 0) throw new System.InvalidOperationException($"process exited {ExitCode}");
                return this;
            }
        }
    }
}
