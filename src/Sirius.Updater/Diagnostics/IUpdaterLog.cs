using System;

namespace Sirius.Updater.Diagnostics;

/// <summary>
/// Minimal logging surface for the updater. Intentionally not a
/// dependency on <c>Microsoft.Extensions.Logging</c> so the core library
/// stays zero-friction — consumers who use ILogger can bridge in a
/// dozen lines via a custom <see cref="IUpdaterLog"/> implementation.
/// </summary>
public interface IUpdaterLog
{
    void Info(string category, string message);
    void Warn(string category, string message, Exception? error = null);
    void Error(string category, string message, Exception? error = null);
}

/// <summary>Default no-op log. Used when no log is configured.</summary>
public sealed class NullUpdaterLog : IUpdaterLog
{
    public static readonly NullUpdaterLog Instance = new();
    public void Info(string category, string message) { }
    public void Warn(string category, string message, Exception? error = null) { }
    public void Error(string category, string message, Exception? error = null) { }
}

/// <summary>
/// Delegates each call to an arbitrary <see cref="Action"/>. Handy for
/// piping into a host logger (ILogger, Serilog, etc.) without
/// implementing a full class.
/// </summary>
public sealed class DelegateUpdaterLog : IUpdaterLog
{
    readonly Action<string, string, string, Exception?> _sink;
    public DelegateUpdaterLog(Action<string /*level*/, string /*category*/, string /*message*/, Exception?> sink)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
    }
    public void Info(string c, string m)                    => _sink("info",  c, m, null);
    public void Warn(string c, string m, Exception? e = null) => _sink("warn",  c, m, e);
    public void Error(string c, string m, Exception? e = null) => _sink("error", c, m, e);
}
