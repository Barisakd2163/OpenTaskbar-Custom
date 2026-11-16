using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Opens Windows' own flyouts and settings pages, which stand in for the hidden taskbar's tray area.
/// </summary>
public static class ShellShortcuts
{
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkA = 0x41;
    private const ushort VkN = 0x4E;
    private const ushort VkP = 0x50;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;

    /// <summary>Win+A: Quick Settings (Windows 11) or Action Center (Windows 10).</summary>
    public static string? OpenQuickSettings() => SendWindowsShortcut(VkA);

    /// <summary>Win+N: notification center and calendar.</summary>
    public static string? OpenNotificationCenter() => SendWindowsShortcut(VkN);

    /// <summary>Win+P: display projection flyout.</summary>
    public static string? OpenProjection() => SendWindowsShortcut(VkP);

    public static string? OpenUri(string uri)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Warning($"Cannot open {uri}: {ex.Message}");
            return "Windows ayar sayfası açılamadı.";
        }
    }

    private static string? SendWindowsShortcut(ushort key)
    {
        var inputs = new[]
        {
            KeyInput(VkLeftWindows, 0),
            KeyInput(key, 0),
            KeyInput(key, KeyEventKeyUp),
            KeyInput(VkLeftWindows, KeyEventKeyUp)
        };
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length) return null;
        AppLog.Warning($"SendInput sent {sent}/{inputs.Length} events: {Marshal.GetLastPInvokeError()}");
        return "Windows kısayolu gönderilemedi.";
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);
}
