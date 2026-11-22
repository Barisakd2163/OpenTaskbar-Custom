using System.Text.Json;

namespace OpenTaskbar.Services;

/// <summary>
/// Global rendering preference kept outside the profile-aware settings file so Program.Main can read it
/// before any WPF visual tree exists. Software rendering saves roughly 100 MB of private memory but makes
/// magnification and flyouts less fluid; the choice takes effect at the next start.
/// </summary>
internal static class RenderModePreference
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar", "render-mode.json");

    public static bool IsSoftware()
    {
        try
        {
            if (!System.IO.File.Exists(Path)) return false;
            using var document = JsonDocument.Parse(System.IO.File.ReadAllText(Path));
            return document.RootElement.TryGetProperty("software", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch
        {
            return false;
        }
    }

    public static void SetSoftware(bool value)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.WriteAllText(Path, JsonSerializer.Serialize(new { software = value }));
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Render mode preference could not be saved: {ex.Message}");
        }
    }
}
