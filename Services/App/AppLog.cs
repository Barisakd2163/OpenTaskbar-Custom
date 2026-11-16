using System.Diagnostics;
using System.IO;

namespace OpenTaskbar.Services;

/// <summary>
/// Routes Trace output to %LOCALAPPDATA%\OpenTaskbar\log.txt, rotating to log.old.txt when it grows past 1 MB.
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

    public static void Shutdown()
    {
        if (_listener is null) return;
        Trace.Listeners.Remove(_listener);
        _listener.Flush();
        _listener.Dispose();
        _listener = null;
    }
}
