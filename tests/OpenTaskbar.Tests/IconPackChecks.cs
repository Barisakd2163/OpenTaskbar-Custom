using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Services;

internal static class IconPackChecks
{
    public static int Run()
    {
        Exception? failure = null;
        var checks = 0;
        // RenderTargetBitmap, used for the fallback plate, needs an STA thread.
        var thread = new Thread(() =>
        {
            try { checks = RunChecks(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        return checks;
    }

    private static int RunChecks()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var root = Path.Combine(Path.GetTempPath(), "OpenTaskbarIconPacks-" + Path.GetRandomFileName());
        var previousRoot = IconPacks.Root;
        try
        {
            var packDir = Path.Combine(root, "test-pack");
            Directory.CreateDirectory(Path.Combine(packDir, "icons"));
            WritePng(Path.Combine(packDir, "browser.png"), 48);
            WritePng(Path.Combine(packDir, "terminal.png"), 48);
            WritePng(Path.Combine(packDir, "trash.png"), 48);
            WritePng(Path.Combine(packDir, "icons", "code.png"), 64);
            WritePng(Path.Combine(root, "outside.png"), 48);
            File.WriteAllText(Path.Combine(packDir, "pack.json"), """
                {
                  // Comments and trailing commas are allowed.
                  "name": "Test Pack",
                  "icons": {
                    "firefox.exe": "browser.png",
                    "Microsoft.WindowsTerminal": "terminal.png",
                    "@recyclebin": "trash.png",
                    "evil.exe": "..\\outside.png",
                    "missing.exe": "nothing.png",
                  },
                  "fallback": { "shape": "Leaf", "sharpCorner": "TopRight", "background": "#FF336699" }
                }
                """);

            var (pack, error) = IconPack.Load(packDir);
            Check(error is null && pack is not null && pack.DisplayName == "Test Pack" && pack.Id == "test-pack",
                "A valid pack.json must load with its name and folder id.");
            Check(pack!.FindIconFile(@"C:\Program Files\Mozilla Firefox\FIREFOX.EXE") == Path.Combine(packDir, "browser.png"),
                "Executable file names must match case-insensitively.");
            Check(pack.FindIconFile(@"C:\x\WindowsTerminal.exe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App") == Path.Combine(packDir, "terminal.png"),
                "A package name must match an AppUserModelID.");
            Check(pack.FindIconFile(null, specialKey: IconPack.RecycleBinKey) == Path.Combine(packDir, "trash.png"),
                "Special items must resolve by key.");
            Check(pack.FindIconFile(null, specialKey: IconPack.RecycleBinFullKey) is null,
                "An unmapped special item must not fall back to another special item.");
            Check(string.Equals(pack.FindIconFile(@"C:\Apps\Code.exe"), Path.Combine(packDir, "icons", "code.png"), StringComparison.OrdinalIgnoreCase),
                "An image named after the executable in icons\\ must match without a mapping.");
            Check(pack.FindIconFile(@"C:\Apps\evil.exe") is null, "Mapped paths outside the pack folder must be ignored.");
            Check(pack.FindIconFile(@"C:\Apps\missing.exe") is null, "A mapping to a missing file must be ignored.");
            Check(pack.FindIconFile(@"C:\Apps\unknown.exe") is null, "Unknown apps must have no pack image.");
            Check(pack.Fallback is { Shape: IconPackShape.Leaf, SharpCorner: IconPackCorner.TopRight },
                "Fallback enums must parse from strings.");

            var brokenDir = Path.Combine(root, "broken");
            Directory.CreateDirectory(brokenDir);
            File.WriteAllText(Path.Combine(brokenDir, "pack.json"), "{ not json");
            Check(IconPack.Load(brokenDir).Error is not null, "Invalid JSON must report an error.");
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            Check(IconPack.Load(Path.Combine(root, "empty")).Error is not null, "A folder without pack.json must report an error.");

            IconPacks.Root = root;
            var discovered = IconPacks.Discover();
            Check(discovered.Count == 1 && discovered[0].Id == "test-pack", "Discovery must list only loadable packs.");

            var original = CreateIcon(32);
            IconPacks.Select(null);
            var changes = 0;
            EventHandler onChanged = (_, _) => changes++;
            IconPacks.Changed += onChanged;
            try
            {
                Check(ReferenceEquals(IconPacks.Apply(original, @"C:\Apps\unknown.exe"), original), "Without a pack the original icon must be used.");
                IconPacks.Select("test-pack");
                Check(changes == 1 && IconPacks.Current?.Id == "test-pack", "Selecting a pack must activate it and notify once.");
                IconPacks.Select("TEST-PACK");
                Check(changes == 1, "Selecting the active pack again must not notify.");
                IconPacks.Select("test-pack", reload: true);
                Check(changes == 2, "A reload must notify.");

                var themed = IconPacks.Apply(original, @"C:\Apps\firefox.exe");
                Check(themed is not null && themed.PixelWidth == 48, "A mapped app must use the pack image.");
                Check(ReferenceEquals(themed, IconPacks.Apply(original, @"C:\Apps\firefox.exe")), "Pack images must be cached.");

                var plated = IconPacks.Apply(original, @"C:\Apps\unknown.exe");
                Check(plated is not null && plated.PixelWidth == IconPacks.RenderSize, "Unmapped apps must be placed on the pack plate.");
                Check(Alpha(plated!, IconPacks.RenderSize - 1, 0) > 0 && Alpha(plated!, 0, 0) == 0
                    && Alpha(plated!, 0, IconPacks.RenderSize - 1) == 0 && Alpha(plated!, IconPacks.RenderSize - 1, IconPacks.RenderSize - 1) == 0,
                    "A leaf plate must keep only its sharp corner square.");
                Check(ReferenceEquals(plated, IconPacks.Apply(original, @"C:\Apps\unknown.exe")), "Plates must be cached per source icon.");
                Check(IconPacks.Apply(null, @"C:\Apps\unknown.exe") is null, "A missing icon must stay missing when the pack has no image.");

                IconPacks.Select("no-such-pack");
                Check(IconPacks.Current is null && changes == 3, "An unknown pack must fall back to the app icons.");
                Check(ReferenceEquals(IconPacks.Apply(original, @"C:\Apps\firefox.exe"), original), "Clearing the pack must restore original icons.");
            }
            finally
            {
                IconPacks.Changed -= onChanged;
            }

            var color = IconPacks.AutoBackground(CreateIcon(8, Colors.Red));
            Check(color.R == 255 && color.G < 210 && color.G == color.B, "The automatic plate must be a pale tint of the icon color.");
        }
        finally
        {
            IconPacks.Select(null);
            IconPacks.Root = previousRoot;
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }

        return checks;
    }

    private static BitmapSource CreateIcon(int size, Color? color = null)
    {
        var fill = color ?? Colors.Orange;
        var pixels = new byte[size * size * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = fill.B;
            pixels[i + 1] = fill.G;
            pixels[i + 2] = fill.R;
            pixels[i + 3] = 255;
        }

        var icon = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, pixels, size * 4);
        icon.Freeze();
        return icon;
    }

    private static void WritePng(string path, int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateIcon(size)));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static byte Alpha(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(new System.Windows.Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }
}
