using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace OpenTaskbar.Services;

public static class ApplicationIconProvider
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ShellIcon = 0x00000100;
    private const uint ShellSmallIcon = 0x00000001;
    private const uint WmGetIcon = 0x007F;
    private const uint SmtoAbortIfHung = 0x0002;
    private const uint IconRequestTimeoutMs = 75;
    private const nint IconSmall2 = 2;
    private const nint IconBig = 1;
    private const int GclpHIcon = -14;
    private const int GclpHIconSmall = -34;

    public static BitmapSource? GetForWindow(nint windowHandle)
    {
        var iconHandle = RequestWindowIcon(windowHandle, IconSmall2);
        if (iconHandle == 0)
        {
            iconHandle = RequestWindowIcon(windowHandle, IconBig);
        }

        if (iconHandle == 0)
        {
            iconHandle = GetClassLongPtr(windowHandle, GclpHIconSmall);
        }

        if (iconHandle == 0)
        {
            iconHandle = GetClassLongPtr(windowHandle, GclpHIcon);
        }

        var bitmap = ToBitmapSource(iconHandle);
        if (bitmap is not null)
        {
            return bitmap;
        }

        var processId = GetWindowProcessId(windowHandle);
        if (processId == 0)
        {
            return null;
        }

        var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (processHandle == 0)
        {
            return null;
        }

        try
        {
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            return QueryFullProcessImageName(processHandle, 0, path, ref length)
                ? GetForFile(path.ToString())
                : null;
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    public static BitmapSource? GetForFile(string path)
    {
        var fileInfo = new ShellFileInfo();
        var result = SHGetFileInfo(path, 0, ref fileInfo, (uint)Marshal.SizeOf<ShellFileInfo>(), ShellIcon | ShellSmallIcon);
        if (result == 0 || fileInfo.Icon == 0)
        {
            return null;
        }

        try
        {
            return ToBitmapSource(fileInfo.Icon);
        }
        finally
        {
            DestroyIcon(fileInfo.Icon);
        }
    }

    private static BitmapSource? ToBitmapSource(nint iconHandle)
    {
        if (iconHandle == 0)
        {
            return null;
        }

        var iconCopy = CopyIcon(iconHandle);
        if (iconCopy == 0)
        {
            return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(iconCopy, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            DestroyIcon(iconCopy);
        }
    }

    private static uint GetWindowProcessId(nint windowHandle)
    {
        _ = GetWindowThreadProcessId(windowHandle, out var processId);
        return processId;
    }

    private static nint RequestWindowIcon(nint windowHandle, nint iconSize)
    {
        return SendMessageTimeout(windowHandle, WmGetIcon, iconSize, 0,
            SmtoAbortIfHung, IconRequestTimeoutMs, out var iconHandle) == 0
            ? 0
            : iconHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string? TypeName;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint windowHandle, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nint result);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW", SetLastError = true)]
    private static extern nint GetClassLongPtr(nint windowHandle, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint windowHandle, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint processHandle, uint flags, StringBuilder executablePath, ref uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint fileAttributes, ref ShellFileInfo fileInfo, uint fileInfoSize, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint iconHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CopyIcon(nint iconHandle);
}
