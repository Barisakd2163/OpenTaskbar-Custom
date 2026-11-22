using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Resolves a running app to the Start Menu shortcut Windows itself would use to launch it. This is the
/// replacement for guessing a "main" executable from a helper path: ShellExecute on the real .lnk carries
/// the correct target, arguments, working directory and AUMID, so CEF/child/launcher processes just work.
/// Mirrors the approach used by other taskbar replacements (yasb, Firefox, ExplorerPatcher).
/// </summary>
internal static class AppShortcutResolver
{
    private sealed record Shortcut(string Path, string Target, string Arguments, string WorkingDirectory);

    private static readonly Lazy<IReadOnlyList<Shortcut>> Index = new(BuildIndex);
    private static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What to hand to ShellExecute. Target may be a .lnk or shell:AppsFolder\{AUMID}.</summary>
    internal sealed record LaunchSpec(string Target, string Arguments, string WorkingDirectory);

    internal static LaunchSpec Resolve(string? executablePath, string? appUserModelId, string? displayName = null)
    {
        // Packaged apps are activated by AUMID; no shortcut needed.
        if (!string.IsNullOrWhiteSpace(appUserModelId)
            && appUserModelId.EndsWith("!App", StringComparison.OrdinalIgnoreCase))
            return new LaunchSpec($"shell:AppsFolder\\{appUserModelId}", string.Empty, string.Empty);

        var shortcuts = Index.Value;
        var exe = Normalize(executablePath);

        // Explorer must not inherit a shortcut that adds folder arguments (e.g. the SDK folder shortcut,
        // private-browsing variants). The taskbar's Explorer button just opens a new window.
        if (exe is not null && string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase))
            return new LaunchSpec(exe, string.Empty, string.Empty);

        // 1. Exact target path. Prefer plain launchers over argument-bearing special-purpose shortcuts.
        if (exe is not null)
        {
            var exact = shortcuts
                .Where(s => string.Equals(Normalize(s.Target), exe, StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.Arguments.Length > 0 ? 1 : 0)
                .FirstOrDefault();
            if (exact is not null) return ToSpec(exact);
        }

        // 2. Shortcut target directory is an ancestor of the executable's directory (Steam helper case).
        if (exe is not null)
        {
            var exeDir = Path.GetDirectoryName(exe);
            var ancestor = shortcuts
                .Where(s => s.Target.Length > 0 && exeDir is not null
                    && IsAncestor(Path.GetDirectoryName(Normalize(s.Target)), exeDir))
                .OrderBy(s => s.Arguments.Length > 0 ? 1 : 0)
                .ThenByDescending(s => Path.GetDirectoryName(Normalize(s.Target))?.Length ?? 0)
                .FirstOrDefault();
            if (ancestor is not null) return ToSpec(ancestor);
        }

        // 3. Name hint from the dock button / process.
        foreach (var name in HintNames(executablePath, displayName))
        {
            if (NameCache.TryGetValue(name, out var cached)) return new LaunchSpec(cached, string.Empty, string.Empty);
            var match = shortcuts.FirstOrDefault(s =>
                string.Equals(Path.GetFileNameWithoutExtension(s.Path), name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                NameCache[name] = match.Path;
                return ToSpec(match);
            }
        }

        // Final fallback: no shortcut exists (portable apps). Keep the existing directory-walk heuristic.
        var fallback = LaunchTargetResolver.Resolve(executablePath);
        return new LaunchSpec(fallback.Path, string.Empty, string.Empty);
    }

    private static LaunchSpec ToSpec(Shortcut s) => new(s.Path, s.Arguments, s.WorkingDirectory);

    private static IEnumerable<string> HintNames(string? executablePath, string? displayName)
    {
        if (!string.IsNullOrWhiteSpace(displayName)) yield return displayName.Trim();
        if (!string.IsNullOrWhiteSpace(executablePath))
            yield return Path.GetFileNameWithoutExtension(executablePath);
    }

    private static bool IsAncestor(string? parent, string child)
    {
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(child)) return false;
        var p = parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var c = child.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
    }

    private static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path)); }
        catch { return path; }
    }

    private static IReadOnlyList<Shortcut> BuildIndex()
    {
        var result = new List<Shortcut>();
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return result;
            var shell = Activator.CreateInstance(shellType);
            if (shell is null) return result;
            try
            {
                var roots = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs")
                };
                foreach (var root in roots)
                {
                    if (!Directory.Exists(root)) continue;
                    foreach (var file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                                null, shell, [file]);
                            if (link is null) continue;
                            try
                            {
                                var target = ReadString(link, "TargetPath") ?? string.Empty;
                                if (target.Length == 0) continue;
                                result.Add(new Shortcut(file, Environment.ExpandEnvironmentVariables(target),
                                    ReadString(link, "Arguments") ?? string.Empty,
                                    ReadString(link, "WorkingDirectory") ?? string.Empty));
                            }
                            finally
                            {
                                if (Marshal.IsComObject(link)) _ = Marshal.FinalReleaseComObject(link);
                            }
                        }
                        catch (Exception ex) when (ex is COMException or TargetInvocationException or IOException)
                        {
                        }
                    }
                }
            }
            finally
            {
                if (Marshal.IsComObject(shell)) _ = Marshal.FinalReleaseComObject(shell);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning($"AppShortcutResolver index failed: {ex.Message}");
        }
        return result;
    }

    private static string? ReadString(object comObject, string property)
    {
        try
        {
            return comObject.GetType().InvokeMember(property, BindingFlags.GetProperty, null, comObject, null) as string;
        }
        catch { return null; }
    }
}
