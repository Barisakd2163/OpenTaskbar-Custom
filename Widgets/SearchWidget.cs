using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// A dock-side entry point for Windows Search (Win+S). Clicking it triggers Windows' native search flyout.
/// </summary>
public sealed class SearchWidget : DockWidget
{
    private readonly Button _button;
    private readonly TextBlock _label;

    public static string Id => "Search";

    public override bool SupportsCompactMode => true;
    public override int ShrinkPriority => 45;
    public override double FullWidthEstimate => 132;
    public override double CompactWidthEstimate => 56;

    public SearchWidget()
    {
        var glyph = new ContentControl { Content = FindResource("IconSearch"), Width = 16, Height = 16 };
        glyph.SetResourceReference(StyleProperty, "WidgetIcon");
        glyph.SetResourceReference(ForegroundProperty, "TextPrimary");

        _label = new TextBlock { Margin = new Thickness(8, 0, 2, 0) };
        _label.SetResourceReference(StyleProperty, "WidgetText");

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(glyph);
        content.Children.Add(_label);

        _button = new Button { Content = content, Padding = new Thickness(8, 0, 10, 0) };
        _button.SetResourceReference(StyleProperty, "WidgetCardButton");
        _button.Click += (_, _) =>
        {
            DockFlyout.CloseAll();
            var error = ShellShortcuts.OpenWindowsSearch(_button);
            if (error is not null) ReportError?.Invoke(error);
        };

        Localize();
        Content = _button;
    }

    protected override void OnCompactChanged(bool compact) => ApplyDensity();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensity();

    private void ApplyDensity() =>
        _label.Visibility = IsCompact && !IsHoverExpanded ? Visibility.Collapsed : Visibility.Visible;

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

    private void UiText_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Localize();

    private void Localize()
    {
        _label.Text = UiText.Get("WidgetLauncher");
        _button.ToolTip = UiText.Get("WidgetLauncherDescription");
        System.Windows.Automation.AutomationProperties.SetName(_button, UiText.Get("OpenLauncher"));
    }
}
