using System.Windows;
using System.Windows.Controls;
using OpenTaskbar.Services;

namespace OpenTaskbar.Widgets;

/// <summary>Current virtual desktop with previous/next, Task View and new-desktop shortcuts.</summary>
public sealed class DesktopsWidget : DockWidget
{
    private readonly TextBlock _label;
    private readonly Button _previous;
    private readonly Button _next;
    private (int Current, int Count) _state = (0, 0);

    public DesktopsWidget()
    {
        _previous = IconButton("IconChevronLeft", "DesktopPrevious", () => VirtualDesktops.Previous());
        _next = IconButton("IconChevronNext", "DesktopNext", () => VirtualDesktops.Next());
        var add = IconButton("IconPlus", "DesktopNew", () => VirtualDesktops.Create());
        _label = new TextBlock { MinWidth = 30, TextAlignment = TextAlignment.Center };
        _label.SetResourceReference(StyleProperty, "WidgetText");
        var taskView = new Button { Content = _label, Width = double.NaN, Padding = new Thickness(4, 0, 4, 0) };
        taskView.SetResourceReference(StyleProperty, "WidgetIconButton");
        taskView.Width = double.NaN;
        taskView.Click += (_, _) => Report(VirtualDesktops.TaskView());
        SetLocalizedName(taskView, "DesktopTaskView");
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(_previous);
        row.Children.Add(taskView);
        row.Children.Add(_next);
        row.Children.Add(add);
        var card = new Border { Child = row, Padding = new Thickness(4, 0, 4, 0) };
        card.SetResourceReference(StyleProperty, "WidgetCardBorder");
        Content = card;
    }

    private Button IconButton(string icon, string textKey, Func<string?> action)
    {
        var glyph = new ContentControl { Content = FindResource(icon), Width = 14, Height = 14 };
        glyph.SetResourceReference(StyleProperty, "WidgetIcon");
        glyph.SetResourceReference(ForegroundProperty, "TextPrimary");
        var button = new Button { Content = glyph };
        button.SetResourceReference(StyleProperty, "WidgetIconButton");
        button.Click += (_, _) => Report(action());
        SetLocalizedName(button, textKey);
        return button;
    }

    private static void SetLocalizedName(Button button, string key)
    {
        void Apply()
        {
            button.ToolTip = UiText.Get(key);
            System.Windows.Automation.AutomationProperties.SetName(button, UiText.Get(key));
        }

        Apply();
        UiText.Current.PropertyChanged += (_, _) => Apply();
    }

    protected override void OnActivated()
    {
        _state = (0, 0);
        OnTick(DateTime.Now);
    }

    protected override void OnTick(DateTime now)
    {
        var state = VirtualDesktops.Read();
        if (state == _state) return;
        _state = state;
        _label.Text = state.Count > 1 ? $"{state.Current} / {state.Count}" : state.Current.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _previous.IsEnabled = state.Current > 1;
        _next.IsEnabled = state.Current < state.Count;
        System.Windows.Automation.AutomationProperties.SetName(this, UiText.Format("DesktopState", state.Current, state.Count));
    }
}
