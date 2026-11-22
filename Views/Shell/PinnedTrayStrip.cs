using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ManagedShell.WindowsTray;
using OpenTaskbar.Services;

namespace OpenTaskbar.Views.Shell;

/// <summary>
/// Compact live strip of pinned system tray icons displayed beside <see cref="TrayShellButton"/> on the dock.
/// Only icons that are currently running appear; when empty or when the tray button is hidden the strip collapses
/// to zero size. Left- and right-clicks forward directly to <see cref="TrayHost.Click"/> without letting the dock
/// context menu swallow right-clicks.
/// </summary>
public sealed class PinnedTrayStrip : StackPanel
{
    internal sealed record PinnedTrayItem(
        string Key,
        string Title,
        ImageSource? Icon,
        Action<MouseButton> OnClick);

    private readonly List<INotifyPropertyChanged> _subscribedIcons = [];
    private readonly Dictionary<Button, Action<MouseButton>> _clickActions = [];
    private TrayHost? _attachedHost;
    private bool _isStripEnabled = true;
    private bool _refreshQueued;
    private double _buttonSize = 24;
    private double _imageSize = 18;

    /// <summary>Supplies the persisted pinned tray icon keys in user order.</summary>
    public Func<IReadOnlyList<string>?>? PinnedKeysProvider { get; set; }

    /// <summary>Seam for tests: forwards left- and right-clicks for live tray icons.</summary>
    internal Action<NotifyIcon, MouseButton> ClickHandler { get; set; } = TrayHost.Click;

    /// <summary>Raised whenever the visible pinned count, visibility, or item sizing changes.</summary>
    public event Action? LayoutChanged;

    public PinnedTrayStrip()
    {
        Orientation = Orientation.Horizontal;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(0, 0, 2, 0);
        Visibility = Visibility.Collapsed;
        ContextMenuOpening += (_, e) => e.Handled = true;
    }

    /// <summary>Controls whether the strip may appear (mirrors <c>ShowTrayButton</c>).</summary>
    public bool IsStripEnabled
    {
        get => _isStripEnabled;
        set
        {
            if (_isStripEnabled == value) return;
            _isStripEnabled = value;
            Refresh();
        }
    }

    /// <summary>Number of currently displayed pinned tray icons on the dock.</summary>
    public int VisibleCount => Visibility == Visibility.Visible ? Children.Count : 0;

    internal double ButtonSize => _buttonSize;
    internal double ImageSize => _imageSize;

    /// <summary>Subscribes to the live <see cref="TrayHost"/> icon collection and refreshes the strip.</summary>
    internal void AttachHost(TrayHost? host)
    {
        if (ReferenceEquals(_attachedHost, host))
        {
            Refresh();
            return;
        }

        DetachHost();
        if (host is null)
        {
            UpdateItems([]);
            return;
        }

        _attachedHost = host;
        _attachedHost.Icons.CollectionChanged += HostIcons_CollectionChanged;
        SyncIconPropertySubscriptions();
        Refresh();
    }

    /// <summary>Unsubscribes all <see cref="TrayHost"/> and <see cref="NotifyIcon"/> event handlers.</summary>
    public void DetachHost()
    {
        if (_attachedHost is not null)
        {
            _attachedHost.Icons.CollectionChanged -= HostIcons_CollectionChanged;
            _attachedHost = null;
        }

        ClearIconPropertySubscriptions();
        _refreshQueued = false;
    }

    /// <summary>Switches layout axis and spacing between bottom (horizontal) and side (vertical) docks.</summary>
    public void SetOrientation(Orientation orientation)
    {
        if (Orientation == orientation) return;
        Orientation = orientation;
        var vertical = orientation == Orientation.Vertical;
        HorizontalAlignment = HorizontalAlignment.Center;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = vertical ? new Thickness(0, 0, 0, 2) : new Thickness(0, 0, 2, 0);

        var itemMargin = ItemMargin(vertical);
        foreach (var child in Children.OfType<Button>())
        {
            child.Margin = itemMargin;
        }
    }

