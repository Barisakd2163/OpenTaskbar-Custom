using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Services;

/// <summary>Which corner of a <see cref="IconPackShape.Leaf"/> mask stays square; the other three are rounded.</summary>
public enum IconPackCorner { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>The plate drawn behind icons the pack has no image for, so unthemed apps still match the pack.</summary>
public enum IconPackShape { None, Leaf, Rounded, Circle }

public sealed record IconPackFallback(
    IconPackShape Shape = IconPackShape.None,
    IconPackCorner SharpCorner = IconPackCorner.TopRight,
    double CornerRadius = 0.5,
    string Background = "auto",
    double IconScale = 0.64);

/// <summary>
/// <c>pack.json</c> at the root of a pack folder. <see cref="Icons"/> maps a key to an image path relative to the
/// pack folder. Keys are matched in this order: the special names <c>@recyclebin</c> and <c>@recyclebin-full</c>,
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

/// <summary>
/// The active icon pack. Packs are folders under <c>%LOCALAPPDATA%\OpenTaskbar\IconPacks</c>; none ship with the app.
/// </summary>
public static class IconPacks
{
    public const int RenderSize = 128;
    private static readonly Dictionary<string, BitmapSource?> Images = new(StringComparer.OrdinalIgnoreCase);
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<BitmapSource, BitmapSource> Plated = new();

