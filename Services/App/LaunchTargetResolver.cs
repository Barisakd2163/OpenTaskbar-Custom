using System.IO;

namespace OpenTaskbar.Services;

/// <summary>
/// Maps helper/child executables (Steam's steamwebhelper.exe under bin\cef, CEF subprocesses, etc.) back to
/// the application's real launcher. Windows pins created from a running window otherwise store the helper
/// path, which is not a launcher and silently fails to open the app (#128 follow-up).
/// </summary>
internal static class LaunchTargetResolver
{
    private static readonly string[] HelperMarkers =
        ["helper", "cef", "subprocess", "crashhandler", "webhelper"];

    /// <summary>Returns the executable to ShellExecute, plus whether launch arguments must be dropped.</summary>
    public static (string Path, bool DropArguments) Resolve(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return (executablePath ?? string.Empty, false);

        string full;
        try { full = Path.GetFullPath(executablePath); }
        catch { return (executablePath, false); }
        if (!File.Exists(full)) return (executablePath, false);

        // Packaged Store apps are launched through their AUMID, not by path.
        if (full.Contains($"{Path.DirectorySeparatorChar}WindowsApps{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            return (full, false);

        if (!LooksLikeHelper(full)) return (full, false);

        // Walk up from the helper towards the install root. A directory named after its own launcher
        // (Steam\steam.exe, Firefox\firefox.exe, ...) is the canonical app root.
        var directory = Path.GetDirectoryName(full);
        for (var depth = 0; depth < 6 && !string.IsNullOrEmpty(directory); depth++)
        {
            var name = new DirectoryInfo(directory).Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var candidate = Path.Combine(directory, name + ".exe");
                if (!string.Equals(candidate, full, StringComparison.OrdinalIgnoreCase) && File.Exists(candidate))
                    return (candidate, true);
            }
            directory = Path.GetDirectoryName(directory);
        }

        return (full, false);
    }

    private static bool LooksLikeHelper(string path)
    {
        var fileName = Path.GetFileName(path);
        foreach (var marker in HelperMarkers)
            if (fileName.Contains(marker, StringComparison.OrdinalIgnoreCase)) return true;

        var segments = path.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        // Only treat generic folders as helpers below the install root, never the root itself.
        for (var i = 1; i < segments.Length - 1; i++)
            if (segments[i].Equals("cef", StringComparison.OrdinalIgnoreCase)
                || segments[i].StartsWith("cef.", StringComparison.OrdinalIgnoreCase)
                || segments[i].Equals("bin", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