    /// <summary>Scales compact tray buttons proportionally with the active dock icon size.</summary>
    public void SetIconScale(double dockIconSize)
    {
        if (!double.IsFinite(dockIconSize) || dockIconSize <= 0) return;
        var buttonSize = Math.Clamp(Math.Round(dockIconSize * 0.86), 24, 34);
        var imageSize = Math.Clamp(Math.Round(buttonSize * 0.75), 16, 22);
        if (Math.Abs(_buttonSize - buttonSize) < 0.01 && Math.Abs(_imageSize - imageSize) < 0.01)
            return;

        _buttonSize = buttonSize;
        _imageSize = imageSize;

        foreach (var button in Children.OfType<Button>())
        {
            button.Width = _buttonSize;
            button.Height = _buttonSize;
            if (button.Content is Image image)
            {
                image.Width = _imageSize;
                image.Height = _imageSize;
            }
        }

        if (VisibleCount > 0)
        {
            LayoutChanged?.Invoke();
        }
    }

    /// <summary>Re-synchronizes displayed pinned buttons against the current <see cref="TrayHost"/>.</summary>
    public void Refresh()
    {
        if (!_isStripEnabled || _attachedHost is not { IsRunning: true } host)
        {
            UpdateItems([]);
            return;
        }

        UpdateFromIcons(host.AppIcons(), PinnedKeysProvider?.Invoke());
    }

    internal void UpdateFromIcons(IReadOnlyList<NotifyIcon> runningIcons, IReadOnlyList<string>? pinnedKeys)
    {
        var selected = SelectPinnedItems(
            pinnedKeys,
            runningIcons,
            static icon => (icon.GUID, icon.Path, icon.UID));

        if (selected.Count == 0)
        {
            UpdateItems([]);
            return;
        }

        var fallbackTitle = UiText.Get("TrayApps");
        var items = new List<PinnedTrayItem>(selected.Count);
        foreach (var icon in selected)
        {
            var captured = icon;
            var key = TrayPinIdentity.KeyFor(captured.GUID, captured.Path, captured.UID);
            var title = TrayPinIdentity.CleanTitle(captured.Title, captured.Path, fallbackTitle);
            items.Add(new PinnedTrayItem(key, title, captured.Icon, button => ClickHandler(captured, button)));
        }

        UpdateItems(items);
    }

