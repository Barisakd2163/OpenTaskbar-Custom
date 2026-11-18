using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// A dock-side button for Windows Search (Win+S).
/// Clicking it opens Windows' native search menu positioned directly above the widget.
/// Styled as an icon-only button with identical dimensions to <see cref="TrayWidget"/>.
/// </summary>
public sealed class SearchWidget : DockWidget
{
    private readonly Button _button;

    // Injectable shell actions keep the click lifecycle testable without opening Windows Search.
    internal Func<bool> IsSearchOpen { get; set; } = ShellShortcuts.IsSearchFlyoutOpen;
    internal Func<FrameworkElement?, string?> OpenSearch { get; set; } = ShellShortcuts.OpenWindowsSearch;

    public static string Id => "Search";

    public SearchWidget()
    {
        var glyph = new ContentControl
        {
            Content = FindResource("IconSearch"),
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        glyph.SetResourceReference(StyleProperty, "WidgetIcon");
        glyph.SetResourceReference(ForegroundProperty, "TextPrimary");

        _button = new Button
        {
            Content = glyph,
            Padding = new Thickness(10, 0, 10, 0)
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
        _button.ToolTip = UiText.Get("WidgetLauncherDescription");
        AutomationProperties.SetName(_button, UiText.Get("WidgetLauncher"));
    }
}
