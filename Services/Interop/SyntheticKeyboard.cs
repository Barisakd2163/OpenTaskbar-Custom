using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Sends keyboard chords to the shell with guaranteed modifier release.
///
/// A previous implementation pushed all keydowns and keyups into one SendInput batch and only logged a partial
/// result. If a secure-desktop transition (Win+L) or focus change happened while the batch was being processed,
/// the Win keyup could be lost, leaving Win logically down. The user's next ordinary keystroke was then treated
/// by Windows as Win+I (Settings), Win+E (Explorer), etc. — with no Settings code in our own process involved.
///
/// Here every modifier we press is tracked, released in a finally block, and released again on session unlock
/// if the OS still reports it as down.
/// </summary>
internal static class SyntheticKeyboard
{
    private const ushort VkShift = 0x10;
    private const ushort VkAlt = 0x12;
    private const ushort VkControl = 0x11;
    private const ushort VkRightWindows = 0x5C;
    private const ushort VkLeftWindows = 0x5B;
    private const uint InputKeyboard = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventExtended = 0x0001;
    private const uint KeyEventUnicode = 0x0004;
    private const int ChordHoldMs = 20;

    // Released last first, so Win is the final modifier to go up.
    private static readonly ushort[] Modifiers = [VkShift, VkAlt, VkControl, VkRightWindows, VkLeftWindows];
    private static readonly object Gate = new();
    private static readonly HashSet<ushort> HeldModifiers = [];

    internal static IReadOnlyCollection<ushort> TrackedModifiers
    {
        get { lock (Gate) return HeldModifiers.ToArray(); }
    }

    internal static string? SendChord(params ushort[] keys)
    {
        if (keys.Length == 0) return null;

        var owned = keys.Where(IsModifier).Distinct().ToArray();
        string? error = null;
        try
        {
            foreach (var key in keys)
            {
                if (!SendKey(key, up: false))
                {
                    error = Failure(key, down: true);
                    break;
                }

                if (IsModifier(key))
                {
                    lock (Gate) HeldModifiers.Add(key);
                }
            }

            if (error is null)
            {
                // Give the shell a moment to observe the chord before the first release.
                Thread.Sleep(ChordHoldMs);
                for (var i = keys.Length - 1; i >= 0; i--)
                {
                    if (!SendKey(keys[i], up: true))
                    {
                        error = Failure(keys[i], down: false);
                        break;
                    }

                    if (IsModifier(keys[i]))
                    {
                        lock (Gate) HeldModifiers.Remove(keys[i]);
                    }
                }
            }
        }
        finally
        {
            // Even a partially sent chord must not leave a modifier held down.
            foreach (var key in owned.Reverse())
            {
                bool release;
                lock (Gate) release = HeldModifiers.Remove(key);
                if (release) SendKey(key, up: true);
            }
        }

        return error;
    }

    /// <summary>Called when the interactive desktop returns, so a modifier lost during a lock transition cannot leak into the next keystroke.</summary>
    internal static void ReleaseAll()
    {
        var keys = new HashSet<ushort>();
        lock (Gate)
        {
            keys.UnionWith(HeldModifiers);
            HeldModifiers.Clear();
        }

        foreach (var key in Modifiers)
        {
            if (GetAsyncKeyState(key) < 0) keys.Add(key);
        }

        foreach (var key in Modifiers.Reverse())
        {
            if (keys.Contains(key)) SendKey(key, up: true);
        }
    }

    // The caller verifies that the Search editor is focused. Send one batch so physical keystrokes
    // cannot interleave between selecting the existing value and typing its replacement.
    internal static bool ReplaceFocusedText(string text)
    {
        if (Modifiers.Any(key => GetAsyncKeyState(key) < 0)) return false;
        const ushort selectAll = 0x41;
        const ushort delete = 0x2E;
        var inputs = new List<Input>(4 + Math.Max(2, text.Length * 2))
        {
            CreateKeyInput(VkControl, 0),
            CreateKeyInput(selectAll, 0),
            CreateKeyInput(selectAll, KeyEventKeyUp),
            CreateKeyInput(VkControl, KeyEventKeyUp)
        };
        if (text.Length == 0)
        {
            inputs.Add(CreateKeyInput(delete, KeyEventExtended));
            inputs.Add(CreateKeyInput(delete, KeyEventExtended | KeyEventKeyUp));
        }
        foreach (var character in text)
        {
            inputs.Add(CreateKeyInput(0, KeyEventUnicode, character));
            inputs.Add(CreateKeyInput(0, KeyEventUnicode | KeyEventKeyUp, character));
        }

        var batch = inputs.ToArray();
        var sent = SendInput((uint)batch.Length, batch, Marshal.SizeOf<Input>());
        if (sent is > 0 and < 4)
        {
            if (sent == 2) SendKey(selectAll, up: true);
            SendKey(VkControl, up: true);
        }
        return sent == batch.Length;
    }

    internal static bool SendFocusedNavigationKey(ushort key)
    {
        var flags = key is >= 0x21 and <= 0x28 ? KeyEventExtended : 0;
        var up = CreateKeyInput(key, flags | KeyEventKeyUp);
        var size = Marshal.SizeOf<Input>();
        var sent = SendInput(2, [CreateKeyInput(key, flags), up], size);
        return sent == 2 || (sent == 1 && SendInput(1, [up], size) == 1);
    }

    private static string Failure(ushort key, bool down)
    {
        AppLog.Warning($"Synthetic keyboard: {key:X2} {(down ? "down" : "up")} failed: {Marshal.GetLastPInvokeError()}");
        return UiText.Get("ErrorShortcutSend");
    }

    private static bool IsModifier(ushort key) => key is VkShift or VkAlt or VkControl or VkRightWindows or VkLeftWindows;

    private static bool IsExtended(ushort key) => key is VkLeftWindows or VkRightWindows;

    private static bool SendKey(ushort key, bool up)
    {
        var input = CreateKeyInput(key, (up ? KeyEventKeyUp : 0) | (IsExtended(key) ? KeyEventExtended : 0));
        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    private static Input CreateKeyInput(ushort key, uint flags, ushort scanCode = 0) =>
        new()
        {
            Type = InputKeyboard,
            Data = new InputData
            {
                Keyboard = new KeyboardInput { VirtualKey = key, ScanCode = scanCode, Flags = flags }
            }
        };

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X, Y;
        public uint Data, Flags, Time;
        public nint ExtraInfo;
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

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);
}
