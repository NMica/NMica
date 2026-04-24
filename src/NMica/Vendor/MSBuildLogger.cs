// Minimal adapter from Microsoft.Extensions.Logging.ILogger to MSBuild's TaskLoggingHelper.
// The SDK sources use `Microsoft.Extensions.Logging.MSBuild.MSBuildLoggerProvider` which lives
// in an unpublished SDK project — we can't link it, so we provide a tiny replacement that
// routes ILogger writes to the current task's log. Info → LogMessage, Warning → LogWarning,
// Error → LogError.

using System;
using Microsoft.Build.Utilities;
using Microsoft.Extensions.Logging;

namespace NMica.Vendor;

internal sealed class MSBuildLoggerProvider : ILoggerProvider
{
    private readonly TaskLoggingHelper _log;
    public MSBuildLoggerProvider(TaskLoggingHelper log) => _log = log;
    public ILogger CreateLogger(string categoryName) => new MSBuildLogger(_log, categoryName);
    public void Dispose() { }
}

internal sealed class MSBuildLogger : ILogger
{
    private readonly TaskLoggingHelper _log;
    private readonly string _category;

    public MSBuildLogger(TaskLoggingHelper log, string category)
    {
        _log = log;
        _category = category;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        switch (logLevel)
        {
            case LogLevel.Critical:
            case LogLevel.Error:
                if (exception is not null) _log.LogErrorFromException(exception, showStackTrace: false);
                else _log.LogError(msg);
                break;
            case LogLevel.Warning:
                _log.LogWarning(msg);
                break;
            case LogLevel.Information:
                _log.LogMessage(Microsoft.Build.Framework.MessageImportance.Normal, msg);
                break;
            case LogLevel.Debug:
            case LogLevel.Trace:
                _log.LogMessage(Microsoft.Build.Framework.MessageImportance.Low, msg);
                break;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

/// <summary>
/// Tiny <see cref="ILoggerFactory"/> that only knows how to wrap a single provider. Avoids
/// pulling in Microsoft.Extensions.Logging (the non-Abstractions package) just for
/// <c>LoggerFactory</c>.
/// </summary>
internal sealed class NMicaLoggerFactory : ILoggerFactory
{
    private readonly ILoggerProvider _provider;
    public NMicaLoggerFactory(ILoggerProvider provider) => _provider = provider;
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public ILogger CreateLogger(string categoryName) => _provider.CreateLogger(categoryName);
    public void Dispose() => _provider.Dispose();
}
