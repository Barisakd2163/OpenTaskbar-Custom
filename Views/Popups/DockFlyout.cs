using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

/// <summary>
/// A glass popup anchored to a dock element (stacks, calendar, tray, desktops). Opens away from the docked screen edge
/// and closes when the pointer clicks elsewhere.
/// </summary>
public sealed class DockFlyout
{
    private static readonly SolidColorBrush ShadowBrush1 = FreezeBrush(Color.FromArgb(0x26, 0x05, 0x07, 0x0B));
    private static readonly SolidColorBrush ShadowBrush2 = FreezeBrush(Color.FromArgb(0x18, 0x05, 0x07, 0x0B));
    private static readonly SolidColorBrush ShadowBrush3 = FreezeBrush(Color.FromArgb(0x0E, 0x05, 0x07, 0x0B));
    private static readonly SolidColorBrush ShadowBrush4 = FreezeBrush(Color.FromArgb(0x06, 0x05, 0x07, 0x0B));

    private static SolidColorBrush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    internal static void AttachSoftShadow(Panel host, double cornerRadius)
    {
        // Four concentric outer stroke rings with transparent interiors preserve glass translucency without ShaderEffect/D3DCompiler.
        AddRing(host, 2, 1, cornerRadius + 2, ShadowBrush1);
        AddRing(host, 5, 2, cornerRadius + 5, ShadowBrush2);
        AddRing(host, 9, 3, cornerRadius + 9, ShadowBrush3);
        AddRing(host, 14, 4, cornerRadius + 14, ShadowBrush4);
    }

    private static void AddRing(Panel host, double spread, double yOffset, double radius, Brush brush)
    {
        var prev = spread switch
        {
            2 => 0d,
            5 => 2d,
            9 => 5d,
            _ => 9d
        };
        var thickness = spread - prev;
        host.Children.Add(new Border
        {
            Margin = new Thickness(-spread, -spread + yOffset, -spread, -spread - yOffset),
            CornerRadius = new CornerRadius(radius),
            BorderThickness = new Thickness(thickness),
            BorderBrush = brush,
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        });
    }

    private static int _openCount;
    private WeakReference<UIElement>? _closedTarget;
    private nint _previousForeground;
    private bool _restoreFocus;
    private long _closedAt;
    private readonly Popup _popup;
    private readonly Grid _root;
    private readonly Border _frame;
    private readonly Border _innerBevel;

