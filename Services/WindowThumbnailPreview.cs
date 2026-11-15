using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OpenTaskbar.Services;

public sealed class WindowThumbnailPreview : IDisposable
{
    private const uint DwmThumbnailRectDestination = 0x00000001;
    private const uint DwmThumbnailOpacity = 0x00000004;
    private const uint DwmThumbnailVisible = 0x00000008;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly Window _closeButtonWindow;
    private readonly TextBlock _placeholder;
    private readonly Button _closeButton;
    private readonly Image _windowIcon;
    private readonly TextBlock _windowTitle;
    private readonly double _headerHeight;
    private readonly DispatcherTimer _entranceTimer;
    private readonly double _entranceDurationMs;
    private DwmThumbnailProperties _entranceTarget;
    private long _entranceStarted;
    private nint _destinationHandle;
    private nint _thumbnail;
    private nint _sourceHandle;
    private bool _disposed;
    private bool _windowClosed;
    private bool _closeWindowClosed;

    public WindowThumbnailPreview(Style closeButtonStyle)
    {
        var resources = Application.Current.Resources;
        var popupBrush = (Brush)resources["SurfacePopup"];
        var textBrush = (Brush)resources["TextPrimary"];
        var strokeBrush = (Brush)resources["StrokeSubtle"];
        _headerHeight = (double)resources["PreviewHeaderHeight"];
        _entranceDurationMs = (double)resources["PreviewDurationMs"];
        _entranceTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _entranceTimer.Tick += EntranceTimer_Tick;
        _placeholder = new TextBlock
        {
            Text = "Önizleme kullanılamıyor",
            Foreground = (Brush)resources["TextSecondary"],
            FontFamily = (FontFamily)resources["InterfaceFont"],
            Margin = new Thickness(0, _headerHeight, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _closeButton = new Button
        {
            Style = closeButtonStyle,
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M2,2 L10,10 M10,2 L2,10"),
                Stroke = textBrush,
                StrokeThickness = 1.5,
                Width = 12,
                Height = 12
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "Pencereyi kapat"
        };
        AutomationProperties.SetName(_closeButton, "Pencereyi kapat");
        _closeButton.Click += CloseButton_Click;

        _windowIcon = new Image { Width = 20, Height = 20, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 8, 0) };
        _windowTitle = new TextBlock
        {
            Foreground = textBrush,
            FontFamily = (FontFamily)resources["InterfaceFont"],
            FontSize = (double)resources["TextSizeCaption"],
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var previewHeader = new DockPanel
        {
            LastChildFill = true,
            Margin = new Thickness(8, 4, 6, 4),
            Children = { _closeButton, _windowIcon, _windowTitle }
        };
        DockPanel.SetDock(_closeButton, Dock.Right);
        DockPanel.SetDock(_windowIcon, Dock.Left);
        var headerSurface = new Border
        {
            Height = _headerHeight,
            VerticalAlignment = VerticalAlignment.Top,
            Background = popupBrush,
            BorderBrush = strokeBrush,
            BorderThickness = new Thickness(1, 1, 1, 0),
            Child = previewHeader
        };
        headerSurface.MouseLeftButtonDown += (_, e) =>
        {
            if (!_closeButton.IsMouseOver)
            {
                e.Handled = true;
                PreviewClicked?.Invoke(this, EventArgs.Empty);
            }
        };

        var surfaceContent = new Grid
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 18, 21, 25))
        };
        surfaceContent.Children.Add(_placeholder);
        var surface = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(1, 18, 21, 25)),
            BorderThickness = new Thickness(1),
            BorderBrush = strokeBrush,
            Child = surfaceContent
        };
        _window = new Window
        {
            Width = (double)resources["PreviewWidth"],
            Height = (double)resources["PreviewHeight"],
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = popupBrush,
            UseLayoutRounding = true,
            Cursor = System.Windows.Input.Cursors.Hand,
            Content = surface
        };
        _window.Closed += Window_Closed;
        _window.SourceInitialized += PreviewWindow_SourceInitialized;
        _window.MouseEnter += Window_MouseEnter;
        _window.MouseLeave += Window_MouseLeave;
        _window.PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;

        _closeButtonWindow = new Window
        {
            Width = _window.Width,
            Height = _headerHeight,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = Brushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = headerSurface
        };
        _closeButtonWindow.Closed += CloseButtonWindow_Closed;
        _closeButtonWindow.SourceInitialized += PreviewWindow_SourceInitialized;
        _closeButtonWindow.MouseEnter += Window_MouseEnter;
        _closeButtonWindow.MouseLeave += Window_MouseLeave;
    }

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerLeft;

    public event EventHandler? PreviewClicked;

    public event EventHandler? CloseRequested;

    public bool IsVisible => _window.IsVisible;

    public nint SourceHandle => _sourceHandle;

    public bool ShowFor(nint sourceHandle, FrameworkElement anchor, string windowTitle, BitmapSource? icon)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceHandle == 0 || !IsWindow(sourceHandle))
        {
            Hide();
            return false;
        }

        var animateEntrance = SystemParameters.ClientAreaAnimation
            && (!_window.IsVisible || _sourceHandle != sourceHandle);
        _entranceTimer.Stop();
        _closeButtonWindow.BeginAnimation(UIElement.OpacityProperty, null);
        _closeButtonWindow.Opacity = 1;
        if (_sourceHandle != sourceHandle)
        {
            ReleaseThumbnail();
            _sourceHandle = sourceHandle;
        }

        _windowTitle.Text = windowTitle;
        _windowIcon.Source = icon;

        if (!_window.IsVisible)
        {
            _placeholder.Visibility = Visibility.Visible;
            _window.Show();
        }

        _destinationHandle = new WindowInteropHelper(_window).Handle;
        if (_destinationHandle == 0 || !PositionWindow(anchor))
        {
            _placeholder.Visibility = Visibility.Visible;
            return false;
        }

        if (!PositionCloseButtonWindow())
        {
            _placeholder.Visibility = Visibility.Visible;
            return false;
        }

        if (_thumbnail == 0)
        {
            if (DwmRegisterThumbnail(_destinationHandle, _sourceHandle, out _thumbnail) != 0)
            {
                _thumbnail = 0;
                _placeholder.Visibility = Visibility.Visible;
                return false;
            }
        }

        if (DwmQueryThumbnailSourceSize(_thumbnail, out var sourceSize) != 0
            || sourceSize.Width <= 0 || sourceSize.Height <= 0
            || !GetClientRect(_destinationHandle, out var clientRect))
        {
            ReleaseThumbnail();
            _placeholder.Visibility = Visibility.Visible;
            return false;
        }

        // DWM draws above WPF content, so reserve a separate band for the owned header window.
        var headerPixels = (int)Math.Ceiling(_headerHeight * GetDpiForWindow(_destinationHandle) / 96d);
        clientRect.Top += headerPixels;
        var destination = FitThumbnail(sourceSize, clientRect);
        var properties = new DwmThumbnailProperties
        {
            Flags = DwmThumbnailRectDestination | DwmThumbnailOpacity | DwmThumbnailVisible,
            Destination = destination,
            Opacity = byte.MaxValue,
            Visible = true,
            SourceClientAreaOnly = false
        };
        _entranceTarget = properties;
        if (animateEntrance)
        {
            properties = EntranceFrame(0);
        }
        if (DwmUpdateThumbnailProperties(_thumbnail, ref properties) != 0)
        {
            ReleaseThumbnail();
            _placeholder.Visibility = Visibility.Visible;
            return false;
        }

        _placeholder.Visibility = Visibility.Collapsed;
        if (animateEntrance)
        {
            _entranceStarted = Stopwatch.GetTimestamp();
            _closeButtonWindow.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(_entranceDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
            _entranceTimer.Start();
        }
        return true;
    }

    private DwmThumbnailProperties EntranceFrame(double progress)
    {
        var frame = _entranceTarget;
        var target = frame.Destination;
        var insetX = (int)Math.Round((target.Right - target.Left) * 0.025 * (1 - progress));
        var insetY = (int)Math.Round((target.Bottom - target.Top) * 0.025 * (1 - progress));
        frame.Destination = new NativeRect(target.Left + insetX, target.Top + insetY,
            target.Right - insetX, target.Bottom - insetY);
        frame.Opacity = (byte)Math.Round(byte.MaxValue * progress);
        return frame;
    }

    private void EntranceTimer_Tick(object? sender, EventArgs e)
    {
        if (_thumbnail == 0 || !_window.IsVisible)
        {
            _entranceTimer.Stop();
            return;
        }

        var progress = Math.Clamp(Stopwatch.GetElapsedTime(_entranceStarted).TotalMilliseconds / _entranceDurationMs, 0, 1);
        var frame = EntranceFrame(1 - Math.Pow(1 - progress, 3));
        if (DwmUpdateThumbnailProperties(_thumbnail, ref frame) != 0)
        {
            _entranceTimer.Stop();
            ReleaseThumbnail();
            _placeholder.Visibility = Visibility.Visible;
            return;
        }

        if (progress >= 1) _entranceTimer.Stop();
    }

    public void Hide()
    {
        _entranceTimer.Stop();
        _closeButtonWindow.BeginAnimation(UIElement.OpacityProperty, null);
        ReleaseThumbnail();
        _sourceHandle = 0;
        if (_closeButtonWindow.IsVisible)
        {
            _closeButtonWindow.Hide();
        }

        if (_window.IsVisible)
        {
            _window.Hide();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        Hide();
        _disposed = true;
        _entranceTimer.Tick -= EntranceTimer_Tick;
        _window.Closed -= Window_Closed;
        _closeButtonWindow.Closed -= CloseButtonWindow_Closed;
        _closeButtonWindow.MouseEnter -= Window_MouseEnter;
        _closeButtonWindow.MouseLeave -= Window_MouseLeave;
        _closeButton.Click -= CloseButton_Click;
        if (!_closeWindowClosed)
        {
            _closeButtonWindow.Close();
        }

        if (!_windowClosed)
        {
            _window.Close();
        }
    }

    private bool PositionWindow(FrameworkElement anchor)
    {
        var anchorTop = anchor.PointToScreen(new Point(anchor.ActualWidth / 2, 0));
        var dpi = GetDpiForWindow(_destinationHandle);
        if (dpi == 0)
        {
            return false;
        }

        var width = (int)Math.Ceiling(_window.Width * dpi / 96d);
        var height = (int)Math.Ceiling(_window.Height * dpi / 96d);
        var anchorWindow = Window.GetWindow(anchor);
        var anchorHandle = anchorWindow is null ? _destinationHandle : new WindowInteropHelper(anchorWindow).Handle;
        var monitor = MonitorFromWindow(anchorHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

        width = Math.Min(width, monitorInfo.Monitor.Right - monitorInfo.Monitor.Left);
        height = Math.Min(height, monitorInfo.Monitor.Bottom - monitorInfo.Monitor.Top);

        var left = (int)Math.Round(anchorTop.X) - width / 2;
        left = Math.Clamp(left, monitorInfo.Monitor.Left, monitorInfo.Monitor.Right - width);
        var top = (int)Math.Round(anchorTop.Y) - height - 8;
        if (top < monitorInfo.Monitor.Top)
        {
            var anchorHeight = (int)Math.Ceiling(anchor.ActualHeight * dpi / 96d);
            top = (int)Math.Round(anchorTop.Y) + anchorHeight + 8;
        }

        top = Math.Min(top, monitorInfo.Monitor.Bottom - height);
        return SetWindowPos(_destinationHandle, HwndTopmost, left, top, width, height, SwpNoActivate);
    }

    private bool PositionCloseButtonWindow()
    {
        var closeWindowInterop = new WindowInteropHelper(_closeButtonWindow);
        if (!_closeButtonWindow.IsVisible)
        {
            closeWindowInterop.Owner = _destinationHandle;
            _closeButtonWindow.Show();
        }

        var closeHandle = closeWindowInterop.Handle;
        var dpi = closeHandle == 0 ? 0 : GetDpiForWindow(closeHandle);
        if (dpi == 0 || !GetWindowRect(_destinationHandle, out var previewRect))
        {
            return false;
        }

        var scale = dpi / 96d;
        var width = previewRect.Right - previewRect.Left;
        var height = (int)Math.Ceiling(_closeButtonWindow.Height * scale);
        return SetWindowPos(closeHandle, HwndTopmost,
            previewRect.Left, previewRect.Top, width, height,
            SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static NativeRect FitThumbnail(NativeSize source, NativeRect client)
    {
        const int inset = 2;
        var availableWidth = Math.Max(1, client.Right - client.Left - inset * 2);
        var availableHeight = Math.Max(1, client.Bottom - client.Top - inset * 2);
        var scale = Math.Min((double)availableWidth / source.Width, (double)availableHeight / source.Height);
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var left = client.Left + (client.Right - client.Left - width) / 2;
        var top = client.Top + (client.Bottom - client.Top - height) / 2;
        return new NativeRect(left, top, left + width, top + height);
    }

    private void ReleaseThumbnail()
    {
        _entranceTimer.Stop();
        if (_thumbnail == 0)
        {
            return;
        }

        _ = DwmUnregisterThumbnail(_thumbnail);
        _thumbnail = 0;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _windowClosed = true;
        ReleaseThumbnail();
        _destinationHandle = 0;
        _sourceHandle = 0;
        if (!_closeWindowClosed)
        {
            _closeButtonWindow.Close();
        }
    }

    private void CloseButtonWindow_Closed(object? sender, EventArgs e)
    {
        _closeWindowClosed = true;
        _closeButton.Click -= CloseButton_Click;
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        PointerEntered?.Invoke(this, EventArgs.Empty);
    }

    private static void PreviewWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window && PresentationSource.FromVisual(window) is HwndSource source)
        {
            source.AddHook(PreviewWindowProc);
        }
    }

    private static nint PreviewWindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int wmMouseActivate = 0x0021;
        const int maNoActivate = 3;
        if (message == wmMouseActivate)
        {
            handled = true;
            return maNoActivate;
        }

        return 0;
    }

    private void Window_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        PointerLeft?.Invoke(this, EventArgs.Empty);
    }

    private void Window_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        PreviewClicked?.Invoke(this, EventArgs.Empty);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DwmThumbnailProperties
    {
        public uint Flags;
        public NativeRect Destination;
        public NativeRect Source;
        public byte Opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool Visible;
        [MarshalAs(UnmanagedType.Bool)] public bool SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [DllImport("dwmapi.dll")]
    private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref DwmThumbnailProperties properties);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUnregisterThumbnail(nint thumbnail);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint windowHandle, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
