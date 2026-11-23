using System.Diagnostics;
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
        if (enabled && !IsStartupExecutable(executable)) return "StartupPathError";
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

    internal static string? RefreshPortableTarget()
    {
        if (AppIdentity.IsPackaged || Environment.ProcessPath is not { } current || !IsStartupExecutable(current))
            return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is not string command
                || command.Length < 3 || command[0] != '"' || command[^1] != '"')
                return null;

            var registered = command[1..^1];
            if (!IsStartupPath(registered) || string.Equals(registered, current, StringComparison.OrdinalIgnoreCase))
                return null;

            // An explicitly enabled entry follows a newer portable copy. Opening an older copy never rolls it back.
            if (!File.Exists(registered) || ReadFileVersion(current) > ReadFileVersion(registered))
                key.SetValue(ValueName, $"\"{current}\"", RegistryValueKind.String);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException
            or System.ComponentModel.Win32Exception)
        {
            return "StartupWriteError";
        }
    }

    private static Version ReadFileVersion(string path)
    {
        var version = FileVersionInfo.GetVersionInfo(path);
        return new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart);
    }

    private static bool IsStartupExecutable(string? path) =>
        path is not null && IsStartupPath(path) && File.Exists(path);

    private static bool IsStartupPath(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.Length + 2 > 260
            || !Path.IsPathFullyQualified(executable) || executable.Contains('"')
            || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        // Portable builds can be named OpenTaskbar-x64.exe / OpenTaskbar-v1.2.3-win-x86.exe while ZIP installs
        // keep OpenTaskbar.exe. All of them are valid startup targets; only unrelated executables are rejected.
        var name = Path.GetFileNameWithoutExtension(executable);
        const string BaseName = "OpenTaskbar";
        return name.StartsWith(BaseName, StringComparison.OrdinalIgnoreCase)
            && (name.Length == BaseName.Length || name[BaseName.Length] == '-');
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
