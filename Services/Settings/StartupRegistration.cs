using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace OpenTaskbar.Services;

public sealed class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string StartupApprovedRunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "OpenTaskbar";
    private const string ExecutableFileName = "OpenTaskbar.exe";
    internal const int MaxCommandLength = 1024;

    // Task Manager / Windows Settings stores a 12-byte REG_BINARY entry in StartupApproved\Run:
    // byte 0 even (0x02) = enabled, byte 0 odd (e.g. 0x03) = disabled (with FILETIME in bytes 4..11).
    private static readonly byte[] EnabledStartupApprovedBytes =
        [0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    public (bool Enabled, string? Error) Read()
    {
        if (AppIdentity.IsPackaged)
            return ReadPackaged();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var rawCommand = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(rawCommand))
                return (false, null);

            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey);
            var approvedData = approvedKey?.GetValue(ValueName) as byte[];
            return (IsRegisteredAndActive(rawCommand, approvedData), null);
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
        string? normalizedExecutable = null;
        if (enabled)
        {
            if (!IsValidStartupExecutable(executable))
                return "StartupPathError";
            normalizedExecutable = TryNormalizePath(executable);
            if (normalizedExecutable is null || normalizedExecutable.Length + 2 > MaxCommandLength)
                return "StartupPathError";
        }

        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
                key.SetValue(ValueName, $"\"{normalizedExecutable}\"", RegistryValueKind.String);
                ReenableStartupApprovedIfDisabled();
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                TryDeleteStartupApprovedEntry();
            }
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return "StartupWriteError";
        }
    }

    public void SyncOnStartup(bool isDev, bool isInspection)
    {
        if (isDev || isInspection)
            return;

        if (AppIdentity.IsPackaged)
        {
            RemoveStaleUnpackagedRunEntry();
            return;
        }

        var executable = Environment.ProcessPath;
        if (!IsValidStartupExecutable(executable, Path.GetTempPath()))
            return;

        var normalizedExecutable = TryNormalizePath(executable);
        if (normalizedExecutable is null || normalizedExecutable.Length + 2 > MaxCommandLength)
            return;

        SyncUnpackagedRunEntry(normalizedExecutable);

        var startupFolder = ResolveStartupFolderPath();
        if (!string.IsNullOrWhiteSpace(startupFolder))
            _ = SyncStartupFolderShortcuts(startupFolder, normalizedExecutable);
    }

    internal static (string? ExecutablePath, string Arguments) ParseRunCommand(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return (null, string.Empty);

        var trimmed = commandLine.Trim();
        if (trimmed[0] == '"')
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote >= 0)
            {
                var exe = trimmed[1..closingQuote].Trim();
                var args = trimmed[(closingQuote + 1)..].Trim();
                return exe.Length > 0 ? (exe, args) : (null, string.Empty);
            }

            var unclosedExe = trimmed[1..].Trim();
            return unclosedExe.Length > 0 ? (unclosedExe, string.Empty) : (null, string.Empty);
        }

        // Unquoted command lines may contain spaces in the path (e.g. C:\Program Files\OpenTaskbar\OpenTaskbar.exe --flag).
        // Match the first .exe boundary before falling back to whitespace splitting.
        var searchFrom = 0;
        while (searchFrom < trimmed.Length)
        {
            var exeIdx = trimmed.IndexOf(".exe", searchFrom, StringComparison.OrdinalIgnoreCase);
            if (exeIdx < 0)
                break;

            var afterExe = exeIdx + 4;
            if (afterExe == trimmed.Length || char.IsWhiteSpace(trimmed[afterExe]))
            {
                var exe = trimmed[..afterExe].Trim();
                var args = afterExe < trimmed.Length ? trimmed[afterExe..].Trim() : string.Empty;
                return exe.Length > 0 ? (exe, args) : (null, string.Empty);
            }

            searchFrom = afterExe;
        }

        for (var i = 0; i < trimmed.Length; i++)
        {
            if (char.IsWhiteSpace(trimmed[i]))
                return (trimmed[..i], trimmed[(i + 1)..].Trim());
        }

        return (trimmed, string.Empty);
    }

    internal static bool IsStartupApprovedEnabled(byte[]? data)
        => data is null || data.Length == 0 || (data[0] & 1) == 0;

    internal static bool IsRegisteredAndActive(
        string? existingRunValue,
        byte[]? startupApprovedData,
        Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(existingRunValue) || !IsStartupApprovedEnabled(startupApprovedData))
            return false;

        var (registeredExe, _) = ParseRunCommand(existingRunValue);
        var normalized = TryNormalizePath(registeredExe);
        if (normalized is null)
            return false;

        return (fileExists ?? File.Exists)(normalized);
    }

    internal static bool ShouldUpdateRunRegistration(
        string? existingRunValue,
        byte[]? startupApprovedData,
        string currentExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(existingRunValue) || !IsStartupApprovedEnabled(startupApprovedData))
            return false;

        var normalizedCurrent = TryNormalizePath(currentExecutablePath);
        if (normalizedCurrent is null)
            return false;

        var (registeredExe, _) = ParseRunCommand(existingRunValue);
        var normalizedRegistered = TryNormalizePath(registeredExe);
        return normalizedRegistered is null
            || !string.Equals(normalizedRegistered, normalizedCurrent, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsValidStartupExecutable(
        string? executablePath,
        string? disallowedRootDirectory = null,
        Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)
            || executablePath.Length + 2 > MaxCommandLength
            || executablePath.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
            return false;

        var normalized = TryNormalizePath(executablePath);
        if (normalized is null || normalized.Length + 2 > MaxCommandLength)
            return false;

        var fileName = Path.GetFileName(normalized);
        if (!string.Equals(fileName, ExecutableFileName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (!(fileExists ?? File.Exists)(normalized))
            return false;

        if (!string.IsNullOrWhiteSpace(disallowedRootDirectory) && IsUnderDirectory(normalized, disallowedRootDirectory))
            return false;

        return true;
    }

    internal static bool IsUnderDirectory(string? path, string? directory)
    {
        var normalizedPath = TryNormalizePath(path);
        var normalizedDir = TryNormalizePath(directory);
        if (normalizedPath is null || normalizedDir is null)
            return false;

        var dirWithSep = normalizedDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(dirWithSep, StringComparison.OrdinalIgnoreCase);
    }

    internal static int SyncStartupFolderShortcuts(string startupFolder, string currentExecutablePath)
    {
        if (string.IsNullOrWhiteSpace(startupFolder) || !Directory.Exists(startupFolder))
            return 0;

        var normalizedCurrent = TryNormalizePath(currentExecutablePath);
        if (normalizedCurrent is null)
            return 0;

        List<string> shortcutFiles;
        try
        {
            shortcutFiles = Directory.EnumerateFiles(startupFolder, "OpenTaskbar*.lnk", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            AppLog.Warning($"Startup shortcut scan failed: {ex.Message}");
            return 0;
        }

        if (shortcutFiles.Count == 0)
            return 0;

        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType is null)
            return 0;

        var targetWorkingDir = Path.GetDirectoryName(normalizedCurrent) ?? string.Empty;
        object? shell = null;
        var updatedCount = 0;
        try
        {
            shell = Activator.CreateInstance(shellType);
            if (shell is null)
                return 0;

            foreach (var shortcutPath in shortcutFiles)
            {
                object? link = null;
                try
                {
                    link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, [shortcutPath]);
                    if (link is null)
                        continue;

                    var linkType = link.GetType();
                    var rawTarget = linkType.InvokeMember("TargetPath", BindingFlags.GetProperty, null, link, null) as string;
                    if (string.IsNullOrWhiteSpace(rawTarget))
                        continue;

                    var expandedTarget = Environment.ExpandEnvironmentVariables(rawTarget.Trim());
                    var normalizedTarget = TryNormalizePath(expandedTarget);
                    if (normalizedTarget is null)
                        continue;

                    if (!string.Equals(Path.GetFileName(normalizedTarget), ExecutableFileName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (string.Equals(normalizedTarget, normalizedCurrent, StringComparison.OrdinalIgnoreCase))
                        continue;

                    linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link, [normalizedCurrent]);
                    if (targetWorkingDir.Length > 0)
                        linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link, [targetWorkingDir]);
                    linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);

                    updatedCount++;
                    AppLog.Info($"Updated startup shortcut '{Path.GetFileName(shortcutPath)}' from '{normalizedTarget}' to '{normalizedCurrent}'.");
                }
                catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException or SecurityException)
                {
                    AppLog.Warning($"Failed to update startup shortcut '{shortcutPath}': {ex.Message}");
                }
                finally
                {
                    if (link is not null && Marshal.IsComObject(link))
                        _ = Marshal.FinalReleaseComObject(link);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidOperationException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Startup shortcut sync failed: {ex.Message}");
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
                _ = Marshal.FinalReleaseComObject(shell);
        }

        return updatedCount;
    }

    private static void SyncUnpackagedRunEntry(string normalizedExecutable)
    {
        try
        {
            using var readKey = Registry.CurrentUser.OpenSubKey(RunKey);
            var rawCommand = readKey?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(rawCommand))
                return;

            using var approvedKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey);
            var approvedData = approvedKey?.GetValue(ValueName) as byte[];
            if (!ShouldUpdateRunRegistration(rawCommand, approvedData, normalizedExecutable))
                return;

            var (previousExe, _) = ParseRunCommand(rawCommand);
            using var writeKey = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            writeKey.SetValue(ValueName, $"\"{normalizedExecutable}\"", RegistryValueKind.String);
            AppLog.Info($"Updated startup Run registration from '{previousExe ?? rawCommand}' to '{normalizedExecutable}'.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            AppLog.Warning($"Failed to sync startup Run registration: {ex.Message}");
        }
    }

    private static void RemoveStaleUnpackagedRunEntry()
    {
        try
        {
            using var readKey = Registry.CurrentUser.OpenSubKey(RunKey);
            if (readKey?.GetValue(ValueName) is null)
                return;

            using var writeKey = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            writeKey?.DeleteValue(ValueName, throwOnMissingValue: false);
            TryDeleteStartupApprovedEntry();
            AppLog.Info("Removed stale unpackaged startup Run registration for packaged OpenTaskbar.");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            AppLog.Warning($"Failed to remove stale unpackaged startup Run registration: {ex.Message}");
        }
    }

    private static void ReenableStartupApprovedIfDisabled()
    {
        using var readKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey);
        if (readKey?.GetValue(ValueName) is not byte[] data || IsStartupApprovedEnabled(data))
            return;

        using var writeKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, writable: true);
        writeKey?.SetValue(ValueName, EnabledStartupApprovedBytes, RegistryValueKind.Binary);
    }

    private static void TryDeleteStartupApprovedEntry()
    {
        try
        {
            using var readKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey);
            if (readKey?.GetValue(ValueName) is null)
                return;

            using var writeKey = Registry.CurrentUser.OpenSubKey(StartupApprovedRunKey, writable: true);
            writeKey?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            AppLog.Warning($"Failed to clean StartupApproved entry: {ex.Message}");
        }
    }

    private static string ResolveStartupFolderPath()
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (!string.IsNullOrWhiteSpace(startup))
            return startup;

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return string.IsNullOrWhiteSpace(appData)
            ? string.Empty
            : Path.Combine(appData, "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
    }

    internal static string? TryNormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            var trimmed = path.Trim();
            if (!Path.IsPathFullyQualified(trimmed))
                return null;

            var full = Path.GetFullPath(trimmed);
            return full.Contains('~') ? TryGetLongPath(full) : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    private static string TryGetLongPath(string path)
    {
        var buffer = new char[1024];
        var length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length == 0)
            return path;

        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length)
                return path;
        }

        return new string(buffer, 0, (int)length);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

    private static (bool Enabled, string? Error) ReadPackaged()
    {
        try
        {
            var task = StartupTask.GetAsync(AppIdentity.StartupTaskId).AsTask().GetAwaiter().GetResult();
            return (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy, null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or COMException)
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
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or COMException)
        {
            return "StartupWriteError";
        }
    }
}
