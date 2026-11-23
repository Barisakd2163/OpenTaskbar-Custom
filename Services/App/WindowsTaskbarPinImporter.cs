using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static class WindowsTaskbarPinImporter
{
    public static (IReadOnlyList<DockApp> Apps, string? ErrorKey, int SkippedCount) Import()
    {
        var taskbarPinsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar");

        var apps = new List<DockApp>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedCount = 0;
        if (!Directory.Exists(taskbarPinsPath))
        {
            ImportTaskbandPackagedPins(seenPaths, apps);
            return (apps, null, 0);
        }

        List<string> shortcutPaths;
        try
        {
            shortcutPaths = Directory.EnumerateFiles(taskbarPinsPath, "*.lnk", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (IOException)
        {
            ImportTaskbandPackagedPins(seenPaths, apps);
            return (apps, "ErrorTaskbarPinsRead", 0);
        }
        catch (UnauthorizedAccessException)
        {
            ImportTaskbandPackagedPins(seenPaths, apps);
            return (apps, "ErrorTaskbarPinsAccess", 0);
        }

        if (shortcutPaths.Count == 0)
        {
            ImportTaskbandPackagedPins(seenPaths, apps);
            return (apps, null, 0);
        }

        Type? shellType;
        object? shell;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (COMException)
        {
            ImportTaskbandPackagedPins(seenPaths, apps);
            return (apps, "ErrorShortcutServiceImport", 0);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            ImportTaskbandPackagedPins(seenPaths, apps);
            return (apps, "ErrorShortcutServiceImport", 0);
        }

        if (shellType is null || shell is null)
        {
            ImportTaskbandPackagedPins(seenPaths, apps);
            return (apps, "ErrorShortcutServiceImport", 0);
        }
        try
        {
            foreach (var shortcutPath in shortcutPaths)
            {
                try
                {
                    var shortcut = shellType.InvokeMember(
                        "CreateShortcut",
                        BindingFlags.InvokeMethod,
                        binder: null,
                        target: shell,
                        args: [shortcutPath]);
                    if (shortcut is null)
                    {
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        var shortcutType = shortcut.GetType();
                        var targetPath = ReadStringProperty(shortcutType, shortcut, "TargetPath");
                        var arguments = ReadStringProperty(shortcutType, shortcut, "Arguments");
                        var iconLocation = ReadStringProperty(shortcutType, shortcut, "IconLocation");
                        var executablePath = ResolveExecutablePath(targetPath, iconLocation);
                        if (executablePath is null || !seenPaths.Add(executablePath))
                        {
                            skippedCount++;
                            continue;
                        }

                        apps.Add(new DockApp(Path.GetFileNameWithoutExtension(shortcutPath), executablePath)
                        {
                            LaunchArguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
                            ShortcutPath = Path.GetFullPath(shortcutPath),
                            IconLocation = string.IsNullOrWhiteSpace(iconLocation) ? null : iconLocation,
                            // Store apps pinned via .lnk point at WindowsApps\*.exe, which cannot be launched
                            // directly. Capture the packaged AUMID so shell:AppsFolder activation works.
                            AppUserModelId = ApplicationIconProvider.TryResolvePackagedAppUserModelId(executablePath)
                        });
                    }
                    finally
                    {
                        if (Marshal.IsComObject(shortcut))
                        {
                            _ = Marshal.FinalReleaseComObject(shortcut);
                        }
                    }
                }
                catch (COMException)
                {
                    skippedCount++;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException)
                {
                    skippedCount++;
                }
                catch (IOException)
                {
                    skippedCount++;
                }
                catch (UnauthorizedAccessException)
                {
                    skippedCount++;
                }
            }
        }
        catch (COMException)
        {
            return (apps, "ErrorShortcutServiceStopped", skippedCount);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return (apps, "ErrorShortcutServiceStopped", skippedCount);
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }

        ImportTaskbandPackagedPins(seenPaths, apps);
        return (apps, null, skippedCount);
    }

    private static readonly Regex PackagedAumidPattern = new(
        @"[A-Za-z0-9\.\-]+_[a-z0-9]{13}![A-Za-z0-9\.\-_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Windows 11 stores Microsoft Store taskbar pins in the Explorer Taskband registry blob rather than
    /// creating .lnk shortcuts under User Pinned\TaskBar. Import those packaged AUMIDs so Store pins stay
    /// visible on the dock even when their windows are closed or minimized to the tray.
    /// </summary>
    private static void ImportTaskbandPackagedPins(HashSet<string> seenPaths, List<DockApp> apps)
    {
        byte[]? favorites;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            favorites = key?.GetValue("Favorites") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        if (favorites is null || favorites.Length < 16) return;

        foreach (var aumid in ExtractAumidsFromTaskbandBytes(favorites))
        {
            var resolved = TryResolvePackagedApp(aumid);
            if (resolved is not { } info || !seenPaths.Add(info.ExecutablePath)) continue;
            apps.Add(new DockApp(info.DisplayName, info.ExecutablePath)
            {
                AppUserModelId = aumid
            });
        }
    }

    internal static IReadOnlyList<string> ExtractAumidsFromTaskbandBytes(byte[] bytes)
    {
        if (bytes.Length < 4) return [];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        // PIDL structures inside Taskband\Favorites are not guaranteed to be 2-byte aligned,
        // so scan both even (0) and odd (1) UTF-16LE byte offsets.
        for (var offset = 0; offset <= 1; offset++)
        {
            var count = bytes.Length - offset;
            count -= count % 2;
            if (count <= 0) continue;
            var text = Encoding.Unicode.GetString(bytes, offset, count);
            foreach (Match match in PackagedAumidPattern.Matches(text))
            {
                if (seen.Add(match.Value)) result.Add(match.Value);
            }
        }
        return result;
    }

    internal static (string DisplayName, string ExecutablePath)? TryResolvePackagedApp(string aumid, string? windowsAppsRoot = null)
    {
        var bang = aumid.IndexOf('!');
        if (bang <= 0 || bang >= aumid.Length - 1) return null;
        var familyName = aumid[..bang];
        var appId = aumid[(bang + 1)..];
        var underscore = familyName.LastIndexOf('_');
        if (underscore <= 0 || underscore >= familyName.Length - 1) return null;
        var packageName = familyName[..underscore];
        var publisherId = familyName[(underscore + 1)..];

        var root = windowsAppsRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        if (!Directory.Exists(root)) return null;

        IEnumerable<string> packageDirs;
        try
        {
            packageDirs = Directory.EnumerateDirectories(root, $"{packageName}_*_{publisherId}")
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        foreach (var packageDir in packageDirs)
        {
            var manifestPath = Path.Combine(packageDir, "AppxManifest.xml");
            if (!File.Exists(manifestPath)) continue;
            try
            {
                var doc = XDocument.Load(manifestPath);
                var appElem = doc.Descendants()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "Application", StringComparison.Ordinal)
                        && string.Equals(e.Attribute("Id")?.Value?.Trim(), appId, StringComparison.OrdinalIgnoreCase));
                var relExe = appElem?.Attribute("Executable")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(relExe)) continue;

                var fullExe = Path.GetFullPath(Path.Combine(packageDir, relExe.Replace('/', '\\')));
                if (!File.Exists(fullExe)) continue;

                var displayName = appElem?.Descendants()
                    .FirstOrDefault(e => string.Equals(e.Name.LocalName, "VisualElements", StringComparison.Ordinal))
                    ?.Attribute("DisplayName")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = doc.Descendants()
                        .FirstOrDefault(e => string.Equals(e.Name.LocalName, "DisplayName", StringComparison.Ordinal))
                        ?.Value?.Trim();
                }
                if (string.IsNullOrWhiteSpace(displayName) || displayName.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                {
                    displayName = Path.GetFileNameWithoutExtension(fullExe);
                }

                return (displayName, fullExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }
        }

        return null;
    }

    public static (string? ShortcutPath, string? Error) AddPin(string appName, string executablePath, string? launchArguments = null)
    {
        var pinDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar"));

        try
        {
            Directory.CreateDirectory(pinDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "ErrorTaskbarPinFolderCreate");
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        var safeName = new string(appName.Where(ch => !invalidChars.Contains(ch)).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safeName))
        {
            safeName = Path.GetFileNameWithoutExtension(executablePath);
        }

        var shortcutPath = Path.Combine(pinDirectory, safeName + ".lnk");

        Type? shellType;
        object? shell;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (COMException)
        {
            return (null, "ErrorShortcutServicePin");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return (null, "ErrorShortcutServicePin");
        }

        if (shellType is null || shell is null)
        {
            return (null, "ErrorShortcutServicePin");
        }

        try
        {
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);

            if (shortcut is null)
            {
                return (null, "ErrorShortcutCreate");
            }

            try
            {
                var shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [executablePath]);
                var workingDir = Path.GetDirectoryName(executablePath);
                if (!string.IsNullOrEmpty(workingDir))
                {
                    shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDir]);
                }
                if (!string.IsNullOrWhiteSpace(launchArguments))
                {
                    shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [launchArguments]);
                }
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
                return (shortcutPath, null);
            }
            finally
            {
                if (Marshal.IsComObject(shortcut))
                {
                    _ = Marshal.FinalReleaseComObject(shortcut);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException)
        {
            return (null, "ErrorTaskbarPin");
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    public static string? RemovePin(string shortcutPath)
    {
        var pinDirectory = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar"));
        var fullPath = Path.GetFullPath(shortcutPath);
        if (!string.Equals(Path.GetDirectoryName(fullPath), pinDirectory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(fullPath), ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return "ErrorTaskbarPinPath";
        }

        try
        {
            File.Delete(fullPath);
            return null;
        }
        catch (IOException)
        {
            return "ErrorTaskbarUnpin";
        }
        catch (UnauthorizedAccessException)
        {
            return "ErrorTaskbarUnpinAccess";
        }
    }

    internal sealed record DroppedPinTarget(
        string Name,
        string TargetPath,
        string? LaunchArguments,
        string? IconLocation,
        bool IsDirectory);

    /// <summary>Fast check used during DragOver to decide whether a dropped path can be pinned or added as a stack.</summary>
    internal static bool IsSupportedDockDropPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        if (Directory.Exists(full)) return true;
        if (!File.Exists(full)) return false;
        var extension = Path.GetExtension(full);
        return extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Resolves a dropped .exe, .lnk shortcut, or folder into a dock pin or folder stack target.</summary>
    internal static DroppedPinTarget? TryResolveDroppedTarget(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        string full;
        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (Directory.Exists(full))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(full);
            var folderName = Path.GetFileName(trimmed);
            return new DroppedPinTarget(
                string.IsNullOrWhiteSpace(folderName) ? trimmed : folderName,
                trimmed,
                null,
                null,
                IsDirectory: true);
        }

        if (!File.Exists(full)) return null;

        var extension = Path.GetExtension(full);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var name = ResolveExecutableDisplayName(full);
            return new DroppedPinTarget(name, full, null, null, IsDirectory: false);
        }

        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            return TryResolveShortcutDropTarget(full);
        }

        return null;
    }

    private static string ResolveExecutableDisplayName(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
            if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
        }

        return Path.GetFileNameWithoutExtension(executablePath);
    }

    private static DroppedPinTarget? TryResolveShortcutDropTarget(string shortcutPath)
    {
        Type? shellType;
        object? shell;
        try
        {
            shellType = Type.GetTypeFromProgID("WScript.Shell");
            shell = shellType is null ? null : Activator.CreateInstance(shellType);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
            return null;
        }

        if (shellType is null || shell is null) return null;

        try
        {
            var shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            if (shortcut is null) return null;

            try
            {
                var shortcutType = shortcut.GetType();
                var targetPath = ReadStringProperty(shortcutType, shortcut, "TargetPath");
                var arguments = ReadStringProperty(shortcutType, shortcut, "Arguments");
                var iconLocation = ReadStringProperty(shortcutType, shortcut, "IconLocation");
                var shortcutName = Path.GetFileNameWithoutExtension(shortcutPath);

                var executablePath = ResolveExecutablePath(targetPath, iconLocation);
                if (executablePath is not null)
                {
                    return new DroppedPinTarget(
                        string.IsNullOrWhiteSpace(shortcutName) ? ResolveExecutableDisplayName(executablePath) : shortcutName,
                        executablePath,
                        string.IsNullOrWhiteSpace(arguments) ? null : arguments,
                        string.IsNullOrWhiteSpace(iconLocation) ? null : iconLocation,
                        IsDirectory: false);
                }

                var normalizedTarget = Environment.ExpandEnvironmentVariables(targetPath.Trim().Trim('"'));
                if (!string.IsNullOrWhiteSpace(normalizedTarget) && Directory.Exists(normalizedTarget))
                {
                    var dirFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalizedTarget));
                    var dirName = Path.GetFileName(dirFull);
                    return new DroppedPinTarget(
                        string.IsNullOrWhiteSpace(shortcutName) ? (string.IsNullOrWhiteSpace(dirName) ? dirFull : dirName) : shortcutName,
                        dirFull,
                        null,
                        null,
                        IsDirectory: true);
                }

                return null;
            }
            finally
            {
                if (Marshal.IsComObject(shortcut))
                {
                    _ = Marshal.FinalReleaseComObject(shortcut);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }
    }

    private static string? ResolveExecutablePath(string targetPath, string iconLocation)
    {
        var normalizedTarget = Environment.ExpandEnvironmentVariables(targetPath.Trim().Trim('"'));
        if (Path.GetExtension(normalizedTarget).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(normalizedTarget))
        {
            return Path.GetFullPath(normalizedTarget);
        }

        if (string.IsNullOrWhiteSpace(normalizedTarget))
        {
            var iconPath = iconLocation.Split(',', 2)[0].Trim().Trim('"');
            var normalizedIconPath = Environment.ExpandEnvironmentVariables(iconPath);
            if (string.Equals(Path.GetFileName(normalizedIconPath), "explorer.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(normalizedIconPath))
            {
                return Path.GetFullPath(normalizedIconPath);
            }
        }

        return null;
    }

    private static string ReadStringProperty(Type shortcutType, object shortcut, string propertyName)
    {
        return shortcutType.InvokeMember(
            propertyName,
            BindingFlags.GetProperty,
            binder: null,
            target: shortcut,
            args: null) as string ?? string.Empty;
    }
}