    public DockFlyout()
    {
        _innerBevel = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12),
            Background = System.Windows.Media.Brushes.Transparent
        };
        _innerBevel.SetResourceReference(Border.BorderBrushProperty, "DockInnerBevelMaterial");

        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(0),
            Child = _innerBevel
        };
        _frame.SetResourceReference(Border.BackgroundProperty, "MenuMaterial");
        _frame.SetResourceReference(Border.BorderBrushProperty, "MenuStroke");
        _frame.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        _frame.SetResourceReference(TextElement.FontFamilyProperty, "InterfaceFont");

        var contentHost = new Grid();
        AttachSoftShadow(contentHost, 14);
        contentHost.Children.Add(_frame);

        _root = new Grid { Margin = new Thickness(12, 10, 12, 14), Children = { contentHost } };
        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = false,
            Child = _root,
            PopupAnimation = PopupAnimation.Fade,
            Placement = PlacementMode.Custom,
            CustomPopupPlacementCallback = Place
        };
        _popup.Opened += (_, _) =>
        {
            _openCount++;
            OpenFlyouts.Add(this);
        };
        _popup.Closed += (_, _) =>
        {
            _openCount = Math.Max(0, _openCount - 1);
            OpenFlyouts.Remove(this);
            var target = _popup.PlacementTarget;
            _closedTarget = target is not null ? new WeakReference<UIElement>(target) : null;
            _closedAt = Environment.TickCount64;
            // Light dismiss and Esc hand the keyboard back to the app that had it before the flyout opened.
            if (_restoreFocus && _previousForeground != 0
                && target is not null && Window.GetWindow(target) is { IsActive: true })
            {
                NativeWindowTracker.Activate(_previousForeground);
            }

            _innerBevel.Child = null;
            _popup.PlacementTarget = null;
            Closed?.Invoke(this, EventArgs.Empty);
        };
        _popup.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Escape) return;
            args.Handled = true;
            _popup.IsOpen = false;
        };
    }

    private static readonly List<DockFlyout> OpenFlyouts = [];

    /// <summary>Closes every flyout, e.g. when the dock loses activation or another flyout opens.</summary>
    public static void CloseAll()
    {
        foreach (var flyout in OpenFlyouts.ToArray()) flyout.Close();
    }

    /// <summary>The dock edge the flyouts open away from.</summary>
    public static DockEdge Edge { get; set; } = DockEdge.Bottom;

    public static bool IsAnyOpen => _openCount > 0;

    public event EventHandler? Closed;

    public bool IsOpen => _popup.IsOpen;

    public void Show(UIElement target, UIElement content, bool animate = true, bool focusable = true)
    {
        _root.Margin = Edge switch
        {
            DockEdge.Left => new Thickness(12, 10, 14, 14),
            DockEdge.Right => new Thickness(14, 10, 12, 14),
            _ => new Thickness(12, 10, 12, 14)
        };
        _innerBevel.Child = content;
        _popup.StaysOpen = !focusable;
        _popup.PopupAnimation = animate && SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None;
        foreach (var other in OpenFlyouts.Where(flyout => !ReferenceEquals(flyout, this)).ToArray()) other.Close();
        _popup.PlacementTarget = target;
        if (focusable)
        {
            // The dock never activates on click; give it focus so the flyout gets keyboard input and closes with the dock.
            var owner = Window.GetWindow(target);
            _previousForeground = NativeWindowTracker.GetForegroundApplicationWindow();
            _restoreFocus = true;
            owner?.Activate();
            _popup.IsOpen = true;
            _frame.Focusable = true;
            _frame.Focus();
        }
        else
        {
            _previousForeground = 0;
            _restoreFocus = false;
            _popup.IsOpen = true;
            _frame.Focusable = false;
        }
    }

    public void Toggle(UIElement target, Func<UIElement> content, bool animate = true)
    {
        if (_popup.IsOpen && ReferenceEquals(_popup.PlacementTarget, target))
        {
            Close();
            return;
        }

        // Clicking the anchor of an open flyout first light-dismisses it on mouse down; don't reopen on the click.
        if (RecentlyClosed(target)) return;
        Show(target, content(), animate);
    }

    public bool RecentlyClosed(UIElement target) =>
        _closedTarget is not null && _closedTarget.TryGetTarget(out var prev) && ReferenceEquals(prev, target)
        && Environment.TickCount64 - _closedAt < 350;

    /// <summary>Closes without restoring focus: used when a flyout action opens something that should get it.</summary>
    public void Close()
    {
        _restoreFocus = false;
        _popup.IsOpen = false;
    }

    private static CustomPopupPlacement[] Place(Size popupSize, Size targetSize, Point offset)
    {
        var point = Edge switch
        {
            DockEdge.Left => new Point(targetSize.Width, (targetSize.Height - popupSize.Height) / 2),
            DockEdge.Right => new Point(-popupSize.Width, (targetSize.Height - popupSize.Height) / 2),
            _ => new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height)
        };
        return [new CustomPopupPlacement(point, PopupPrimaryAxis.None)];
    }

    /// <summary>Flat, borderless button used for flyout rows and tiles.</summary>
    public static Button FlatButton(object content, RoutedEventHandler click, string? toolTip = null)
    {
        var button = new Button { Content = content, ToolTip = toolTip, Cursor = System.Windows.Input.Cursors.Hand };
        button.SetResourceReference(FrameworkElement.StyleProperty, "FlyoutButton");
        button.Click += click;
        if (toolTip is not null) System.Windows.Automation.AutomationProperties.SetName(button, toolTip);
        return button;
    }

    public static TextBlock Heading(string text)
    {
        var block = new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(4, 0, 4, 8) };
        return block;
    }

    public static TextBlock Caption(string text)
    {
        var block = new TextBlock { Text = text, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 2) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return block;
    }
}
