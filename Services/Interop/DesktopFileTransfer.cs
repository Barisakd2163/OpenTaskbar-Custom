using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;

namespace OpenTaskbar.Services;

public readonly record struct DesktopTransferResult(bool Transferred, bool Aborted, string? ErrorKey);

/// <summary>
/// Transfers dropped files and folders into the user's Desktop directory via Win32 SHFileOperationW
/// so Explorer undo (Ctrl+Z), progress dialogs, and collision renaming all behave natively.
/// </summary>
public static class DesktopFileTransfer
{
    private const uint FoMove = 0x0001;
    private const uint FoCopy = 0x0002;
    private const ushort FofRenameOnCollision = 0x0008;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoConfirmMkdir = 0x0200;
    private const int ErrorCancelled = 0x04C7;

    public static string GetDesktopDirectory() =>
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static DragDropEffects ResolveFileDropEffect(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return DragDropEffects.None;

        var wantsCopy = e.KeyStates.HasFlag(DragDropKeyStates.ControlKey);
        if (wantsCopy && e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Move)) return DragDropEffects.Move;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        return DragDropEffects.None;
    }

    public static bool ShouldCopy(DragEventArgs e) =>
        e.KeyStates.HasFlag(DragDropKeyStates.ControlKey) || !e.AllowedEffects.HasFlag(DragDropEffects.Move);

    /// <summary>
    /// Filters and normalizes drop paths for Desktop transfer: requires full existing paths, skips drive roots,
    /// Desktop itself or its ancestors, and skips items already directly on Desktop when moving.
    /// </summary>
    internal static IReadOnlyList<string> FilterTransferPaths(
        IEnumerable<string?> paths,
        string desktopDirectory,
        bool copy)
    {
        var normalizedDesktop = NormalizePath(desktopDirectory);
        if (string.IsNullOrEmpty(normalizedDesktop)) return [];

        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in paths)
        {
            var full = NormalizePath(candidate);
            if (string.IsNullOrEmpty(full) || !seen.Add(full)) continue;
            if (IsDriveRoot(full)) continue;
            if (!File.Exists(full) && !Directory.Exists(full)) continue;

            // Never move or copy Desktop into itself or an ancestor directory into Desktop.
            if (string.Equals(full, normalizedDesktop, StringComparison.OrdinalIgnoreCase)
                || normalizedDesktop.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Moving an item that is already directly on the Desktop is a no-op; copying creates a renamed copy.
            if (!copy)
            {
                var parent = NormalizePath(Path.GetDirectoryName(full));
                if (string.Equals(parent, normalizedDesktop, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            results.Add(full);
        }

        return results;
    }

    /// <summary>Runs the shell file transfer on a dedicated background STA thread so neither the drag source nor the UI blocks.</summary>
    public static Task<DesktopTransferResult> TransferToDesktopAsync(IReadOnlyList<string> paths, bool copy)
    {
        var completion = new TaskCompletionSource<DesktopTransferResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                // Passing hwnd = 0 avoids cross-thread modality deadlock if Windows shows a progress or conflict dialog.
                completion.SetResult(TransferToDesktop(paths, copy, owner: 0));
            }
            catch (Exception ex)
            {
                AppLog.Warning($"Desktop file transfer failed: {ex.Message}");
                completion.SetResult(new DesktopTransferResult(false, false, "ErrorDesktopDropFailed"));
            }
        })
        {
            IsBackground = true,
            Name = "OpenTaskbar desktop transfer"
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    public static DesktopTransferResult TransferToDesktop(
        IReadOnlyList<string> paths,
        bool copy,
        nint owner = 0,
        string? desktopDirectory = null)
    {
        if (paths.Count == 0) return new DesktopTransferResult(false, false, null);

        var desktop = NormalizePath(desktopDirectory ?? GetDesktopDirectory());
        if (string.IsNullOrEmpty(desktop))
        {
            return new DesktopTransferResult(false, false, "ErrorDesktopDropFailed");
        }

        var filtered = FilterTransferPaths(paths, desktop, copy);
        if (filtered.Count == 0)
        {
            return new DesktopTransferResult(false, false, null);
        }

        var fromBuffer = string.Join('\0', filtered) + "\0\0";
        var toBuffer = desktop + "\0\0";
        nint fromPtr = 0;
        nint toPtr = 0;
        try
        {
            fromPtr = AllocDoubleNullString(fromBuffer);
            toPtr = AllocDoubleNullString(toBuffer);

            var operation = new FileOperation
            {
                Owner = owner,
                Function = copy ? FoCopy : FoMove,
                From = fromPtr,
                To = toPtr,
                Flags = FofAllowUndo | FofNoConfirmMkdir | FofRenameOnCollision
            };

            var result = SHFileOperation(ref operation);
            if (result == 0 && !operation.Aborted)
            {
                return new DesktopTransferResult(true, false, null);
            }

            if (operation.Aborted || result == ErrorCancelled)
            {
                return new DesktopTransferResult(false, true, null);
            }

            AppLog.Warning($"SHFileOperation desktop {(copy ? "copy" : "move")} failed: 0x{result:X}");
            return new DesktopTransferResult(false, false, "ErrorDesktopDropFailed");
        }
        finally
        {
            if (fromPtr != 0) Marshal.FreeHGlobal(fromPtr);
            if (toPtr != 0) Marshal.FreeHGlobal(toPtr);
        }
    }

    public static string? OpenDesktopFolder()
    {
        var desktop = GetDesktopDirectory();
        if (string.IsNullOrWhiteSpace(desktop) || !Directory.Exists(desktop))
        {
            return "ErrorOpenFileOrFolder";
        }

        try
        {
            using var _ = Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            AppLog.Warning($"Cannot open Desktop folder: {ex.Message}");
            return "ErrorOpenFileOrFolder";
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim());
            return Path.TrimEndingDirectorySeparator(full);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    private static bool IsDriveRoot(string normalizedPath)
    {
        var root = Path.GetPathRoot(normalizedPath);
        if (string.IsNullOrEmpty(root)) return false;
        var trimmedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(normalizedPath, root, StringComparison.OrdinalIgnoreCase)
            || string.Equals(normalizedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static nint AllocDoubleNullString(string value)
    {
        var chars = value.ToCharArray();
        var ptr = Marshal.AllocHGlobal(checked(chars.Length * sizeof(char)));
        Marshal.Copy(chars, 0, ptr, chars.Length);
        return ptr;
    }

    // shellapi.h packs SHFILEOPSTRUCT to 1 byte only on 32-bit builds; this app is x64-only.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Owner;
        public uint Function;
        public nint From;
        public nint To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint NameMappings;
        public nint ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref FileOperation operation);
}
