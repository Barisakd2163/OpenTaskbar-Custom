using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// A dock widget that displays real-time internet download and upload speeds.
/// Clicking it opens Windows Network &amp; Internet Settings.
/// Conforms to the Widget Height Uniformity Rule (46 DIP).
/// </summary>
public sealed class NetSpeedWidget : DockWidget
{
    public static string Id => "NetSpeed";

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 35;
    public override double CompactWidthEstimate => 54;
    public override double FullWidthEstimate => 84;

    private readonly Button _cardButton;
    private readonly TextBlock _downArrow;
    private readonly TextBlock _downloadText;
    private readonly TextBlock _upArrow;
    private readonly TextBlock _uploadText;

    private long _lastReceivedBytes = -1;
    private long _lastSentBytes = -1;
    private long _lastTickMs;
    private double _downloadBytesPerSec;
    private double _uploadBytesPerSec;
    private long _sessionDownloadedBytes;
    private long _sessionUploadedBytes;
    private string _primaryInterfaceName = string.Empty;

    internal Func<(long Received, long Sent, string PrimaryInterfaceName)> NetworkReader { get; set; } = ReadNetworkBytes;
    internal Action OpenSettingsAction { get; set; } = DefaultOpenSettings;

    public NetSpeedWidget()
    {
        _downArrow = new TextBlock
        {
            Text = "↓ ",
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        _downArrow.SetResourceReference(TextBlock.ForegroundProperty, "WidgetAccent");

        _downloadText = new TextBlock
        {
            Text = "0 B/s",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        _downloadText.SetResourceReference(StyleProperty, "WidgetText");

        var downRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        downRow.Children.Add(_downArrow);
        downRow.Children.Add(_downloadText);

        _upArrow = new TextBlock
        {
            Text = "↑ ",
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        _upArrow.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        _uploadText = new TextBlock
        {
            Text = "0 B/s",
            FontSize = 10.5,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        _uploadText.SetResourceReference(StyleProperty, "WidgetCaption");

        var upRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, -1, 0, 0)
        };
        upRow.Children.Add(_upArrow);
        upRow.Children.Add(_uploadText);

        var stack = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(downRow);
        stack.Children.Add(upRow);

        _cardButton = new Button
        {
            Content = stack,
            Padding = new Thickness(10, 2, 10, 2)
        };
        _cardButton.SetResourceReference(StyleProperty, "WidgetCardButton");
        _cardButton.Click += (_, _) => OpenSettingsAction();

        Content = _cardButton;
        Localize();
    }

    protected override void OnActivated()
    {
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        Localize();
        _lastReceivedBytes = -1;
        _lastSentBytes = -1;
        _lastTickMs = 0;
        OnTick(DateTime.Now);
    }

    protected override void OnDeactivated()
    {
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
    }

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Localize();

    private void Localize()
    {
        var description = UiText.Get("WidgetNetSpeedDescription");
        var heading = UiText.Get("WidgetNetSpeed");
        _cardButton.ToolTip = description;
        AutomationProperties.SetName(_cardButton, heading);
        AutomationProperties.SetName(this, heading);
    }

    protected override void OnTick(DateTime now)
    {
        var nowMs = Environment.TickCount64;
        var (received, sent, ifName) = NetworkReader();
        _primaryInterfaceName = ifName;

        if (_lastReceivedBytes >= 0 && _lastSentBytes >= 0 && _lastTickMs > 0)
        {
            var elapsedSeconds = Math.Max(0.2, (nowMs - _lastTickMs) / 1000.0);
            var deltaReceived = received >= _lastReceivedBytes ? received - _lastReceivedBytes : 0;
            var deltaSent = sent >= _lastSentBytes ? sent - _lastSentBytes : 0;

            _downloadBytesPerSec = deltaReceived / elapsedSeconds;
            _uploadBytesPerSec = deltaSent / elapsedSeconds;

            _sessionDownloadedBytes += deltaReceived;
            _sessionUploadedBytes += deltaSent;
        }
        else
        {
            _downloadBytesPerSec = 0;
            _uploadBytesPerSec = 0;
        }

        _lastReceivedBytes = received;
        _lastSentBytes = sent;
        _lastTickMs = nowMs;

        UpdateLabels();
    }

    protected override void OnCompactChanged(bool compact) => UpdateLabels();

    private void UpdateLabels()
    {
        if (IsCompact)
        {
            _downArrow.Text = "↓";
            _upArrow.Text = "↑";
            _downloadText.Text = FormatCompactSpeed(_downloadBytesPerSec);
            _uploadText.Text = FormatCompactSpeed(_uploadBytesPerSec);
        }
        else
        {
            _downArrow.Text = "↓ ";
            _upArrow.Text = "↑ ";
            _downloadText.Text = FormatSpeed(_downloadBytesPerSec);
            _uploadText.Text = FormatSpeed(_uploadBytesPerSec);
        }

        var speedStatus = $"↓ {FormatSpeed(_downloadBytesPerSec)} · ↑ {FormatSpeed(_uploadBytesPerSec)}";
        AutomationProperties.SetName(_cardButton, $"{UiText.Get("WidgetNetSpeed")}: {speedStatus}");
    }

    public override UIElement? BuildHoverFlyoutContent()
    {
        var panel = new StackPanel { Width = 230 };
        panel.Children.Add(DockFlyout.Heading(UiText.Get("WidgetNetSpeed")));

        if (!string.IsNullOrEmpty(_primaryInterfaceName))
        {
            panel.Children.Add(DockFlyout.Caption($"{UiText.Get("NetworkAdapter")}: {_primaryInterfaceName}"));
        }

        var speedGrid = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        speedGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var downCol = new StackPanel { Margin = new Thickness(0, 0, 4, 0) };
        downCol.Children.Add(new TextBlock
        {
            Text = $"↓ {UiText.Get("NetworkDownload")}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("WidgetAccent")
        });
        downCol.Children.Add(new TextBlock
        {
            Text = FormatSpeed(_downloadBytesPerSec),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        });
        downCol.Children.Add(new TextBlock
        {
            Text = $"{UiText.Get("NetworkTotalReceived")}:\n{FormatBytes(_sessionDownloadedBytes)}",
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 4, 0, 0)
        });

        var upCol = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        upCol.Children.Add(new TextBlock
        {
            Text = $"↑ {UiText.Get("NetworkUpload")}",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextSecondary")
        });
        upCol.Children.Add(new TextBlock
        {
            Text = FormatSpeed(_uploadBytesPerSec),
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        });
        upCol.Children.Add(new TextBlock
        {
            Text = $"{UiText.Get("NetworkTotalSent")}:\n{FormatBytes(_sessionUploadedBytes)}",
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 4, 0, 0)
        });

        Grid.SetColumn(downCol, 0);
        Grid.SetColumn(upCol, 1);
        speedGrid.Children.Add(downCol);
        speedGrid.Children.Add(upCol);
        panel.Children.Add(speedGrid);

        return panel;
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return $"{(int)Math.Max(0, bytesPerSecond)} B/s";
        }

