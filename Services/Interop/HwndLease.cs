using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// A validated snapshot of a native window handle. Raw HWNDs can be recycled by Windows as soon as a process
/// exits, so every shell operation re-checks <see cref="IsWindow"/> and the owning PID captured at acquisition
/// time before the handle is used. A lease whose owner changed is treated as missing, never as a valid target.
/// </summary>
internal readonly struct HwndLease
{
    private HwndLease(nint handle, uint processId)
    {
        Handle = handle;
        ProcessId = processId;
    }

    public nint Handle { get; }

    public uint ProcessId { get; }

    /// <summary>True only while the handle is still alive and owned by the same process.</summary>
    public bool IsValid => Handle != 0 && ProcessId != 0 && IsWindow(Handle)
        && GetWindowThreadProcessId(Handle, out var current) != 0 && current == ProcessId;

    /// <summary>True when the handle is alive but belongs to a different process than OpenTaskbar.</summary>
    public bool IsForeign => IsValid && ProcessId != (uint)Environment.ProcessId;

    public static HwndLease Invalid => default;

    /// <summary>Captures a handle together with its current owner. Call <see cref="IsValid"/> before use.</summary>
    public static HwndLease Capture(nint handle)
    {
        if (handle == 0 || GetWindowThreadProcessId(handle, out var processId) == 0 || processId == 0)
        {
            return Invalid;
        }

        return new HwndLease(handle, processId);
    }

    /// <summary>A cheap liveness probe used while enumerating candidate windows.</summary>
    public static bool IsAlive(nint handle) => handle != 0 && IsWindow(handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);
}
