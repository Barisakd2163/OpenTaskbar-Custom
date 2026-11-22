using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Detects whether OpenTaskbar is running inside a packaged MSIX (Microsoft Store) container
/// so Store-specific policies (StartupTask, Store-managed updates, package-local watchdog)
/// activate automatically while portable GitHub builds keep their standalone behavior.
/// </summary>
public static class AppIdentity
{
    public const string StartupTaskId = "OpenTaskbarStartup";
    public const string StoreUpdatesUri = "ms-windows-store://downloadsandupdates";

    private const int ErrorSuccess = 0;
    private const int ErrorInsufficientBuffer = 122;

    public static bool IsPackaged { get; } = DetectPackaged();

    private static bool DetectPackaged()
    {
        try
        {
            uint length = 0;
            var result = GetCurrentPackageFullName(ref length, nint.Zero);
            return result is ErrorSuccess or ErrorInsufficientBuffer;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, nint packageFullName);
}
