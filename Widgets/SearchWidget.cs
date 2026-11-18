using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>
/// An active dock-side search capsule for Windows Search (Win+S).
/// Provides an embedded search input on the dock that seamlessly opens and controls Windows Search.
/// </summary>
public sealed class SearchWidget : DockWidget
{
    private const double ExpandedWidth = 196.0;
    private const double CompactWidth = 44.0;

    private readonly Border _container;
    private readonly ContentControl _icon;
    private readonly Grid _inputArea;
    private readonly TextBlock _placeholder;
    private readonly TextBox _inputBox;
    private readonly Button _clearButton;
    private bool _syncing;

    // Injectable shell actions keep the click/focus lifecycle testable without opening Windows Search.
    internal Func<bool> IsSearchOpen { get; set; } = ShellShortcuts.IsSearchFlyoutOpen;
    internal Func<FrameworkElement?, string?> OpenSearch { get; set; } = ShellShortcuts.OpenWindowsSearch;
    internal Action<string> SyncQuery { get; set; } = ShellShortcuts.SyncSearchQuery;

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
            Margin = new Thickness(2, 0, 0, 0),
            IsHitTestVisible = false
        };
        _icon.SetResourceReference(StyleProperty, "WidgetIcon");
        _icon.SetResourceReference(ForegroundProperty, "TextSecondary");

        _placeholder = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            FontSize = 12,
            Margin = new Thickness(2, 0, 0, 0)
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        _inputBox = new TextBox
        {
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            FontSize = 12,
            Padding = new Thickness(2, 0, 0, 0),
            MaxLength = 250
        };
        _inputBox.SetResourceReference(TextBox.ForegroundProperty, "TextPrimary");
        _inputBox.SetResourceReference(TextBox.CaretBrushProperty, "IndicatorForegroundBrush");

        var closeIcon = new ContentControl
        {
            Content = FindResource("IconClose"),
            Width = 10,
            Height = 10,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        closeIcon.SetResourceReference(StyleProperty, "WidgetIcon");

        _clearButton = new Button
        {
            Width = 18,
            Height = 18,
            Content = closeIcon,
            Visibility = Visibility.Collapsed,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0)
        };
        _clearButton.SetResourceReference(StyleProperty, "WidgetIconButton");
        _clearButton.Click += (_, _) =>
        {
            _inputBox.Text = string.Empty;
            _inputBox.Focus();
        };

        _inputArea = new Grid { Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center };
        _inputArea.Children.Add(_placeholder);
        _inputArea.Children.Add(_inputBox);

        var innerGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        innerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(_icon, 0);
        Grid.SetColumn(_inputArea, 1);
        Grid.SetColumn(_clearButton, 2);

        innerGrid.Children.Add(_icon);
        innerGrid.Children.Add(_inputArea);
        innerGrid.Children.Add(_clearButton);

        _container = new Border
        {
            Height = 32,
            Width = ExpandedWidth,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 0, 6, 0),
            Child = innerGrid,
            VerticalAlignment = VerticalAlignment.Center
        };
        _container.SetResourceReference(StyleProperty, "WidgetCardBorder");
        _container.PreviewMouseLeftButtonDown += (_, e) =>
        {
            var alreadyFocused = _inputBox.IsFocused;
            HandleSearchPress(alreadyFocused);
            if (!alreadyFocused) e.Handled = true;
        };

        _inputBox.GotFocus += (_, _) =>
        {
            DockFlyout.CloseAll();
            ApplyDensity();
            EnsureSearchOpened();
            if (!string.IsNullOrEmpty(_inputBox.Text))
            {
                SyncQuery(_inputBox.Text);
            }
        };

        _inputBox.LostFocus += (_, _) =>
        {
            ApplyDensity();
            if (!IsSearchOpen())
            {
                // Clearing a stale query must not trigger TextChanged to reopen Windows Search.
                _syncing = true;
                try { _inputBox.Clear(); }
                finally { _syncing = false; }
            }
        };

        _inputBox.TextChanged += (_, _) =>
        {
            var hasText = !string.IsNullOrEmpty(_inputBox.Text);
            _placeholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
            _clearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;

            if (_syncing) return;
            EnsureSearchOpened();
            SyncQuery(_inputBox.Text);
        };

        _inputBox.PreviewKeyDown += InputBox_PreviewKeyDown;

        Localize();
        Content = _container;
    }

    // GotFocus runs only the first time. Later clicks must also reopen Search if the textbox kept focus.
    internal void HandleSearchPress(bool alreadyFocused)
    {
        if (alreadyFocused) EnsureSearchOpened();
        else _inputBox.Focus();
    }

    private void EnsureSearchOpened()
    {
        if (!IsSearchOpen())
        {
            var error = OpenSearch(this);
            if (error is not null) ReportError?.Invoke(error);
        }
    }

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _syncing = true;
                _inputBox.Text = string.Empty;
                _syncing = false;
                ShellShortcuts.CloseWindowsSearch();
                Keyboard.ClearFocus();
                e.Handled = true;
                break;

            case Key.Enter:
                ShellShortcuts.ForwardKeyToSearch(0x0D); // VK_RETURN
                e.Handled = true;
                break;

            case Key.Down:
                ShellShortcuts.ForwardKeyToSearch(0x28); // VK_DOWN
                e.Handled = true;
                break;

            case Key.Up:
                ShellShortcuts.ForwardKeyToSearch(0x26); // VK_UP
                e.Handled = true;
                break;
        }
    }

    protected override void OnCompactChanged(bool compact) => ApplyDensity();
    protected override void OnHoverExpandedChanged(bool expanded) => ApplyDensity();

    private void ApplyDensity()
    {
        var isCompactMode = IsCompact && !IsHoverExpanded && !_inputBox.IsFocused;
        _inputArea.Visibility = isCompactMode ? Visibility.Collapsed : Visibility.Visible;
        _clearButton.Visibility = isCompactMode || string.IsNullOrEmpty(_inputBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        _container.Width = isCompactMode ? CompactWidth : ExpandedWidth;
        _container.Padding = isCompactMode ? new Thickness(0) : new Thickness(8, 0, 6, 0);
        _icon.HorizontalAlignment = isCompactMode ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _icon.Margin = isCompactMode ? new Thickness(0) : new Thickness(2, 0, 0, 0);
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
        _placeholder.Text = UiText.Get("WidgetLauncher");
        _container.ToolTip = UiText.Get("WidgetLauncherDescription");
        _clearButton.ToolTip = UiText.Get("Close");
        AutomationProperties.SetName(_container, UiText.Get("OpenLauncher"));
        AutomationProperties.SetName(_inputBox, UiText.Get("WidgetLauncher"));
    }
}
