using System.Globalization;

namespace OpenTaskbar.Services;

/// <summary>
/// Stable, pure identity helpers for pinning background system tray icons to the dock.
/// Prefers <c>guid:&lt;D&gt;</c> when the icon supplies a non-empty GUID; otherwise falls back to
/// <c>path:&lt;normalized-path&gt;|&lt;uid&gt;</c> so multiple tray icons from the same executable stay distinct.
/// Registry-only fallback entries use <c>path:&lt;normalized-path&gt;</c> and match live icons for that executable.
/// </summary>
public static class TrayPinIdentity
{
    public const int MaxPinnedIcons = 64;
    public const int MaxKeyLength = 512;

    private const string GuidPrefix = "guid:";
    private const string PathPrefix = "path:";

    /// <summary>Builds the canonical key for a live tray icon, or <see cref="string.Empty"/> when unpinnable.</summary>
    public static string KeyFor(Guid guid, string? path, uint uid)
    {
        if (guid != Guid.Empty)
            return GuidPrefix + guid.ToString("D");

        var pathKey = KeyForPath(path);
        return pathKey.Length == 0
            ? string.Empty
            : pathKey + "|" + uid.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Builds the path-only fallback key for registry-enumerated tray apps, or <see cref="string.Empty"/>.</summary>
    public static string KeyForPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return PathPrefix + path.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Returns true when a stored <paramref name="pinKey"/> matches the live tray icon.
    /// Matches exact <see cref="KeyFor"/> identity, exact <c>path:&lt;lower&gt;|&lt;uid&gt;</c>, or fallback
    /// <c>path:&lt;lower&gt;</c> / <c>path:&lt;lower&gt;|*</c> keys while keeping different UIDs and GUIDs distinct.
    /// </summary>
    public static bool Matches(string? pinKey, Guid guid, string? path, uint uid)
    {
        if (string.IsNullOrWhiteSpace(pinKey)) return false;
        var trimmed = pinKey.Trim();

        var exact = KeyFor(guid, path, uid);
        if (exact.Length > 0 && string.Equals(trimmed, exact, StringComparison.OrdinalIgnoreCase))
            return true;

        var pathKey = KeyForPath(path);
        if (pathKey.Length == 0) return false;

        var pathWithUid = pathKey + "|" + uid.ToString(CultureInfo.InvariantCulture);
        if (string.Equals(trimmed, pathWithUid, StringComparison.OrdinalIgnoreCase))
            return true;

        return string.Equals(trimmed, pathKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, pathKey + "|*", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Returns true when a stored <paramref name="pinKey"/> matches a registry-fallback executable path.</summary>
    public static bool MatchesPath(string? pinKey, string? path)
    {
        if (string.IsNullOrWhiteSpace(pinKey)) return false;
        var pathKey = KeyForPath(path);
        if (pathKey.Length == 0) return false;

        var trimmed = pinKey.Trim();
        if (string.Equals(trimmed, pathKey, StringComparison.OrdinalIgnoreCase))
            return true;

        return trimmed.Length > pathKey.Length
            && trimmed.StartsWith(pathKey, StringComparison.OrdinalIgnoreCase)
            && trimmed[pathKey.Length] == '|';
    }

    /// <summary>Returns true when the live tray icon matches any entry in <paramref name="pinned"/>.</summary>
    public static bool IsPinned(IReadOnlyList<string>? pinned, Guid guid, string? path, uint uid) =>
        FindPinIndex(pinned, guid, path, uid) >= 0;

    /// <summary>Returns true when the fallback executable path matches any entry in <paramref name="pinned"/>.</summary>
    public static bool IsPathPinned(IReadOnlyList<string>? pinned, string? path) =>
        FindMatchingPathKey(pinned, path) is not null;

    /// <summary>
    /// Returns the earliest index in <paramref name="pinned"/> matching the live icon, or <c>-1</c> if unpinned.
    /// </summary>
    public static int FindPinIndex(IReadOnlyList<string>? pinned, Guid guid, string? path, uint uid)
    {
        if (pinned is not { Count: > 0 }) return -1;
        for (var i = 0; i < pinned.Count; i++)
        {
            if (Matches(pinned[i], guid, path, uid))
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Resolves the stored key matching a live tray icon (preferring exact <see cref="KeyFor"/> match before fallback
    /// matches) so unpinning from the flyout removes the actual stored entry.
    /// </summary>
    public static string? FindMatchingKey(IReadOnlyList<string>? pinned, Guid guid, string? path, uint uid)
    {
        if (pinned is not { Count: > 0 }) return null;

        var exact = KeyFor(guid, path, uid);
        if (exact.Length > 0)
        {
            for (var i = 0; i < pinned.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(pinned[i])
                    && string.Equals(pinned[i].Trim(), exact, StringComparison.OrdinalIgnoreCase))
                {
                    return pinned[i];
                }
            }
        }

        for (var i = 0; i < pinned.Count; i++)
        {
            if (Matches(pinned[i], guid, path, uid))
                return pinned[i];
        }

        return null;
    }

    /// <summary>Resolves the stored key matching a fallback executable path, or <c>null</c> if unpinned.</summary>
    public static string? FindMatchingPathKey(IReadOnlyList<string>? pinned, string? path)
    {
        if (pinned is not { Count: > 0 }) return null;
        var exactPath = KeyForPath(path);
        if (exactPath.Length == 0) return null;

        for (var i = 0; i < pinned.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(pinned[i])
                && string.Equals(pinned[i].Trim(), exactPath, StringComparison.OrdinalIgnoreCase))
            {
                return pinned[i];
            }
        }

        for (var i = 0; i < pinned.Count; i++)
        {
            if (MatchesPath(pinned[i], path))
                return pinned[i];
        }

        return null;
    }

    /// <summary>
    /// Toggles <paramref name="key"/> in <paramref name="pinned"/> (case-insensitively), deduplicating valid entries
    /// and enforcing <see cref="MaxPinnedIcons"/>. Returns <c>null</c> when the resulting list is empty.
    /// </summary>
    public static IReadOnlyList<string>? TogglePin(IReadOnlyList<string>? pinned, string? key)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (pinned is not null)
        {
            foreach (var entry in pinned)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var candidate = entry.Trim();
                if (candidate.Length > MaxKeyLength) continue;
                if (seen.Add(candidate) && normalized.Count < MaxPinnedIcons)
                {
                    normalized.Add(candidate);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(key))
            return normalized.Count == 0 ? null : normalized;

        var trimmedKey = key.Trim();
        if (trimmedKey.Length > MaxKeyLength)
            return normalized.Count == 0 ? null : normalized;

        var existingIndex = normalized.FindIndex(entry =>
            string.Equals(entry, trimmedKey, StringComparison.OrdinalIgnoreCase));

        if (existingIndex >= 0)
        {
            normalized.RemoveAt(existingIndex);
        }
        else if (normalized.Count < MaxPinnedIcons)
        {
            normalized.Add(trimmedKey);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    /// <summary>Extracts a clean single-line display name for a tray icon tooltip/automation label.</summary>
    public static string CleanTitle(string? rawTitle, string? path, string fallback)
    {
        var title = rawTitle?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?.Trim();
        if (!string.IsNullOrWhiteSpace(title)) return title;
        if (!string.IsNullOrWhiteSpace(path))
        {
            var fileName = System.IO.Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(fileName)) return fileName;
        }
        return fallback;
    }
}
