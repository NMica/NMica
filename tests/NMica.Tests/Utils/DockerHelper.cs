using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace NMica.Tests.Utils
{
    /// <summary>
    /// Thin wrappers over <c>DotNet.Testcontainers</c> to keep test code readable.
    /// </summary>
    public static class DockerHelper
    {
        public const string ContainerMount = "/app";

        /// <summary>
        /// Starts a long-lived "idle" container with the host directory bind-mounted at
        /// <see cref="ContainerMount"/>. Callers <c>await using</c> the returned container and
        /// invoke <see cref="ExecAsync"/> against it.
        /// </summary>
        public static async Task<IContainer> StartSdkAsync(string image, string hostMountDir)
        {
            var container = new ContainerBuilder(image)
                .WithBindMount(hostMountDir, ContainerMount)
                .WithWorkingDirectory(ContainerMount)
                .WithEntrypoint("sleep", "infinity")
                .WithCleanUp(true)
                .Build();

            await container.StartAsync();
            return container;
        }

        public static async Task<DockerResult> ExecAsync(this IContainer container, string script, TextWriter output)
        {
            var result = await container.ExecAsync(new[] { "sh", "-c", script });
            output.WriteLine($"$ {script}");
            if (!string.IsNullOrEmpty(result.Stdout)) output.WriteLine(result.Stdout);
            if (!string.IsNullOrEmpty(result.Stderr)) output.WriteLine("stderr: " + result.Stderr);
            return new DockerResult(result.ExitCode ?? -1, result.Stdout ?? string.Empty, result.Stderr ?? string.Empty);
        }

        /// <summary>
        /// Shells out to the <c>docker</c> CLI rather than going through Testcontainers'
        /// Engine-API <c>/build</c> endpoint. On GHA's Docker 28 + BuildKit, the daemon-side
        /// build path hits a layer-export race ("failed to get layer sha256:... layer does
        /// not exist") on multi-stage Dockerfiles. Using the CLI lets us route through
        /// <c>buildx</c>'s <c>docker-container</c> driver (set up in CI), which runs an
        /// isolated BuildKit instance and sidesteps the daemon bug.
        /// </summary>
        public static async Task BuildImageAsync(string contextDir, string dockerfileRelative, string tag)
        {
            var dockerfilePath = Path.Combine(contextDir, dockerfileRelative.Replace("\\", "/"));
            var psi = new ProcessStartInfo("docker")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = contextDir,
            };
            psi.ArgumentList.Add("build");
            psi.ArgumentList.Add("--load");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(tag);
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(dockerfilePath);
            psi.ArgumentList.Add(contextDir);

            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"docker build failed (exit {process.ExitCode}) for {dockerfilePath}\nstdout:\n{stdout}\nstderr:\n{stderr}");
            }
        }

        public static async Task<DockerResult> RunImageOnceAsync(string imageTag, TextWriter output)
        {
            await using var container = new ContainerBuilder(imageTag)
                .WithCleanUp(true)
                .Build();

            try { await container.StartAsync(); } catch { /* may have already exited */ }

            long exitCode;
            try { exitCode = await container.GetExitCodeAsync(); }
            catch { exitCode = -1; }

            var (stdout, stderr) = await container.GetLogsAsync(timestampsEnabled: false);

            output.WriteLine($"[run image {imageTag}]");
            if (!string.IsNullOrEmpty(stdout)) output.WriteLine(stdout);
            if (!string.IsNullOrEmpty(stderr)) output.WriteLine("stderr: " + stderr);

            return new DockerResult(exitCode, stdout ?? string.Empty, stderr ?? string.Empty);
        }
    }

    public record DockerResult(long ExitCode, string Stdout, string Stderr)
    {
        public void EnsureSuccess()
        {
            if (ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Docker command exited with code {ExitCode}.\nStdout:\n{Stdout}\nStderr:\n{Stderr}");
            }
        }
    }
}
