using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;

namespace OpenTaskbar.Services;

/// <summary>Local, bounded process counters only: no windows, titles, settings or external service.</summary>
internal static class ProcessResourceMonitor
{
    private static readonly object Gate = new();
    private static readonly string Build = CrashReport.BuildVersion;
    private static Session? _session;
    internal const long MaxFileBytes = 256 * 1024;

    public static void Start()
    {
        lock (Gate)
        {
            if (_session is not null) return;
            try { _session = new Session(CrashReport.DefaultDirectory, TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { AppLog.Warning($"Process resource monitoring unavailable ({ex.GetType().Name})."); }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>Best-effort final counters for a crash handler, before its raw report is written.</summary>
    public static void CaptureNow()
    {
        if (!Monitor.TryEnter(Gate)) return;
        try { _session?.CaptureNow(); }
        finally { Monitor.Exit(Gate); }
    }

    // An independently owned session permits real sampling/rotation/lifecycle checks in temporary directories.
    internal sealed class Session : IDisposable
    {
        private readonly object _gate = new();
        private readonly Process _process;
        private readonly nint _handle;
        private readonly string _path;
        private readonly string _oldPath;
        private readonly long _maxBytes;
        private readonly ArrayBufferWriter<byte> _buffer = new(512);
        private readonly Utf8JsonWriter _json;
        private FileStream? _stream;
        private Timer? _timer;
        private bool _disposed;
        private bool _reportedFailure;

        internal Session(string directory, TimeSpan interval, long maxBytes = MaxFileBytes)
        {
            if (interval <= TimeSpan.Zero || interval.TotalMilliseconds > uint.MaxValue - 1)
                throw new ArgumentOutOfRangeException(nameof(interval));
            if (maxBytes < 1024) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            _maxBytes = maxBytes;
            _path = Path.Combine(directory, $"resources-{Environment.ProcessId}.jsonl");
            _oldPath = Path.Combine(directory, $"resources-{Environment.ProcessId}.old.jsonl");
            _process = Process.GetCurrentProcess();
            _json = new Utf8JsonWriter(_buffer);
            try
            {
                _handle = _process.Handle; // Open once; reuse for every GDI/USER sample.
                Directory.CreateDirectory(directory);
                CaptureNow(); // Baseline before the first timer tick.
                _timer = new Timer(_ => CaptureNow(), null, interval, interval);
            }
            catch
            {
                _stream?.Dispose();
                _json.Dispose();
                _process.Dispose();
                throw;
            }
        }

        internal bool CaptureNow()
        {
            if (!Monitor.TryEnter(_gate)) return false; // Skip overlap; a crash handler must not wait for the timer.
            try
            {
                if (_disposed) return false;
                try
                {
                    _buffer.Clear();
                    _json.Reset(_buffer);
                    _json.WriteStartObject();
                    _json.WriteNumber("pid", Environment.ProcessId);
                    _json.WriteString("build", Build);
                    _json.WriteString("utc", DateTimeOffset.UtcNow);
                    string? error = null;
                    WriteGuiCount("gdiObjects", 0, ref error);
                    WriteGuiCount("userObjects", 1, ref error);
                    WriteGuiCount("gdiPeakObjects", 2, ref error);
                    WriteGuiCount("userPeakObjects", 4, ref error);
                    int? handles = null;
                    long? privateBytes = null;
                    long? workingSetBytes = null;
                    try
                    {
                        _process.Refresh();
                        handles = _process.HandleCount;
                        privateBytes = _process.PrivateMemorySize64;
                        workingSetBytes = _process.WorkingSet64;
                    }
                    catch (Exception ex) { error ??= ex.GetType().Name; }
                    WriteOptionalNumber("handles", handles);
                    WriteOptionalNumber("privateBytes", privateBytes);
                    WriteOptionalNumber("workingSetBytes", workingSetBytes);
                    _json.WriteNumber("managedBytes", GC.GetTotalMemory(forceFullCollection: false));
                    if (error is not null) _json.WriteString("error", error);
                    _json.WriteEndObject();
                    _json.Flush();
                    OpenOrRotate(_buffer.WrittenCount + 1);
                    _stream!.Write(_buffer.WrittenSpan);
                    _stream.WriteByte((byte)'\n');
                    _stream.Flush();
                    if (error is not null) ReportFailure(error);
                    return true;
                }
                catch (Exception ex)
                {
                    ReportFailure(ex.GetType().Name);
                    CloseStream();
                    return false;
                }
            }
            finally { Monitor.Exit(_gate); }
        }

        private void WriteGuiCount(string name, uint flag, ref string? error)
        {
            var count = GetGuiResources(_handle, flag);
            var nativeError = Marshal.GetLastPInvokeError();
            if (count == 0 && nativeError != 0)
            {
                _json.WriteNull(name);
                error = "GetGuiResources:" + nativeError;
            }
            else _json.WriteNumber(name, count);
        }

        private void WriteOptionalNumber(string name, long? value)
        {
            if (value is long number) _json.WriteNumber(name, number);
            else _json.WriteNull(name);
        }

        private void OpenOrRotate(int nextBytes)
        {
            var needsSeparator = false;
            if (_stream is null)
            {
                _stream = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, 4096);
                if (_stream.Length > 0)
                {
                    _stream.Seek(-1, SeekOrigin.End);
                    needsSeparator = _stream.ReadByte() != '\n';
                }
            }
            if (_stream.Length + nextBytes + (needsSeparator ? 1 : 0) > _maxBytes)
            {
                _stream.Dispose();
                _stream = null;
                File.Move(_path, _oldPath, overwrite: true);
                _stream = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, 4096);
                needsSeparator = false;
            }
            _stream.Seek(0, SeekOrigin.End);
            if (needsSeparator) _stream.WriteByte((byte)'\n'); // Preserve a kill-truncated line without merging the next sample.
        }

        private void ReportFailure(string category)
        {
            if (_reportedFailure) return;
            _reportedFailure = true;
            AppLog.Warning($"Process resource sample unavailable ({category}); resource evidence may be incomplete.");
        }

        private void CloseStream()
        {
            try { _stream?.Dispose(); }
            catch (Exception ex) { ReportFailure(ex.GetType().Name); }
            _stream = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
                CloseStream();
                _json.Dispose();
                _process.Dispose();
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint flags);
}