        var kb = bytesPerSecond / 1024.0;
        if (kb < 1024)
        {
            return kb < 100
                ? $"{kb.ToString("0.0", CultureInfo.InvariantCulture)} KB/s"
                : $"{(int)kb} KB/s";
        }

        var mb = kb / 1024.0;
        if (mb < 1024)
        {
            return mb < 100
                ? $"{mb.ToString("0.0", CultureInfo.InvariantCulture)} MB/s"
                : $"{(int)mb} MB/s";
        }

        var gb = mb / 1024.0;
        return $"{gb.ToString("0.00", CultureInfo.InvariantCulture)} GB/s";
    }

    public static string FormatCompactSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1024)
        {
            return "0";
        }

        var kb = bytesPerSecond / 1024.0;
        if (kb < 1024)
        {
            return $"{(int)kb}K";
        }

        var mb = kb / 1024.0;
        if (mb < 1024)
        {
            return mb < 10
                ? $"{mb.ToString("0.0", CultureInfo.InvariantCulture)}M"
                : $"{(int)mb}M";
        }

        var gb = mb / 1024.0;
        return $"{gb.ToString("0.0", CultureInfo.InvariantCulture)}G";
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{Math.Max(0, bytes)} B";
        }

        var kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb.ToString("0.0", CultureInfo.InvariantCulture)} KB";
        }

        var mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb.ToString("0.0", CultureInfo.InvariantCulture)} MB";
        }

        var gb = mb / 1024.0;
        return $"{gb.ToString("0.00", CultureInfo.InvariantCulture)} GB";
    }

    private static (long Received, long Sent, string PrimaryInterfaceName) ReadNetworkBytes()
    {
        long totalReceived = 0;
        long totalSent = 0;
        string primaryName = string.Empty;
        long maxTraffic = -1;

        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var stats = ni.GetIPStatistics();
                totalReceived += stats.BytesReceived;
                totalSent += stats.BytesSent;

                var traffic = stats.BytesReceived + stats.BytesSent;
                if (traffic > maxTraffic)
                {
                    maxTraffic = traffic;
                    primaryName = ni.Name;
                }
            }
        }
        catch
        {
        }

        return (totalReceived, totalSent, primaryName);
    }

    private static void DefaultOpenSettings()
    {
        DockFlyout.CloseAll();
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:network") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("control.exe", "netconnections") { UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
