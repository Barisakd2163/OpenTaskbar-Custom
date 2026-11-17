using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar;

/// <summary>
/// A transient message popup shown beside the dock instead of inside it, so errors never resize the dock.
/// It opens away from the docked screen edge, dismisses itself, and pauses while the pointer rests on it.
/// </summary>
public sealed class DockNotice : IDisposable
{
    private static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(5);
    private const double Gap = 8;

    private readonly FrameworkElement _anchor;
    private readonly Popup _popup;
    private readonly Border _frame;
    private readonly ContentControl _icon;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _timer;
    private (string Message, bool IsError)? _pending;
    private bool _disposed;

    public DockNotice(FrameworkElement anchor)
    {
        _anchor = anchor;
        _timer = new DispatcherTimer(DispatcherPriority.Background, anchor.Dispatcher);
        _timer.Tick += (_, _) => Hide();
        _icon = new ContentControl { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0) };
        _icon.SetResourceReference(FrameworkElement.StyleProperty, "WidgetIcon");
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        _text.SetResourceReference(TextBlock.FontSizeProperty, "TextSizeCaption");
        AutomationProperties.SetLiveSetting(_text, AutomationLiveSetting.Assertive);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_icon);
        row.Children.Add(_text);
        var inner = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(13),
            Padding = new Thickness(12, 9, 14, 9),
            Child = row
        };
        inner.SetResourceReference(Border.BorderBrushProperty, "DockInnerBevelMaterial");
        _frame = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Child = inner,
            Cursor = Cursors.Hand,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 24, ShadowDepth = 5, Direction = 270, Opacity = 0.45,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance
            }
        };
        _frame.SetResourceReference(Border.BackgroundProperty, "MenuMaterial");
        _frame.SetResourceReference(Border.BorderBrushProperty, "MenuStroke");
        _frame.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
        _frame.SetResourceReference(TextElement.FontFamilyProperty, "InterfaceFont");
        _frame.MouseLeftButtonUp += (_, _) => Hide();
        _frame.MouseEnter += (_, _) => _timer.Stop();
        _frame.MouseLeave += (_, _) => RestartTimer();

        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen = true,
            Focusable = false,
            Child = _frame,
            Placement = PlacementMode.Custom,
            PlacementTarget = anchor,
            CustomPopupPlacementCallback = Place
        };
    }

    public bool IsOpen => _popup.IsOpen;

    private bool _lastIsError;

    public void Show(string message, bool isError)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // The dock can report a failure before its window has been shown; wait for the anchor to exist on screen.
        if (!_anchor.IsLoaded)
        {
            if (_pending is null) _anchor.Loaded += Anchor_Loaded;
            _pending = (message, isError);
            return;
        }

        _lastIsError = isError;
        _text.Text = message;
        AutomationProperties.SetName(_text, message);
        _icon.Content = _frame.FindResource(isError ? "IconAlertCircle" : "IconInfoCircle");
        _icon.SetResourceReference(Control.ForegroundProperty, isError ? "DangerText" : "TextSecondary");
        _frame.Margin = DockFlyout.Edge switch
        {
            DockEdge.Left => new Thickness(Gap, 0, 0, 0),
            DockEdge.Right => new Thickness(0, 0, Gap, 0),
            _ => new Thickness(0, 0, 0, Gap)
        };
        _popup.PopupAnimation = SystemParameters.ClientAreaAnimation ? PopupAnimation.Fade : PopupAnimation.None;
        _popup.IsOpen = true;
        RestartTimer();
    }

    public void Hide()
    {
        _timer.Stop();
        _popup.IsOpen = false;
    }

    private void RestartTimer()
    {
        if (!_popup.IsOpen) return;
        _timer.Stop();
        _timer.Interval = _lastIsError ? ErrorDuration : InfoDuration;
        _timer.Start();
    }

    private void Anchor_Loaded(object sender, RoutedEventArgs e)
    {
        _anchor.Loaded -= Anchor_Loaded;
        if (_pending is { } pending && !_disposed) Show(pending.Message, pending.IsError);
        _pending = null;
    }

    private static CustomPopupPlacement[] Place(Size popupSize, Size targetSize, Point offset) =>
        DockFlyout.PlaceAwayFromEdge(popupSize, targetSize);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _anchor.Loaded -= Anchor_Loaded;
        Hide();
    }
}
