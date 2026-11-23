using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Dedicated Show Desktop dock widget: left-click toggles Show Desktop, middle-click or context menu opens
/// the Desktop folder, dragging files over it reveals the desktop after a short dwell, and dropping files
/// directly onto it moves (or copies with Ctrl) them to the user's Desktop with full Shell undo support.
/// </summary>
public sealed class ShowDesktopWidget : DockWidget
{
    public static string Id => DockWidgetService.ShowDesktopId;

    internal const int DragHoverDwellMs = 450;
    private const int ConfirmationDisplayMs = 1600;

    private readonly Border _card;
    private readonly Border _dropHighlight;
    private readonly StackPanel _contentRow;
    private readonly ContentControl _icon;
    private readonly TextBlock _label;
    private readonly DispatcherTimer _dragHoverTimer;
    private readonly DispatcherTimer _feedbackTimer;

    private MenuItem? _showDesktopMenuItem;
    private MenuItem? _openDesktopMenuItem;
    private bool _leftPressedOnCard;
    private bool _isDragOver;
    private bool _dragHoverRevealed;
    private bool _dragLeavePending;
    private bool _showingConfirmation;
    private bool _confirmationCopied;
    private bool _disposed;

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 50;
    public override bool SupportsStaticPreview => true;
    public override double FullWidthEstimate => 104;
    public override double CompactWidthEstimate => 40;

    public ShowDesktopWidget()
    {
        _dropHighlight = new Border
        {
            BorderThickness = new Thickness(1.5),
            Opacity = 0,
            IsHitTestVisible = false
        };
        _dropHighlight.SetResourceReference(Border.CornerRadiusProperty, "WidgetRadius");
        _dropHighlight.SetResourceReference(Border.BackgroundProperty, "WidgetSelectedFill");
        _dropHighlight.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");

        _icon = new ContentControl
        {
            Width = 15,
            Height = 15,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _icon.SetResourceReference(StyleProperty, "WidgetIcon");
        _icon.SetResourceReference(ContentProperty, "IconMonitor");
        _icon.SetResourceReference(ForegroundProperty, "TextPrimary");

        _label = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            IsHitTestVisible = false
        };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        _contentRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            IsHitTestVisible = false
        };
        _contentRow.Children.Add(_icon);
        _contentRow.Children.Add(_label);

        var root = new Grid { IsHitTestVisible = false };
        root.Children.Add(_dropHighlight);
        root.Children.Add(_contentRow);

        _card = new Border
        {
            Padding = new Thickness(0),
            Cursor = Cursors.Hand,
            AllowDrop = true,
            Child = root
        };
        _card.SetResourceReference(StyleProperty, "WidgetCardBorder");

        _card.PreviewMouseLeftButtonDown += Card_PreviewMouseLeftButtonDown;
        _card.MouseLeave += Card_MouseLeave;
        _card.MouseLeftButtonUp += Card_MouseLeftButtonUp;
        _card.MouseUp += Card_MouseUp;
        _card.DragOver += Card_DragOver;
        _card.DragLeave += Card_DragLeave;
        _card.Drop += Card_Drop;

