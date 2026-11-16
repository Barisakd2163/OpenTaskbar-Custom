using System.Globalization;
using System.Windows;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public partial class ClockWidget : DockWidget
{
    private string _lastMinute = string.Empty;

    public ClockWidget()
    {
        InitializeComponent();
    }

    protected override void OnSettingsChanged() => _lastMinute = string.Empty;

    protected override void OnTick(DateTime now)
    {
        var culture = CultureInfo.GetCultureInfo(Settings.Language == AppLanguage.English ? "en-US" : "tr-TR");
        var minute = now.ToString("g", culture);
        if (minute == _lastMinute) return;
        _lastMinute = minute;
        TimeText.Text = now.ToString("t", culture);
        DateText.Text = now.ToString("ddd d MMM", culture);
        ClockButton.ToolTip = now.ToString("D", culture);
    }

    private void ClockButton_Click(object sender, RoutedEventArgs e) => Report(ShellShortcuts.OpenNotificationCenter());
}
