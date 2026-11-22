using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;

namespace OpenTaskbar;

public partial class SettingsWindow
{
    private DispatcherTimer? _searchHighlightTimer;
    private HighlightedBorderState? _highlightedBorder;

    private sealed record HighlightedBorderState(
        Border Target,
        object Background,
        object BorderBrush,
        object BorderThickness,
        object CornerRadius);

    internal IReadOnlyList<SettingsSearchEntry> CollectSearchEntries()
    {
        var entries = new List<SettingsSearchEntry>();
        var rowStyles = ResolveRowStyles();
        var rowLabelStyle = TryFindResource("SettingsRowLabel") as Style;
        var rowDescriptionStyle = TryFindResource("SettingsRowDescription") as Style;

        for (var tabIndex = 0; tabIndex < SettingsTabs.Items.Count; tabIndex++)
        {
            if (SettingsTabs.Items[tabIndex] is not TabItem tab) continue;

            var pageName = ResolveTabHeader(tab);
            if (tab.Content is DependencyObject contentRoot)
            {
                CollectRowEntries(
                    contentRoot,
                    tabIndex,
                    pageName,
                    context: null,
                    rowStyles,
                    rowLabelStyle,
                    rowDescriptionStyle,
                    entries);
            }

            if (ReferenceEquals(tab, WidgetsTab))
            {
                foreach (var option in _widgetOptions)
                {
                    if (!string.IsNullOrWhiteSpace(option.Label))
                    {
                        entries.Add(new SettingsSearchEntry(
                            tabIndex,
                            pageName,
                            option.Label,
                            option.Description ?? string.Empty,
                            Target: option,
                            Context: null));
                    }

                    if (option.SettingsView is DependencyObject settingsView)
                    {
                        CollectRowEntries(
                            settingsView,
                            tabIndex,
                            pageName,
                            context: option,
                            rowStyles,
                            rowLabelStyle,
                            rowDescriptionStyle,
                            entries);
                    }
                }
            }
        }

        return entries;
    }

