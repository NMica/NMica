// Minimal shim for `Microsoft.DotNet.Cli.Utils` — an unpublished internal SDK helper library
// the linked DockerCli.cs and RegistrySettings.cs depend on. We provide *only* the surface
// those two files actually use so the SDK source files compile verbatim against our shim.
//
// Upstream reference (for anyone tracking drift):
//   https://github.com/dotnet/sdk/tree/main/src/Cli/Microsoft.DotNet.Cli.Utils

using System;
using System.Diagnostics;

namespace Microsoft.DotNet.Cli.Utils;

/// <summary>Thin wrapper over <see cref="Process"/>. Matches the shape the SDK Cli.Utils type exposes.</summary>
internal sealed class Command
{
    private readonly Process _process;
    private bool _captureStdOut;
    private bool _captureStdErr;

    public Command(Process process)
    {
        _process = process;
    }

    public Command CaptureStdOut() { _captureStdOut = true; return this; }
    public Command CaptureStdErr() { _captureStdErr = true; return this; }

    public CommandResult Execute()
    {
        if (_captureStdOut)
        {
            _process.StartInfo.RedirectStandardOutput = true;
            _process.StartInfo.UseShellExecute = false;
        }
        if (_captureStdErr)
        {
            _process.StartInfo.RedirectStandardError = true;
            _process.StartInfo.UseShellExecute = false;
        }

        _process.Start();
        string? stdout = _captureStdOut ? _process.StandardOutput.ReadToEnd() : null;
        string? stderr = _captureStdErr ? _process.StandardError.ReadToEnd() : null;
        _process.WaitForExit();
        return new CommandResult(_process.ExitCode, stdout, stderr);
    }
}

internal readonly record struct CommandResult(int ExitCode, string? StdOut, string? StdErr);

/// <summary>
/// Environment-variable accessor with typed parsing helpers. Same shape as SDK's
/// <c>IEnvironmentProvider</c>; we just back it with <see cref="Environment"/> directly.
/// </summary>
internal interface IEnvironmentProvider
{
    string? GetEnvironmentVariable(string name);
    bool GetEnvironmentVariableAsBool(string name, bool defaultValue);
    int? GetEnvironmentVariableAsNullableInt(string name);
}

internal sealed class EnvironmentProvider : IEnvironmentProvider
{
    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public bool GetEnvironmentVariableAsBool(string name, bool defaultValue)
    {
        var v = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrEmpty(v)) return defaultValue;
        return v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("1", StringComparison.Ordinal)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    public int? GetEnvironmentVariableAsNullableInt(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) ? n : (int?)null;
    }
}
