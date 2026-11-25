using System.Buffers;
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
    private static readonly nint CurrentProcess = new(-1); // Pseudohandle: valid for this process, never closed.
    private static readonly uint MemoryCountersSize = (uint)Marshal.SizeOf<ProcessMemoryCounters>();
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
            _json = new Utf8JsonWriter(_buffer);
            try
            {
                Directory.CreateDirectory(directory);
                CaptureNow(); // Baseline before the first timer tick.
                _timer = new Timer(_ => CaptureNow(), null, interval, interval);
            }
            catch
            {
                _stream?.Dispose();
                _json.Dispose();
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
                    if (GetProcessHandleCount(CurrentProcess, out var handles))
                        _json.WriteNumber("handles", handles);
                    else
                    {
                        error ??= "GetProcessHandleCount:" + Marshal.GetLastPInvokeError();
                        _json.WriteNull("handles");
                    }
                    var memory = new ProcessMemoryCounters { Size = MemoryCountersSize };
                    if (GetProcessMemoryInfo(CurrentProcess, ref memory, MemoryCountersSize))
                    {
                        _json.WriteNumber("privateBytes", (ulong)memory.PrivateUsage);
                        _json.WriteNumber("workingSetBytes", (ulong)memory.WorkingSetSize);
                        _json.WriteNumber("peakWorkingSetBytes", (ulong)memory.PeakWorkingSetSize);
                        _json.WriteNumber("pageFaults", memory.PageFaultCount);
                    }
                    else
                    {
                        error ??= "K32GetProcessMemoryInfo:" + Marshal.GetLastPInvokeError();
                        _json.WriteNull("privateBytes");
                        _json.WriteNull("workingSetBytes");
                        _json.WriteNull("peakWorkingSetBytes");
                        _json.WriteNull("pageFaults");
                    }
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
            var count = GetGuiResources(CurrentProcess, flag);
            var nativeError = Marshal.GetLastPInvokeError();
            if (count == 0 && nativeError != 0)
            {
                _json.WriteNull(name);
                error = "GetGuiResources:" + nativeError;
            }
            else _json.WriteNumber(name, count);
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
            }
        }
    }

    // PROCESS_MEMORY_COUNTERS_EX: SIZE_T fields must retain pointer width on both architectures.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        internal uint Size;
        internal uint PageFaultCount;
        internal nuint PeakWorkingSetSize;
        internal nuint WorkingSetSize;
        internal nuint QuotaPeakPagedPoolUsage;
        internal nuint QuotaPagedPoolUsage;
        internal nuint QuotaPeakNonPagedPoolUsage;
        internal nuint QuotaNonPagedPoolUsage;
        internal nuint PagefileUsage;
        internal nuint PeakPagefileUsage;
        internal nuint PrivateUsage;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(nint process, ref ProcessMemoryCounters counters, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(nint process, out uint handles);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint flags);
}
