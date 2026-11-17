using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>
/// Loads a country flag PNG from embedded resources. Flags are stored as
/// Services/Localization/Flags/&lt;culture&gt;.png and embedded under OpenTaskbar.Localization.Flags.
/// </summary>
internal static class CountryFlag
{
    private const string ResourcePrefix = "OpenTaskbar.Localization.Flags.";

    public static ImageSource? Load(AppLanguage language)
    {
        var name = ResourcePrefix + language.CultureName + ".png";
        var assembly = typeof(CountryFlag).Assembly;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null) return null;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.DecodePixelWidth = 32;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
