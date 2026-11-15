using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class DockSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "settings.json");

    public (DockSettings Settings, string? Error) Load()
    {
        if (!File.Exists(_path))
        {
            return (new DockSettings(), null);
        }

        try
        {
            var settings = JsonSerializer.Deserialize<DockSettings>(File.ReadAllText(_path), JsonOptions);
            if (settings is null || !Enum.IsDefined(settings.Position))
            {
                return (new DockSettings(), "Dock ayarları geçersiz. Dosyayı düzeltin veya silip uygulamayı yeniden başlatın.");
            }

            return (settings, null);
        }
        catch (JsonException)
        {
            return (new DockSettings(), "Dock ayarları okunamadı. JSON biçimini düzeltin veya dosyayı silip yeniden başlatın.");
        }
        catch (IOException)
        {
            return (new DockSettings(), "Dock ayarları diskten okunamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            return (new DockSettings(), "Dock ayarlarına erişim izni yok.");
        }
    }

    public string? Save(DockSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is null)
            {
                return "Dock ayarları için kayıt yolu oluşturulamadı.";
            }

            Directory.CreateDirectory(directory);
            var temporaryPath = _path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
            return null;
        }
        catch (IOException)
        {
            return "Dock ayarları diske kaydedilemedi.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Dock ayarlarına yazma izni yok.";
        }
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { WriteIndented = true };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
