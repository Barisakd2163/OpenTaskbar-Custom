using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Views.Widgets;
using OpenTaskbar.Widgets;
using LiveDockWidget = OpenTaskbar.Widgets.DockWidget;

namespace OpenTaskbar.Views.Windows;

public partial class DynamicIslandWindow : Window, IDisposable
{
    private const double CompactHeight = 50;
    private const double ExpandedHeight = 246;
    private const double CompactMinimumWidth = 360;
    private const double ExpandedMinimumWidth = 430;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExAppWindow = 0x00040000;
    private const int WmMouseActivate = 0x0021;
    private const int MaNoActivate = 3;
    private static readonly TimeSpan SlideDuration = TimeSpan.FromMilliseconds(190);
    private static readonly TimeSpan ResizeDuration = TimeSpan.FromMilliseconds(180);

    private readonly WidgetCoordinator _widgetCoordinator;
    private readonly Action<DockSettings> _saveSettings;
    private readonly Action<string> _removeWidget;
    private readonly Func<string, IEnumerable<Control>> _createPlacementMenuItems;
    private readonly HashSet<string> _placementIds = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, DockWidgetItem> _timedWidgets = new Dictionary<string, DockWidgetItem>();
    private IReadOnlyList<string> _pageIds = [];
    private DisplayInfo _display;
    private DockSettings _settings = new();
    private UIElement? _details;
    private Point _pointerOrigin;
    private double _dragOffset;
    private int _currentPageIndex;
    private int _dragStartIndex;
    private bool _isDragging;
    private bool _isExpanded;
    private bool _syncingPages;
    private bool _resourcesDisposed;

    public DynamicIslandWindow(
        DisplayInfo display,
        Action<string> reportError,
        Action<DockSettings> saveSettings,
        Action<string> removeWidget,
        Func<string, IEnumerable<Control>> createPlacementMenuItems)
    {
        InitializeComponent();
        PageViewport.SizeChanged += PageViewport_SizeChanged;
        _display = display;
        _saveSettings = saveSettings;
        _removeWidget = removeWidget;
        _createPlacementMenuItems = createPlacementMenuItems;

        _widgetCoordinator = new WidgetCoordinator(Pages, Dispatcher, reportError, lazy: true)
        {
            ExternalLayout = true,
            AllowWidgetReorder = false,
            EnableWidgetHover = false,
            IsWidgetEnabled = IsAssignedHere,
            CreateWidgetAdditionalMenuItems = _createPlacementMenuItems,
            WidgetSettingsUpdated = _saveSettings
        };
        _widgetCoordinator.WidgetRemoveRequested += _removeWidget;
        _widgetCoordinator.AvailabilityChanged += SyncPages;
        _widgetCoordinator.WidgetsPlaced += SyncPages;
        InputManager.Current.PreProcessInput += InputManager_PreProcessInput;
        UiText.Current.PropertyChanged += UiText_PropertyChanged;
        UpdateControlLabels();
        PositionWindow(CompactMinimumWidth, CompactHeight, animate: false);
    }

    public string DeviceName => _display.DeviceName;

    public void Apply(
        DisplayInfo display,
        IReadOnlyList<string> widgetIds,
        IReadOnlyDictionary<string, DockWidgetItem> timedWidgets,
        DockSettings settings)
    {
        if (_resourcesDisposed) return;
        _display = display;
        _settings = settings;
        _placementIds.Clear();
        foreach (var id in widgetIds) _placementIds.Add(id);
        _timedWidgets = timedWidgets;
        _widgetCoordinator.Apply(settings);
        SyncPages();
        if (_pageIds.Count > 0)
        {
            PositionWindow(WidthForCurrentPage(), _isExpanded ? ExpandedHeight : CompactHeight, animate: false);
            if (!IsVisible) Show();
        }
    }

    public void Dispose()
    {
        if (_resourcesDisposed) return;
        ReleaseResources();
        if (IsVisible) Close();
    }

    private bool IsAssignedHere(DockSettings settings, string id) =>
        _placementIds.Contains(id)
        && settings.DynamicIslandPlacements?.TryGetValue(id, out var deviceName) == true
        && string.Equals(deviceName, _display.DeviceName, StringComparison.OrdinalIgnoreCase);

