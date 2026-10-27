using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopBuddy.Services;

internal enum ThrottledLogResult
{
    Written,
    AlreadyRecorded,
    Failed
}

/// <summary>
/// Writes privacy-conscious diagnostics to the current user's local app-data directory.
/// Exception messages, command-line arguments, process names, and user content are never written.
/// </summary>
internal static class CrashLogger
{
    private const int RetainedLogFiles = 7;
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime At, bool Succeeded)> LastThrottledWrite = new(StringComparer.Ordinal);

    private static string? AcceptanceLogDirectory;
    internal static void SetAcceptanceLogDirectory(string directory)
    {
        lock(Gate) AcceptanceLogDirectory=Path.GetFullPath(directory);
    }
    private static string LogDirectory => AcceptanceLogDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopBuddy", "logs");

    public static void Write(string component, Exception exception) => _ = WriteCore(component, exception);

    public static ThrottledLogResult WriteThrottled(string component, Exception exception)
    {
        var key = component + "|" + exception.GetType().FullName + "|" + exception.HResult.ToString("X8");
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (LastThrottledWrite.TryGetValue(key, out var previous) && now - previous.At < TimeSpan.FromMinutes(1))
                return previous.Succeeded ? ThrottledLogResult.AlreadyRecorded : ThrottledLogResult.Failed;

            var succeeded = WriteCoreLocked(component, exception);
            LastThrottledWrite[key] = (now, succeeded);
            return succeeded ? ThrottledLogResult.Written : ThrottledLogResult.Failed;
        }
    }

    private static bool WriteCore(string component, Exception exception)
    {
        lock (Gate) return WriteCoreLocked(component, exception);
    }

    private static bool WriteCoreLocked(string component, Exception exception)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var entry = new StringBuilder()
                .AppendLine($"[{DateTimeOffset.Now:O}] component={SafeLabel(component)}")
                .AppendLine($"appVersion={Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown"}; runtime={RuntimeInformation.FrameworkDescription}; processArchitecture={RuntimeInformation.ProcessArchitecture}");

            var current = exception;
            for (var depth = 0; current is not null && depth < 8; depth++, current = current.InnerException)
            {
                entry.AppendLine($"exception[{depth}]={current.GetType().FullName ?? "unknown"}; hresult=0x{current.HResult:X8}");
                var stack = new StackTrace(current, fNeedFileInfo: false).ToString();
                if (!string.IsNullOrWhiteSpace(stack)) entry.AppendLine(stack.TrimEnd());
            }
            entry.AppendLine();
            File.AppendAllText(path, entry.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            TrimOldLogs();
            return true;
        }
        catch
        {
            // Diagnostics must never cause another application failure.
            return false;
        }
    }

    private static void TrimOldLogs()
    {
        try
        {
            var files = Directory.EnumerateFiles(LogDirectory, "crash-*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
            foreach (var oldFile in files.Skip(RetainedLogFiles))
            {
                try { File.Delete(oldFile); }
                catch { /* Retention cleanup is best-effort. */ }
            }
        }
        catch { /* Retention cleanup is best-effort. */ }
    }

    private static string SafeLabel(string value) =>
        string.IsNullOrWhiteSpace(value) ? "application" : new string(value.Where(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_').Take(48).ToArray());
}
