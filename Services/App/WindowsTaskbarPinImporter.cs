using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;
using OpenTaskbar.Models;
using Windows.Management.Deployment;

namespace OpenTaskbar.Services;

public static class WindowsTaskbarPinImporter
{
    private const string AppsFolderPrefix = "shell:AppsFolder\\";

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
                        LogSkipped("Shortcut unavailable");
                        skippedCount++;
                        continue;
                    }

                    try
                    {
                        var shortcutType = shortcut.GetType();
                        var targetPath = ReadStringProperty(shortcutType, shortcut, "TargetPath");
                        var arguments = ReadStringProperty(shortcutType, shortcut, "Arguments");
                        var iconLocation = ReadStringProperty(shortcutType, shortcut, "IconLocation");
                        var aumid = TryExtractAumidFromArguments(arguments)
                            ?? TryExtractAumidFromArguments(targetPath);
                        var packaged = aumid is null ? null : TryResolvePackagedApp(aumid);
                        var executablePath = packaged?.ExecutablePath ?? ResolveExecutablePath(targetPath, iconLocation);
                        // An AppsFolder shortcut may target Explorer, which is also separately pinned.
                        // Never assign that shared host path to a packaged app.
                        if (executablePath is null || (aumid is not null && packaged is null
                            && string.Equals(Path.GetFileName(executablePath), "explorer.exe", StringComparison.OrdinalIgnoreCase)))
                        {
                            LogSkipped("Application target unavailable");
                            skippedCount++;
                            continue;
                        }
                        if (!seenPaths.Add(executablePath)) continue;

                        apps.Add(new DockApp(Path.GetFileNameWithoutExtension(shortcutPath), executablePath)
                        {
                            LaunchArguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments,
                            ShortcutPath = Path.GetFullPath(shortcutPath),
                            IconLocation = string.IsNullOrWhiteSpace(iconLocation) ? null : iconLocation,
                            // Store apps pinned via .lnk point at WindowsApps\*.exe, which cannot be launched
                            // directly. Capture the packaged AUMID so shell:AppsFolder activation works.
                            AppUserModelId = aumid ?? ApplicationIconProvider.TryResolvePackagedAppUserModelId(executablePath)
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
                    LogSkipped("Shell shortcut read failed");
                    skippedCount++;
                }
                catch (TargetInvocationException ex) when (ex.InnerException is COMException)
                {
                    LogSkipped("Shell shortcut read failed");
                    skippedCount++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    LogSkipped(ex.GetType().Name);
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

        string[] packageDirs;
        try
        {
            // The WindowsApps parent may deny enumeration, and apps can be installed on another volume.
            // Query packages registered for the current user to get their actual locations.
            packageDirs = windowsAppsRoot is null
                ? new PackageManager().FindPackagesForUser(string.Empty, familyName)
                    .OrderByDescending(package => package.Id.Version.Major)
                    .ThenByDescending(package => package.Id.Version.Minor)
                    .ThenByDescending(package => package.Id.Version.Build)
                    .ThenByDescending(package => package.Id.Version.Revision)
                    .Select(package => package.InstalledLocation.Path)
                    .ToArray()
                : Directory.EnumerateDirectories(windowsAppsRoot, $"{packageName}_*_{publisherId}")
                    .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or COMException)
        {
            AppLog.Warning($"Packaged taskbar pin lookup failed: {ex.GetType().Name}.");
            packageDirs = [];
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
                    displayName = (windowsAppsRoot is null
                        ? ApplicationIconProvider.GetShellItemDisplayName(AppsFolderPrefix + aumid)
                        : null) ?? Path.GetFileNameWithoutExtension(fullExe);
                }

                return (displayName, fullExe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                continue;
            }
        }

        // Some valid AppsFolder entries, including system apps, expose no accessible executable.
        if (windowsAppsRoot is not null || !AppShortcutResolver.IsPackagedAppUserModelId(aumid)) return null;
        var parsingName = AppsFolderPrefix + aumid;
        var shellName = ApplicationIconProvider.GetShellItemDisplayName(parsingName);
        return string.IsNullOrWhiteSpace(shellName) ? null : (shellName, parsingName);
    }

    public static (string? ShortcutPath, string? Error) AddPin(
        string appName, string executablePath, string? launchArguments = null, string? appUserModelId = null, string? iconLocation = null)
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
        for (var suffix = 2; File.Exists(shortcutPath); suffix++)
        {
            if (suffix > 100) return (null, "ErrorTaskbarPin");
            shortcutPath = Path.Combine(pinDirectory, $"{safeName} ({suffix}).lnk");
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
                appUserModelId ??= TryExtractAumidFromArguments(launchArguments) ?? GetAppsFolderAumid(executablePath);
                var packaged = AppShortcutResolver.IsPackagedAppUserModelId(appUserModelId);
                var target = packaged
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                    : executablePath;
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, [target]);
                var workingDir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(workingDir))
                {
                    shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, [workingDir]);
                }
                var arguments = packaged
                    ? $"shell:AppsFolder\\{appUserModelId?.Trim()}"
                    : launchArguments;
                if (!string.IsNullOrWhiteSpace(arguments))
                {
                    shortcutType.InvokeMember("Arguments", BindingFlags.SetProperty, null, shortcut, [arguments]);
                }
                if (!string.IsNullOrWhiteSpace(iconLocation))
                {
                    shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, [iconLocation]);
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

    private static void LogSkipped(string reason) => AppLog.Warning($"Skipped taskbar pin. {reason}.");

    /// <summary>
    /// Explorer-based pins for Store apps carry "shell:AppsFolder\\&lt;AUMID&gt;" in the arguments. Capturing it
    /// lets the launcher activate the packaged app instead of opening a bare Explorer window.
    /// </summary>
    internal static string? TryExtractAumidFromArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return null;
        const string marker = "shell:appsfolder\\";
        var index = arguments.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return null;
        var start = index + marker.Length;
        var end = arguments.IndexOfAny([' ', '"', '\t'], start);
        var candidate = (end < 0 ? arguments[start..] : arguments[start..end]).Trim();
        return AppShortcutResolver.IsPackagedAppUserModelId(candidate) ? candidate : null;
    }

    internal static string? GetAppsFolderAumid(string path)
    {
        if (!path.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)) return null;
        var aumid = path[AppsFolderPrefix.Length..];
        return AppShortcutResolver.IsPackagedAppUserModelId(aumid) ? aumid.Trim() : null;
    }

    private static string? ResolveExecutablePath(string targetPath, string iconLocation)
    {
        var normalizedTarget = Environment.ExpandEnvironmentVariables(targetPath.Trim().Trim('"'));
        if (Path.GetExtension(normalizedTarget).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(normalizedTarget))
        {
            return Path.GetFullPath(normalizedTarget);
        }

        // Many taskbar pins are helper/stub shortcuts whose TargetPath has moved or points at a protocol.
        // The icon location still names the real executable in most of those cases, so use it as a fallback
        // instead of dropping the pin (#147).
        if (ApplicationIconProvider.TryParseIconLocation(iconLocation, out var iconPath, out _)
            && Path.GetExtension(iconPath).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(iconPath))
        {
            return Path.GetFullPath(iconPath);
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
