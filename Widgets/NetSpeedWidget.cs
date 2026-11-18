using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

public enum SpeedTestStatus
{
    Idle,
    Running,
    Completed,
    Failed
}

public sealed record SpeedTestProgress(string StatusText, double CurrentMbps, double Percent);
public sealed record SpeedTestResult(
    int PingMs,
    double DownloadMbps,
    double UploadMbps,
    string? Server = null,
    string? Isp = null,
    string? ResultUrl = null
);

/// <summary>
/// A dock widget that displays real-time internet download and upload speeds side-by-side.
/// Clicking it opens an interactive flyout allowing the user to run an official Ookla Speedtest.
/// Conforms to the Widget Height Uniformity Rule (46 DIP).
/// </summary>
public sealed class NetSpeedWidget : DockWidget
{
    public static string Id => "NetSpeed";

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 30;
    public override double CompactWidthEstimate => 68;
    public override double FullWidthEstimate => 140;

    private readonly Button _cardButton;
    private readonly ContentControl _downIcon;
    private readonly TextBlock _downloadText;
    private readonly ContentControl _upIcon;
    private readonly TextBlock _uploadText;
    private readonly StackPanel _uploadGroup;

    private readonly DockFlyout _flyout = new();
    private StackPanel? _flyoutPanel;
    private StackPanel? _speedTestContainer;

    private long _lastReceivedBytes = -1;
    private long _lastSentBytes = -1;
    private long _lastTickMs;
    private double _downloadBytesPerSec;
    private double _uploadBytesPerSec;
    private long _sessionDownloadedBytes;
    private long _sessionUploadedBytes;
    private string _primaryInterfaceName = string.Empty;

    // Speed test state
    private SpeedTestStatus _testStatus = SpeedTestStatus.Idle;
    private SpeedTestResult? _lastTestResult;
    private string _testStatusMessage = string.Empty;
    private double _testLiveMbps;
    private double _testProgressPercent;
    private CancellationTokenSource? _testCts;

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    internal Func<(long Received, long Sent, string PrimaryInterfaceName)> NetworkReader { get; set; } = ReadNetworkBytes;
    internal Func<IProgress<SpeedTestProgress>, CancellationToken, Task<SpeedTestResult>> SpeedTestRunner { get; set; } = RunOoklaSpeedTestAsync;
    internal Action OpenSettingsAction { get; set; } = DefaultOpenSettings;