        _dragHoverTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(DragHoverDwellMs)
        };
        _dragHoverTimer.Tick += DragHoverTimer_Tick;

        _feedbackTimer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(ConfirmationDisplayMs)
        };
        _feedbackTimer.Tick += FeedbackTimer_Tick;

        Content = _card;
        HasData = true;
        Render();
    }

    public override IReadOnlyList<Control> BuildContextMenuItems()
    {
        _showDesktopMenuItem = new MenuItem { Header = UiText.Get("ShowDesktop") };
        if (Application.Current?.TryFindResource("IconMonitor") is Geometry monitorIcon)
        {
            _showDesktopMenuItem.Icon = monitorIcon;
        }
        _showDesktopMenuItem.Click += (_, _) => Report(ShellShortcuts.ToggleShowDesktop());

        _openDesktopMenuItem = new MenuItem { Header = UiText.Get("OpenDesktopFolder") };
        if (Application.Current?.TryFindResource("IconFolder") is Geometry folderIcon)
        {
            _openDesktopMenuItem.Icon = folderIcon;
        }
        _openDesktopMenuItem.Click += (_, _) => Report(DesktopFileTransfer.OpenDesktopFolder());

        return [_showDesktopMenuItem, _openDesktopMenuItem];
    }

    protected override void OnActivated()
    {
        RefreshMenuHeaders();
        Render();
    }

    protected override void OnDeactivated()
    {
        _leftPressedOnCard = false;
        ResetDragHoverState();
        ClearDropConfirmation();
    }

    protected override void OnDisposed()
    {
        _disposed = true;
        _dragHoverTimer.Stop();
        _dragHoverTimer.Tick -= DragHoverTimer_Tick;
        _feedbackTimer.Stop();
        _feedbackTimer.Tick -= FeedbackTimer_Tick;
        _card.PreviewMouseLeftButtonDown -= Card_PreviewMouseLeftButtonDown;
        _card.MouseLeave -= Card_MouseLeave;
        _card.MouseLeftButtonUp -= Card_MouseLeftButtonUp;
        _card.MouseUp -= Card_MouseUp;
        _card.DragOver -= Card_DragOver;
        _card.DragLeave -= Card_DragLeave;
        _card.Drop -= Card_Drop;
        base.OnDisposed();
    }

    protected override void OnRenderPreview() => Render();
    protected override void OnCompactChanged(bool compact) => Render();
    protected override void OnHoverExpandedChanged(bool expanded) => Render();

    protected override void OnLanguageChanged()
    {
        RefreshMenuHeaders();
        Render();
    }

    private void RefreshMenuHeaders()
    {
        if (_showDesktopMenuItem is not null) _showDesktopMenuItem.Header = UiText.Get("ShowDesktop");
        if (_openDesktopMenuItem is not null) _openDesktopMenuItem.Header = UiText.Get("OpenDesktopFolder");
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        // Guard the outer widget spacing so WidgetCoordinator's generic ExternalLayout DragOver
        // never overrides FileDrop with a misleading Move cursor outside the drop card.
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        base.OnDragOver(e);
    }

    protected override void OnDrop(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            return;
        }

        base.OnDrop(e);
    }

    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _leftPressedOnCard = true;
    }

    private void Card_MouseLeave(object sender, MouseEventArgs e)
    {
        _leftPressedOnCard = false;
    }

    private void Card_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_leftPressedOnCard) return;
        _leftPressedOnCard = false;
        e.Handled = true;
        Report(ShellShortcuts.ToggleShowDesktop());
    }

    private void Card_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        Report(DesktopFileTransfer.OpenDesktopFolder());
    }

    private void Card_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        _dragLeavePending = false;
        e.Effects = DesktopFileTransfer.ResolveFileDropEffect(e);
        e.Handled = true;

        SetDragHighlight(active: true);
        if (!_dragHoverRevealed && !_dragHoverTimer.IsEnabled)
        {
            _dragHoverTimer.Start();
        }
    }

    private void Card_DragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Handled = true;
        _dragLeavePending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_disposed && _dragLeavePending)
            {
                ResetDragHoverState();
            }
        });
    }

    private void Card_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        ResetDragHoverState();
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var copy = DesktopFileTransfer.ShouldCopy(e);

        // Let the drag source finish cleanly before launching Shell file operations.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => TransferDroppedFilesAsync(paths, copy));
    }

    private async void TransferDroppedFilesAsync(string[] paths, bool copy)
    {
        var result = await DesktopFileTransfer.TransferToDesktopAsync(paths, copy);
        if (_disposed || !IsActive) return;

        if (result.ErrorKey is { } errorKey)
        {
            Report(errorKey);
            return;
        }

        if (result.Transferred)
        {
            ShowDropConfirmation(copy);
        }
    }

    private void DragHoverTimer_Tick(object? sender, EventArgs e)
    {
        _dragHoverTimer.Stop();
        _dragHoverRevealed = true;
        if (_disposed || !IsActive) return;
        Report(ShellShortcuts.ShowDesktopForDrag());
    }

    private void FeedbackTimer_Tick(object? sender, EventArgs e)
    {
        ClearDropConfirmation();
    }

    private void SetDragHighlight(bool active)
    {
        if (_isDragOver == active) return;
        _isDragOver = active;
        ApplyVisualState();
    }

    private void ResetDragHoverState()
    {
        _dragLeavePending = false;
        _dragHoverTimer.Stop();
        _dragHoverRevealed = false;
        SetDragHighlight(active: false);
    }

    private void ShowDropConfirmation(bool copied)
    {
        _showingConfirmation = true;
        _confirmationCopied = copied;
        _feedbackTimer.Stop();
        _feedbackTimer.Start();
        Render();
    }

    private void ClearDropConfirmation()
    {
        _feedbackTimer.Stop();
        if (!_showingConfirmation) return;
        _showingConfirmation = false;
        Render();
    }

    private void ApplyVisualState()
    {
        if (_showingConfirmation)
        {
            _icon.SetResourceReference(ContentProperty, "IconCheck");
            _icon.SetResourceReference(ForegroundProperty, "WidgetGreen");
            _dropHighlight.SetResourceReference(Border.BackgroundProperty, "WidgetSelectedFill");
            _dropHighlight.SetResourceReference(Border.BorderBrushProperty, "WidgetGreen");
            _dropHighlight.Opacity = 1;
            return;
        }

        _icon.SetResourceReference(ContentProperty, "IconMonitor");
        _icon.SetResourceReference(ForegroundProperty, _isDragOver ? "AccentBrush" : "TextPrimary");
        _dropHighlight.SetResourceReference(Border.BackgroundProperty, "WidgetSelectedFill");
        _dropHighlight.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _dropHighlight.Opacity = _isDragOver ? 1 : 0;
    }

    internal void Render()
    {
        var compact = IsCompact && !IsHoverExpanded;
        _label.Text = _showingConfirmation
            ? UiText.Get(_confirmationCopied ? "CopiedToDesktop" : "MovedToDesktop")
            : UiText.Get("WidgetShowDesktopShort");
        _label.Visibility = compact && !_showingConfirmation ? Visibility.Collapsed : Visibility.Visible;
        _contentRow.Margin = compact && !_showingConfirmation
            ? new Thickness(9, 0, 9, 0)
            : new Thickness(10, 0, 10, 0);

        ApplyVisualState();

        var tooltip = UiText.Get("ShowDesktopTooltip");
        _card.ToolTip = tooltip;
        AutomationProperties.SetName(_card, UiText.Get("WidgetShowDesktop"));
        RaiseContentWidthChanged();
    }
}
