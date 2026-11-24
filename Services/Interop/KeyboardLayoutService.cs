using System.Globalization;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Reads the active Windows keyboard layout and switches input languages on the target application window (#156).
/// </summary>
public static class KeyboardLayoutService
{
    private const uint WmInputLangChangeRequest = 0x0050;

    public sealed record KeyboardLayoutInfo(nint Handle, string ShortCode, string DisplayName, string CultureName);

    public static IReadOnlyList<KeyboardLayoutInfo> GetInstalledLayouts()
    {
        var count = GetKeyboardLayoutList(0, null);
        if (count <= 0) return [];

        var handles = new nint[count];
        var actual = GetKeyboardLayoutList(count, handles);
        if (actual <= 0) return [];

        var seen = new HashSet<nint>();
        var layouts = new List<KeyboardLayoutInfo>(actual);
        for (var i = 0; i < Math.Min(actual, handles.Length); i++)
        {
            var handle = handles[i];
            if (handle == 0 || !seen.Add(handle)) continue;
            layouts.Add(DescribeHandle(handle));
        }
        return layouts;
    }

    public static KeyboardLayoutInfo GetActiveLayout(nint targetWindow = 0)
    {
        var hwnd = targetWindow != 0 && IsWindow(targetWindow) ? targetWindow : GetForegroundWindow();
        var threadId = hwnd != 0 ? GetWindowThreadProcessId(hwnd, out _) : 0u;
        var handle = GetKeyboardLayout(threadId);
        if (handle == 0) handle = GetKeyboardLayout(0);
        return DescribeHandle(handle);
    }

    /// <summary>
    /// Requests the target window (or current foreground window) to switch to <paramref name="layoutHandle"/>.
    /// </summary>
    public static bool SwitchLayout(nint targetWindow, nint layoutHandle)
    {
        if (layoutHandle == 0) return false;
        var hwnd = targetWindow != 0 && IsWindow(targetWindow) ? targetWindow : GetForegroundWindow();
        if (hwnd == 0) return false;
        return PostMessage(hwnd, WmInputLangChangeRequest, 0, layoutHandle);
    }

    internal static KeyboardLayoutInfo DescribeHandle(nint handle)
    {
        var langId = unchecked((int)(handle.ToInt64() & 0xFFFF));
        try
        {
            var culture = CultureInfo.GetCultureInfo(langId);
            var shortCode = FormatShortCode(culture);
            var displayName = FormatDisplayName(culture);
            return new KeyboardLayoutInfo(handle, shortCode, displayName, culture.Name);
        }
        catch (CultureNotFoundException)
        {
            return new KeyboardLayoutInfo(handle, "??", $"0x{langId:X4}", string.Empty);
        }
    }

    internal static string FormatShortCode(CultureInfo culture)
    {
        var iso = culture.TwoLetterISOLanguageName;
        if (string.IsNullOrWhiteSpace(iso) || iso.Equals("iv", StringComparison.OrdinalIgnoreCase))
            return "??";
        return iso.ToUpperInvariant();
    }

    internal static string FormatDisplayName(CultureInfo culture)
    {
        var native = string.IsNullOrWhiteSpace(culture.NativeName) ? culture.DisplayName : culture.NativeName;
        return culture.TextInfo.ToTitleCase(native);
    }

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int nBuff, [Out] nint[]? lpList);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}
