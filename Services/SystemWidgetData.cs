using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

internal static class SystemWidgetData
{
    public static string ReadMemory(CultureInfo culture)
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref status)
            ? (status.MemoryLoad / 100d).ToString("P0", culture)
            : UiText.Get("SystemDataUnavailable");
    }

    public static string ReadStorage(CultureInfo culture)
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (root is null) return UiText.Get("SystemDataUnavailable");
        try
        {
            var drive = new DriveInfo(root);
            return drive.IsReady
                ? UiText.Format("StorageFree", (drive.AvailableFreeSpace / 1073741824d).ToString("N1", culture))
                : UiText.Get("SystemDataUnavailable");
        }
        catch (IOException) { return UiText.Get("SystemDataUnavailable"); }
        catch (UnauthorizedAccessException) { return UiText.Get("SystemDataUnavailable"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
