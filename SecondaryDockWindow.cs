using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

/// <summary>
/// A lightweight dock for a non-primary display. It sits on the strip Explorer's hidden secondary taskbar still
/// reserves, so it never changes the work area and has nothing to restore on exit. It lists the windows on its display.
/// </summary>
public sealed class SecondaryDockWindow : Window
{
    private readonly nint _monitor;
    private readonly StackPanel _apps;
    private readonly TextBlock _clock;
    private readonly Dictionary<string, BitmapSource?> _icons;
    private nint _handle;
    private string _signature = string.Empty;
    private bool _stripAvailable = true;
    private bool _fullScreen;

    private void ApplyVisibility() => Visibility = _stripAvailable && !_fullScreen ? Visibility.Visible : Visibility.Hidden;

    public SecondaryDockWindow(nint monitor, Dictionary<string, BitmapSource?> iconCache)
    {
        _monitor = monitor;
        _icons = iconCache;
        Title = "OpenTaskbar Secondary";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SetResourceReference(FontFamilyProperty, "InterfaceFont");

        _apps = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _clock = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 6, 0), FontSize = 12, FontWeight = FontWeights.SemiBold };
        _clock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(_clock, Dock.Right);
        row.Children.Add(_clock);
        row.Children.Add(_apps);
        var surface = new Border
        {
            Child = row,
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(6, 3, 6, 3)
        };
        surface.SetResourceReference(Border.BackgroundProperty, "DockMaterial");
        surface.SetResourceReference(Border.BorderBrushProperty, "DockBorderMaterial");
        Content = surface;
    }

    public nint Monitor => _monitor;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        MainWindow.ExcludeFromTaskSwitcher(_handle);
        // Tool window + no-activate: out of Alt+Tab, out of the running-app list, and clicks never steal focus.
        var style = GetWindowLongPtr(_handle, -20).ToInt64();
        SetWindowLongPtr(_handle, -20, new nint((style | 0x80 | 0x08000000) & ~0x40000));
        HwndSource.FromHwnd(_handle)?.AddHook((nint _, int message, nint _, nint _, ref bool handled) =>
        {
            if (message != 0x0021) return 0; // WM_MOUSEACTIVATE
            handled = true;
            return 3; // MA_NOACTIVATE
        });
        PositionOnMonitor();
    }

    /// <summary>
    /// Covers the strip below the work area. When the strip is released (auto-hide or side docks set Explorer's
    /// taskbars to auto-hide), the dock hides instead of covering maximized windows.
    /// </summary>
    public void PositionOnMonitor()
    {
        if (_handle == 0) return;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(_monitor, ref info)) return;
        var dpi = GetDpiForMonitor(_monitor);
        var height = info.Monitor.Bottom - info.Work.Bottom;
        _stripAvailable = height >= 24 * dpi / 96;
        ApplyVisibility();
        if (!_stripAvailable) return;
        SetWindowPos(_handle, new nint(-1), info.Monitor.Left, info.Monitor.Bottom - height,
            info.Monitor.Right - info.Monitor.Left, height, 0x0010 | 0x0040);
    }

    public void Update(IReadOnlyList<TrackedWindow> windows, nint foreground)
    {
        _clock.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);
        var mine = windows
            .Where(window => window.ExecutablePath is not null && MonitorFromWindow(window.Handle, 2) == _monitor)
            .GroupBy(window => window.ExecutablePath!, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Captionless windows covering the display (games, video) hide this dock like the main one.
        _fullScreen = foreground != 0 && MonitorFromWindow(foreground, 2) == _monitor && IsFullScreen(foreground);
        ApplyVisibility();
        var signature = string.Join("|", mine.Select(group => group.Key + ":" + group.Count() + ":" + group.Any(w => w.Handle == foreground)));
        if (signature == _signature) return;
        _signature = signature;
        _apps.Children.Clear();
        foreach (var group in mine)
        {
            var path = group.Key;
            if (!_icons.TryGetValue(path, out var icon)) _icons[path] = icon = ApplicationIconProvider.GetForFile(path);
            var isForeground = group.Any(window => window.Handle == foreground);
            var image = new Image { Source = icon, Width = 26, Height = 26 };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var dot = new System.Windows.Shapes.Ellipse { Width = isForeground ? 5 : 4, Height = isForeground ? 5 : 4, Margin = new Thickness(0, 2, 0, 0) };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, isForeground ? "IndicatorForegroundBrush" : "IndicatorBrush");
            var cell = new StackPanel();
            cell.Children.Add(image);
            cell.Children.Add(dot);
            var first = group.First();
            var button = DockFlyout.FlatButton(cell, (_, _) => NativeWindowTracker.ToggleActivation(
                (group.FirstOrDefault(window => window.Handle == foreground) ?? first).Handle, path), first.Title);
            button.Padding = new Thickness(6, 2, 6, 0);
            System.Windows.Automation.AutomationProperties.SetName(button, first.Title);
            _apps.Children.Add(button);
        }
    }

    private bool IsFullScreen(nint window)
    {
        const long caption = 0x00C00000;
        if ((GetWindowLongPtr(window, -16).ToInt64() & caption) == caption) return false;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetMonitorInfo(_monitor, ref info) && GetWindowRect(window, out var rect)
            && rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top
            && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom;
    }

    /// <summary>Handles of every display except the primary one.</summary>
    public static List<nint> SecondaryMonitors()
    {
        var monitors = new List<nint>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && (info.Flags & 1) == 0) monitors.Add(monitor);
            return true;
        }, 0);
        return monitors;
    }

    private static uint GetDpiForMonitor(nint monitor) =>
        GetDpiForMonitor(monitor, 0, out var x, out _) == 0 && x > 0 ? x : 96;

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc callback, nint data);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
}