    private void SyncPages()
    {
        if (_resourcesDisposed || _syncingPages) return;
        _syncingPages = true;
        try
        {
            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _widgetCoordinator.ActiveWidgets)
            {
                if (pair.Value.Visibility == Visibility.Visible) available.Add(pair.Key);
            }

            if (!_settings.FocusModeEnabled)
            {
                foreach (var pair in _timedWidgets)
                {
                    if (_placementIds.Contains(pair.Key)
                        && _settings.Widgets?.Contains(pair.Key, StringComparer.OrdinalIgnoreCase) == true)
                        available.Add(pair.Key);
                }
            }

            var order = (_settings.WidgetOrder ?? [])
                .Concat(WidgetCoordinator.DefaultWidgetOrder)
                .Concat(_timedWidgets.Keys)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var nextPageIds = order.Where(available.Contains).ToArray();
            if (_pageIds.SequenceEqual(nextPageIds, StringComparer.OrdinalIgnoreCase))
            {
                UpdatePageControls();
                return;
            }

            var selectedId = _pageIds.Count > 0 && _pageIds[_currentPageIndex] is { } selected
                ? selected
                : nextPageIds.FirstOrDefault();
            DetailsContent.Content = null;
            _details = null;
            DetailsPanel.Visibility = Visibility.Collapsed;
            _isExpanded = false;
            Pages.Children.Clear();
            foreach (var id in nextPageIds)
            {
                if (_widgetCoordinator.ActiveWidgets.TryGetValue(id, out var widget))
                {
                    widget.SetCompactState(false);
                    widget.SetHoverExpanded(false);
                    Pages.Children.Add(widget);
                }
                else if (_timedWidgets.TryGetValue(id, out var timedWidget))
                {
                    var tile = new TimedWidgetTile
                    {
                        DataContext = timedWidget,
                        Title = timedWidget.Label,
                        ContextMenu = CreateTimedContextMenu(id)
                    };
                    tile.AddHandler(TimedWidgetTile.DetailsRequestedEvent,
                        new RoutedEventHandler(TimedWidget_DetailsRequested));
                    Pages.Children.Add(tile);
                }
            }

            _pageIds = nextPageIds;
            var newIndex = selectedId is null ? 0 : Array.FindIndex(nextPageIds,
                id => string.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase));
            SetCurrentPageIndex(newIndex < 0 ? 0 : newIndex, animate: false);
            if (_isExpanded && !HasExpandableContent(CurrentPageId())) SetExpanded(false, animate: false);
            if (_pageIds.Count == 0)
            {
                _details = null;
                DetailsContent.Content = null;
                DetailsPanel.Visibility = Visibility.Collapsed;
                Hide();
                return;
            }

