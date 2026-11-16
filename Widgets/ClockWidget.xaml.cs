using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public partial class ClockWidget : DockWidget
{
    private readonly DockFlyout _calendar = new();
    private string _lastRenderKey = string.Empty;
    private DateOnly _shownMonth;
    private DateTime _lastWeatherFetchUtc = DateTime.MinValue;

    public ClockWidget()
    {
        InitializeComponent();
        WeatherService.ReportUpdated += _ => Dispatcher.BeginInvoke(UpdateWeatherDisplay);
    }

    private CultureInfo Culture => CultureInfo.GetCultureInfo(Settings.Language == AppLanguage.English ? "en-US" : "tr-TR");

    protected override void OnActivated()
    {
        _lastRenderKey = string.Empty;
        OnTick(DateTime.Now);
        EnsureWeatherLoaded();
    }

    protected override void OnSettingsChanged()
    {
        _lastRenderKey = string.Empty;
        if (IsActive)
        {
            OnTick(DateTime.Now);
            EnsureWeatherLoaded();
        }
    }

    protected override void OnDeactivated() => _calendar.Close();

    protected override void OnTick(DateTime now)
    {
        var culture = Culture;
        var timeFormat = Settings.Clock24Hour
            ? (Settings.ClockShowSeconds ? "HH:mm:ss" : "HH:mm")
            : (Settings.ClockShowSeconds ? "hh:mm:ss tt" : "hh:mm tt");
        var timeString = now.ToString(timeFormat, culture);
        var renderKey = $"{timeString}|{Settings.DateStyle}|{Settings.Language}";
        if (renderKey != _lastRenderKey)
        {
            _lastRenderKey = renderKey;
            TimeText.Text = timeString;
            switch (Settings.DateStyle)
            {
                case ClockDateStyle.DayAndShortDate:
                    DateColumn.Visibility = Visibility.Visible;
                    DayOfWeekText.Visibility = Visibility.Visible;
                    DayOfWeekText.Text = now.ToString("dddd", culture).ToUpper(culture);
                    DateText.Text = now.ToString(Settings.Language == AppLanguage.English ? "MMM d" : "d MMM", culture);
                    break;
                case ClockDateStyle.ShortDateOnly:
                    DateColumn.Visibility = Visibility.Visible;
                    DayOfWeekText.Visibility = Visibility.Collapsed;
                    DateText.Text = now.ToString(Settings.Language == AppLanguage.English ? "MMM d, yyyy" : "d MMM yyyy", culture);
                    break;
                case ClockDateStyle.NumericDate:
                    DateColumn.Visibility = Visibility.Visible;
                    DayOfWeekText.Visibility = Visibility.Collapsed;
                    DateText.Text = now.ToString("d", culture);
                    break;
                case ClockDateStyle.Hidden:
                    DateColumn.Visibility = Visibility.Collapsed;
                    break;
            }
            ClockButton.ToolTip = now.ToString("D", culture);
        }

        if (Settings.ClockShowWeather && (DateTime.UtcNow - _lastWeatherFetchUtc).TotalMinutes >= 30)
        {
            EnsureWeatherLoaded();
        }
        UpdateWeatherDisplay();
    }

    private void EnsureWeatherLoaded()
    {
        if (!Settings.ClockShowWeather) return;
        _lastWeatherFetchUtc = DateTime.UtcNow;
        _ = WeatherService.LoadAsync(Settings.WeatherCity, Settings.Language == AppLanguage.English, CancellationToken.None);
    }

    private void UpdateWeatherDisplay()
    {
        if (Settings.ClockShowWeather && WeatherService.LastReport is { } report)
        {
            WeatherIcon.Text = DescribeWeatherGlyph(report.Code);
            WeatherTemp.Text = $"{Math.Round(report.Temperature)}°";
            WeatherPanel.Visibility = Visibility.Visible;
        }
        else
        {
            WeatherPanel.Visibility = Visibility.Collapsed;
        }
    }

    private static string DescribeWeatherGlyph(int code) => code switch
    {
        0 => "☀",
        1 or 2 => "⛅",
        3 => "☁",
        45 or 48 => "🌫",
        >= 51 and <= 67 or >= 80 and <= 82 => "🌧",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⛈",
        _ => "☁"
    };

    private void ClockButton_Click(object sender, RoutedEventArgs e)
    {
        _shownMonth = DateOnly.FromDateTime(DateTime.Today);
        _shownMonth = new DateOnly(_shownMonth.Year, _shownMonth.Month, 1);
        var root = new ContentControl();
        root.Content = BuildCalendar(root);
        _calendar.Toggle(ClockButton, () => root);
    }

    /// <summary>Month view with the user's first day of week; arrows rebuild it in place.</summary>
    private UIElement BuildCalendar(ContentControl host)
    {
        var culture = Culture;
        var today = DateOnly.FromDateTime(DateTime.Today);
        var root = new StackPanel { Width = 280 };
        root.Children.Add(new TextBlock
        {
            Text = DateTime.Today.ToString("D", culture), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(4, 0, 4, 10)
        });

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var previous = NavButton("IconChevronLeft", -1, host);
        var next = NavButton("IconChevronNext", 1, host);
        DockPanel.SetDock(previous, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        header.Children.Add(previous);
        header.Children.Add(next);
        var title = _shownMonth.ToString("MMMM yyyy", culture);
        header.Children.Add(new TextBlock
        {
            Text = culture.TextInfo.ToTitleCase(title), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold
        });
        root.Children.Add(header);

        var grid = new UniformGrid { Columns = 7 };
        var firstDay = culture.DateTimeFormat.FirstDayOfWeek;
        for (var index = 0; index < 7; index++)
        {
            var day = (DayOfWeek)(((int)firstDay + index) % 7);
            var name = DockFlyout.Caption(culture.DateTimeFormat.GetShortestDayName(day));
            name.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(name);
        }

        var offset = ((int)_shownMonth.DayOfWeek - (int)firstDay + 7) % 7;
        var start = _shownMonth.AddDays(-offset);
        for (var cell = 0; cell < 42; cell++)
        {
            var date = start.AddDays(cell);
            var label = new TextBlock
            {
                Text = date.Day.ToString(culture), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            var border = new Border { Width = 32, Height = 30, CornerRadius = new CornerRadius(15), Child = label, Margin = new Thickness(1) };
            if (date == today)
            {
                border.SetResourceReference(Border.BackgroundProperty, "WidgetAccent");
                label.SetResourceReference(TextBlock.ForegroundProperty, "WidgetAccentText");
                label.FontWeight = FontWeights.SemiBold;
            }
            else if (date.Month != _shownMonth.Month)
            {
                label.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
                label.Opacity = 0.6;
            }

            grid.Children.Add(border);
        }

        root.Children.Add(grid);
        var notifications = DockFlyout.FlatButton(new TextBlock { Text = UiText.Get("OpenNotifications"), FontWeight = FontWeights.SemiBold }, (_, _) =>
        {
            _calendar.Close();
            Report(ShellShortcuts.OpenNotificationCenter());
        });
        notifications.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(notifications);
        return root;
    }

    private Button NavButton(string icon, int months, ContentControl host)
    {
        var glyph = new ContentControl { Content = FindResource(icon), Width = 14, Height = 14 };
        glyph.SetResourceReference(StyleProperty, "WidgetIcon");
        glyph.SetResourceReference(ForegroundProperty, "TextPrimary");
        var button = new Button { Content = glyph };
        button.SetResourceReference(StyleProperty, "WidgetIconButton");
        var key = months < 0 ? "PreviousMonth" : "NextMonth";
        button.ToolTip = UiText.Get(key);
        System.Windows.Automation.AutomationProperties.SetName(button, UiText.Get(key));
        button.Click += (_, _) =>
        {
            _shownMonth = _shownMonth.AddMonths(months);
            host.Content = BuildCalendar(host);
        };
        return button;
    }
}
