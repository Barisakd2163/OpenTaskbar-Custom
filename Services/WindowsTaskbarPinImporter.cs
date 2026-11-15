using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static class WindowsTaskbarPinImporter
{
    public static (IReadOnlyList<DockApp> Apps, string? Error) Import()
    {
        var taskbarPinsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Internet Explorer",
            "Quick Launch",
            "User Pinned",
            "TaskBar");

        if (!Directory.Exists(taskbarPinsPath))
        {
            return ([], null);
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
            return ([], "Windows görev çubuğu kısayol klasörü okunamadı.");
        }
        catch (UnauthorizedAccessException)
        {
            return ([], "Windows görev çubuğu kısayol klasörüne erişim izni yok.");
        }

        if (shortcutPaths.Count == 0)
        {
            return ([], null);
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
            return ([], "Windows kısayol hizmeti başlatılamadı; görev çubuğu uygulamaları içe aktarılamadı.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return ([], "Windows kısayol hizmeti başlatılamadı; görev çubuğu uygulamaları içe aktarılamadı.");
        }

        if (shellType is null || shell is null)
        {
            return ([], "Windows kısayol hizmeti başlatılamadı; görev çubuğu uygulamaları içe aktarılamadı.");
        }

        var apps = new List<DockApp>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skippedCount = 0;
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
                            LaunchArguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments
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
            return (apps, "Windows kısayol hizmeti durdu; görev çubuğu uygulamalarının bir bölümü içe aktarılamadı.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException)
        {
            return (apps, "Windows kısayol hizmeti durdu; görev çubuğu uygulamalarının bir bölümü içe aktarılamadı.");
        }
        finally
        {
            if (Marshal.IsComObject(shell))
            {
                _ = Marshal.FinalReleaseComObject(shell);
            }
        }

        var error = skippedCount == 0
            ? null
            : $"Windows görev çubuğundaki {skippedCount} kısayol desteklenmedi veya okunamadı; diğerleri içe aktarıldı.";
        return (apps, error);
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