            if (!IsVisible) Show();
            UpdatePageControls();
            PositionWindow(WidthForCurrentPage(), _isExpanded ? ExpandedHeight : CompactHeight, animate: false);
        }
        finally
        {
            _syncingPages = false;
        }
    }

    private ContextMenu CreateTimedContextMenu(string id)
    {
        var menu = new ContextMenu();
        foreach (var item in _createPlacementMenuItems(id)) menu.Items.Add(item);
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = UiText.Get("RemoveWidget") };
        if (Application.Current?.TryFindResource("IconTrash") is Geometry icon) remove.Icon = icon;
        remove.Click += (_, _) => _removeWidget(id);
        menu.Items.Add(remove);
        return menu;
    }

    private void TimedWidget_DetailsRequested(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not TimedWidgetTile { DataContext: DockWidgetItem widget }) return;
        e.Handled = true;
        ShowDetails(new TimedWidgetPanel { DataContext = widget }, widget.Id);
    }

    private void UpdatePageControls()
    {
        if (_pageIds.Count == 0)
        {
            PreviousButton.IsEnabled = false;
            NextButton.IsEnabled = false;
            PageCount.Text = string.Empty;
            ExpandButton.Visibility = Visibility.Collapsed;
            return;
        }

        PreviousButton.IsEnabled = _pageIds.Count > 1;
        NextButton.IsEnabled = _pageIds.Count > 1;
        PageCount.Text = _pageIds.Count > 1
            ? string.Create(UiText.Current.Language.Culture, $"{_currentPageIndex + 1:00} / {_pageIds.Count:00}")
            : string.Empty;
        var expandable = HasExpandableContent(CurrentPageId());
        ExpandButton.Visibility = expandable ? Visibility.Visible : Visibility.Collapsed;
        ExpandGlyph.Content = Application.Current?.TryFindResource(
            _isExpanded ? "IconChevronUp" : "IconChevronDown");
        AutomationProperties.SetName(ExpandButton,
            UiText.Get(_isExpanded ? "DynamicIslandCollapse" : "DynamicIslandExpand"));
    }

    private bool HasExpandableContent(string? id) => id is not null
        && (_timedWidgets.ContainsKey(id)
            || id is DockWidgetService.ClockId or DockWidgetService.FocusId or DockWidgetService.SystemStatsId
                or DockWidgetService.WeatherId or DockWidgetService.AudioId or DockWidgetService.NetSpeedId
                or DockWidgetService.BluetoothBatteryId
            || id.StartsWith("AiUsage", StringComparison.OrdinalIgnoreCase));

    private UIElement? GetDetails(LiveDockWidget widget, string id) => id switch
    {
        DockWidgetService.ClockId or DockWidgetService.FocusId or DockWidgetService.SystemStatsId =>
            widget.BuildPreviewFlyoutContent(),
        DockWidgetService.WeatherId when widget is WeatherWidget weather => weather.BuildFlyoutContent(),
        DockWidgetService.MediaId => null,
        _ => widget.BuildHoverFlyoutContent()
    };

    private void SetExpanded(bool expanded, bool animate = true)
    {
        if (expanded && !HasExpandableContent(CurrentPageId())) return;
        _isExpanded = expanded;
        DetailsPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        if (expanded)
        {
            _details = GetCurrentDetails();
            DetailsContent.Content = _details;
        }
        else
        {
            DetailsContent.Content = null;
            _details = null;
        }

        UpdatePageControls();
        PositionWindow(WidthForCurrentPage(), expanded ? ExpandedHeight : CompactHeight,
            animate && _settings.EffectiveAnimationsEnabled);
    }

    private void ShowDetails(UIElement details, string id)
    {
        if (!string.Equals(id, CurrentPageId(), StringComparison.OrdinalIgnoreCase)) return;
        _details = details;
        DetailsContent.Content = details;
        DetailsPanel.Visibility = Visibility.Visible;
        _isExpanded = true;
        UpdatePageControls();
        PositionWindow(WidthForCurrentPage(), ExpandedHeight, animate: _settings.EffectiveAnimationsEnabled);
    }

    private UIElement? GetCurrentDetails()
    {
        var id = CurrentPageId();
        if (id is null) return null;
        if (_timedWidgets.TryGetValue(id, out var timedWidget))
            return new TimedWidgetPanel { DataContext = timedWidget };
        return _widgetCoordinator.ActiveWidgets.TryGetValue(id, out var widget) ? GetDetails(widget, id) : null;
    }

    private double WidthForCurrentPage()
    {
        var id = CurrentPageId();
        var widgetWidth = id is not null && _widgetCoordinator.ActiveWidgets.TryGetValue(id, out var widget)
            ? widget.FullWidthEstimate
            : 280;
        var detailWidth = 0d;
        if (_details is not null)
        {
            _details.Measure(new Size(640, ExpandedHeight - CompactHeight - 20));
            detailWidth = _details.DesiredSize.Width;
        }
        var minimum = _isExpanded ? ExpandedMinimumWidth : CompactMinimumWidth;
        var preferred = _isExpanded
            ? Math.Max(Math.Max(540, widgetWidth + 176), detailWidth + 32)
            : widgetWidth + 176;
        return Math.Clamp(preferred, minimum, 680);
    }

    private void PositionWindow(double width, double height, bool animate)
    {
        if (!IsInitialized) return;
        var left = _display.Bounds.Left + (_display.Bounds.Width - width) / 2;
        Top = _display.Bounds.Top;
        if (animate && IsVisible)
        {
            AnimateWindowValue(WidthProperty, ActualWidth, width);
            AnimateWindowValue(LeftProperty, Left, left);
            AnimateWindowValue(HeightProperty, ActualHeight, height);
        }
        else
        {
            BeginAnimation(WidthProperty, null);
            BeginAnimation(LeftProperty, null);
            BeginAnimation(HeightProperty, null);
            Width = width;
            Height = height;
            Left = left;
        }
    }

    private void AnimateWindowValue(DependencyProperty property, double current, double target)
    {
        var animation = new DoubleAnimation(current, target, ResizeDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        animation.Completed += (_, _) =>
        {
            SetValue(property, target);
            BeginAnimation(property, null);
        };
        BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private string? CurrentPageId() => _pageIds.Count == 0 ? null : _pageIds[CurrentPageIndex()];

    private int CurrentPageIndex() => _pageIds.Count == 0 ? 0 : _currentPageIndex;

    private void PageViewport_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        Pages.BeginAnimation(DynamicIslandPagePanel.HorizontalOffsetProperty, null);
        Pages.HorizontalOffset = -_currentPageIndex * Math.Max(1, e.NewSize.Width);
    }

    private void SetCurrentPageIndex(int index, bool animate)
    {
        if (_pageIds.Count == 0) return;
        var targetIndex = Math.Clamp(index, 0, _pageIds.Count - 1);
        _currentPageIndex = targetIndex;
        var targetOffset = -targetIndex * Math.Max(1, PageViewport.ActualWidth);
        if (animate && _settings.EffectiveAnimationsEnabled)
        {
            var animation = new DoubleAnimation(Pages.HorizontalOffset, targetOffset, SlideDuration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            };
            animation.Completed += (_, _) =>
            {
                Pages.HorizontalOffset = targetOffset;
                Pages.BeginAnimation(DynamicIslandPagePanel.HorizontalOffsetProperty, null);
                if (!_resourcesDisposed)
                    PositionWindow(WidthForCurrentPage(), CompactHeight, animate: true);
            };
            Pages.BeginAnimation(DynamicIslandPagePanel.HorizontalOffsetProperty, animation);
        }
        else
        {
            Pages.BeginAnimation(DynamicIslandPagePanel.HorizontalOffsetProperty, null);
            Pages.HorizontalOffset = targetOffset;
        }

        _details = null;
        DetailsContent.Content = null;
        DetailsPanel.Visibility = Visibility.Collapsed;
        _isExpanded = false;
        UpdatePageControls();
        if (!animate || !_settings.EffectiveAnimationsEnabled)
            PositionWindow(WidthForCurrentPage(), CompactHeight, animate: false);
    }

    private void MovePage(int delta)
    {
        if (_pageIds.Count <= 1) return;
        var targetIndex = Math.Clamp(CurrentPageIndex() + delta, 0, _pageIds.Count - 1);
        if (targetIndex == CurrentPageIndex()) return;
        SetCurrentPageIndex(targetIndex, animate: true);
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => MovePage(-1);
    private void Next_Click(object sender, RoutedEventArgs e) => MovePage(1);
    private void Expand_Click(object sender, RoutedEventArgs e) => SetExpanded(!_isExpanded);

    private void Header_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginPointerGesture(e);
    private void Details_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => BeginPointerGesture(e);

    private void BeginPointerGesture(MouseButtonEventArgs e)
    {
        _isDragging = false;
        if (e.ChangedButton != MouseButton.Left || _pageIds.Count <= 1
            || IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        _pointerOrigin = e.GetPosition(PageViewport);
        _dragStartIndex = _currentPageIndex;
        _dragOffset = 0;
    }

    private void Header_PreviewMouseMove(object sender, MouseEventArgs e) => UpdatePointerGesture(e);
    private void Details_PreviewMouseMove(object sender, MouseEventArgs e) => UpdatePointerGesture(e);

    private void UpdatePointerGesture(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pageIds.Count <= 1) return;
        if (!_isDragging && IsInteractiveSource(e.OriginalSource as DependencyObject)) return;
        var position = e.GetPosition(PageViewport);
        var dx = position.X - _pointerOrigin.X;
        var dy = position.Y - _pointerOrigin.Y;
        if (!_isDragging)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance
                || Math.Abs(dx) < Math.Abs(dy)) return;
            _isDragging = true;
            Pages.BeginAnimation(DynamicIslandPagePanel.HorizontalOffsetProperty, null);
            Pages.HorizontalOffset = -_dragStartIndex * Math.Max(1, PageViewport.ActualWidth);
            _details = null;
            DetailsContent.Content = null;
            DetailsPanel.Visibility = Visibility.Collapsed;
            _isExpanded = false;
            Header.CaptureMouse();
        }

        var pageWidth = Math.Max(1, PageViewport.ActualWidth);
        _dragOffset = dx;
        var baseOffset = -_dragStartIndex * pageWidth;
        var targetIndex = _dragStartIndex + (dx < 0 ? 1 : -1);
        var resisted = targetIndex < 0 || targetIndex >= _pageIds.Count ? dx * 0.22 : dx;
        Pages.HorizontalOffset = baseOffset + Math.Clamp(resisted, -pageWidth, pageWidth);
        e.Handled = true;
    }

    private void Header_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => FinishPointerGesture(e);
    private void Details_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => FinishPointerGesture(e);

    private void FinishPointerGesture(MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (Header.IsMouseCaptured) Header.ReleaseMouseCapture();
        var threshold = Math.Max(36, PageViewport.ActualWidth * 0.18);
        _dragOffset = e.GetPosition(PageViewport).X - _pointerOrigin.X;
        var delta = Math.Abs(_dragOffset) >= threshold ? Math.Sign(_dragOffset) : 0;
        if (delta != 0)
            SetCurrentPageIndex(_dragStartIndex + (delta < 0 ? 1 : -1), animate: true);
        else
            SetCurrentPageIndex(_dragStartIndex, animate: true);
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _isExpanded)
        {
            SetExpanded(false);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right && !IsInteractiveSource(Keyboard.FocusedElement as DependencyObject))
        {
            MovePage(e.Key == Key.Right ? 1 : -1);
            e.Handled = true;
        }
    }

    private void InputManager_PreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not KeyEventArgs { RoutedEvent: var routed, Key: var key } input
            || routed != Keyboard.KeyDownEvent || !PointerIsOverIsland()
            || IsInteractiveSource(Keyboard.FocusedElement as DependencyObject)) return;
        if (key == Key.Escape && _isExpanded)
        {
            SetExpanded(false);
            input.Handled = true;
        }
        else if (key is Key.Left or Key.Right)
        {
            MovePage(key == Key.Right ? 1 : -1);
            input.Handled = true;
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!IsWithin(e.OriginalSource as DependencyObject, DetailsScrollViewer)) e.Handled = true;
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        var current = source;
        while (current is not null)
        {
            if (current is ButtonBase or Slider or TextBoxBase or Selector or ScrollBar or Thumb) return true;
            current = GetParent(current);
        }
        return false;
    }

    private bool PointerIsOverIsland()
    {
        var current = Mouse.DirectlyOver as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, this)) return true;
            current = GetParent(current);
        }
        return false;
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject target)
    {
        var current = source;
        while (current is not null)
        {
            if (ReferenceEquals(current, target)) return true;
            current = GetParent(current);
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject current) => current is Visual or System.Windows.Media.Media3D.Visual3D
        ? VisualTreeHelper.GetParent(current)
        : current is FrameworkContentElement content ? content.Parent : null;

    private void UpdateControlLabels()
    {
        Title = UiText.Get("DynamicIslandTitle");
        AutomationProperties.SetName(PreviousButton, UiText.Get("DynamicIslandPrevious"));
        AutomationProperties.SetName(NextButton, UiText.Get("DynamicIslandNext"));
        UpdatePageControls();
    }

    private void UiText_PropertyChanged(object? sender, PropertyChangedEventArgs e) => UpdateControlLabels();

    private void Window_Closed(object? sender, EventArgs e) => ReleaseResources();

    private void ReleaseResources()
    {
        if (_resourcesDisposed) return;
        _resourcesDisposed = true;
        InputManager.Current.PreProcessInput -= InputManager_PreProcessInput;
        UiText.Current.PropertyChanged -= UiText_PropertyChanged;
        _widgetCoordinator.AvailabilityChanged -= SyncPages;
        _widgetCoordinator.WidgetsPlaced -= SyncPages;
        _widgetCoordinator.WidgetRemoveRequested -= _removeWidget;
        _widgetCoordinator.Dispose();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new nint((style | WsExToolWindow | WsExNoActivate) & ~WsExAppWindow));
        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private static nint WindowProc(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message != WmMouseActivate) return 0;
        handled = true;
        return new nint(MaNoActivate);
    }

    private static nint GetWindowLongPtr(nint window, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(window, index)
        : new nint(GetWindowLong32(window, index));

    private static void SetWindowLongPtr(nint window, int index, nint value)
    {
        if (IntPtr.Size == 8) _ = SetWindowLongPtr64(window, index, value);
        else _ = SetWindowLong32(window, index, value.ToInt32());
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr64(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr64(nint window, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(nint window, int index, int value);
}
