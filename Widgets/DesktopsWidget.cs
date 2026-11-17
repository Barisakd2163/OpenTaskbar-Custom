using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>Current virtual desktop with previous/next, Task View and new-desktop shortcuts.</summary>
public sealed class DesktopsWidget : DockWidget
{
    private readonly TextBlock _label;
    private readonly Button _previous;
    private readonly Button _next;
    private readonly Button _add;
    private readonly Button _taskView;
    private (int Current, int Count) _state = (0, 0);

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 60;
    public override double FullWidthEstimate => 134;
    public override double CompactWidthEstimate => 52;

    protected override void OnCompactChanged(bool compact) => ApplyDensityLayout();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensityLayout();

    private void ApplyDensityLayout()
    {
        var vis = IsCompact && !IsHoverExpanded ? Visibility.Collapsed : Visibility.Visible;
        _previous.Visibility = vis;
        _next.Visibility = vis;
        _add.Visibility = vis;
    }

    public override UIElement? BuildHoverFlyoutContent()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(IconButton("IconChevronLeft", () => VirtualDesktops.Previous()));
        row.Children.Add(IconButton("IconChevronNext", () => VirtualDesktops.Next()));
        row.Children.Add(IconButton("IconPlus", () => VirtualDesktops.Create()));
        return row;
    }

    public DesktopsWidget()
    {
        _previous = IconButton("IconChevronLeft", () => VirtualDesktops.Previous());
        _next = IconButton("IconChevronNext", () => VirtualDesktops.Next());
        _add = IconButton("IconPlus", () => VirtualDesktops.Create());
        _label = new TextBlock { MinWidth = 30, TextAlignment = TextAlignment.Center };
        _label.SetResourceReference(StyleProperty, "WidgetText");
        _taskView = new Button { Content = _label, Width = double.NaN, Padding = new Thickness(4, 0, 4, 0) };
        _taskView.SetResourceReference(StyleProperty, "WidgetIconButton");
        _taskView.Width = double.NaN;
        _taskView.Click += (_, _) => Report(VirtualDesktops.TaskView());

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_previous);
        row.Children.Add(_taskView);
        row.Children.Add(_next);
        row.Children.Add(_add);
        var card = new Border { Child = row, Padding = new Thickness(4, 0, 4, 0) };
        card.SetResourceReference(StyleProperty, "WidgetCardBorder");
        Content = card;
        ApplyLocalization();
    }

    private Button IconButton(string icon, Func<string?> action)
    {
        var glyph = new ContentControl { Content = FindResource(icon), Width = 14, Height = 14 };
        glyph.SetResourceReference(StyleProperty, "WidgetIcon");
        glyph.SetResourceReference(ForegroundProperty, "TextPrimary");
        var button = new Button { Content = glyph };
        button.SetResourceReference(StyleProperty, "WidgetIconButton");
        button.Click += (_, _) => Report(action());
        return button;
    }

    private static void SetButtonLocalization(Button button, string key)
    {
        var text = UiText.Get(key);
        button.ToolTip = text;
        AutomationProperties.SetName(button, text);
    }

    private void ApplyLocalization()
    {
        SetButtonLocalization(_previous, "DesktopPrevious");
        SetButtonLocalization(_next, "DesktopNext");
        SetButtonLocalization(_add, "DesktopNew");
        SetButtonLocalization(_taskView, "DesktopTaskView");
        if (_state.Count > 0)
        {
            AutomationProperties.SetName(this, UiText.Format("DesktopState", _state.Current, _state.Count));
        }
    }

    protected override void OnActivated()
    {
        _state = (0, 0);
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        ApplyLocalization();
        RefreshState();
    }

    protected override void OnDeactivated()
    {
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
    }

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsActive) ApplyLocalization();
    }

    protected override void OnTick(DateTime now) => RefreshState();

    private void RefreshState()
    {
        if (!IsActive) return;
        var state = VirtualDesktops.Read();
        if (state == _state) return;
        _state = state;
        _label.Text = state.Count > 1 ? $"{state.Current} / {state.Count}" : state.Current.ToString(CultureInfo.CurrentCulture);
        _previous.IsEnabled = state.Current > 1;
        _next.IsEnabled = state.Current < state.Count;
        AutomationProperties.SetName(this, UiText.Format("DesktopState", state.Current, state.Count));
    }
}