    public NetSpeedWidget()
    {
        _downIcon = new ContentControl
        {
            Content = FindResource("IconArrowDown"),
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _downIcon.SetResourceReference(StyleProperty, "WidgetIcon");
        _downIcon.SetResourceReference(ForegroundProperty, "WidgetAccent");

        _downloadText = new TextBlock
        {
            Text = "0 B/s",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        _downloadText.SetResourceReference(StyleProperty, "WidgetText");

        var downGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        downGroup.Children.Add(_downIcon);
        downGroup.Children.Add(_downloadText);

        _upIcon = new ContentControl
        {
            Content = FindResource("IconArrowUp"),
            Width = 12,
            Height = 12,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _upIcon.SetResourceReference(StyleProperty, "WidgetIcon");
        _upIcon.SetResourceReference(ForegroundProperty, "TextSecondary");

        _uploadText = new TextBlock
        {
            Text = "0 B/s",
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        };
        _uploadText.SetResourceReference(StyleProperty, "WidgetText");

        _uploadGroup = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        _uploadGroup.Children.Add(_upIcon);
        _uploadGroup.Children.Add(_uploadText);

        var contentRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        contentRow.Children.Add(downGroup);
        contentRow.Children.Add(_uploadGroup);

        _cardButton = new Button
        {
            Content = contentRow,
            Padding = new Thickness(10, 0, 10, 0)
        };
        _cardButton.SetResourceReference(StyleProperty, "WidgetCardButton");
        _cardButton.Click += CardButton_Click;

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
        CancelSpeedTest();
        _flyout.Close();
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

    protected override void OnCompactChanged(bool compact)
    {
        _uploadGroup.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        UpdateLabels();
    }

    private void UpdateLabels()
    {
        _downloadText.Text = FormatSpeed(_downloadBytesPerSec);
        _uploadText.Text = FormatSpeed(_uploadBytesPerSec);

        var speedStatus = $"↓ {FormatSpeed(_downloadBytesPerSec)} · ↑ {FormatSpeed(_uploadBytesPerSec)}";
        AutomationProperties.SetName(_cardButton, $"{UiText.Get("WidgetNetSpeed")}: {speedStatus}");
    }

    private void CardButton_Click(object sender, RoutedEventArgs e)
    {
        _flyout.Toggle(_cardButton, BuildClickFlyoutContent, focusable: true);
    }

    public override UIElement? BuildHoverFlyoutContent()
    {
        var panel = new StackPanel { Width = 240 };
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

    internal UIElement BuildClickFlyoutContent()
    {
        _flyoutPanel = new StackPanel { Width = 280 };
        _flyoutPanel.Children.Add(DockFlyout.Heading(UiText.Get("WidgetNetSpeed")));

        if (!string.IsNullOrEmpty(_primaryInterfaceName))
        {
            _flyoutPanel.Children.Add(DockFlyout.Caption($"{UiText.Get("NetworkAdapter")}: {_primaryInterfaceName}"));
        }

        // Live stats grid
        var speedGrid = new Grid { Margin = new Thickness(0, 6, 0, 8) };
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
            Text = $"{UiText.Get("NetworkTotalReceived")}: {FormatBytes(_sessionDownloadedBytes)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 2, 0, 0)
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
            Text = $"{UiText.Get("NetworkTotalSent")}: {FormatBytes(_sessionUploadedBytes)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 2, 0, 0)
        });

        Grid.SetColumn(downCol, 0);
        Grid.SetColumn(upCol, 1);
        speedGrid.Children.Add(downCol);
        speedGrid.Children.Add(upCol);
        _flyoutPanel.Children.Add(speedGrid);

        // Divider
        _flyoutPanel.Children.Add(CreateDivider());

        // Speed Test Section Container
        _speedTestContainer = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        UpdateSpeedTestView();
        _flyoutPanel.Children.Add(_speedTestContainer);

        // Divider
        _flyoutPanel.Children.Add(CreateDivider());

        // Settings link
        var settingsButton = DockFlyout.FlatButton(
            new TextBlock { Text = UiText.Get("NetworkSettings"), FontSize = 11.5, FontWeight = FontWeights.SemiBold },
            (_, _) => OpenSettingsAction(),
            UiText.Get("NetworkSettings")
        );
        settingsButton.Margin = new Thickness(0, 2, 0, 0);
        _flyoutPanel.Children.Add(settingsButton);

        return _flyoutPanel;
    }

    private Border CreateDivider()
    {
        var border = new Border
        {
            Height = 1,
            Margin = new Thickness(0, 6, 0, 6)
        };
        border.SetResourceReference(Border.BackgroundProperty, "MenuStroke");
        return border;
    }

    private void UpdateSpeedTestView()
    {
        if (_speedTestContainer == null) return;
        _speedTestContainer.Children.Clear();

        var headerRow = new Grid { Margin = new Thickness(0, 2, 0, 6) };
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleBlock = new TextBlock
        {
            Text = UiText.Get("SpeedTestTitle"),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        };

        var ooklaBadge = new TextBlock
        {
            Text = UiText.Get("SpeedTestPoweredByOokla"),
            FontSize = 9.5,
            Foreground = (Brush)FindResource("TextSecondary"),
            VerticalAlignment = VerticalAlignment.Center
        };

        Grid.SetColumn(titleBlock, 0);
        Grid.SetColumn(ooklaBadge, 1);
        headerRow.Children.Add(titleBlock);
        headerRow.Children.Add(ooklaBadge);
        _speedTestContainer.Children.Add(headerRow);

        switch (_testStatus)
        {
            case SpeedTestStatus.Idle:
                if (_lastTestResult != null)
                {
                    _speedTestContainer.Children.Add(CreateResultsGrid(_lastTestResult));
                }

                var startButton = DockFlyout.FlatButton(
                    new TextBlock
                    {
                        Text = _lastTestResult == null ? UiText.Get("SpeedTestStart") : UiText.Get("SpeedTestRestart"),
                        FontWeight = FontWeights.Bold,
                        FontSize = 11.5,
                        Foreground = (Brush)FindResource("WidgetAccent")
                    },
                    async (_, _) => await StartSpeedTestAsync()
                );
                startButton.Margin = new Thickness(0, 4, 0, 0);
                _speedTestContainer.Children.Add(startButton);
                break;

            case SpeedTestStatus.Running:
                var statusBlock = new TextBlock
                {
                    Text = string.IsNullOrEmpty(_testStatusMessage) ? UiText.Get("SpeedTestTestingPing") : _testStatusMessage,
                    FontSize = 11,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    Margin = new Thickness(0, 0, 0, 4)
                };
                _speedTestContainer.Children.Add(statusBlock);

                if (_testLiveMbps > 0)
                {
                    var liveSpeedBlock = new TextBlock
                    {
                        Text = $"{_testLiveMbps.ToString("0.0", CultureInfo.InvariantCulture)} Mbps",
                        FontSize = 18,
                        FontWeight = FontWeights.Bold,
                        Foreground = (Brush)FindResource("WidgetAccent"),
                        Margin = new Thickness(0, 0, 0, 6),
                        Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
                    };
                    _speedTestContainer.Children.Add(liveSpeedBlock);
                }

                var progress = new ProgressBar
                {
                    Height = 4,
                    Minimum = 0,
                    Maximum = 100,
                    Value = _testProgressPercent,
                    Margin = new Thickness(0, 2, 0, 6)
                };
                _speedTestContainer.Children.Add(progress);

                var cancelButton = DockFlyout.FlatButton(
                    new TextBlock { Text = UiText.Get("SpeedTestCancel"), FontSize = 11, Foreground = (Brush)FindResource("TextSecondary") },
                    (_, _) => CancelSpeedTest()
                );
                cancelButton.Margin = new Thickness(0, 2, 0, 0);
                _speedTestContainer.Children.Add(cancelButton);
                break;

            case SpeedTestStatus.Completed:
                if (_lastTestResult != null)
                {
                    _speedTestContainer.Children.Add(CreateResultsGrid(_lastTestResult));
                }

                var testAgainBtn = DockFlyout.FlatButton(
                    new TextBlock
                    {
                        Text = UiText.Get("SpeedTestRestart"),
                        FontWeight = FontWeights.Bold,
                        FontSize = 11.5,
                        Foreground = (Brush)FindResource("WidgetAccent")
                    },
                    async (_, _) => await StartSpeedTestAsync()
                );
                testAgainBtn.Margin = new Thickness(0, 6, 0, 0);
                _speedTestContainer.Children.Add(testAgainBtn);
                break;

            case SpeedTestStatus.Failed:
                var errorBlock = new TextBlock
                {
                    Text = UiText.Get("SpeedTestFailed"),
                    FontSize = 11,
                    Foreground = (Brush)FindResource("WidgetWarning"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 2, 0, 6)
                };
                _speedTestContainer.Children.Add(errorBlock);

                var retryBtn = DockFlyout.FlatButton(
                    new TextBlock { Text = UiText.Get("SpeedTestRetry"), FontWeight = FontWeights.Bold, FontSize = 11.5 },
                    async (_, _) => await StartSpeedTestAsync()
                );
                retryBtn.Margin = new Thickness(0, 2, 0, 0);
                _speedTestContainer.Children.Add(retryBtn);
                break;
        }
    }

    private Grid CreateResultsGrid(SpeedTestResult result)
    {
        var mainGrid = new Grid { Margin = new Thickness(0, 4, 0, 6) };
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mainGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var statGrid = new Grid();
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var pingStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        pingStack.Children.Add(new TextBlock
        {
            Text = UiText.Get("SpeedTestPing"),
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondary")
        });
        pingStack.Children.Add(new TextBlock
        {
            Text = result.PingMs >= 0 ? $"{result.PingMs} ms" : "—",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        });

        var downStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        downStack.Children.Add(new TextBlock
        {
            Text = $"↓ {UiText.Get("NetworkDownload")}",
            FontSize = 10,
            Foreground = (Brush)FindResource("WidgetAccent")
        });
        downStack.Children.Add(new TextBlock
        {
            Text = $"{result.DownloadMbps.ToString("0.0", CultureInfo.InvariantCulture)}",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        });
        downStack.Children.Add(new TextBlock
        {
            Text = "Mbps",
            FontSize = 9,
            Foreground = (Brush)FindResource("TextSecondary")
        });

        var upStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        upStack.Children.Add(new TextBlock
        {
            Text = $"↑ {UiText.Get("NetworkUpload")}",
            FontSize = 10,
            Foreground = (Brush)FindResource("TextSecondary")
        });
        upStack.Children.Add(new TextBlock
        {
            Text = $"{result.UploadMbps.ToString("0.0", CultureInfo.InvariantCulture)}",
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Typography = { NumeralAlignment = FontNumeralAlignment.Tabular }
        });
        upStack.Children.Add(new TextBlock
        {
            Text = "Mbps",
            FontSize = 9,
            Foreground = (Brush)FindResource("TextSecondary")
        });

        Grid.SetColumn(pingStack, 0);
        Grid.SetColumn(downStack, 1);
        Grid.SetColumn(upStack, 2);

        statGrid.Children.Add(pingStack);
        statGrid.Children.Add(downStack);
        statGrid.Children.Add(upStack);
        Grid.SetRow(statGrid, 0);
        mainGrid.Children.Add(statGrid);

        // Server, ISP and Link details
        if (!string.IsNullOrEmpty(result.Server) || !string.IsNullOrEmpty(result.Isp) || !string.IsNullOrEmpty(result.ResultUrl))
        {
            var metaStack = new StackPanel { Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            if (!string.IsNullOrEmpty(result.Isp) || !string.IsNullOrEmpty(result.Server))
            {
                var infoParts = new List<string>();
                if (!string.IsNullOrEmpty(result.Isp)) infoParts.Add(result.Isp);
                if (!string.IsNullOrEmpty(result.Server)) infoParts.Add(result.Server);

                metaStack.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", infoParts),
                    FontSize = 9.5,
                    Foreground = (Brush)FindResource("TextSecondary"),
                    TextAlignment = TextAlignment.Center
                });
            }

            if (!string.IsNullOrEmpty(result.ResultUrl))
            {
                var linkBtn = DockFlyout.FlatButton(
                    new TextBlock
                    {
                        Text = UiText.Get("SpeedTestOpenResult"),
                        FontSize = 9.5,
                        Foreground = (Brush)FindResource("WidgetAccent")
                    },
                    (_, _) =>
                    {
                        try { Process.Start(new ProcessStartInfo(result.ResultUrl) { UseShellExecute = true }); } catch { }
                    }
                );
                linkBtn.Padding = new Thickness(4, 2, 4, 2);
                linkBtn.HorizontalAlignment = HorizontalAlignment.Center;
                metaStack.Children.Add(linkBtn);
            }

            Grid.SetRow(metaStack, 1);
            mainGrid.Children.Add(metaStack);
        }

        return mainGrid;
    }

    internal async Task StartSpeedTestAsync()
    {
        CancelSpeedTest();
        _testCts = new CancellationTokenSource();
        var ct = _testCts.Token;

        _testStatus = SpeedTestStatus.Running;
        _testStatusMessage = UiText.Get("SpeedTestTestingPing");
        _testLiveMbps = 0;
        _testProgressPercent = 5;
        UpdateSpeedTestView();

        var progress = new Progress<SpeedTestProgress>(p =>
        {
            if (ct.IsCancellationRequested) return;
            _testStatusMessage = p.StatusText;
            _testLiveMbps = p.CurrentMbps;
            _testProgressPercent = p.Percent;
            UpdateSpeedTestView();
        });

        try
        {
            var result = await SpeedTestRunner(progress, ct).ConfigureAwait(true);
            if (!ct.IsCancellationRequested)
            {
                _lastTestResult = result;
                _testStatus = SpeedTestStatus.Completed;
            }
        }
        catch (OperationCanceledException)
        {
            _testStatus = SpeedTestStatus.Idle;
        }
        catch
        {
            _testStatus = SpeedTestStatus.Failed;
        }
        finally
        {
            _testLiveMbps = 0;
            UpdateSpeedTestView();
        }
    }

    internal void CancelSpeedTest()
    {
        if (_testCts != null)
        {
            try
            {
                _testCts.Cancel();
                _testCts.Dispose();
            }
            catch
            {
            }
            _testCts = null;
        }
        _testStatus = SpeedTestStatus.Idle;
        UpdateSpeedTestView();
    }

    private static string EnsureSpeedtestBinary()
    {
        var localDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTaskbar");
        Directory.CreateDirectory(localDir);
        var targetExe = Path.Combine(localDir, "speedtest.exe");

        if (File.Exists(targetExe))
        {
            return targetExe;
        }

        var projectExe = Path.Combine(AppContext.BaseDirectory, "tools", "speedtest", "speedtest.exe");
        if (File.Exists(projectExe))
        {
            try { File.Copy(projectExe, targetExe, true); return targetExe; } catch { return projectExe; }
        }

        using var stream = typeof(NetSpeedWidget).Assembly.GetManifestResourceStream("OpenTaskbar.Resources.speedtest.exe");
        if (stream is not null)
        {
            try
            {
                using var fs = File.Create(targetExe);
                stream.CopyTo(fs);
                return targetExe;
            }
            catch
            {
            }
        }

        return targetExe;
    }

    private static async Task<SpeedTestResult> RunOoklaSpeedTestAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct)
    {
        var exePath = EnsureSpeedtestBinary();
        if (!File.Exists(exePath))
        {
            return await RunFallbackSpeedTestAsync(progress, ct).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = "--accept-license --accept-gdpr -f jsonl",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var tcs = new TaskCompletionSource<bool>();
        using var reg = ct.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(); } catch { }
            tcs.TrySetCanceled(ct);
        });

