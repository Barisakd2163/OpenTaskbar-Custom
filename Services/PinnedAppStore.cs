using System.IO;
using System.Text.Json;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class PinnedAppStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "pinned-apps.json");

    public (IReadOnlyList<PinnedApp> Apps, string? Error) Load()
    {
        if (!File.Exists(_path))
        {
            return ([], null);
        }

        try
        {
            var apps = JsonSerializer.Deserialize<List<PinnedApp>>(File.ReadAllText(_path), JsonOptions);
            if (apps is null || apps.Any(app => string.IsNullOrWhiteSpace(app.Name)
                || string.IsNullOrWhiteSpace(app.ExecutablePath)))
            {
                return ([], "Sabitleme listesi geçersiz. Dosyayı düzeltin veya silip uygulamayı yeniden başlatın.");
            }

            return (apps, null);
        }
        catch (JsonException)
        {
            return ([], "Sabitleme listesi okunamadı. JSON biçimini düzeltin veya dosyayı silip yeniden başlatın.");
        }
        catch (IOException)
        {
            return ([], "Sabitleme listesi diskten okunamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            return ([], "Sabitleme listesine erişim izni yok.");
        }
    }

    public string? Save(IEnumerable<PinnedApp> apps)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is null)
            {
                return "Sabitleme listesi için kayıt yolu oluşturulamadı.";
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(apps, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
            return null;
        }
        catch (IOException)
        {
            return "Sabitleme listesi diske kaydedilemedi.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Sabitleme listesine yazma izni yok.";
        }
    }
}
