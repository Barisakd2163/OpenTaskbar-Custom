using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>Current temperature and conditions; the flyout shows a short forecast.</summary>
public sealed class WeatherWidget : DockWidget
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
    private readonly ContentControl _icon;
    private readonly TextBlock _temperature;
    private readonly Button _card;
    private readonly DockFlyout _flyout = new();
    private WeatherReport? _report;
    private string? _error;
    private DateTime _loadedAt = DateTime.MinValue;
    private string? _loadedFor;
    private CancellationTokenSource? _loading;

    public WeatherWidget()
    {
        _icon = new ContentControl { Width = 18, Height = 18, Margin = new Thickness(0, 0, 6, 0) };
        _icon.SetResourceReference(StyleProperty, "WidgetIcon");
        _icon.SetResourceReference(ForegroundProperty, "TextPrimary");
        _temperature = new TextBlock { FontSize = 15 };
        _temperature.SetResourceReference(StyleProperty, "WidgetText");
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(_icon);
        content.Children.Add(_temperature);
        _card = new Button { Content = content, Padding = new Thickness(10, 0, 12, 0) };
        _card.SetResourceReference(StyleProperty, "WidgetCardButton");
        _card.Click += (_, _) => _flyout.Toggle(_card, BuildFlyout);
        Content = _card;
        HasData = false;
        UiText.Current.PropertyChanged += (_, _) => Render();
    }

    private string LocationKey => $"{Settings.WeatherCity.Trim()}|{Settings.Language}";

    protected override void OnActivated() => _ = LoadAsync();

    protected override void OnDeactivated()
    {
        _loading?.Cancel();
        _flyout.Close();
    }

    protected override void OnSettingsChanged()
    {
        if (IsActive && _loadedFor is not null && _loadedFor != LocationKey) _ = LoadAsync();
    }

    protected override void OnTick(DateTime now)
    {
        if (now - _loadedAt > (_report is null ? RetryInterval : RefreshInterval) && _loading is null) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        var loading = new CancellationTokenSource();
        _loading = loading;
        _loadedAt = DateTime.Now;
        var key = LocationKey;
        var (report, error) = await WeatherService.LoadAsync(Settings.WeatherCity, Settings.Language == AppLanguage.English, loading.Token);
        if (!ReferenceEquals(_loading, loading)) return;
        _loading = null;
        if (!IsActive) return;
        // Keep the last good report through brief network failures.
        if (report is not null || _loadedFor != key) _report = report;
        // Without a city, missing location access just hides the widget instead of raising an error.
        if (error is not null && _loadedFor != key && !string.IsNullOrWhiteSpace(Settings.WeatherCity)) Report(error);
        _error = _report is null ? error : null;
        _loadedFor = key;
        Render();
    }

    private void Render()
    {
        // A failed load stays visible with its cause instead of silently hiding the widget.
        HasData = _report is not null || _error is not null;
        if (_report is not { } report)
        {
            _icon.Content = null;
            _temperature.Text = UiText.Get("WeatherUnavailable");
            _card.ToolTip = _error;
            System.Windows.Automation.AutomationProperties.SetName(_card, $"{UiText.Get("Weather")}: {_error}");
            return;
        }
        var (icon, text) = WeatherService.Describe(report.Code);
        _icon.Content = FindResource(icon);
        _temperature.Text = FormatDegrees(report.Temperature);
        _card.ToolTip = string.IsNullOrEmpty(report.Place) ? UiText.Get(text) : $"{report.Place} · {UiText.Get(text)}";
        System.Windows.Automation.AutomationProperties.SetName(_card, $"{UiText.Get("Weather")}: {_temperature.Text}, {_card.ToolTip}");
    }

    private static string FormatDegrees(double value) => $"{Math.Round(value).ToString(CultureInfo.CurrentCulture)}°";

    private UIElement BuildFlyout()
    {
        var root = new StackPanel { Width = 280 };
        if (_report is not { } report)
        {
            root.Children.Add(DockFlyout.Heading(UiText.Get("Weather")));
            root.Children.Add(DockFlyout.Caption(_error ?? UiText.Get("WeatherUnavailable")));
            return root;
        }
        var (icon, text) = WeatherService.Describe(report.Code);
        root.Children.Add(DockFlyout.Heading(string.IsNullOrEmpty(report.Place) ? UiText.Get("Weather") : report.Place));
        var now = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 10) };
        var bigIcon = new ContentControl { Content = FindResource(icon), Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) };
        bigIcon.SetResourceReference(StyleProperty, "WidgetIcon");
        bigIcon.SetResourceReference(ForegroundProperty, "TextPrimary");
        var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        summary.Children.Add(new TextBlock { Text = FormatDegrees(report.Temperature), FontSize = 28, FontWeight = FontWeights.Light });
        summary.Children.Add(DockFlyout.Caption($"{UiText.Get(text)} · {UiText.Format("WeatherFeelsLike", FormatDegrees(report.ApparentTemperature))}"));
        now.Children.Add(bigIcon);
        now.Children.Add(summary);
        root.Children.Add(now);
        var culture = CultureInfo.GetCultureInfo(Settings.Language == AppLanguage.English ? "en-US" : "tr-TR");
        var days = new UniformGrid { Columns = Math.Max(1, report.Days.Count) };
        foreach (var day in report.Days)
        {
            var column = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(2) };
            column.Children.Add(DockFlyout.Caption(day.Date.ToString("ddd", culture)));
            var dayIcon = new ContentControl { Content = FindResource(WeatherService.Describe(day.Code).Icon), Width = 20, Height = 20, Margin = new Thickness(0, 4, 0, 4) };
            dayIcon.SetResourceReference(StyleProperty, "WidgetIcon");
            dayIcon.SetResourceReference(ForegroundProperty, "TextPrimary");
            column.Children.Add(dayIcon);
            column.Children.Add(new TextBlock { Text = $"{FormatDegrees(day.Max)} / {FormatDegrees(day.Min)}", FontSize = 11.5, HorizontalAlignment = HorizontalAlignment.Center });
            days.Children.Add(column);
        }

        root.Children.Add(days);
        root.Children.Add(DockFlyout.Caption("Open-Meteo"));
        return root;
    }
}
