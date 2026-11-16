using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

internal static class SystemWidgetData
{
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
}
