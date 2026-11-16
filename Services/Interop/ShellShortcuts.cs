using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>
/// Opens Windows' own flyouts and settings pages, which stand in for the hidden taskbar's tray area.
/// </summary>
public static class ShellShortcuts
{
    public const ushort VkLeftWindows = 0x5B;
    public const ushort VkControl = 0x11;
    public const ushort VkLeft = 0x25;
    public const ushort VkRight = 0x27;
    public const ushort VkTab = 0x09;
    public const ushort VkD = 0x44;
    private const ushort VkA = 0x41;
    private const ushort VkN = 0x4E;
    private const ushort VkP = 0x50;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtended = 0x0001;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int ErrorCancelled = 1223;

    private static bool IsExtended(ushort key) => key is VkLeft or VkRight or VkLeftWindows;

    /// <summary>Win+A: Quick Settings (Windows 11) or Action Center (Windows 10), anchored above the clicked button when given.</summary>
    public static string? OpenQuickSettings(FrameworkElement? anchor = null)
    {
        var error = SendWindowsShortcut(VkA);
        if (error is null && anchor is not null)
        {
            _ = RepositionControlCenterAsync(anchor);
        }
        return error;
    }

    /// <summary>Win+N: notification center and calendar.</summary>
    public static string? OpenNotificationCenter() => SendWindowsShortcut(VkN);

    /// <summary>Win+P: display projection flyout, anchored above the clicked button when given.</summary>
    public static string? OpenProjection(FrameworkElement? anchor = null)
    {
        var error = SendWindowsShortcut(VkP);
        if (error is null && anchor is not null)
        {
            _ = RepositionControlCenterAsync(anchor);
        }
        return error;
    }

    private static async Task RepositionControlCenterAsync(FrameworkElement anchor)
    {
        if (PresentationSource.FromVisual(anchor) is not HwndSource source || source.Handle == 0) return;
        Point topLeft;
        Point bottomRight;
        try
        {
            topLeft = anchor.PointToScreen(new Point(0, 0));
            bottomRight = anchor.PointToScreen(new Point(anchor.ActualWidth, anchor.ActualHeight));
        }
        catch (InvalidOperationException)
        {
            return;
        }

        var monitor = MonitorFromWindow(source.Handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref monitorInfo)) return;
        var edge = DockFlyout.Edge;
        var movedOnce = false;

        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(25);
            var hwnd = FindWindow("ControlCenterWindow", null);
            if (hwnd == 0 || !IsWindowVisible(hwnd) || !GetWindowRect(hwnd, out var rect)) continue;
            var width = rect.Right - rect.Left;
            var height = rect.Bottom - rect.Top;
            if (width < 80 || height < 80) continue;

            var centerX = (int)Math.Round((topLeft.X + bottomRight.X) / 2);
            var centerY = (int)Math.Round((topLeft.Y + bottomRight.Y) / 2);
            int targetX;
            int targetY;
            switch (edge)
            {
                case DockEdge.Left:
                    targetX = (int)Math.Round(bottomRight.X) + 12;
                    targetY = Math.Clamp(centerY - height / 2, monitorInfo.Work.Top + 8, monitorInfo.Work.Bottom - height - 8);
                    break;
                case DockEdge.Right:
                    targetX = (int)Math.Round(topLeft.X) - width - 12;
                    targetY = Math.Clamp(centerY - height / 2, monitorInfo.Work.Top + 8, monitorInfo.Work.Bottom - height - 8);
                    break;
                default:
                    targetX = Math.Clamp(centerX - width / 2, monitorInfo.Work.Left + 8, monitorInfo.Work.Right - width - 8);
                    targetY = Math.Max(monitorInfo.Work.Top + 8, (int)Math.Round(topLeft.Y) - height - 12);
                    break;
            }

            SetWindowPos(hwnd, 0, targetX, targetY, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
            if (movedOnce) break;
            movedOnce = true;
            // Re-apply once more after ShellHost finishes its entrance animation.
            await Task.Delay(75);
        }
    }

    public static string? OpenUri(string uri) => Start(uri, "ErrorSettingsPageOpen");

    /// <summary>
    /// Task Manager by its full path, so a program named taskmgr elsewhere on the search path is never started.
    /// The Shell handles its elevation request.
    /// </summary>
    public static string? OpenTaskManager() => Start(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Taskmgr.exe"), "ErrorTaskManagerOpen");

    private static string? Start(string target, string failureKey)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return null;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            // The user declined the elevation prompt; that is a choice, not a failure.
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Warning($"Cannot open {target}: {ex.Message}");
            return UiText.Get(failureKey);
        }
    }

    private static string? SendWindowsShortcut(ushort key) => SendChord(VkLeftWindows, key);

    /// <summary>Presses the keys in order and releases them in reverse, like a user typing the chord.</summary>
    public static string? SendChord(params ushort[] keys)
    {
        var inputs = keys.Select(key => KeyInput(key, IsExtended(key) ? KeyEventExtended : 0))
            .Concat(keys.Reverse().Select(key => KeyInput(key, KeyEventKeyUp | (IsExtended(key) ? KeyEventExtended : 0))))
            .ToArray();
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return null;
        AppLog.Warning($"SendInput sent {sent}/{inputs.Length} events: {Marshal.GetLastPInvokeError()}");
        return UiText.Get("ErrorShortcutSend");
    }

    private static Input KeyInput(ushort key, uint flags) => new()
    {
        Type = InputKeyboard,
        Keyboard = new KeyboardInput { VirtualKey = key, Flags = flags }
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public KeyboardInput Keyboard;
        // Pads the union to the size of MOUSEINPUT, the largest member.
        private readonly long _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hWnd, out NativeRect lpRect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
