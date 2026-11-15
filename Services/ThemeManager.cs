using System.Windows;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static class ThemeManager
{
    private static ResourceDictionary? _palette;
    public static void Apply(AppTheme theme)
    {
        var source = $"/OpenTaskbar;component/Design/Palettes/{theme}.xaml";
        if (_palette?.Source?.OriginalString == source) return;
        var next = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        if (_palette is not null) dictionaries.Remove(_palette);
        dictionaries.Add(next);
        _palette = next;
    }
}
