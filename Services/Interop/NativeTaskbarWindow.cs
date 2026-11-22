using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Finds Explorer's taskbar window. When OpenTaskbar hosts the notification area it owns a second, invisible
/// <c>Shell_TrayWnd</c> that is kept topmost so apps register their tray icons with it; a plain
/// <c>FindWindow("Shell_TrayWnd")</c> would then return that window instead of the real taskbar.
/// </summary>
internal static class NativeTaskbarWindow
{
    private const string TrayWindowClass = "Shell_TrayWnd";
    private const string SecondaryTrayWindowClass = "Shell_SecondaryTrayWnd";

    /// <summary>Explorer's primary taskbar, skipping any <c>Shell_TrayWnd</c> owned by this process.</summary>
    public static nint Find()
    {
        nint candidate = 0;
        while ((candidate = FindWindowEx(0, candidate, TrayWindowClass, null)) != 0)
        {
            // A stale candidate can be destroyed between FindWindowEx and the PID query; HwndLease owns both
            // liveness and ownership checks so callers never act on a recycled handle.
            if (!HwndLease.IsAlive(candidate)) continue;
            GetWindowThreadProcessId(candidate, out var processId);
            if (processId != 0 && processId != Environment.ProcessId) return candidate;
        }
        return 0;
    }

    /// <summary>Acquires a re-validated lease. False means Explorer's taskbar is absent or was replaced mid-scan.</summary>
    public static bool TryAcquire(out HwndLease lease)
    {
        lease = HwndLease.Capture(Find());
        return lease.IsValid && lease.ProcessId != (uint)Environment.ProcessId;
    }

    /// <summary>Re-validated secondary taskbars; primary tray visibility alone is not enough for journal deletion.</summary>
    public static IReadOnlyList<HwndLease> FindSecondaryLeases()
    {
        var handles = new List<HwndLease>();
        EnumWindows((handle, _) =>
        {
            var className = new char[64];
            var length = GetClassName(handle, className, className.Length);
            if (length > 0 && className.AsSpan(0, length).SequenceEqual(SecondaryTrayWindowClass))
            {
                var lease = HwndLease.Capture(handle);
                if (lease.IsValid && lease.ProcessId != (uint)Environment.ProcessId)
                {
                    handles.Add(lease);
                }
            }

            return true;
        }, 0);
        return handles;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    private delegate bool EnumWindowsProc(nint windowHandle, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint windowHandle, char[] name, int length);
}
