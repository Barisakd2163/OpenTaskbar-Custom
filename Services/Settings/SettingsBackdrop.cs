using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

internal static class SettingsBackdrop
{
    internal const uint DwmwaUseImmersiveDarkMode = 20;
    internal const uint DwmwaWindowCornerPreference = 33;
    internal const uint DwmwaBorderColor = 34;
    internal const uint DwmwaSystemBackdropType = 38;
    internal const int DwmwcpRound = 2;
    internal const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);
    internal const int DwmsbtNone = 1;
    internal const int DwmsbtTransientWindow = 3;
    internal const double NativeCornerRadius = 8;

    internal static bool SupportsNativeRoundedCorners => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static bool TryEnable(Window window, AppTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;
        // Suppress the native border regardless of whether acrylic is available: on Win11 21H2 or
        // High Contrast the DWM still rounds the window, so the WPF overlay must own the only rim.
        SuppressNativeBorder(handle);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) || SystemParameters.HighContrast)
            return false;
        var dark = theme == AppTheme.Dark ? 1 : 0;
        var backdrop = DwmsbtTransientWindow; // Desktop Acrylic on supported Windows 11 builds.
        var corners = DwmwcpRound;
        var darkResult = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        var backdropResult = DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        var margins = new Margins(-1);
        var frameResult = DwmExtendFrameIntoClientArea(handle, ref margins);
        if (darkResult < 0 || backdropResult < 0 || frameResult < 0)
        {
            AppLog.Warning($"Settings backdrop unavailable: theme=0x{darkResult:X8}, backdrop=0x{backdropResult:X8}, frame=0x{frameResult:X8}");
            return false;
        }
        var cornerResult = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
        if (cornerResult < 0) AppLog.Warning($"Settings window corners unavailable: 0x{cornerResult:X8}");
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
        if (handle == 0) return false;
        SuppressNativeBorder(handle);
        if (SystemParameters.HighContrast) return false;
        var dark = theme == AppTheme.Dark ? 1 : 0;
        var backdrop = DwmsbtNone;
        var corners = DwmwcpRound;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
        var margins = new Margins(-1);
        var frameResult = DwmExtendFrameIntoClientArea(handle, ref margins);
        if (frameResult < 0) return false;
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        return true;
    }

    /// <summary>Removes the native acrylic and extended frame for an opaque fallback surface.</summary>
    public static void Disable(Window window, AppTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        var dark = theme == AppTheme.Dark ? 1 : 0;
        var backdrop = DwmsbtNone;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        if (SupportsNativeRoundedCorners)
        {
            var corners = DwmwcpRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));
            SuppressNativeBorder(handle);
        }
        var margins = new Margins(0);
        DwmExtendFrameIntoClientArea(handle, ref margins);
        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target)
        {
            target.BackgroundColor = window.TryFindResource("SettingsBackground") is SolidColorBrush brush
                ? Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B)
                : theme == AppTheme.Dark
                    ? Color.FromRgb(0x18, 0x1A, 0x20)
                    : Color.FromRgb(0xF3, 0xF5, 0xF8);
        }
    }

    private static void SuppressNativeBorder(nint handle)
    {
        if (!SupportsNativeRoundedCorners) return;
        // Clear the native Windows 11 1px perimeter border so the WPF SettingsBorderOverlay owns the outer rim.
        var noBorder = DwmwaColorNone;
        var result = DwmSetWindowAttribute(handle, DwmwaBorderColor, ref noBorder, sizeof(int));
        if (result < 0) AppLog.Warning($"Settings window border suppression unavailable: 0x{result:X8}");
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
