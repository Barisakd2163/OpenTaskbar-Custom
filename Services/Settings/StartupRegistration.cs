using System.IO;
using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace OpenTaskbar.Services;

public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenTaskbar";

    public (bool Enabled, string? Error) Read()
    {
        if (AppIdentity.IsPackaged)
            return ReadPackaged();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return (key?.GetValue(ValueName) is string { Length: > 0 }, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return (false, "StartupReadError");
        }
    }

    public string? SetEnabled(bool enabled)
    {
        if (AppIdentity.IsPackaged)
            return SetEnabledPackaged(enabled);

        var executable = Environment.ProcessPath;
        if (enabled && (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable)
            || !string.Equals(Path.GetFileName(executable), "OpenTaskbar.exe", StringComparison.OrdinalIgnoreCase)
            || executable.Length + 2 > 260)) return "StartupPathError";
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled) key.SetValue(ValueName, $"\"{executable}\"", RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return "StartupWriteError";
        }
    }

    private static (bool Enabled, string? Error) ReadPackaged()
    {
        try
        {
            var task = StartupTask.GetAsync(AppIdentity.StartupTaskId).AsTask().GetAwaiter().GetResult();
            return (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return (false, "StartupReadError");
        }
    }

    private static string? SetEnabledPackaged(bool enabled)
    {
        try
        {
            var task = StartupTask.GetAsync(AppIdentity.StartupTaskId).AsTask().GetAwaiter().GetResult();
            if (enabled)
            {
                var state = task.RequestEnableAsync().AsTask().GetAwaiter().GetResult();
                return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy ? null : "StartupWriteError";
            }

            task.Disable();
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return "StartupWriteError";
        }
    }
}
