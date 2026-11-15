using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace OpenTaskbar.Services;

public sealed class WindowThumbnailPreview : IDisposable
{
    private const uint DwmThumbnailRectDestination = 0x00000001;
    private const uint DwmThumbnailOpacity = 0x00000004;
    private const uint DwmThumbnailVisible = 0x00000008;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly TextBlock _placeholder;
    private nint _destinationHandle;
    private nint _thumbnail;
    private nint _sourceHandle;
    private bool _disposed;
    private bool _windowClosed;

    public WindowThumbnailPreview()
    {
        _placeholder = new TextBlock
        {
            Text = "Önizleme kullanılamıyor",
            Foreground = new SolidColorBrush(Color.FromRgb(190, 196, 204)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var surface = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(18, 21, 25)),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromRgb(74, 81, 90)),
            Child = _placeholder
        };
        _window = new Window
        {
            Width = 360,
            Height = 220,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Background = surface.Background,
            Content = surface
        };
        _window.Closed += Window_Closed;
        _window.MouseEnter += Window_MouseEnter;
        _window.MouseLeave += Window_MouseLeave;
        _window.PreviewMouseLeftButtonDown += Window_PreviewMouseLeftButtonDown;
    }

    public event EventHandler? PointerEntered;

    public event EventHandler? PointerLeft;

    public event EventHandler? PreviewClicked;

    public bool IsVisible => _window.IsVisible;

    public nint SourceHandle => _sourceHandle;

    public bool ShowFor(nint sourceHandle, FrameworkElement anchor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (sourceHandle == 0 || !IsWindow(sourceHandle))
        {
            Hide();
            return false;
        }

        if (_sourceHandle != sourceHandle)
        {
            ReleaseThumbnail();
            _sourceHandle = sourceHandle;
        }

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

        var destination = FitThumbnail(sourceSize, clientRect);
        var properties = new DwmThumbnailProperties
        {
            Flags = DwmThumbnailRectDestination | DwmThumbnailOpacity | DwmThumbnailVisible,
            Destination = destination,
            Opacity = byte.MaxValue,
            Visible = true,
            SourceClientAreaOnly = false
        };
        if (DwmUpdateThumbnailProperties(_thumbnail, ref properties) != 0)
        {
            ReleaseThumbnail();
            _placeholder.Visibility = Visibility.Visible;
            return false;
        }

        _placeholder.Visibility = Visibility.Collapsed;
        return true;
    }

    public void Hide()
    {
        ReleaseThumbnail();
        _sourceHandle = 0;
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
        _window.Closed -= Window_Closed;
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
        var monitor = MonitorFromWindow(_destinationHandle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return false;
        }

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
    }

    private void Window_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        PointerEntered?.Invoke(this, EventArgs.Empty);
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
