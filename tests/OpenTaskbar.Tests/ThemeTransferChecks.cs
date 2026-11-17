using System.IO;
using System.IO.Compression;
using OpenTaskbar.Services;

internal static class ThemeTransferChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        var root = Path.Combine(Path.GetTempPath(), "OpenTaskbarTransfer-" + Path.GetRandomFileName());
        var previousRoot = ThemePacks.Root;
        try
        {
            ThemePacks.Root = root;
            Directory.CreateDirectory(root);

            // Create writes a valid, loadable theme with a sanitized unique id.
            var id = ThemePackTransfer.CreateTheme("My Theme!", "author", null);
            Check(id == "My-Theme", "Theme id must be sanitized.");
            var manifest = Path.Combine(root, id, ThemePack.ManifestFileName);
            Check(File.Exists(manifest), "New theme must have a manifest.");
            Check(ThemePackTransfer.Validate(Path.Combine(root, id)) is null, "New theme must pass validation.");
            var (pack, loadError, _) = ThemePack.Load(Path.Combine(root, id));
            Check(pack is not null && loadError is null, "New theme must be loadable.");

            var id2 = ThemePackTransfer.CreateTheme("My Theme!", null, null);
            Check(id2 != id, "Duplicate names must get unique ids.");

            // Duplicate copies the source contents.
            File.WriteAllText(Path.Combine(root, id, "extra.txt"), "payload");
            var copyId = ThemePackTransfer.CreateTheme("Copy", null, id);
            Check(File.Exists(Path.Combine(root, copyId, "extra.txt")), "Duplicate must copy files.");
            Check(Path.GetFileName(Path.Combine(root, copyId, ThemePack.ManifestFileName)) == ThemePack.ManifestFileName,
                "Duplicate must keep its own manifest.");

            // Export then import round-trips the theme.
            var archive = Path.Combine(root, "export.ottheme");
            Check(ThemePackTransfer.ExportTheme(id, archive) is null, "Export must succeed.");
            using (var zip = ZipFile.OpenRead(archive))
                Check(zip.Entries.All(e => e.FullName.StartsWith(id + "/", System.StringComparison.Ordinal)),
                    "Exported entries must be relative to the theme folder.");
            var (importedId, importError) = ThemePackTransfer.ImportTheme(archive);
            Check(importError is null && importedId is not null, "Import must succeed.");
            Check(File.Exists(Path.Combine(root, importedId!, ThemePack.ManifestFileName)), "Imported theme must be installed.");

            // Reject path traversal and disallowed files.
            var evil = Path.Combine(root, "evil.zip");
            using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes("{}");
                var entry = zip.CreateEntry("../outside.json");
                using var stream = entry.Open();
                stream.Write(bytes);
            }
            var (_, evilError) = ThemePackTransfer.ImportTheme(evil);
            Check(evilError is not null, "Path traversal must be rejected.");
            Check(!File.Exists(Path.Combine(Path.GetTempPath(), "outside.json")), "No file must escape the theme root.");

            // Reject broken JSON in a manifest.
            var broken = Path.Combine(root, "broken");
            Directory.CreateDirectory(broken);
            File.WriteAllText(Path.Combine(broken, ThemePack.ManifestFileName), "{ not json");
            Check(ThemePackTransfer.Validate(broken) is not null, "Invalid JSON must fail validation.");

            // Delete is safe and removes the folder.
            Check(ThemePackTransfer.DeleteTheme(id2) is null, "Delete must succeed.");
            Check(!Directory.Exists(Path.Combine(root, id2)), "Deleted theme folder must be gone.");
            Check(ThemePackTransfer.DeleteTheme("../escape") is not null, "Unsafe delete id must be rejected.");
        }
        finally
        {
            ThemePacks.Root = previousRoot;
            try { Directory.Delete(root, recursive: true); } catch { }
        }

        return checks;
    }
}
