using System.Collections.Concurrent;
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
    private const uint Th32csSnapProcess = 0x00000002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaxCacheEntries = 64;

    private static readonly ConcurrentDictionary<Guid, string?> KnownFolderCache = new();
    private static readonly ConcurrentDictionary<string, string> DescriptionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, CachedIconEntry> IconCache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly int CurrentSessionId = ResolveCurrentSessionId();

    private sealed record CandidateEntry(string Path, string FileName, string? Tooltip, byte[]? SnapshotBytes, bool IsPromoted, bool PreferExeIcon);
    private sealed record CachedIconEntry(int SnapshotLen, int SnapshotHash, bool PreferExeIcon, BitmapSource? Icon);

    public static IReadOnlyList<TrayApp> RunningApps()
    {
        var candidates = ReadCandidateEntries();
        if (candidates.Count == 0) return [];

        var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            targetNames.Add(candidate.FileName);
        }

        var running = RunningExecutables(targetNames);
        if (running.Count == 0) return [];

        var apps = new List<TrayApp>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates
            .OrderByDescending(c => c.IsPromoted)
            .ThenBy(c => IsDriverStorePath(c.Path)))
        {
            if (!running.Contains(candidate.Path)) continue;
            var name = string.IsNullOrWhiteSpace(candidate.Tooltip)
                ? DescribeExecutable(candidate.Path)
                : candidate.Tooltip;
            if (!seenNames.Add(name)) continue;
            apps.Add(new TrayApp(name, candidate.Path, GetCachedIcon(candidate.Path, candidate.SnapshotBytes, candidate.PreferExeIcon)));
        }

        apps.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return apps;
    }

    internal static bool IsTransientTooltip(string? rawTooltip)
    {
        if (string.IsNullOrWhiteSpace(rawTooltip)) return false;
        return rawTooltip.Contains("starting", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("initializing", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("installing", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("updating", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("connecting", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("loading", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("başlatılıyor", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("yükleniyor", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("güncelleniyor", StringComparison.OrdinalIgnoreCase)
            || rawTooltip.Contains("bağlanıyor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDriverStorePath(string path) =>
        path.Contains(@"\DriverStore\FileRepository\", StringComparison.OrdinalIgnoreCase);

    private static List<CandidateEntry> ReadCandidateEntries()
    {
        var candidates = new List<CandidateEntry>();
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(SettingsKey);
            if (root is null) return candidates;
            foreach (var name in root.GetSubKeyNames())
            {
                using var entry = root.OpenSubKey(name);
                if (entry?.GetValue("ExecutablePath") is not string rawPath) continue;
                var path = ResolveKnownFolderPath(rawPath);
                if (path is null) continue;
                var fileName = Path.GetFileName(path);
                if (string.IsNullOrEmpty(fileName) || fileName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) continue;

                var rawTooltip = (entry.GetValue("InitialTooltip") as string)?.Trim('\u202a', '\u202c', '\u200e', '\u200f', ' ');
                var preferExe = IsTransientTooltip(rawTooltip);
                var tooltip = rawTooltip?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                var isPromoted = entry.GetValue("IsPromoted") as int? == 1;
                candidates.Add(new CandidateEntry(path, fileName, tooltip, entry.GetValue("IconSnapshot") as byte[], isPromoted, preferExe));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warning($"Tray icon settings unavailable: {ex.Message}");
        }

        return candidates
            .OrderByDescending(candidate => candidate.IsPromoted)
            .ThenBy(candidate => candidate.PreferExeIcon)
            .GroupBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
    }

    /// <summary>Focus an existing app window when possible; otherwise use the application's normal launch behavior.</summary>
    public static string? Open(TrayApp app)
    {
        try
        {
            // Many tray apps are already running. Prefer their existing window to launching a second copy.
            foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(app.ExecutablePath)))
            {
                using (process)
                {
                    try
                    {
                        if (!string.Equals(process.MainModule?.FileName, app.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                            continue;
                        var window = process.MainWindowHandle;
                        if (window != 0 && NativeWindowTracker.Activate(window)) return null;
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        // Elevated processes may not expose MainModule; use the normal launch fallback.
                    }
                }
            }

            using var launched = Process.Start(new ProcessStartInfo(app.ExecutablePath) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warning($"Cannot open tray app {app.ExecutablePath}: {ex.Message}");
            return "ErrorOpenBackgroundApp";
        }
    }

    private static HashSet<string> RunningExecutables(HashSet<string> targetFileNames)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == 0 || snapshot == new nint(-1)) return paths;

        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32FirstW(snapshot, ref entry)) return paths;

            var buffer = new char[1024];
            do
            {
                if (entry.ProcessId == 0 || string.IsNullOrEmpty(entry.ExeFile)) continue;
                if (!targetFileNames.Contains(entry.ExeFile)) continue;
                if (CurrentSessionId >= 0 && (!ProcessIdToSessionId(entry.ProcessId, out var sessionId) || sessionId != CurrentSessionId)) continue;

                // QueryFullProcessImageName works for elevated processes that Process.MainModule cannot open.
                var handle = OpenProcess(ProcessQueryLimitedInformation, false, (int)entry.ProcessId);
                if (handle == 0) continue;
                try
                {
                    var length = buffer.Length;
                    if (QueryFullProcessImageName(handle, 0, buffer, ref length))
                    {
                        paths.Add(new string(buffer, 0, length));
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }
            while (Process32NextW(snapshot, ref entry));
        }
        finally
        {
            CloseHandle(snapshot);
        }

        return paths;
    }

    /// <summary>Paths are stored as <c>{KNOWNFOLDERID}\relative\path.exe</c> or as plain absolute paths.</summary>
    private static string? ResolveKnownFolderPath(string path)
    {
        if (!path.StartsWith('{'))
        {
            return path;
        }

        var end = path.IndexOf('}');
        if (end < 0 || !Guid.TryParse(path[..(end + 1)], out var folderId)) return null;
        var folder = KnownFolderCache.GetOrAdd(folderId, static id =>
        {
            if (SHGetKnownFolderPath(ref id, 0, 0, out var folderPointer) != 0) return null;
            try
            {
                return Marshal.PtrToStringUni(folderPointer);
            }
            finally
            {
                Marshal.FreeCoTaskMem(folderPointer);
            }
        });

        return folder is null ? null : Path.Combine(folder, path[(end + 1)..].TrimStart('\\'));
    }

    private static string DescribeExecutable(string path)
    {
        if (DescriptionCache.TryGetValue(path, out var cached)) return cached;
        string? description;
        try
        {
            description = FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            description = null;
        }
        var resolved = string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description.Trim();
        if (DescriptionCache.Count >= MaxCacheEntries) DescriptionCache.Clear();
        DescriptionCache[path] = resolved;
        return resolved;
    }

    private static BitmapSource? GetCachedIcon(string path, byte[]? png, bool preferExeIcon)
    {
        var len = png?.Length ?? 0;
        var hash = ComputeBytesHash(png);
        if (IconCache.TryGetValue(path, out var entry)
            && entry.SnapshotLen == len
            && entry.SnapshotHash == hash
            && entry.PreferExeIcon == preferExeIcon)
        {
            return entry.Icon;
        }

        var icon = preferExeIcon
            ? ApplicationIconProvider.GetForFile(path) ?? DecodeSnapshot(png)
            : DecodeSnapshot(png) ?? ApplicationIconProvider.GetForFile(path);
        if (IconCache.Count >= MaxCacheEntries) IconCache.Clear();
        IconCache[path] = new CachedIconEntry(len, hash, preferExeIcon, icon);
        return icon;
    }

    private static int ResolveCurrentSessionId() =>
        ProcessIdToSessionId((uint)Environment.ProcessId, out var sessionId) ? (int)sessionId : -1;

    private static int ComputeBytesHash(byte[]? data)
    {
        if (data is null || data.Length == 0) return 0;
        unchecked
        {
            var hash = (int)2166136261;
            for (var i = 0; i < data.Length; i++)
            {
                hash = (hash ^ data[i]) * 16777619;
            }
            return hash;
        }
    }

    private static BitmapSource? DecodeSnapshot(byte[]? png)
    {
        if (png is not { Length: > 0 and <= 524288 }) return null;
        try
        {
            using var stream = new MemoryStream(png);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            // Cap oversized PNG snapshots at 32px (rendered at 20 DIP) without upscaling smaller icons.
            if (TryReadPngWidth(png, out var width) && width > 32)
            {
                bitmap.DecodePixelWidth = 32;
            }
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool TryReadPngWidth(ReadOnlySpan<byte> bytes, out int width)
    {
        width = 0;
        // PNG signature (8) + IHDR length (4) + "IHDR" (4) + width (4)
        if (bytes.Length < 24 || bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4E || bytes[3] != 0x47)
            return false;
        width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(20, 4));
        return width is > 0 and <= 2048 && height is > 0 and <= 2048;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(nint hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(nint hSnapshot, ref ProcessEntry32 lppe);

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

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
}
