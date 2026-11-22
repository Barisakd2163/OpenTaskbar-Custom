using System.Windows.Input;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>
/// Represents a validated global hotkey combination (Win32 modifiers + virtual key + canonical display text).
/// MOD_WIN is never permitted because lost Win key-up events across lock/secure-desktop transitions can leave
/// Windows thinking Win is stuck.
/// </summary>
public readonly record struct HotkeyBinding(uint Modifiers, uint VirtualKey, string DisplayText)
{
    public const uint ModAlt = 0x1;
    public const uint ModControl = 0x2;
    public const uint ModShift = 0x4;

    private const uint VkEscape = 0x1B;
    private const uint VkSnapshot = 0x2C;
    private const uint VkShift = 0x10;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkLWin = 0x5B;
    private const uint VkRWin = 0x5C;
    private const uint VkLShift = 0xA0;
    private const uint VkRMenu = 0xA5;
    private const uint VkSpace = 0x20;

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (string.Equals(trimmed, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;

        if (!TryTokenize(trimmed, out var tokens) || tokens.Count < 2) return false;

        uint modifiers = 0;
        string? keyToken = null;

        foreach (var token in tokens)
        {
            if (string.Equals(token, "Ctrl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(token, "Control", StringComparison.OrdinalIgnoreCase))
            {
                if ((modifiers & ModControl) != 0) return false;
                modifiers |= ModControl;
                continue;
            }

            if (string.Equals(token, "Alt", StringComparison.OrdinalIgnoreCase))
            {
                if ((modifiers & ModAlt) != 0) return false;
                modifiers |= ModAlt;
                continue;
            }

            if (string.Equals(token, "Shift", StringComparison.OrdinalIgnoreCase))
            {
                if ((modifiers & ModShift) != 0) return false;
                modifiers |= ModShift;
                continue;
            }

            if (IsWinToken(token)) return false;

            // Reject duplicate non-modifier keys (e.g. "Ctrl+A+B").
            if (keyToken is not null) return false;
            keyToken = token;
        }

        if (keyToken is null) return false;
        if ((modifiers & (ModAlt | ModControl)) == 0) return false;

        if (!TryParseKeyToken(keyToken, out var key)) return false;
        if (IsDisallowedKey(key)) return false;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!IsValidVirtualKey(vk)) return false;

        var canonicalKey = KeyInterop.KeyFromVirtualKey((int)vk);
        if (IsDisallowedKey(canonicalKey)) return false;
        if (IsSystemReserved(modifiers, vk)) return false;

        var displayText = FormatDisplayText(modifiers, canonicalKey);
        binding = new HotkeyBinding(modifiers, vk, displayText);
        return true;
    }

    public static bool TryFromKeyEvent(ModifierKeys modifiers, Key key, out HotkeyBinding binding, out string? errorKey)
    {
        binding = default;
        errorKey = null;

        if (IsIgnoredOrModifierKey(key))
            return false;

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            errorKey = "HotkeyWinNotAllowed";
            return false;
        }

        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == 0)
        {
            errorKey = "HotkeyModifierRequired";
            return false;
        }

        if (IsDisallowedKey(key))
            return false;

        var vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (!IsValidVirtualKey(vk))
            return false;

        var canonicalKey = KeyInterop.KeyFromVirtualKey((int)vk);
        if (IsDisallowedKey(canonicalKey))
            return false;

        uint winModifiers = 0;
        if ((modifiers & ModifierKeys.Control) != 0) winModifiers |= ModControl;
        if ((modifiers & ModifierKeys.Alt) != 0) winModifiers |= ModAlt;
        if ((modifiers & ModifierKeys.Shift) != 0) winModifiers |= ModShift;

        if (IsSystemReserved(winModifiers, vk)) return false;

        var displayText = FormatDisplayText(winModifiers, canonicalKey);
        binding = new HotkeyBinding(winModifiers, vk, displayText);
        return true;
    }

    public static HotkeyBinding? FromChoice(HotkeyChoice choice) => choice switch
    {
        HotkeyChoice.AltSpace => new HotkeyBinding(ModAlt, 0x20, "Alt+Space"),
        HotkeyChoice.CtrlSpace => new HotkeyBinding(ModControl, 0x20, "Ctrl+Space"),
        HotkeyChoice.CtrlAltSpace => new HotkeyBinding(ModControl | ModAlt, 0x20, "Ctrl+Alt+Space"),
        HotkeyChoice.CtrlAltD => new HotkeyBinding(ModControl | ModAlt, 0x44, "Ctrl+Alt+D"),
        _ => null
    };

    public static HotkeyBinding? Resolve(HotkeyChoice legacyChoice, string? customText)
    {
        if (string.Equals(customText, "Disabled", StringComparison.OrdinalIgnoreCase))
            return null;

        if (!string.IsNullOrWhiteSpace(customText))
            return TryParse(customText, out var parsed) ? parsed : null;

        return FromChoice(legacyChoice);
    }

    public static bool IsValidCustomString(string? customText) =>
        customText is null
        || string.Equals(customText, "Disabled", StringComparison.OrdinalIgnoreCase)
        || TryParse(customText, out _);

    public static bool EqualsBinding(HotkeyBinding? a, HotkeyBinding? b)
    {
        if (a is null || b is null) return false;
        return a.Value.Modifiers == b.Value.Modifiers && a.Value.VirtualKey == b.Value.VirtualKey;
    }

    private static bool TryTokenize(string trimmed, out List<string> tokens)
    {
        tokens = [];
        var i = 0;
        while (i < trimmed.Length)
        {
            while (i < trimmed.Length && char.IsWhiteSpace(trimmed[i])) i++;
            if (i >= trimmed.Length) return false;

            // A single '+' at the very end represents the '+' key itself (e.g. "Ctrl++").
            if (i == trimmed.Length - 1 && trimmed[i] == '+')
            {
                tokens.Add("+");
                break;
            }

            var plus = trimmed.IndexOf('+', i);
            if (plus < 0)
            {
                var part = trimmed[i..].Trim();
                if (part.Length == 0) return false;
                tokens.Add(part);
                break;
            }

            var token = trimmed[i..plus].Trim();
            if (token.Length == 0) return false;
            tokens.Add(token);
            i = plus + 1;
            if (i == trimmed.Length) return false;
        }

        return tokens.Count > 0;
    }

    private static bool IsWinToken(string token) =>
        string.Equals(token, "Win", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "Windows", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "LWin", StringComparison.OrdinalIgnoreCase)
        || string.Equals(token, "RWin", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredOrModifierKey(Key key) => key is
        Key.None or Key.DeadCharProcessed or Key.ImeProcessed or Key.System
        or Key.LeftCtrl or Key.RightCtrl
        or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift
        or Key.LWin or Key.RWin;

    private static bool IsDisallowedKey(Key key) =>
        IsIgnoredOrModifierKey(key)
        || key is Key.Escape or Key.Snapshot;

    private static bool IsValidVirtualKey(uint vk) =>
        vk is > 0 and <= 0xFE
        && vk != VkEscape
        && vk != VkSnapshot
        && vk != VkShift
        && vk != VkControl
        && vk != VkMenu
        && vk != VkLWin
        && vk != VkRWin
        && !(vk >= VkLShift && vk <= VkRMenu);

    /// <summary>
    /// Windows reserves Alt+Space (the window system menu) and Ctrl+Alt+Del (secure desktop).
    /// Registering them globally can steal OS behavior or silently fail, so reject them up front.
    /// </summary>
    private static bool IsSystemReserved(uint modifiers, uint vk) =>
        (modifiers == ModAlt && vk == VkSpace)
        || ((modifiers & (ModControl | ModAlt)) == (ModControl | ModAlt) && vk == 0x2E); // Ctrl+Alt+Del

    private static bool TryParseKeyToken(string token, out Key key)
    {
        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z')
            {
                key = Key.A + (ch - 'A');
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                key = Key.D0 + (ch - '0');
                return true;
            }

            key = ch switch
            {
                '+' => Key.OemPlus,
                '-' => Key.OemMinus,
                ',' => Key.OemComma,
                '.' => Key.OemPeriod,
                '/' => Key.OemQuestion,
                ';' => Key.OemSemicolon,
                '\'' => Key.OemQuotes,
                '[' => Key.OemOpenBrackets,
                ']' => Key.OemCloseBrackets,
                '\\' => Key.OemPipe,
                '`' => Key.OemTilde,
                _ => Key.None
            };
            return key != Key.None;
        }

        if (string.Equals(token, "Space", StringComparison.OrdinalIgnoreCase)) { key = Key.Space; return true; }
        if (string.Equals(token, "Enter", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Return", StringComparison.OrdinalIgnoreCase)) { key = Key.Return; return true; }
        if (string.Equals(token, "Tab", StringComparison.OrdinalIgnoreCase)) { key = Key.Tab; return true; }
        if (string.Equals(token, "Backspace", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Back", StringComparison.OrdinalIgnoreCase)) { key = Key.Back; return true; }
        if (string.Equals(token, "PageUp", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Prior", StringComparison.OrdinalIgnoreCase)) { key = Key.PageUp; return true; }
        if (string.Equals(token, "PageDown", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "Next", StringComparison.OrdinalIgnoreCase)) { key = Key.PageDown; return true; }
        if (string.Equals(token, "Plus", StringComparison.OrdinalIgnoreCase)) { key = Key.OemPlus; return true; }
        if (string.Equals(token, "Minus", StringComparison.OrdinalIgnoreCase)) { key = Key.OemMinus; return true; }
        if (string.Equals(token, "Comma", StringComparison.OrdinalIgnoreCase)) { key = Key.OemComma; return true; }
        if (string.Equals(token, "Period", StringComparison.OrdinalIgnoreCase)) { key = Key.OemPeriod; return true; }

        // Reject raw numeric strings so Enum.TryParse does not accept arbitrary enum integer values.
        if (char.IsDigit(token[0]) || token[0] == '-')
        {
            key = Key.None;
            return false;
        }

        if (Enum.TryParse<Key>(token, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            key = parsed;
            return true;
        }

        key = Key.None;
        return false;
    }

    private static string FormatDisplayText(uint modifiers, Key key)
    {
        var parts = new List<string>(4);
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        parts.Add(FormatKeyName(key));
        return string.Join('+', parts);
    }

    private static string FormatKeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => ((char)('A' + (key - Key.A))).ToString(),
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.F1 and <= Key.F24 => $"F{1 + (key - Key.F1)}",
        >= Key.NumPad0 and <= Key.NumPad9 => $"NumPad{key - Key.NumPad0}",
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.Tab => "Tab",
        Key.Back => "Backspace",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.OemPlus => "+",
        Key.OemMinus => "-",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        _ => key.ToString()
    };
}
