using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace OpenTaskbar.Services;

public sealed record TrayApp(string Name, string ExecutablePath, BitmapSource? Icon);

/// <summary>
/// Background apps that own a notification-area icon. Windows 11 records every tray icon (path, tooltip and an icon
/// snapshot) under <c>HKCU\Control Panel\NotifyIconSettings</c>; the hidden taskbar cannot host the live icons, so
/// the dock lists the running ones and opens the app itself.
/// </summary>
public static class TrayIcons
{
    private const string SettingsKey = @"Control Panel\NotifyIconSettings";

    public static IReadOnlyList<TrayApp> RunningApps()
    {
        var running = RunningExecutables();
        var apps = new Dictionary<string, TrayApp>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (root is null) return [];
            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name);
                if (entry?.GetValue("ExecutablePath") is not string rawPath) continue;
                var path = ResolveKnownFolder(rawPath);
                if (path is null || apps.ContainsKey(path) || !running.Contains(path)) continue;
                var fileName = Path.GetFileName(path);
                if (fileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;
                var tooltip = (entry.GetValue("InitialTooltip") as string)?.Trim('‪', '‬', ' ').Split('\n')[0];
                apps[path] = new TrayApp(
                    string.IsNullOrWhiteSpace(tooltip) ? DescribeExecutable(path) : tooltip,
                    path,
                    DecodeSnapshot(entry.GetValue("IconSnapshot") as byte[]) ?? ApplicationIconProvider.GetForFile(path));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warning($"Tray icon settings unavailable: {ex.Message}");
        }

        return apps.Values.OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Starting a running tray app again brings up its window for most single-instance apps.</summary>
    public static string? Open(TrayApp app)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(app.ExecutablePath) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warning($"Cannot open tray app {app.ExecutablePath}: {ex.Message}");
            return "Arka plan uygulaması açılamadı.";
        }
    }

    private static HashSet<string> RunningExecutables()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[1024];
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                // QueryFullProcessImageName works for elevated processes that Process.MainModule cannot open.
                var handle = OpenProcess(0x1000, false, process.Id);
                if (handle == 0) continue;
                try
                {
                    var length = buffer.Length;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref length)) paths.Add(new string(buffer, 0, length));
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
        }

        return paths;
    }

    /// <summary>Paths are stored as <c>{KNOWNFOLDERID}\relative\path.exe</c> or as plain absolute paths.</summary>
    private static string? ResolveKnownFolder(string path)
    {
        if (!path.StartsWith('{'))
        {
            return File.Exists(path) ? path : null;
        }

        var end = path.IndexOf('}');
        if (end < 0 || !Guid.TryParse(path[..(end + 1)], out var folderId)) return null;
        if (SHGetKnownFolderPath(ref folderId, 0, 0, out var folderPointer) != 0) return null;
        try
        {
            var folder = Marshal.PtrToStringUni(folderPointer);
            if (folder is null) return null;
            var resolved = Path.Combine(folder, path[(end + 1)..].TrimStart('\\'));
            return File.Exists(resolved) ? resolved : null;
        }
        finally
        {
            Marshal.FreeCoTaskMem(folderPointer);
        }
    }

    private static string DescribeExecutable(string path)
    {
        var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        return string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description.Trim();
    }

    private static BitmapSource? DecodeSnapshot(byte[]? png)
    {
        if (png is not { Length: > 0 }) return null;
        try
        {
            using var stream = new MemoryStream(png);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException)
        {
            return null;
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid folderId, uint flags, nint token, out nint path);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, char[] name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
