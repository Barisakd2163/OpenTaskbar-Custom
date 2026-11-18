using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Trims inactive virtual memory and working set pages back to the operating system after startup
/// and during extended idle periods.
/// </summary>
internal static class NativeMemoryMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(nint process, nint minimumWorkingSetSize, nint maximumWorkingSetSize);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint process);

    private static long _lastTrimTicks;
    private static readonly TimeSpan MinTrimInterval = TimeSpan.FromSeconds(30);

    public static void TrimWorkingSet(bool force = false)
    {
        var now = Stopwatch.GetTimestamp();
        if (!force && _lastTrimTicks != 0 && Stopwatch.GetElapsedTime(_lastTrimTicks, now) < MinTrimInterval) return;
        _lastTrimTicks = now;

        try
        {
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false);
            var handle = Process.GetCurrentProcess().Handle;
            if (handle != 0)
            {
                _ = EmptyWorkingSet(handle);
                _ = SetProcessWorkingSetSize(handle, -1, -1);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Memory trim skipped: {ex.Message}");
        }
    }
}
