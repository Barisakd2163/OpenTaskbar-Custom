using System.Diagnostics;
using System.IO;

namespace OpenTaskbar.Services;

/// <summary>
/// Routes Trace output to %LOCALAPPDATA%\OpenTaskbar\log.txt, rotating to log.old.txt when it grows past 1 MB.
/// Failures are recorded as structured entries containing the localization key, the resolved message and
/// the exception details, so the UI never has to display them.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 1024 * 1024;
    private static TextWriterTraceListener? _listener;

    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTaskbar", "log.txt");

    public static void Initialize()
    {
        if (_listener is not null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
            {
                File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.txt"), overwrite: true);
            }

            var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            // A BOM on new files lets Notepad and Windows PowerShell read Turkish text correctly.
            var encoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: stream.Length == 0);
            _listener = new TextWriterTraceListener(new StreamWriter(stream, encoding) { AutoFlush = true }, "OpenTaskbarLog");
            Trace.Listeners.Add(_listener);
            Trace.AutoFlush = true;
            Info($"OpenTaskbar {typeof(AppLog).Assembly.GetName().Version} started.");
        }
        catch (IOException)
        {
            _listener = null;
        }
        catch (UnauthorizedAccessException)
        {
            _listener = null;
        }
    }

    public static void Info(string message) => Trace.TraceInformation($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
    public static void Warning(string message) => Trace.TraceWarning($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");
    public static void Error(string message) => Trace.TraceError($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}");

    /// <summary>
    /// Resolves a localization key the same way the log records it. Already-resolved text and raw exception
    /// messages are returned unchanged instead of throwing a missing-key exception.
    /// </summary>
    public static string ResolveMessage(string messageKey, params object[] arguments)
    {
        if (string.IsNullOrWhiteSpace(messageKey)) return string.Empty;
        try
        {
            return arguments.Length == 0 ? UiText.Get(messageKey) : UiText.Format(messageKey, arguments);
        }
        catch
        {
            return messageKey;
        }
    }

    /// <summary>Writes a failure as one structured record: localization key, message and exception details.</summary>
    public static void ReportError(string messageKey) => WriteError(messageKey, [], null);

    public static void ReportError(string messageKey, params object[] arguments) => WriteError(messageKey, arguments, null);

    public static void ReportError(string messageKey, Exception exception) => WriteError(messageKey, [], exception);

    public static void ReportError(string messageKey, Exception exception, params object[] arguments) =>
        WriteError(messageKey, arguments, exception);

    private static void WriteError(string messageKey, object[] arguments, Exception? exception)
    {
        var parts = new List<string>
        {
            $"key={messageKey}",
            $"message={ResolveMessage(messageKey, arguments)}"
        };
        if (exception is not null)
        {
            parts.Add($"exception={exception.GetType().Name}: {exception.Message}");
            if (exception.InnerException is not null)
                parts.Add($"inner={exception.InnerException.GetType().Name}: {exception.InnerException.Message}");
            if (!string.IsNullOrWhiteSpace(exception.StackTrace))
                parts.Add($"stack={exception.StackTrace.Replace(Environment.NewLine, " | ", StringComparison.Ordinal)}");
        }
        Error(string.Join(" | ", parts));
    }

    /// <summary>Opens log.txt in the default text editor for users who need to inspect recorded failures.</summary>
    public static bool TryOpenLogFile()
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                Warning($"Log file not found: {LogPath}");
                return false;
            }

            using var process = Process.Start(new ProcessStartInfo(LogPath) { UseShellExecute = true });
            return process is not null;
        }
        catch (Exception ex)
        {
            Warning($"Cannot open log file: {ex.Message}");
            return false;
        }
    }

    public static void Shutdown()
    {
        if (_listener is null) return;
        Trace.Listeners.Remove(_listener);
        _listener.Flush();
        _listener.Dispose();
        _listener = null;
    }
}
