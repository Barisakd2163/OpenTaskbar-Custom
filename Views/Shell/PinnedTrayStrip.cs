using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    private static readonly Func<IReadOnlyList<TrayApp>> SharedAppsProvider = static () => TrayIconSource.Shared.Snapshot;

    private readonly List<INotifyPropertyChanged> _subscribedIcons = [];
    private readonly Dictionary<Button, Action<MouseButton>> _clickActions = [];
    private TrayHost? _attachedHost;
    private bool _isStripEnabled = true;
    private bool _refreshQueued;
    private bool _snapshotSubscribed;
    private int _refreshRequested;
    private double _buttonSize = 24;
    private double _imageSize = 18;

    private static readonly Action<NotifyIcon, MouseButton> DefaultClickHandler = TrayHost.Click;
    private static readonly Action<TrayApp, MouseButton> DefaultOpenAppHandler = static (app, _) =>
    {
        var error = TrayIcons.Open(app);
        if (error is not null) AppLog.Warning($"Cannot open pinned tray app {app.Name}: {error}");
    };

    /// <summary>Supplies the persisted pinned tray icon keys in user order.</summary>
    public Func<IReadOnlyList<string>?>? PinnedKeysProvider { get; set; }

    /// <summary>Raised when the user unpins an icon from the fallback context menu.</summary>
    public event Action<string>? PinToggled;

    public Action<string>? CloseApplicationRequested { get; set; }
    public Action<string>? TerminateApplicationRequested { get; set; }

    /// <summary>Seam for tests: forwards context-menu clicks for live tray icons.</summary>
    internal Action<NotifyIcon, MouseButton> ClickHandler { get; set; } = DefaultClickHandler;

    /// <summary>Seam for tests: opens a background app by executable path (activate window or launch).</summary>
    internal Action<TrayApp, MouseButton> OpenAppHandler { get; set; } = DefaultOpenAppHandler;

    /// <summary>Seam for tests: supplies apps for path-based pins with no live icon. Defaults to the shared snapshot.</summary>
    internal Func<IReadOnlyList<TrayApp>> AppsProvider { get; set; } = SharedAppsProvider;

    /// <summary>Seam for tests: captures <c>PinClick(keyKind, strategy)</c> telemetry without parsing logs.</summary>
    internal Action<string, string>? PinClickTelemetry { get; set; }

    // Mirror of the single shared snapshot; never populated by an independent enumeration.
    private IReadOnlyList<TrayApp> _fallbackApps = [];
    private Task? _fallbackRefresh;
    private CancellationTokenSource? _fallbackCts;

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
        Loaded += (_, _) => SubscribeSharedSnapshot();
        Unloaded += (_, _) =>
        {
            TrayIconSource.Shared.SnapshotChanged -= OnSharedSnapshotChanged;
            _snapshotSubscribed = false;
            _fallbackCts?.Cancel();
            _fallbackCts?.Dispose();
            _fallbackCts = null;
        };
        SubscribeSharedSnapshot();
    }

    private void SubscribeSharedSnapshot()
    {
        if (_snapshotSubscribed) return;
        _snapshotSubscribed = true;
        TrayIconSource.Shared.SnapshotChanged += OnSharedSnapshotChanged;
    }

    /// <summary>Adopts the shared snapshot on the dispatcher; the strip never keeps a private enumeration.</summary>
    private void OnSharedSnapshotChanged()
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, OnSharedSnapshotChanged);
            }
            return;
        }
        if (!ReferenceEquals(AppsProvider, SharedAppsProvider)) return;
        _fallbackApps = TrayIconSource.Shared.Snapshot;
        if (_attachedHost is not null) Refresh();
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
            InvalidateFallbackApps();
            Refresh();
            return;
        }

        DetachHost();
        if (host is null)
        {
            UpdateItemsDeferred([]);
            return;
        }

        _attachedHost = host;
        _attachedHost.Icons.CollectionChanged += HostIcons_CollectionChanged;
        SyncIconPropertySubscriptions();
        InvalidateFallbackApps();
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
        // Bound any in-flight enumeration when the host goes away; a later attach starts a fresh one.
        _fallbackCts?.Cancel();
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
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, Refresh);
            return;
        }
        if (!_isStripEnabled || _attachedHost is not { IsRunning: true } host)
        {
            UpdateItemsDeferred([]);
            return;
        }

        UpdateFromIcons(host.AppIcons(), PinnedKeysProvider?.Invoke(), _fallbackApps);
    }

    /// <summary>
    /// Requests a bounded, coalesced refresh of the shared app snapshot. Multiple registry changes while a refresh
    /// is running collapse into one follow-up pass instead of stacking work.
    /// </summary>
    private void InvalidateFallbackApps()
    {
        Volatile.Write(ref _refreshRequested, 1);
        if (_fallbackRefresh is { IsCompleted: false }) return;
        _ = RefreshFallbackAppsAsync();
    }

    private Task RefreshFallbackAppsAsync(CancellationToken cancellationToken = default)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return Task.CompletedTask;
            return Dispatcher.InvokeAsync(() => { _ = RefreshFallbackAppsAsync(cancellationToken); }).Task;
        }
        if (_fallbackRefresh is { IsCompleted: false } && _fallbackCts is { IsCancellationRequested: false })
        {
            return _fallbackRefresh;
        }

        _fallbackCts?.Cancel();
        _fallbackCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _fallbackCts = cts;
        return _fallbackRefresh = RefreshFallbackCoreAsync(cts.Token);
    }

    private async Task RefreshFallbackCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            do
            {
                Volatile.Write(ref _refreshRequested, 0);
                IReadOnlyList<TrayApp> apps;
                if (ReferenceEquals(AppsProvider, SharedAppsProvider))
                {
                    await TrayIconSource.Shared.RefreshAsync(cancellationToken).ConfigureAwait(true);
                    apps = TrayIconSource.Shared.Snapshot;
                }
                else
                {
                    var provider = AppsProvider;
                    apps = await Task.Run(provider, cancellationToken).ConfigureAwait(true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                // Await above resumes on the captured dispatcher; Children is only touched here on that thread.
                _fallbackApps = apps;
                if (_attachedHost is not null) Refresh();
            }
            while (Volatile.Read(ref _refreshRequested) == 1 && !cancellationToken.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Host detached or strip unloaded; a later attach will request a fresh snapshot.
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException
            or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Warning($"Pinned tray app enumeration failed: {ex.Message}");
        }
    }

    private void UpdateItemsDeferred(IReadOnlyList<PinnedTrayItem> items)
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateItems(items);
            return;
        }
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        var copy = items.ToArray();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => UpdateItems(copy));
    }

    internal void UpdateFromIcons(
        IReadOnlyList<NotifyIcon> runningIcons,
        IReadOnlyList<string>? pinnedKeys,
        IReadOnlyList<TrayApp>? fallbackApps = null)
    {
        var fallbackTitle = UiText.Get("TrayApps");
        var candidates = new List<(int PinIndex, int SourceIndex, PinnedTrayItem Item)>();
        var livePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < runningIcons.Count; i++)
        {
            var icon = runningIcons[i];
            var pinIndex = TrayPinIdentity.FindPinIndex(pinnedKeys, icon.GUID, icon.Path, icon.UID);
            if (pinIndex < 0) continue;
            if (!string.IsNullOrWhiteSpace(icon.Path)) livePaths.Add(icon.Path.Trim());

            var captured = icon;
            // Use the STORED pin key (not the freshly computed canonical one) for both identity and
            // click strategy. A path-based pin matched to a live icon that happens to expose a GUID
            // must keep opening by path — otherwise we fall back to the synthetic tray click that
            // apps like Steam ignore.
            var key = TrayPinIdentity.FindMatchingKey(pinnedKeys, captured.GUID, captured.Path, captured.UID)
                ?? TrayPinIdentity.KeyFor(captured.GUID, captured.Path, captured.UID);
            var title = TrayPinIdentity.CleanTitle(captured.Title, captured.Path, fallbackTitle);
            var strategy = StrategyFor(key);
            candidates.Add((pinIndex, i, new PinnedTrayItem(key, title, captured.Icon, button =>
            {
                ReportPinClick(TrayPinIdentity.KeyKindOf(key).ToString(), strategy);
                // GUID pins are system/virtual icons whose own tray message handler is the only
                // reliable way to open them. Path pins belong to real executables: activating the
                // existing window (or launching the exe) is far more reliable than synthetic tray
                // clicks, which some apps (Steam, etc.) only answer on double-click.
                if (button == MouseButton.Right)
                {
                    if (ReferenceEquals(ClickHandler, DefaultClickHandler))
                    {
                        if (!TrayHost.TryClick(captured, MouseButton.Right))
                        {
                            var fallbackApp = CanOpenByPath(captured.Path)
                                ? new TrayApp(title, captured.Path.Trim(), captured.Icon as BitmapSource,
                                    captured.GUID == Guid.Empty ? (Guid?)null : captured.GUID, captured.UID, IconSource.Live)
                                : null;
                            ShowFallbackContextMenu(FindButtonByKey(key), key, fallbackApp, captured.Path);
                        }
                    }
                    else
                    {
                        ClickHandler(captured, MouseButton.Right);
                    }
                    return;
                }
                if (strategy != "open-path" || !CanOpenByPath(captured.Path))
                {
                    ClickHandler(captured, button);
                    return;
                }
                OpenAppHandler(new TrayApp(title, captured.Path.Trim(), captured.Icon as BitmapSource,
                    captured.GUID == Guid.Empty ? (Guid?)null : captured.GUID, captured.UID, IconSource.Live), button);
            })));
        }

        if (fallbackApps is not null && pinnedKeys is { Count: > 0 })
        {
            for (var i = 0; i < fallbackApps.Count; i++)
            {
                var app = fallbackApps[i];
                if (string.IsNullOrWhiteSpace(app.ExecutablePath)
                    || livePaths.Contains(app.ExecutablePath.Trim())) continue;
                var matchedKey = TrayPinIdentity.FindMatchingPathKey(pinnedKeys, app.ExecutablePath);
                if (matchedKey is null) continue;
                var pinIndex = -1;
                for (var p = 0; p < pinnedKeys.Count; p++)
                {
                    if (string.Equals(pinnedKeys[p], matchedKey, StringComparison.OrdinalIgnoreCase))
                    {
                        pinIndex = p;
                        break;
                    }
                }
                if (pinIndex < 0) continue;

                var captured = app;
                candidates.Add((pinIndex, runningIcons.Count + i, new PinnedTrayItem(
                    matchedKey, captured.Name, captured.Icon,
                    button =>
                    {
                        ReportPinClick(TrayPinIdentity.KeyKindOf(matchedKey).ToString(), "open-path");
                        if (button == MouseButton.Right && ReferenceEquals(OpenAppHandler, DefaultOpenAppHandler))
                        {
                            ShowFallbackContextMenu(FindButtonByKey(matchedKey), matchedKey, captured, captured.ExecutablePath);
                            return;
                        }
                        OpenAppHandler(captured, button);
                    })));
            }
        }

        if (candidates.Count == 0)
        {
            UpdateItemsDeferred([]);
            return;
        }

        candidates.Sort(static (a, b) =>
        {
            var byPin = a.PinIndex.CompareTo(b.PinIndex);
            return byPin != 0 ? byPin : a.SourceIndex.CompareTo(b.SourceIndex);
        });
        UpdateItemsDeferred(candidates.Select(static c => c.Item).ToArray());
    }

    /// <summary>
    /// Chooses the click strategy from the persisted key kind only. A path pin matched to a live icon that also
    /// exposes a GUID must still open by path; the live icon's GUID never changes the stored intent.
    /// </summary>
    private static string StrategyFor(string key) => TrayPinIdentity.KeyKindOf(key) switch
    {
        PinKeyKind.Path => "open-path",
        PinKeyKind.Structured when TrayPinIdentity.TryParseRef(key, out var reference)
            && !string.IsNullOrEmpty(reference.NormalizedPath) => "open-path",
        PinKeyKind.Guid => "tray-click",
        _ => "unknown"
    };

    private void ReportPinClick(string keyKind, string strategy)
    {
        PinClickTelemetry?.Invoke(keyKind, strategy);
        ShellTelemetry.Event("PinClick", ("keyKind", keyKind), ("strategy", strategy));
    }

    private static bool CanOpenByPath(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathRooted(path)
        && path!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

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
            button.PreviewMouseRightButtonUp += (_, e) =>
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

    private Button? FindButtonByKey(string key)
    {
        for (var i = 0; i < Children.Count; i++)
        {
            if (Children[i] is Button btn && string.Equals(btn.Tag as string, key, StringComparison.OrdinalIgnoreCase))
                return btn;
        }
        return null;
    }

    private void ShowFallbackContextMenu(Button? target, string pinKey, TrayApp? app, string? executablePath)
    {
        var menu = new ContextMenu();

        if (app is not null)
        {
            var openItem = new MenuItem { Header = UiText.Get("OpenApplication") };
            openItem.Click += (_, _) =>
            {
                var error = TrayIcons.Open(app);
                if (error is not null) AppLog.Warning($"Cannot open pinned tray app {app.Name}: {error}");
            };
            menu.Items.Add(openItem);
        }

        var unpinItem = new MenuItem
        {
            Header = UiText.Get("UnpinTrayIcon"),
            Icon = TryFindResource("IconPin")
        };
        unpinItem.Click += (_, _) => PinToggled?.Invoke(pinKey);
        menu.Items.Add(unpinItem);

        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            var path = executablePath.Trim();
            var closeItem = new MenuItem
            {
                Header = UiText.Get("CloseApplication"),
                Icon = TryFindResource("IconClose")
            };
            closeItem.Click += (_, _) => CloseApplicationRequested?.Invoke(path);

            var terminateItem = new MenuItem
            {
                Header = UiText.Get("Terminate"),
                Icon = TryFindResource("IconTerminate")
            };
            terminateItem.SetResourceReference(StyleProperty, "DestructiveMenuItem");
            terminateItem.Click += (_, _) => TerminateApplicationRequested?.Invoke(path);

            menu.Items.Add(new Separator());
            menu.Items.Add(closeItem);
            menu.Items.Add(terminateItem);
        }

        if (target is not null)
        {
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.MousePoint;
        }
        menu.IsOpen = true;
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
