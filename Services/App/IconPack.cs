using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Services;

/// <summary>
/// The square corner of a <see cref="IconPackShape.Leaf"/> plate, or the first of the two opposite square corners
/// of a <see cref="IconPackShape.Petal"/> plate; every other corner is rounded.
/// </summary>
public enum IconPackCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>The plate drawn behind icons the pack has no image for, so unthemed apps still match the pack.</summary>
public enum IconPackShape { None, Leaf, Petal, Rounded, Circle }

public sealed record IconPackFallback(
    IconPackShape Shape = IconPackShape.None,
    IconPackCorner SharpCorner = IconPackCorner.TopRight,
    double CornerRadius = 0.5,
    string Background = "auto",
    double IconScale = 0.64);

/// <summary>
/// <c>pack.json</c> at the root of a pack folder. <see cref="Icons"/> maps a key to an image path relative to the
/// pack folder. Keys are matched in this order: the special names <c>@start</c>, <c>@recyclebin</c> and <c>@recyclebin-full</c>,
/// an AppUserModelID or its package name (the part before <c>_</c>), a full executable path, an executable file
/// name (<c>firefox.exe</c>) and an executable name without extension (<c>firefox</c>). An image named after the
/// executable (<c>firefox.png</c>) in the pack folder or its <c>icons</c> subfolder is found without a mapping.
/// </summary>
public sealed record IconPackManifest(
    string? Name = null,
    string? Author = null,
    string? Description = null,
    IReadOnlyDictionary<string, string>? Icons = null,
    IconPackFallback? Fallback = null);

public sealed class IconPack
{
    public const string ManifestFileName = "pack.json";
    public const string StartKey = "@start";
    public const string RecycleBinKey = "@recyclebin";
    public const string RecycleBinFullKey = "@recyclebin-full";
    internal static readonly string[] ImageExtensions = [".png", ".ico", ".jpg", ".jpeg", ".bmp"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Dictionary<string, string> _icons;

    private IconPack(string id, string directory, IconPackManifest manifest)
    {
        Id = id;
        Directory = directory;
        Manifest = manifest;
        _icons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in manifest.Icons ?? new Dictionary<string, string>())
        {
            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value)) _icons[key.Trim()] = value.Trim();
        }
    }

    /// <summary>The folder name, which is what settings store.</summary>
    public string Id { get; }
    public string Directory { get; }
    public IconPackManifest Manifest { get; }
    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Name) ? Id : Manifest.Name!;
    public IconPackFallback Fallback => Manifest.Fallback ?? new IconPackFallback();

    public static (IconPack? Pack, string? Error) Load(string directory)
    {
        var id = Path.GetFileName(Path.TrimEndingDirectorySeparator(directory));
        var manifestPath = Path.Combine(directory, ManifestFileName);
        try
        {
            if (!File.Exists(manifestPath)) return (null, $"{manifestPath} not found.");
            var manifest = JsonSerializer.Deserialize<IconPackManifest>(File.ReadAllText(manifestPath), JsonOptions);
            if (manifest is null) return (null, $"{manifestPath} is empty.");
            return (new IconPack(id, directory, manifest), null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (null, $"{manifestPath}: {ex.Message}");
        }
    }

    /// <summary>The image file this pack uses for the app or special item, or null when the pack has none.</summary>
    public string? FindIconFile(string? executablePath, string? appId = null, string? specialKey = null)
    {
        foreach (var key in CandidateKeys(executablePath, appId, specialKey))
        {
            if (_icons.TryGetValue(key, out var relative) && ResolveInsidePack(relative) is { } mapped) return mapped;
        }

        // Convention: firefox.png next to pack.json or in icons\ needs no mapping.
        var stem = specialKey?.TrimStart('@')
            ?? (executablePath is null ? null : Path.GetFileNameWithoutExtension(executablePath));
        if (string.IsNullOrWhiteSpace(stem)) return null;
        foreach (var folder in new[] { "", "icons" })
        {
            foreach (var extension in ImageExtensions)
            {
                if (ResolveInsidePack(Path.Combine(folder, stem + extension)) is { } conventional) return conventional;
            }
        }

        return null;
    }

    internal static IEnumerable<string> CandidateKeys(string? executablePath, string? appId, string? specialKey)
    {
        if (!string.IsNullOrWhiteSpace(specialKey))
        {
            yield return specialKey;
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            yield return appId;
            var package = appId.Split('!')[0];
            var underscore = package.LastIndexOf('_');
            if (underscore > 0) yield return package[..underscore];
            yield return package;
        }

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            yield return executablePath;
            yield return Path.GetFileName(executablePath);
            yield return Path.GetFileNameWithoutExtension(executablePath);
        }
    }

    // Mapped paths are relative to the pack; anything escaping the folder is ignored.
    private string? ResolveInsidePack(string relative)
    {
        try
        {
            var root = Path.GetFullPath(Directory);
            var full = Path.GetFullPath(Path.Combine(root, relative));
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            return File.Exists(full) && ImageExtensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