        int pingMs = -1;
        double downloadMbps = 0;
        double uploadMbps = 0;
        string? server = null;
        string? isp = null;
        string? resultUrl = null;

        process.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try
            {
                using var doc = JsonDocument.Parse(e.Data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp)) return;
                var type = typeProp.GetString();

                switch (type)
                {
                    case "testStart":
                        if (root.TryGetProperty("isp", out var ispProp)) isp = ispProp.GetString();
                        if (root.TryGetProperty("server", out var srvProp))
                        {
                            var sName = srvProp.TryGetProperty("name", out var sn) ? sn.GetString() : null;
                            var sLoc = srvProp.TryGetProperty("location", out var sl) ? sl.GetString() : null;
                            server = string.IsNullOrEmpty(sLoc) ? sName : $"{sName} ({sLoc})";
                        }
                        break;

                    case "ping":
                        if (root.TryGetProperty("ping", out var pingElem))
                        {
                            if (pingElem.TryGetProperty("latency", out var latProp))
                            {
                                pingMs = (int)Math.Round(latProp.GetDouble());
                            }
                            var prog = pingElem.TryGetProperty("progress", out var pr) ? pr.GetDouble() : 0.5;
                            progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingPing"), 0, prog * 15.0));
                        }
                        break;

                    case "download":
                        if (root.TryGetProperty("download", out var dlElem))
                        {
                            if (dlElem.TryGetProperty("bandwidth", out var bwProp))
                            {
                                var bwBytes = bwProp.GetDouble();
                                downloadMbps = (bwBytes * 8.0) / 1_000_000.0;
                            }
                            var prog = dlElem.TryGetProperty("progress", out var pr) ? pr.GetDouble() : 0.5;
                            progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingDownload"), downloadMbps, 15.0 + (prog * 45.0)));
                        }
                        break;

                    case "upload":
                        if (root.TryGetProperty("upload", out var ulElem))
                        {
                            if (ulElem.TryGetProperty("bandwidth", out var bwProp))
                            {
                                var bwBytes = bwProp.GetDouble();
                                uploadMbps = (bwBytes * 8.0) / 1_000_000.0;
                            }
                            var prog = ulElem.TryGetProperty("progress", out var pr) ? pr.GetDouble() : 0.5;
                            progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingUpload"), uploadMbps, 60.0 + (prog * 40.0)));
                        }
                        break;

                    case "result":
                        if (root.TryGetProperty("ping", out var finalPing) && finalPing.TryGetProperty("latency", out var fpLat))
                        {
                            pingMs = (int)Math.Round(fpLat.GetDouble());
                        }
                        if (root.TryGetProperty("download", out var finalDl) && finalDl.TryGetProperty("bandwidth", out var fdBw))
                        {
                            downloadMbps = (fdBw.GetDouble() * 8.0) / 1_000_000.0;
                        }
                        if (root.TryGetProperty("upload", out var finalUl) && finalUl.TryGetProperty("bandwidth", out var fuBw))
                        {
                            uploadMbps = (fuBw.GetDouble() * 8.0) / 1_000_000.0;
                        }
                        if (root.TryGetProperty("isp", out var fIsp)) isp = fIsp.GetString();
                        if (root.TryGetProperty("server", out var fSrv))
                        {
                            var sName = fSrv.TryGetProperty("name", out var sn) ? sn.GetString() : null;
                            var sLoc = fSrv.TryGetProperty("location", out var sl) ? sl.GetString() : null;
                            server = string.IsNullOrEmpty(sLoc) ? sName : $"{sName} ({sLoc})";
                        }
                        if (root.TryGetProperty("result", out var resElem) && resElem.TryGetProperty("url", out var urlProp))
                        {
                            resultUrl = urlProp.GetString();
                        }
                        break;
                }
            }
            catch
            {
            }
        };

        process.Exited += (_, _) => tcs.TrySetResult(true);

        if (!process.Start())
        {
            return await RunFallbackSpeedTestAsync(progress, ct).ConfigureAwait(false);
        }

        process.BeginOutputReadLine();
        await tcs.Task.ConfigureAwait(false);

        progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingUpload"), uploadMbps, 100.0));
        return new SpeedTestResult(pingMs, downloadMbps, uploadMbps, server, isp, resultUrl);
    }

    private static async Task<SpeedTestResult> RunFallbackSpeedTestAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct)
    {
        // 1. Measure latency
        progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingPing"), 0, 10));
        var pingMs = await MeasurePingAsync(ct).ConfigureAwait(false);

        // 2. Measure download
        progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingDownload"), 0, 25));
        var downMbps = await MeasureDownloadAsync(progress, ct).ConfigureAwait(false);

        // 3. Measure upload
        progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingUpload"), 0, 65));
        var upMbps = await MeasureUploadAsync(progress, ct).ConfigureAwait(false);

        progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingUpload"), upMbps, 100));
        return new SpeedTestResult(pingMs, downMbps, upMbps);
    }

    private static async Task<int> MeasurePingAsync(CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync("1.1.1.1", 2000).ConfigureAwait(false);
            if (reply.Status == IPStatus.Success)
            {
                return (int)reply.RoundtripTime;
            }
        }
        catch
        {
        }

        try
        {
            var sw = Stopwatch.StartNew();
            using var request = new HttpRequestMessage(HttpMethod.Head, "https://1.1.1.1");
            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            return Math.Max(1, (int)sw.ElapsedMilliseconds);
        }
        catch
        {
            return -1;
        }
    }

    private static async Task<double> MeasureDownloadAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct)
    {
        try
        {
            var url = "https://speed.cloudflare.com/__down?bytes=5000000";
            using var response = await SharedHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            long totalBytesRead = 0;
            var sw = Stopwatch.StartNew();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false);
                if (bytesRead == 0) break;
                totalBytesRead += bytesRead;

                var elapsedSec = sw.Elapsed.TotalSeconds;
                if (elapsedSec > 0.05)
                {
                    var currentMbps = (totalBytesRead * 8.0) / (elapsedSec * 1_000_000.0);
                    var percent = 25 + Math.Min(35, (totalBytesRead / 5000000.0) * 35);
                    progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingDownload"), currentMbps, percent));
                }
            }

            sw.Stop();
            var finalSec = Math.Max(0.05, sw.Elapsed.TotalSeconds);
            return (totalBytesRead * 8.0) / (finalSec * 1_000_000.0);
        }
        catch when (!ct.IsCancellationRequested)
        {
            return 0;
        }
    }

    private static async Task<double> MeasureUploadAsync(IProgress<SpeedTestProgress> progress, CancellationToken ct)
    {
        try
        {
            var payload = new byte[2 * 1024 * 1024];
            using var content = new ByteArrayContent(payload);
            var sw = Stopwatch.StartNew();
            using var response = await SharedHttpClient.PostAsync("https://speed.cloudflare.com/__up", content, ct).ConfigureAwait(false);
            sw.Stop();

            var finalSec = Math.Max(0.05, sw.Elapsed.TotalSeconds);
            var upMbps = (payload.Length * 8.0) / (finalSec * 1_000_000.0);
            progress.Report(new SpeedTestProgress(UiText.Get("SpeedTestTestingUpload"), upMbps, 95));
            return upMbps;
        }
        catch when (!ct.IsCancellationRequested)
        {
            return 0;
        }
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
