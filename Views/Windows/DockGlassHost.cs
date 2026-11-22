using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

/// <summary>
/// A click-through, non-activated topmost window painted with the real DWM Desktop Acrylic backdrop.
/// The dock itself must stay a layered window for per-pixel alpha (rounded pill, transparent headroom,
/// click-through gaps) and WS_EX_LAYERED windows cannot host a DWM system backdrop. This sibling plate
/// is placed immediately behind the dock so the layered surface shows genuine blurred glass through its
/// translucent fill, while the win32 region clips the acrylic to the same rounded pill.
/// </summary>
internal sealed class DockGlassHost : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x00000020;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private nint _handle;
    private nint _dockHandle;
    private int _x = int.MinValue;
    private int _y = int.MinValue;
    private int _width = -1;
    private int _height = -1;
    private int _radius = -1;
    private bool _active;

    /// <summary>True while the native acrylic plate is actually enabled and visible.</summary>
    public bool IsAcrylicEnabled => _active;

    public DockGlassHost()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Transparent;
        // A layered window cannot host a DWM system backdrop; the dock owns the per-pixel alpha instead.
        AllowsTransparency = false;
        Focusable = false;
        Width = 1;
        Height = 1;
        Left = -32000;
        Top = -32000;
        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>Remembers the dock HWND so the plate can be inserted directly behind it in the topmost group.</summary>
    public void Attach(nint dockHandle) => _dockHandle = dockHandle;

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        if (source is null) return;
        _handle = source.Handle;
        var style = GetWindowLongPtr(_handle, GwlExStyle).ToInt64();
        _ = SetWindowLongPtr(_handle, GwlExStyle,
            new nint(style | WsExTransparent | WsExNoActivate | WsExToolWindow));
    }

    /// <summary>Enables or removes the native acrylic for the current dock surface mode.</summary>
    public void Refresh(SurfaceStyle style)
    {
        if (_handle == 0) return;
        var enable = style.Background == SurfaceBackground.Glass
            && SettingsBackdrop.TryEnable(this, ThemeManager.EffectiveTheme);
        if (!enable) SettingsBackdrop.Disable(this);

        if (enable != _active)
        {
            _active = enable;
            if (enable)
            {
                Show();
                if (_dockHandle != 0)
                    _ = SetWindowPos(_handle, _dockHandle, 0, 0, 0, 0,
                        SwpNoMove | SwpNoSize | SwpNoActivate);
            }
            else
            {
                Hide();
                _x = int.MinValue;
                _y = int.MinValue;
                _width = -1;
                _height = -1;
                _radius = -1;
            }
        }
    }

    /// <summary>Positions and clips the plate. Coordinates and radius are in device pixels.</summary>
    public void SetBounds(double left, double top, double width, double height, double radiusDip)
    {
        if (!_active || _handle == 0 || width <= 0 || height <= 0) return;
        var scale = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var x = (int)Math.Round(left);
        var y = (int)Math.Round(top);
        var w = Math.Max(1, (int)Math.Round(width));
        var h = Math.Max(1, (int)Math.Round(height));
        var radius = (int)Math.Round(radiusDip * scale);
        if (x == _x && y == _y && w == _width && h == _height && radius == _radius) return;

        _x = x;
        _y = y;
        _width = w;
        _height = h;
        _radius = radius;
        _ = SetWindowPos(_handle, nint.Zero, x, y, w, h,
            SwpNoZOrder | SwpNoActivate | SwpShowWindow);
        ApplyRegion(w, h, radius);
    }

    private void ApplyRegion(int width, int height, int radius)
    {
        nint region = radius <= 0
            ? CreateRectRgn(0, 0, width + 1, height + 1)
            : CreateRoundRectRgn(0, 0, width + 1, height + 1,
                Math.Min(Math.Min(width, height), radius * 2) + 1,
                Math.Min(Math.Min(width, height), radius * 2) + 1);
        if (region == 0) return;
        // SetWindowRgn takes ownership; the old region is released when it is replaced.
        _ = SetWindowRgn(_handle, region, true);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(nint window, nint region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
}
