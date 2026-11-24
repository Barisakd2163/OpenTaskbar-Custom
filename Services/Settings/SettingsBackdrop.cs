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
    private const int WcaAccentPolicy = 19;
    private const int AccentDisabled = 0;
    private const int AccentEnableAcrylicBlurBehind = 4;
    private const uint AccentFlagDrawAllBorders = 0x20 | 0x40 | 0x80 | 0x100;

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
    /// Enables frosted Acrylic blur via <c>SetWindowCompositionAttribute</c> with a near-zero DWM tint so
    /// WPF's <c>SettingsGlassMaterial</c> controls the exact theme hue and user opacity slider while keeping
    /// the blur active even when the Settings window loses focus.
    /// </summary>
    public static bool TryEnableAcrylic(Window window, AppTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return false;
        SuppressNativeBorder(handle);
        if (SystemParameters.HighContrast) return false;

        var dark = theme == AppTheme.Dark ? 1 : 0;
        var backdrop = DwmsbtNone;
        var corners = DwmwcpRound;
        DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        // Keep Win11's auto-disabling system backdrop off so it does not override the acrylic policy on blur/focus.
        DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
        if (SupportsNativeRoundedCorners)
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corners, sizeof(int));

        var margins = new Margins(-1);
        if (DwmExtendFrameIntoClientArea(handle, ref margins) < 0) return false;

        // 1/255 alpha keeps DWM acrylic blur active while letting WPF's SettingsGlassMaterial own color & opacity.
        if (!TrySetAccentPolicy(handle, AccentEnableAcrylicBlurBehind, gradientColor: 0x01000000))
            return false;

        if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
        return true;
    }

    /// <summary>Removes the native acrylic and extended frame for an opaque fallback surface.</summary>
    public static void Disable(Window window, AppTheme theme)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        TrySetAccentPolicy(handle, AccentDisabled, gradientColor: 0);
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

    private static bool TrySetAccentPolicy(nint handle, int accentState, uint gradientColor)
    {
        var policy = new AccentPolicy
        {
            AccentState = accentState,
            AccentFlags = accentState == AccentDisabled ? 0u : 2u,
            GradientColor = gradientColor,
            AnimationId = 0
        };
        var size = Marshal.SizeOf<AccentPolicy>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, ptr, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = ptr,
                SizeOfData = size
            };
            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public uint AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public nint Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, ref int value, uint size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint window, ref Margins margins);
    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);
}
