using System.Collections.Concurrent;
using System.Text;

namespace VirtualMirage;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Tiny dependency-free thread-safe logger: appends to a daily file under
/// %AppData%\VirtualMirage\logs and keeps a bounded in-memory ring of recent lines
/// (used by the detection diagnostics view).
/// </summary>
public static class Log
{
    private static readonly object _fileGate = new();
    private static string _logFile = "";
    private static readonly ConcurrentQueue<string> _recent = new();
    private const int RecentMax = 1000;

    public static LogLevel MinLevel { get; set; } = LogLevel.Info;
    public static string LogFilePath => _logFile;

    /// <summary>Raised for every emitted line (already formatted). UI/diagnostics can subscribe.</summary>
    public static event Action<string>? LineWritten;

    public static void Init()
    {
        Paths.EnsureCreated();
        _logFile = Path.Combine(Paths.LogsDir, $"virtualmirage-{DateTime.Now:yyyyMMdd}.log");
        Info($"=== VirtualMirage started (pid {Environment.ProcessId}, {Environment.OSVersion}) ===");
    }

    public static void Debug(string msg) => Write(LogLevel.Debug, msg);
    public static void Info(string msg) => Write(LogLevel.Info, msg);
    public static void Warn(string msg) => Write(LogLevel.Warn, msg);

    public static void Error(string msg, Exception? ex = null) =>
        Write(LogLevel.Error, ex is null ? msg : $"{msg} :: {ex}");

    public static void Write(LogLevel level, string msg)
    {
        if (level < MinLevel) return;
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()[0]}] {msg}";

        _recent.Enqueue(line);
        while (_recent.Count > RecentMax && _recent.TryDequeue(out _)) { }

        try
        {
            lock (_fileGate)
            {
                if (!string.IsNullOrEmpty(_logFile))
                    File.AppendAllText(_logFile, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* never throw from logging */ }

        try { LineWritten?.Invoke(line); } catch { }
    }

    public static IReadOnlyList<string> RecentLines() => _recent.ToArray();
}