    public static string Root { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTaskbar", "IconPacks");

    public static IconPack? Current { get; private set; }

    /// <summary>Raised after <see cref="Select"/> changes the pack, so views can reload their icons.</summary>
    public static event EventHandler? Changed;

    public static IReadOnlyList<IconPack> Discover()
    {
        try
        {
            if (!System.IO.Directory.Exists(Root)) return [];
            return System.IO.Directory.EnumerateDirectories(Root)
                .Select(IconPack.Load)
                .Where(result => result.Pack is not null)
                .Select(result => result.Pack!)
                .OrderBy(pack => pack.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Icon packs could not be listed: {ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// Activates the pack in folder <paramref name="id"/>; null or an unknown folder restores the app icons.
    /// Selecting the active pack again does nothing unless <paramref name="reload"/> rereads it from disk.
    /// </summary>
    public static void Select(string? id, bool reload = false)
    {
        var requested = string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        if (!reload && string.Equals(Current?.Id, requested, StringComparison.OrdinalIgnoreCase)
            && (Current is not null || requested is null)) return;
        IconPack? pack = null;
        if (!string.IsNullOrWhiteSpace(id))
        {
            var (loaded, error) = IconPack.Load(Path.Combine(Root, id));
            if (error is not null) AppLog.Warning($"Icon pack '{id}' could not be loaded: {error}");
            pack = loaded;
        }

        var changed = reload || !string.Equals(Current?.Id, pack?.Id, StringComparison.OrdinalIgnoreCase);
        Current = pack;
        if (!changed) return;
        Images.Clear();
        Plated.Clear();
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>The pack's image for an app or special item, the original placed on the pack's plate, or the original.</summary>
    public static BitmapSource? Apply(BitmapSource? original, string? executablePath, string? appId = null, string? specialKey = null)
    {
        var pack = Current;
        if (pack is null) return original;
        var file = pack.FindIconFile(executablePath, appId, specialKey);
        if (file is not null && LoadImage(file) is { } themed) return themed;
        if (original is null || pack.Fallback.Shape == IconPackShape.None) return original;
        if (Plated.TryGetValue(original, out var plated)) return plated;
        try
        {
            plated = RenderPlate(original, pack.Fallback);
            Plated.AddOrUpdate(original, plated);
            return plated;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OutOfMemoryException)
        {
            AppLog.Warning($"Icon pack plate failed: {ex.Message}");
            return original;
        }
    }

    private static BitmapSource? LoadImage(string file)
    {
        if (Images.TryGetValue(file, out var cached)) return cached;
        BitmapSource? image = null;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bitmap.UriSource = new Uri(file, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            image = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
            or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            AppLog.Warning($"Icon pack image '{file}' could not be loaded: {ex.Message}");
        }

        Images[file] = image;
        return image;
    }

    internal static Geometry CreateShape(IconPackFallback fallback, double size)
    {
        var rect = new Rect(0, 0, size, size);
        switch (fallback.Shape)
        {
            case IconPackShape.Circle:
                return new EllipseGeometry(rect);
            case IconPackShape.Rounded:
            {
                var radius = Math.Clamp(fallback.CornerRadius, 0, 0.5) * size;
                return new RectangleGeometry(rect, radius, radius);
            }
            default:
                return CreateLeaf(fallback.SharpCorner, Math.Clamp(fallback.CornerRadius, 0, 0.5) * size, size);
        }
    }

    // Three rounded corners and one square corner, clockwise from the top left.
    private static Geometry CreateLeaf(IconPackCorner sharp, double radius, double size)
    {
        double R(IconPackCorner corner) => corner == sharp ? 0 : radius;
        var tl = R(IconPackCorner.TopLeft);
        var tr = R(IconPackCorner.TopRight);
        var br = R(IconPackCorner.BottomRight);
        var bl = R(IconPackCorner.BottomLeft);
        var figure = new PathFigure { StartPoint = new Point(tl, 0), IsClosed = true, IsFilled = true };
        void Line(double x, double y) => figure.Segments.Add(new LineSegment(new Point(x, y), true));
        void Arc(double x, double y, double r)
        {
            if (r > 0) figure.Segments.Add(new ArcSegment(new Point(x, y), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
        }

        Line(size - tr, 0);
        Arc(size, tr, tr);
        Line(size, size - br);
        Arc(size - br, size, br);
        Line(bl, size);
        Arc(0, size - bl, bl);
        Line(0, tl);
        Arc(tl, 0, tl);
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    private static BitmapSource RenderPlate(BitmapSource icon, IconPackFallback fallback)
    {
        const double size = RenderSize;
        var shape = CreateShape(fallback, size);
        var plate = ParseColor(fallback.Background) ?? AutoBackground(icon);
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawGeometry(new SolidColorBrush(plate), null, shape);
            var scale = Math.Clamp(fallback.IconScale, 0.2, 1.0);
            var inner = size * scale;
            var offset = (size - inner) / 2;
            context.PushClip(shape);
            context.DrawImage(icon, new Rect(offset, offset, inner, inner));
            context.Pop();
        }

        var bitmap = new RenderTargetBitmap(RenderSize, RenderSize, 96, 96, PixelFormats.Pbgra32);
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color? ParseColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return (Color)ColorConverter.ConvertFromString(value);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A pale tint of the icon's average opaque color, light enough for the icon to stay readable on it.</summary>
    internal static Color AutoBackground(BitmapSource icon)
    {
        var source = icon.Format == PixelFormats.Bgra32 || icon.Format == PixelFormats.Pbgra32
            ? icon
            : new FormatConvertedBitmap(icon, PixelFormats.Bgra32, null, 0);
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        double r = 0, g = 0, b = 0, weight = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            var alpha = pixels[i + 3];
            if (alpha < 128) continue;
            var a = alpha / 255.0;
            // Pbgra32 channels are premultiplied; undo it so translucent edges do not darken the tint.
            var premultiplied = source.Format == PixelFormats.Pbgra32;
            b += premultiplied ? pixels[i] / a : pixels[i];
            g += premultiplied ? pixels[i + 1] / a : pixels[i + 1];
            r += premultiplied ? pixels[i + 2] / a : pixels[i + 2];
            weight++;
        }

        if (weight == 0) return Colors.White;
        static byte Tint(double channel) => (byte)Math.Clamp(Math.Round(channel * 0.22 + 255 * 0.78), 0, 255);
        return Color.FromRgb(Tint(r / weight), Tint(g / weight), Tint(b / weight));
    }
}
