using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenTaskbar.Services;

/// <summary>
/// Virtual desktop state from documented sources: <c>IVirtualDesktopManager</c> for window membership and Explorer's
/// registry values for the desktop list. Switching uses the standard Win+Ctrl+Arrow shortcuts.
/// </summary>
public static class VirtualDesktops
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";
    private static IVirtualDesktopManager? _manager;
    private static bool _managerFailed;

    /// <summary>1-based index of the current desktop and the desktop count; (1, 1) when only one desktop exists.</summary>
    public static (int Current, int Count) Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Key);
            if (key?.GetValue("VirtualDesktopIDs") is not byte[] { Length: >= 16 } ids) return (1, 1);
            var count = ids.Length / 16;
            var current = key.GetValue("CurrentVirtualDesktop") as byte[] ?? ReadSessionCurrent();
            if (current is not { Length: 16 }) return (1, count);
            for (var index = 0; index < count; index++)
            {
                if (ids.AsSpan(index * 16, 16).SequenceEqual(current)) return (index + 1, count);
            }

            return (1, count);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return (1, 1);
        }
    }

    // Windows 10 keeps the current desktop per logon session.
    private static byte[]? ReadSessionCurrent()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session)) return null;
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\{session}\VirtualDesktops");
        return key?.GetValue("CurrentVirtualDesktop") as byte[];
    }

    /// <summary>True when the window is on the current desktop, or when that cannot be determined.</summary>
    public static bool IsOnCurrentDesktop(nint window)
    {
        if (_managerFailed) return true;
        try
        {
            _manager ??= (IVirtualDesktopManager)new VirtualDesktopManagerClass();
            return _manager.IsWindowOnCurrentVirtualDesktop(window, out var onCurrent) != 0 || onCurrent;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            AppLog.Warning($"IVirtualDesktopManager unavailable: {ex.Message}");
            _managerFailed = true;
            return true;
        }
    }

    public static string? Previous() => ShellShortcuts.SendChord(ShellShortcuts.VkControl, ShellShortcuts.VkLeftWindows, ShellShortcuts.VkLeft);
    public static string? Next() => ShellShortcuts.SendChord(ShellShortcuts.VkControl, ShellShortcuts.VkLeftWindows, ShellShortcuts.VkRight);
    public static string? Create() => ShellShortcuts.SendChord(ShellShortcuts.VkControl, ShellShortcuts.VkLeftWindows, ShellShortcuts.VkD);
    public static string? TaskView() => ShellShortcuts.SendChord(ShellShortcuts.VkLeftWindows, ShellShortcuts.VkTab);

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    private class VirtualDesktopManagerClass;

    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(nint topLevelWindow, [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
