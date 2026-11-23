using System.IO;
using System.Text.Json;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed record PinBackup(string Name, string ExecutablePath, string? Arguments);

/// <summary>One-file backup of the dock settings and the pinned apps, for moving to another PC or restoring.</summary>
public static class SettingsBackup
{
    private sealed record BackupFile(int Version, DockSettings Settings, IReadOnlyList<PinBackup> Pins);

    public static string? Export(string path, DockSettings settings)
    {
        var json = Serialize(settings);
        try
        {
            File.WriteAllText(path, json);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Settings export failed: {ex.Message}");
            return "ErrorSettingsFileWrite";
        }
    }

    public static (DockSettings? Settings, IReadOnlyList<PinBackup> Pins, string? Error) Read(string path)
    {
        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Settings import failed: {ex.Message}");
            return (null, [], "ErrorSettingsFileInvalid");
        }
    }

    internal static string Serialize(DockSettings settings, IReadOnlyList<PinBackup>? pinsOverride = null)
    {
        var pins = pinsOverride ?? WindowsTaskbarPinImporter.Import().Apps
            .Select(app => new PinBackup(app.Name, app.ExecutablePath, app.LaunchArguments))
            .ToList();
        return JsonSerializer.Serialize(new BackupFile(1, settings, pins), DockSettingsStore.SerializerOptions);
    }

    internal static (DockSettings? Settings, IReadOnlyList<PinBackup> Pins, string? Error) Parse(string json)
    {
        try
        {
            var backup = JsonSerializer.Deserialize<BackupFile>(json, DockSettingsStore.SerializerOptions);
            if (backup?.Settings is null) return (null, [], "ErrorSettingsFileInvalid");
            var settings = DockSettingsStore.MigrateRemovedWidgets(backup.Settings);
            settings = DockSettingsStore.MigrateLegacyAiUsage(settings);
            settings = DockProfileService.EnsureDefaultProfile(settings);
            if (!DockSettingsStore.Validate(settings)) return (null, [], "ErrorSettingsFileInvalid");
            var pins = (backup.Pins ?? []).Where(pin => pin is not null && !string.IsNullOrWhiteSpace(pin.ExecutablePath)).ToList();
            return (settings, pins, null);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or NullReferenceException)
        {
            AppLog.Warning($"Settings import failed: {ex.Message}");
            return (null, [], "ErrorSettingsFileInvalid");
        }
    }
}
