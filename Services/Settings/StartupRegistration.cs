using System.IO;
using System.Security;
using Microsoft.Win32;

namespace OpenTaskbar.Services;

public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenTaskbar";

    public (bool Enabled, string? Error) Read()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return (key?.GetValue(ValueName) is string { Length: > 0 }, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return (false, UiText.Get("StartupReadError"));
        }
    }

    public string? SetEnabled(bool enabled)
    {
        var executable = Environment.ProcessPath;
        if (enabled && (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), "OpenTaskbar.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Length + 2 > 260)) return UiText.Get("StartupPathError");
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return UiText.Get("StartupWriteError");
        }
    }
}
