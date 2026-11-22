using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

internal static class SettingsBackdrop
{
    public static bool TryEnable(Window window, AppTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
            return false;
        var dark = theme == AppTheme.Dark ? 1 : 0;
        var backdrop = 3; // DWMSBT_TRANSIENTWINDOW: Desktop Acrylic on supported Windows 11 builds.
        var corners = 2;
        var darkResult = DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        var backdropResult = DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        var margins = new Margins(-1);
        var frameResult = DwmExtendFrameIntoClientArea(handle, ref margins);
        if (darkResult < 0 || backdropResult < 0 || frameResult < 0)
        {
            Trace.TraceWarning("Settings backdrop unavailable: theme=0x{0:X8}, backdrop=0x{1:X8}, frame=0x{2:X8}", darkResult, backdropResult, frameResult);
            return false;
        }
        var cornerResult = DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        if (cornerResult < 0) Trace.TraceWarning("Settings window corners unavailable: 0x{0:X8}", cornerResult);
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        return true;
    }

    /// <summary>
    /// Disables the acrylic blur (DWMSBT_NONE = 1) while keeping the DWM frame extended into the
    /// client area so a transparent WPF surface reveals the desktop behind the Settings window.
    /// </summary>
    public static bool TryEnableTransparent(Window window, AppTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0 || SystemParameters.HighContrast) return false;
        var dark = theme == AppTheme.Dark ? 1 : 0;
        var backdrop = 1; // DWMSBT_NONE (0 is DWMSBT_AUTO)
        var corners = 2;
        DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        DwmSetWindowAttribute(handle, 33, ref corners, sizeof(int));
        var margins = new Margins(-1);
        var frameResult = DwmExtendFrameIntoClientArea(handle, ref margins);
        if (frameResult < 0) return false;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        return true;
    }

    /// <summary>Removes the native acrylic and extended frame for an opaque fallback surface.</summary>
    public static void Disable(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var backdrop = 1; // DWMSBT_NONE
        DwmSetWindowAttribute(handle, 38, ref backdrop, sizeof(int));
        var margins = new Margins(0);
        DwmExtendFrameIntoClientArea(handle, ref margins);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int value)
    {
        public int Left = value;
        public int Right = value;
        public int Top = value;
        public int Bottom = value;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
}
