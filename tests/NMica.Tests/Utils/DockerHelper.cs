using System;
using System.IO;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

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

        public static async Task<IFutureDockerImage> BuildImageAsync(string contextDir, string dockerfileRelative, string tag)
        {
            var image = new ImageFromDockerfileBuilder()
                .WithDockerfileDirectory(contextDir)
                .WithDockerfile(dockerfileRelative.Replace("\\", "/"))
                .WithName(tag)
                .WithCleanUp(true)
                .Build();
            await image.CreateAsync();
            return image;
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