    private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = SettingsSearchBox.Text;
        var hasText = !string.IsNullOrEmpty(text);
        SettingsSearchPlaceholder.Visibility = hasText ? Visibility.Collapsed : Visibility.Visible;
        SettingsSearchClearButton.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        // Re-indexing the whole settings tree and rebuilding result templates on every keystroke
        // is expensive; coalesce rapid typing into one update ~120ms after the last character.
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(120);
        _searchDebounceTimer.Tick -= SearchDebounce_Tick;
        _searchDebounceTimer.Tick += SearchDebounce_Tick;
        _searchDebounceTimer.Start();
    }

    private void SearchDebounce_Tick(object? sender, EventArgs e)
    {
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Tick -= SearchDebounce_Tick;
        ApplySettingsSearch(SettingsSearchBox.Text);
    }

    private void SettingsSearchClearButton_Click(object sender, RoutedEventArgs e)
    {
        SettingsSearchBox.Text = string.Empty;
        SettingsSearchBox.Focus();
    }

    private void RefreshSettingsSearchIfActive()
    {
        if (!string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
        {
            ApplySettingsSearch(SettingsSearchBox.Text);
        }
    }

    private void ApplySettingsSearch(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            SettingsSearchOverlay.Visibility = Visibility.Collapsed;
            SettingsSearchEmptyText.Visibility = Visibility.Collapsed;
            SettingsSearchResultsGroup.Visibility = Visibility.Collapsed;
            SettingsSearchResultsPanel.Children.Clear();
            for (var i = 0; i < SettingsTabs.Items.Count; i++)
            {
                if (SettingsTabs.Items[i] is TabItem tab)
                {
                    tab.Visibility = Visibility.Visible;
                }
            }
            return;
        }

        var matches = SettingsSearchMatcher.Search(CollectSearchEntries(), query);
        var matchedTabs = new HashSet<int>();
        foreach (var match in matches)
        {
            matchedTabs.Add(match.TabIndex);
        }

        for (var i = 0; i < SettingsTabs.Items.Count; i++)
        {
            if (SettingsTabs.Items[i] is TabItem tab)
            {
                tab.Visibility = matchedTabs.Contains(i) ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        SettingsSearchResultsPanel.Children.Clear();
        if (matches.Count == 0)
        {
            SettingsSearchEmptyText.Visibility = Visibility.Visible;
            SettingsSearchResultsGroup.Visibility = Visibility.Collapsed;
        }
        else
        {
            SettingsSearchEmptyText.Visibility = Visibility.Collapsed;
            SettingsSearchResultsGroup.Visibility = Visibility.Visible;
            foreach (var match in matches)
            {
                SettingsSearchResultsPanel.Children.Add(CreateSearchResultButton(match));
            }
        }

        SettingsSearchOverlay.Visibility = Visibility.Visible;
    }

    private Button CreateSearchResultButton(SettingsSearchEntry entry)
    {
        var rowContent = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var headerRow = new DockPanel();
        if (!string.IsNullOrWhiteSpace(entry.PageName))
        {
            var badgeText = new TextBlock
            {
                Text = entry.PageName,
                FontSize = 10.5,
                VerticalAlignment = VerticalAlignment.Center
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = badgeText
            };
            badge.SetResourceReference(Border.BackgroundProperty, "SettingsControl");
            badge.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");
            DockPanel.SetDock(badge, Dock.Right);
            headerRow.Children.Add(badge);
        }

        var titleText = new TextBlock
        {
            Text = entry.Title
        };
        if (TryFindResource("SettingsRowLabel") is Style labelStyle)
        {
            titleText.Style = labelStyle;
        }
        headerRow.Children.Add(titleText);
        rowContent.Children.Add(headerRow);

        if (!string.IsNullOrWhiteSpace(entry.Description))
        {
            var descriptionText = new TextBlock
            {
                Text = entry.Description
            };
            if (TryFindResource("SettingsRowDescription") is Style descStyle)
            {
                descriptionText.Style = descStyle;
            }
            rowContent.Children.Add(descriptionText);
        }

        var button = new Button
        {
            Content = rowContent,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            MinHeight = 56,
            Padding = new Thickness(8, 10, 8, 10),
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            Template = CreateSearchResultButtonTemplate()
        };
        button.Click += (_, _) => NavigateToSearchResult(entry);
        return button;
    }

    private static ControlTemplate CreateSearchResultButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.Name = "Surface";
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        surface.SetResourceReference(Border.BorderBrushProperty, "StrokeSubtle");
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(0, 0, 0, 1));
        surface.SetValue(Border.SnapsToDevicePixelsProperty, true);
        surface.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, new TemplateBindingExtension(Control.HorizontalContentAlignmentProperty));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, new TemplateBindingExtension(Control.VerticalContentAlignmentProperty));
        surface.AppendChild(presenter);

        template.VisualTree = surface;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        var hoverSetter = new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SurfaceHover"), "Surface");
        hoverTrigger.Setters.Add(hoverSetter);
        template.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger { Property = System.Windows.Controls.Primitives.ButtonBase.IsPressedProperty, Value = true };
        var pressedSetter = new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SurfacePressed"), "Surface");
        pressedTrigger.Setters.Add(pressedSetter);
        template.Triggers.Add(pressedTrigger);

        return template;
    }

    private void NavigateToSearchResult(SettingsSearchEntry entry)
    {
        SettingsSearchBox.Text = string.Empty;
        if (entry.TabIndex >= 0 && entry.TabIndex < SettingsTabs.Items.Count)
        {
            SettingsTabs.SelectedIndex = entry.TabIndex;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_closed) return;

            if (entry.Context is DockWidgetOption parentWidget)
            {
                foreach (var other in _widgetOptions)
                {
                    other.IsSettingsExpanded = ReferenceEquals(other, parentWidget);
                }
                UpdateWidgetOptionState();
                WidgetOptionsList.UpdateLayout();
            }

            FrameworkElement? target = null;
            if (entry.Target is FrameworkElement fe)
            {
                target = fe;
            }
            else if (entry.Target is DockWidgetOption opt)
            {
                WidgetOptionsList.UpdateLayout();
                target = WidgetOptionsList.ItemContainerGenerator.ContainerFromItem(opt) as FrameworkElement
                    ?? FindVisualContainerForItem(WidgetOptionsList, opt);
            }

            if (target is null) return;
            target.BringIntoView();
            HighlightSearchTarget(target);
        });
    }

    private void HighlightSearchTarget(FrameworkElement target)
    {
        ClearSearchHighlight();

        var border = target as Border ?? FindFirstVisualChild<Border>(target);
        if (border is null) return;

        _highlightedBorder = new HighlightedBorderState(
            border,
            border.ReadLocalValue(Border.BackgroundProperty),
            border.ReadLocalValue(Border.BorderBrushProperty),
            border.ReadLocalValue(Border.BorderThicknessProperty),
            border.ReadLocalValue(Border.CornerRadiusProperty));

        var accentColor = TryFindResource("AccentBrush") is SolidColorBrush accentBrush
            ? accentBrush.Color
            : Color.FromRgb(0x38, 0xBD, 0xF8);

        var fillBrush = new SolidColorBrush(Color.FromArgb(36, accentColor.R, accentColor.G, accentColor.B));
        fillBrush.Freeze();
        var strokeBrush = new SolidColorBrush(Color.FromArgb(190, accentColor.R, accentColor.G, accentColor.B));
        strokeBrush.Freeze();

        border.Background = fillBrush;
        border.BorderBrush = strokeBrush;
        border.BorderThickness = new Thickness(1);
        border.CornerRadius = new CornerRadius(6);

        _searchHighlightTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
        _searchHighlightTimer.Tick += (_, _) => ClearSearchHighlight();
        _searchHighlightTimer.Start();
    }

    private void ClearSearchHighlight()
    {
        if (_searchHighlightTimer is not null)
        {
            _searchHighlightTimer.Stop();
            _searchHighlightTimer = null;
        }

        if (_highlightedBorder is not { } state) return;
        _highlightedBorder = null;

        RestoreProperty(state.Target, Border.BackgroundProperty, state.Background);
        RestoreProperty(state.Target, Border.BorderBrushProperty, state.BorderBrush);
        RestoreProperty(state.Target, Border.BorderThicknessProperty, state.BorderThickness);
        RestoreProperty(state.Target, Border.CornerRadiusProperty, state.CornerRadius);
    }

    private static void RestoreProperty(DependencyObject target, DependencyProperty property, object localValue)
    {
        if (ReferenceEquals(localValue, DependencyProperty.UnsetValue))
        {
            target.ClearValue(property);
        }
        else
        {
            target.SetValue(property, localValue);
        }
    }

    private HashSet<Style> ResolveRowStyles()
    {
        var styles = new HashSet<Style>();
        if (TryFindResource("SettingsRow") is Style row) styles.Add(row);
        if (TryFindResource("SettingsRowLast") is Style rowLast) styles.Add(rowLast);
        if (TryFindResource("CompactSettingsRow") is Style compact) styles.Add(compact);
        return styles;
    }

    private void CollectRowEntries(
        DependencyObject node,
        int tabIndex,
        string pageName,
        object? context,
        HashSet<Style> rowStyles,
        Style? rowLabelStyle,
        Style? rowDescriptionStyle,
        List<SettingsSearchEntry> entries)
    {
        if (ReferenceEquals(node, WidgetPanelsPool)) return;

        if (node is Border border && border.Style is { } style && rowStyles.Contains(style))
        {
            if (TryExtractRowText(border, rowLabelStyle, rowDescriptionStyle, out var title, out var description))
            {
                entries.Add(new SettingsSearchEntry(tabIndex, pageName, title, description, Target: border, Context: context));
            }
            return;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject dep)
            {
                CollectRowEntries(dep, tabIndex, pageName, context, rowStyles, rowLabelStyle, rowDescriptionStyle, entries);
            }
        }
    }

    private static bool TryExtractRowText(
        Border row,
        Style? rowLabelStyle,
        Style? rowDescriptionStyle,
        out string title,
        out string description)
    {
        TextBlock? preferredLabel = null;
        TextBlock? fallbackLabel = null;
        TextBlock? descriptionBlock = null;

        void Walk(DependencyObject current)
        {
            if (current is TextBlock tb)
            {
                if (rowLabelStyle is not null && ReferenceEquals(tb.Style, rowLabelStyle))
                {
                    fallbackLabel ??= tb;
                    if (preferredLabel is null && DockPanel.GetDock(tb) != Dock.Right)
                    {
                        preferredLabel = tb;
                    }
                }
                else if (descriptionBlock is null && rowDescriptionStyle is not null && ReferenceEquals(tb.Style, rowDescriptionStyle))
                {
                    descriptionBlock = tb;
                }
            }

            foreach (var child in LogicalTreeHelper.GetChildren(current))
            {
                if (child is DependencyObject dep) Walk(dep);
            }
        }

        Walk(row);

        var labelBlock = preferredLabel ?? fallbackLabel;
        title = labelBlock is not null ? ResolveTextBlockText(labelBlock) : string.Empty;
        description = descriptionBlock is not null ? ResolveTextBlockText(descriptionBlock) : string.Empty;
        return !string.IsNullOrWhiteSpace(title);
    }

    private static string ResolveTabHeader(TabItem tab)
    {
        if (TryResolveLocBinding(tab, HeaderedContentControl.HeaderProperty, out var localized))
        {
            return localized;
        }
        return tab.Header as string ?? string.Empty;
    }

    private static string ResolveTextBlockText(TextBlock textBlock)
    {
        if (TryResolveLocBinding(textBlock, TextBlock.TextProperty, out var localized))
        {
            return localized;
        }
        return textBlock.Text ?? string.Empty;
    }

    private static bool TryResolveLocBinding(DependencyObject target, DependencyProperty property, out string value)
    {
        if (BindingOperations.GetBinding(target, property) is { Path.Path: { Length: > 2 } path }
            && path[0] == '['
            && path[^1] == ']')
        {
            var key = path[1..^1];
            if (UiText.TryGet(key, out var localized) && !string.IsNullOrWhiteSpace(localized))
            {
                value = localized;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static FrameworkElement? FindVisualContainerForItem(DependencyObject root, object item)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && ReferenceEquals(fe.DataContext, item))
            {
                return fe;
            }
            if (FindVisualContainerForItem(child, item) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static T? FindFirstVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (FindFirstVisualChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