    internal void UpdateItems(IReadOnlyList<PinnedTrayItem> items)
    {
        var wasVisible = Visibility == Visibility.Visible;
        var previousCount = Children.Count;

        if (!_isStripEnabled || items.Count == 0)
        {
            Children.Clear();
            _clickActions.Clear();
            Visibility = Visibility.Collapsed;
            if (wasVisible || previousCount > 0)
            {
                LayoutChanged?.Invoke();
            }
            return;
        }

        var itemMargin = ItemMargin(Orientation == Orientation.Vertical);
        if (Children.Count == items.Count && AreSameKeys(items))
        {
            for (var i = 0; i < items.Count; i++)
            {
                var item = items[i];
                var button = (Button)Children[i];
                button.Width = _buttonSize;
                button.Height = _buttonSize;
                button.Margin = itemMargin;
                button.ToolTip = item.Title;
                button.Tag = item.Key;
                AutomationProperties.SetName(button, item.Title);
                if (button.Content is Image image)
                {
                    image.Source = item.Icon;
                    image.Width = _imageSize;
                    image.Height = _imageSize;
                }
                _clickActions[button] = item.OnClick;
            }

            Visibility = Visibility.Visible;
            if (!wasVisible)
            {
                LayoutChanged?.Invoke();
            }
            return;
        }

        Children.Clear();
        _clickActions.Clear();
        foreach (var item in items)
        {
            var captured = item;
            var image = new Image
            {
                Source = captured.Icon,
                Width = _imageSize,
                Height = _imageSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);

            var button = new Button
            {
                Content = image,
                Width = _buttonSize,
                Height = _buttonSize,
                Margin = itemMargin,
                Padding = new Thickness(0),
                ToolTip = captured.Title,
                Tag = captured.Key,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            button.SetResourceReference(StyleProperty, "ShellDockButton");
            ToolTipService.SetInitialShowDelay(button, 650);
            AutomationProperties.SetName(button, captured.Title);
            _clickActions[button] = captured.OnClick;
            button.Click += (_, _) =>
            {
                if (_clickActions.TryGetValue(button, out var action))
                    action(MouseButton.Left);
            };
            button.MouseRightButtonUp += (_, e) =>
            {
                e.Handled = true;
                if (_clickActions.TryGetValue(button, out var action))
                    action(MouseButton.Right);
            };
            button.ContextMenuOpening += (_, e) => e.Handled = true;
            Children.Add(button);
        }

        Visibility = Visibility.Visible;
        if (!wasVisible || previousCount != Children.Count)
        {
            LayoutChanged?.Invoke();
        }
    }

    private bool AreSameKeys(IReadOnlyList<PinnedTrayItem> items)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (Children[i] is not Button btn
                || !string.Equals(btn.Tag as string, items[i].Key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Filters <paramref name="runningItems"/> to entries matching <paramref name="pinnedKeys"/> and orders them
    /// according to their earliest matching position in <paramref name="pinnedKeys"/> (preserving source order on ties).
    /// </summary>
    internal static IReadOnlyList<T> SelectPinnedItems<T>(
        IReadOnlyList<string>? pinnedKeys,
        IReadOnlyList<T> runningItems,
        Func<T, (Guid Guid, string? Path, uint Uid)> identitySelector)
    {
        if (pinnedKeys is not { Count: > 0 } || runningItems.Count == 0)
            return [];

        var matched = new List<(T Item, int PinIndex, int SourceIndex)>();
        for (var i = 0; i < runningItems.Count; i++)
        {
            var item = runningItems[i];
            var (guid, path, uid) = identitySelector(item);
            var pinIndex = TrayPinIdentity.FindPinIndex(pinnedKeys, guid, path, uid);
            if (pinIndex >= 0)
            {
                matched.Add((item, pinIndex, i));
            }
        }

        if (matched.Count == 0) return [];
        matched.Sort(static (a, b) =>
        {
            var byPin = a.PinIndex.CompareTo(b.PinIndex);
            return byPin != 0 ? byPin : a.SourceIndex.CompareTo(b.SourceIndex);
        });

        var ordered = new List<T>(matched.Count);
        for (var i = 0; i < matched.Count; i++) ordered.Add(matched[i].Item);
        return ordered;
    }

    private static Thickness ItemMargin(bool vertical) =>
        vertical ? new Thickness(0, 1, 0, 1) : new Thickness(1, 0, 1, 0);

    private void HostIcons_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        QueueHostRefresh();

    private void NotifyIcon_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NotifyIcon.Title) or nameof(NotifyIcon.Icon)
            && sender is NotifyIcon icon
            && !TrayPinIdentity.IsPinned(PinnedKeysProvider?.Invoke(), icon.GUID, icon.Path, icon.UID))
        {
            return;
        }

        if (e.PropertyName is null or ""
            or nameof(NotifyIcon.Title)
            or nameof(NotifyIcon.Icon)
            or nameof(NotifyIcon.IsHidden)
            or nameof(NotifyIcon.GUID)
            or nameof(NotifyIcon.Path)
            or nameof(NotifyIcon.UID))
        {
            QueueHostRefresh();
        }
    }

    private void QueueHostRefresh()
    {
        if (_attachedHost is null) return;
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, QueueHostRefresh);
            }
            return;
        }

        if (_refreshQueued) return;
        _refreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _refreshQueued = false;
            if (_attachedHost is null) return;
            SyncIconPropertySubscriptions();
            Refresh();
        });
    }

    private void SyncIconPropertySubscriptions()
    {
        ClearIconPropertySubscriptions();
        if (_attachedHost is null) return;

        foreach (var icon in _attachedHost.Icons)
        {
            if (icon is INotifyPropertyChanged notify)
            {
                notify.PropertyChanged += NotifyIcon_PropertyChanged;
                _subscribedIcons.Add(notify);
            }
        }
    }

    private void ClearIconPropertySubscriptions()
    {
        for (var i = 0; i < _subscribedIcons.Count; i++)
        {
            _subscribedIcons[i].PropertyChanged -= NotifyIcon_PropertyChanged;
        }
        _subscribedIcons.Clear();
    }
}
