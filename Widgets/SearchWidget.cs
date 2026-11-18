using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// A dock-side button for Windows Search (Win+S).
/// Clicking it opens Windows' native search menu positioned directly above the widget.
/// </summary>
public sealed class SearchWidget : DockWidget
{
    private const double ExpandedWidth = 84.0;
    private const double CompactWidth = 40.0;

    private readonly Button _button;
    private readonly ContentControl _icon;
    private readonly TextBlock _label;

    // Injectable shell actions keep the click lifecycle testable without opening Windows Search.
    internal Func<bool> IsSearchOpen { get; set; } = ShellShortcuts.IsSearchFlyoutOpen;
    internal Func<FrameworkElement?, string?> OpenSearch { get; set; } = ShellShortcuts.OpenWindowsSearch;

    public static string Id => "Search";

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 45;
    public override double FullWidthEstimate => ExpandedWidth;
    public override double CompactWidthEstimate => CompactWidth;

    public SearchWidget()
    {
        _icon = new ContentControl
        {
            Content = FindResource("IconSearch"),
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        _icon.SetResourceReference(StyleProperty, "WidgetIcon");
        _icon.SetResourceReference(ForegroundProperty, "TextSecondary");

        _label = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            IsHitTestVisible = false
        };
        _label.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        content.Children.Add(_icon);
        content.Children.Add(_label);

        _button = new Button
        {
            Content = content,
            Height = 32,
            Padding = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        _button.SetResourceReference(StyleProperty, "WidgetCardButton");
        _button.Click += (_, _) => HandleSearchClick();

        Localize();
        Content = _button;
    }

    internal void HandleSearchClick()
    {
        DockFlyout.CloseAll();
        if (!IsSearchOpen())
        {
            var error = OpenSearch(this);
            if (error is not null) ReportError?.Invoke(error);
        }
    }

    protected override void OnCompactChanged(bool compact) => ApplyDensity();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensity();

    private void ApplyDensity()
    {
        var isCompactMode = IsCompact && !IsHoverExpanded;
        _label.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        _button.Width = isCompactMode ? CompactWidth : double.NaN;
        _button.Padding = isCompactMode ? new Thickness(0) : new Thickness(10, 0, 10, 0);
    }

    public override UIElement? BuildHoverFlyoutContent()
    {
        var root = new StackPanel { Width = 232 };
        root.Children.Add(DockFlyout.Heading(UiText.Get("WidgetLauncher")));
        root.Children.Add(DockFlyout.Caption(UiText.Get("WidgetLauncherDescription")));
        return root;
    }

    protected override void OnActivated()
    {
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        Localize();
    }

    protected override void OnDeactivated() =>
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e) => Localize();

    private void Localize()
    {
        _label.Text = UiText.Get("WidgetLauncher");
        _button.ToolTip = UiText.Get("WidgetLauncherDescription");
        AutomationProperties.SetName(_button, UiText.Get("WidgetLauncher"));
    }
}
