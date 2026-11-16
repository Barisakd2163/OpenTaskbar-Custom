using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Services;

/// <summary>Recycle Bin state, drop-to-delete and emptying through the Shell, so Windows keeps its own prompts and undo.</summary>
public static class RecycleBin
{
    private const uint FoDelete = 0x0003;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofWantNukeWarning = 0x4000;
    private static BitmapSource? _emptyIcon;
    private static BitmapSource? _fullIcon;

    public static BitmapSource? EmptyIcon => _emptyIcon ??= ApplicationIconProvider.GetForResource(@"%SystemRoot%\System32\imageres.dll", -55);
    public static BitmapSource? FullIcon => _fullIcon ??= ApplicationIconProvider.GetForResource(@"%SystemRoot%\System32\imageres.dll", -54);

    /// <summary>Item count across all drives, or null when the Shell cannot report it.</summary>
    public static long? ItemCount()
    {
        var info = new QueryInfo { Size = Marshal.SizeOf<QueryInfo>() };
        return SHQueryRecycleBin(null, ref info) == 0 ? info.Items : null;
    }

    public static string? Open() => ShellShortcuts.OpenUri("shell:RecycleBinFolder");

    /// <summary>Moves files to the Recycle Bin; Windows shows its own confirmation and progress UI.</summary>
    public static string? Delete(IReadOnlyList<string> paths, nint owner)
    {
        if (paths.Count == 0) return null;
        var operation = new FileOperation
        {
            Owner = owner,
            Function = FoDelete,
            From = string.Join('\0', paths) + "\0\0",
            Flags = FofAllowUndo | FofWantNukeWarning
        };
        var result = SHFileOperation(ref operation);
        if (result == 0 || operation.Aborted) return null;
        AppLog.Warning($"SHFileOperation delete failed: 0x{result:X}");
        return "Dosyalar Geri Dönüşüm Kutusu'na taşınamadı.";
    }

    /// <summary>Empties the bin; Windows asks the user for confirmation first.</summary>
    public static string? Empty(nint owner)
    {
        var result = SHEmptyRecycleBin(owner, null, 0);
        // S_OK, or E_UNEXPECTED / cancelled when the bin is already empty or the user declines.
        if (result is 0 or unchecked((int)0x8000FFFF) or unchecked((int)0x800704C7)) return null;
        AppLog.Warning($"SHEmptyRecycleBin failed: 0x{result:X8}");
        return "Geri Dönüşüm Kutusu boşaltılamadı.";
    }

    // shellapi.h packs these structs to 1 byte only on 32-bit builds; this app is x64-only.
    [StructLayout(LayoutKind.Sequential)]
    private struct QueryInfo
    {
        public int Size;
        public long Bytes;
        public long Items;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public nint Owner;
        public uint Function;
        public string From;
        public string? To;
        public ushort Flags;
        [MarshalAs(UnmanagedType.Bool)] public bool Aborted;
        public nint NameMappings;
        public string? ProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string? rootPath, ref QueryInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
    private static extern int SHFileOperation(ref FileOperation operation);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(nint owner, string? rootPath, uint flags);
}
