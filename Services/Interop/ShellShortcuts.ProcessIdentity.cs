using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

public static partial class ShellShortcuts
{
    /// <summary>Queries one PID, without a system-wide Process snapshot or a persistent PID/name cache.</summary>
    internal static bool IsProcessImage(uint processId, ReadOnlySpan<char> executableFileName)
    {
        if (processId == 0 || executableFileName.IsEmpty) return false;
        var handle = OpenIdentityProcess(0x1000, false, processId); // QUERY_LIMITED_INFORMATION
        if (handle == 0) return false;
        char[]? buffer = null;
        try
        {
            buffer = ArrayPool<char>.Shared.Rent(512);
            uint length = (uint)buffer.Length;
            if (!QueryIdentityImage(handle, 0, buffer, ref length))
            {
                if (Marshal.GetLastPInvokeError() != 122) return false; // ERROR_INSUFFICIENT_BUFFER
                ArrayPool<char>.Shared.Return(buffer);
                buffer = null;
                buffer = ArrayPool<char>.Shared.Rent(32768);
                length = (uint)buffer.Length;
                if (!QueryIdentityImage(handle, 0, buffer, ref length)) return false;
            }
            return Path.GetFileName(buffer.AsSpan(0, checked((int)length)))
                .Equals(executableFileName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (buffer is not null) ArrayPool<char>.Shared.Return(buffer);
            CloseIdentityProcess(handle);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "OpenProcess", SetLastError = true)]
    private static extern nint OpenIdentityProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryIdentityImage(nint process, uint flags, [Out] char[] path, ref uint length);

    [DllImport("kernel32.dll", EntryPoint = "CloseHandle")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseIdentityProcess(nint process);
}
