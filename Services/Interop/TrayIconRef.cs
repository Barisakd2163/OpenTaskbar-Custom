using System.Globalization;
using System.Text.Json;

namespace OpenTaskbar.Services;

/// <summary>Where a tray icon identity was discovered.</summary>
public enum IconSource
{
    Unknown = 0,
    Live = 1,
    Registry = 2
}

/// <summary>Shape of a stored pin key, as understood by <see cref="TrayPinIdentity"/>.</summary>
public enum PinKeyKind
{
    Unknown = 0,
    Guid = 1,
    Path = 2,
    Structured = 3
}

/// <summary>
/// Stable, value-typed identity for a tray icon: an optional 128-bit GUID, a normalized executable path and a
/// numeric UID. The legacy <c>guid:</c> / <c>path:</c> strings remain the persisted representation; this type is
/// the in-memory currency used for deduplication and for migrating future structured JSON pin entries.
/// </summary>
public readonly record struct TrayIconRef
{
    /// <param name="guid">Non-empty GUID supplied by the icon, or <c>null</c> for path/UID identities.</param>
    /// <param name="normalizedPath">Executable path; normalized to lowercase by <see cref="TrayPinIdentity.NormalizePath"/>.</param>
    /// <param name="uid">Classic notification-area <c>uID</c>, or 0 when unknown.</param>
    /// <param name="source">Which snapshot produced the identity (ignored by equality).</param>
    public TrayIconRef(Guid? guid, string? normalizedPath, uint uid, IconSource source)
    {
        Guid = guid is { } value && value != System.Guid.Empty ? value : null;
        NormalizedPath = TrayPinIdentity.NormalizePath(normalizedPath);
        Uid = uid;
        Source = source;
    }

    public Guid? Guid { get; }

    public string NormalizedPath { get; }

    public uint Uid { get; }

    public IconSource Source { get; }

    public bool IsPinnable => Guid.HasValue || !string.IsNullOrEmpty(NormalizedPath);

    /// <summary>Legacy string key written to settings; identical to <see cref="TrayPinIdentity.KeyFor"/>.</summary>
    public string Key => TrayPinIdentity.KeyFor(Guid ?? System.Guid.Empty, NormalizedPath, Uid);

    public PinKeyKind KeyKind => Guid.HasValue ? PinKeyKind.Guid
        : !string.IsNullOrEmpty(NormalizedPath) ? PinKeyKind.Path
        : PinKeyKind.Unknown;

    /// <summary>Alias for <see cref="KeyKind"/>.</summary>
    public PinKeyKind Kind => KeyKind;

    /// <summary>Positional deconstruction, matching the value-tuple shape used for identity comparisons.</summary>
    public void Deconstruct(out Guid? guid, out string normalizedPath, out uint uid, out IconSource source)
    {
        guid = Guid;
        normalizedPath = NormalizedPath;
        uid = Uid;
        source = Source;
    }

    /// <summary>Returns the same identity tagged with a different discovery source.</summary>
    public TrayIconRef WithSource(IconSource source) => new(Guid, NormalizedPath, Uid, source);

    /// <summary>Serializes to the forward-compatible structured pin shape (<c>schemaVersion: 1</c>).</summary>
    public string ToJson() => JsonSerializer.Serialize(new
    {
        schemaVersion = 1,
        kind = KeyKind switch
        {
            PinKeyKind.Guid => "guid",
            _ => "path"
        },
        guid = Guid?.ToString("D"),
        path = NormalizedPath,
        uid = Uid
    });

    /// <summary>
    /// Parses both legacy pin keys (<c>guid:&lt;D&gt;</c>, <c>path:&lt;lower&gt;</c>,
    /// <c>path:&lt;lower&gt;|&lt;uid&gt;</c>, <c>path:&lt;lower&gt;|*</c>) and structured JSON pin objects.
    /// Returns <c>false</c> for unknown/blank input instead of throwing.
    /// </summary>
    public static bool TryParse(string? pinKey, out TrayIconRef reference)
    {
        reference = default;
        if (string.IsNullOrWhiteSpace(pinKey)) return false;
        var text = pinKey.Trim();

        if (text.StartsWith('{'))
        {
            return TryParseJson(text, out reference);
        }

        if (text.StartsWith("guid:", StringComparison.OrdinalIgnoreCase))
        {
            if (!System.Guid.TryParse(text[5..], out var guid)) return false;
            reference = new TrayIconRef(guid, null, 0, IconSource.Unknown);
            return true;
        }

        if (text.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
        {
            var path = text[5..];
            uint uid = 0;
            var separator = path.LastIndexOf('|');
            if (separator >= 0)
            {
                var suffix = path[(separator + 1)..];
                if (suffix == "*"
                    || uint.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out uid))
                {
                    path = path[..separator];
                }
            }
            if (string.IsNullOrWhiteSpace(path)) return false;
            reference = new TrayIconRef(null, path, uid, IconSource.Unknown);
            return true;
        }

        return false;
    }

    /// <summary>Parses a pin key, returning <c>default</c> for unknown/blank input.</summary>
    public static TrayIconRef Parse(string? pinKey) => TryParse(pinKey, out var reference) ? reference : default;

    private static bool TryParseJson(string json, out TrayIconRef reference)
    {
        reference = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            Guid? guid = TryGetString(root, "guid", out var guidText) && System.Guid.TryParse(guidText, out var parsedGuid)
                ? parsedGuid
                : null;
            TryGetString(root, "path", out var path);
            uint uid = TryGetString(root, "uid", out var uidText)
                && uint.TryParse(uidText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedUid)
                ? parsedUid
                : 0;
            if (TryGetInt(root, "uid", out var numericUid) && numericUid >= 0) uid = (uint)numericUid;

            if (guid is null && string.IsNullOrWhiteSpace(path)) return false;
            reference = new TrayIconRef(guid, path, uid, IconSource.Unknown);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!TryGetProperty(root, name, out var element) || element.ValueKind != JsonValueKind.String) return false;
        value = element.GetString();
        return value is not null;
    }

    private static bool TryGetInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return TryGetProperty(root, name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement element)
    {
        if (root.TryGetProperty(name, out element)) return true;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                element = property.Value;
                return true;
            }
        }
        element = default;
        return false;
    }
}

/// <summary>
/// Deduplicates tray icons on <c>(path, GUID, UID)</c> only. The discovery source is deliberately ignored so a
/// live icon and a registry snapshot entry for the same icon collapse into a single authoritative item.
/// </summary>
public sealed class TrayIconRefComparer : IEqualityComparer<TrayIconRef>
{
    public static TrayIconRefComparer Instance { get; } = new();

    public bool Equals(TrayIconRef x, TrayIconRef y) =>
        x.Guid == y.Guid
        && string.Equals(x.NormalizedPath, y.NormalizedPath, StringComparison.OrdinalIgnoreCase)
        && x.Uid == y.Uid;

    public int GetHashCode(TrayIconRef obj) => HashCode.Combine(
        obj.Guid,
        StringComparer.OrdinalIgnoreCase.GetHashCode(obj.NormalizedPath ?? string.Empty),
        obj.Uid);
}
