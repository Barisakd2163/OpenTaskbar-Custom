using System.IO;
using System.Security;
using Microsoft.VisualBasic.FileIO;

namespace OpenTaskbar.Services;

internal static class DesktopFileDrop
{
    internal static string? Transfer(IReadOnlyList<string> paths, bool copy)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(desktop)) return "ErrorDesktopDropFailed";
        desktop = Path.TrimEndingDirectorySeparator(desktop);

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var transfers = new List<(string Source, string Destination, bool IsDirectory)>();
            foreach (var path in paths)
            {
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
                    || path.StartsWith(@"\\?\", StringComparison.Ordinal))
                    return "ErrorDesktopDropFailed";

                var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (!seen.Add(source)) continue;
                var name = Path.GetFileName(source);
                if (string.IsNullOrEmpty(name) || string.Equals(source, desktop, StringComparison.OrdinalIgnoreCase)
                    || desktop.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "ErrorDesktopDropFailed";

                var destination = Path.Combine(desktop, name);
                if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) continue;

                var isDirectory = Directory.Exists(source);
                if (!isDirectory && !File.Exists(source)) return "ErrorDesktopDropFailed";
                transfers.Add((source, destination, isDirectory));
            }

            foreach (var (source, destination, isDirectory) in transfers)
            {
                // Use Windows file dialogs for progress and collision handling.
                if (isDirectory)
                {
                    if (copy) FileSystem.CopyDirectory(source, destination, UIOption.AllDialogs);
                    else FileSystem.MoveDirectory(source, destination, UIOption.AllDialogs);
                }
                else if (copy) FileSystem.CopyFile(source, destination, UIOption.AllDialogs);
                else FileSystem.MoveFile(source, destination, UIOption.AllDialogs);
            }
            return null;
        }
        catch (OperationCanceledException)
        {
            return "ErrorDesktopDropIncomplete";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or InvalidOperationException or SecurityException)
        {
            AppLog.Warning($"Desktop file transfer failed: {ex.GetType().Name}.");
            return "ErrorDesktopDropFailed";
        }
    }
}
