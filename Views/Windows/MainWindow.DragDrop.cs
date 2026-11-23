using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OpenTaskbar.Models;
using OpenTaskbar.Services;
using OpenTaskbar.Widgets;

namespace OpenTaskbar;

public partial class MainWindow
{
    private const int DragHoverDwellMs = 450;
    private const int ShowDesktopFeedbackMs = 900;
    private const int ErrorCancelledByUser = 1223;

    private DispatcherTimer? _dragHoverAppTimer;
    private DockApp? _dragHoverApp;
    private Button? _dragHoverAppButton;
    private bool _dragHoverAppActivated;
    private bool _dragHoverAppLeavePending;

    private DispatcherTimer? _showDesktopDragHoverTimer;
    private bool _showDesktopDragHoverTriggered;
    private bool _showDesktopDragLeavePending;
    private DispatcherTimer? _showDesktopFeedbackTimer;
    private bool _dragDropDisposed;
    private void DockApp_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Snapshot before Button focus handling and preview dismissal can change foreground activation.
        var foreground = OpenTaskbar.Services.NativeWindowTracker.GetForegroundApplicationWindow();
        _foregroundAtAppPress = foreground != 0 ? foreground : _foregroundWindow;

        if (sender is Button { DataContext: DockApp app } && (_pinnedApps.Contains(app) || _runningApps.Contains(app)))
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedApp = app;
        }
        else
        {
            _draggedApp = null;
        }
    }

    private void DockApp_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggedApp = null;
    }

    private void DockApp_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _draggedApp is null || sender is not Button button)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var app = _draggedApp;
            _draggedApp = null;
            _foregroundAtAppPress = null;
            _previewCloseTimer.Stop();
            _thumbnailPreview.Hide();
            ResetMagnification(immediate: true);
            AnimateAppDrag(button, app);
        }
    }

    private void AnimateAppDrag(Button button, DockApp app)
    {
        var icon = GetButtonIcon(button);
        var (scale, translate) = icon != null ? GetIconTransforms(icon) : (null, null);
        var animDuration = TimeSpan.FromMilliseconds(130);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        button.Opacity = 0.55;
        if (scale is not null && translate is not null && _preferences.Current.EffectiveAnimationsEnabled && SystemParameters.ClientAreaAnimation)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.10, animDuration) { EasingFunction = easeOut });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.10, animDuration) { EasingFunction = easeOut });
            translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                new DoubleAnimation(-4 * BounceDirection, animDuration) { EasingFunction = easeOut });
        }

        try
        {
            DragDrop.DoDragDrop(button, app, DragDropEffects.Move);
        }
        finally
        {
            CancelLiveDrag();
            button.Opacity = 1.0;
            ResetDisplacements();
            if (scale is not null && translate is not null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
                translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                    new DoubleAnimation(0, animDuration) { EasingFunction = easeOut });
            }
        }
    }

    private void UpdateDragDisplacements(Button targetButton, DragEventArgs e)
    {
        var icon = GetButtonIcon(targetButton);
        if (icon is null) return;

        var pos = e.GetPosition(targetButton);
        var isPastHalf = IsVertical ? pos.Y > targetButton.ActualHeight / 2 : pos.X > targetButton.ActualWidth / 2;
        var shift = isPastHalf ? -12.0 : 12.0;

        var (_, translate) = GetIconTransforms(icon);
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        var anim = new DoubleAnimation(shift, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(prop, anim);
        _displacedIcons.Add(icon);
    }

    private void ResetIconDisplacement(FrameworkElement icon)
    {
        if (!_displacedIcons.Remove(icon)) return;
        var (_, translate) = GetIconTransforms(icon);
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(prop, anim);
    }

    private void ResetDisplacements()
    {
        foreach (var icon in _displacedIcons)
        {
            var (_, translate) = GetIconTransforms(icon);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.X = 0;
            translate.Y = 0;
        }
        _displacedIcons.Clear();
    }

    private void AnimateDropSettle(Button button)
    {
        if (!_preferences.Current.EffectiveAnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var icon = GetButtonIcon(button);
            if (icon is null) return;

            var (scale, _) = GetIconTransforms(icon);
            var settleScale = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(200),
                KeyFrames =
                {
                    new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new CubicEase { EasingMode = EasingMode.EaseOut }),
                    new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new CubicEase { EasingMode = EasingMode.EaseOut })
                }
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settleScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleScale);
        });
    }

    private void DockApp_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button)
        {
            if (GetButtonIcon(button) is { } icon)
            {
                ResetIconDisplacement(icon);
            }

            if (ReferenceEquals(button, _dragHoverAppButton))
            {
                _dragHoverAppLeavePending = true;
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
                {
                    if (!_dragDropDisposed && _dragHoverAppLeavePending && ReferenceEquals(button, _dragHoverAppButton))
                    {
                        CancelDragHoverApp();
                    }
                });
            }
        }
    }

    private void DockApp_DragOver(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out _))
        {
            CancelDragHoverApp();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(typeof(DockApp))
            && sender is Button targetButton
            && targetButton.DataContext is DockApp)
        {
            CancelDragHoverApp();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            if (_preferences.Current.EffectiveAnimationsEnabled && SystemParameters.ClientAreaAnimation)
            {
                UpdateDragDisplacements(targetButton, e);
            }
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && sender is Button fileTargetButton
            && fileTargetButton.DataContext is DockApp fileTargetApp)
        {
            var effect = ResolveAppFileDropEffect(e, fileTargetApp);
            e.Effects = effect;
            e.Handled = true;

            if (effect != DragDropEffects.None)
            {
                BeginOrContinueDragHoverApp(fileTargetButton, fileTargetApp);
            }
            else
            {
                CancelDragHoverApp();
            }
            return;
        }

        CancelDragHoverApp();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void DockApp_Drop(object sender, DragEventArgs e)
    {
        CancelDragHoverApp();

        // In the unified model every drop is an insert into the single item order.
        if (TryGetDockItemToken(e, out var droppedToken))
        {
            ResetDisplacements();
            DropDockItem(droppedToken, e);
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop)
            && sender is Button fileDropButton
            && fileDropButton.DataContext is DockApp fileDropApp)
        {
            ResetDisplacements();
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } droppedPaths)
            {
                AnimateDropSettle(fileDropButton);
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => OpenFilesWithApp(fileDropApp, droppedPaths));
            }
            return;
        }

        if (e.Data.GetData(typeof(DockApp)) is DockApp sourceApp
            && sender is Button targetButton
            && targetButton.DataContext is DockApp targetApp
            && !ReferenceEquals(sourceApp, targetApp))
        {
            var isSourcePinned = _pinnedApps.Contains(sourceApp);
            var isTargetPinned = _pinnedApps.Contains(targetApp);

            if (isSourcePinned && isTargetPinned)
            {
                var oldIndex = _pinnedApps.IndexOf(sourceApp);
                var newIndex = _pinnedApps.IndexOf(targetApp);
                if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                {
                    ResetDisplacements();
                    _pinnedApps.Move(oldIndex, newIndex);
                    SavePinOrder();
                    _replacementSession?.RefreshPosition();
                    AnimateDropSettle(targetButton);
                }
            }
            else if (!isSourcePinned && isTargetPinned)
            {
                var targetIndex = _pinnedApps.IndexOf(targetApp);
                ResetDisplacements();
                PinRunningApp(sourceApp, targetIndex >= 0 ? targetIndex : -1);
                AnimateDropSettle(targetButton);
            }
            else if (!isSourcePinned && !isTargetPinned)
            {
                var oldIndex = _runningApps.IndexOf(sourceApp);
                var newIndex = _runningApps.IndexOf(targetApp);
                if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
                {
                    ResetDisplacements();
                    _runningApps.Move(oldIndex, newIndex);
                    AnimateDropSettle(targetButton);
                }
            }
            else if (isSourcePinned && !isTargetPinned)
            {
                var oldIndex = _pinnedApps.IndexOf(sourceApp);
                if (oldIndex >= 0 && oldIndex != _pinnedApps.Count - 1)
                {
                    ResetDisplacements();
                    _pinnedApps.Move(oldIndex, _pinnedApps.Count - 1);
                    SavePinOrder();
                    _replacementSession?.RefreshPosition();
                    AnimateDropSettle(targetButton);
                }
            }
        }

        ResetDisplacements();
        e.Handled = true;
    }

    // ---- Permanent shell button drag & drop ----

    private Point _shellDragStartPoint;
    private Button? _shellDragButton;
    private string? _shellDragToken;

    private void ShellButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Button button) return;
        _shellDragButton = button;
        // Start, Search, Task View and Background apps are pinned groups; only Show Desktop can move.
        _shellDragToken = button.Name switch
        {
            nameof(ShowDesktopButton) => "peek",
            _ => null
        };
        _shellDragStartPoint = e.GetPosition(this);
    }

    private void ShellButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _shellDragButton = null;
        _shellDragToken = null;
    }

    private void ShellButton_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_shellDragButton is null || _shellDragToken is null
            || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var diff = _shellDragStartPoint - e.GetPosition(this);
        if (Math.Abs(diff.X) <= SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(diff.Y) <= SystemParameters.MinimumVerticalDragDistance) return;

        var button = _shellDragButton;
        var token = _shellDragToken;
        _shellDragButton = null;
        _shellDragToken = null;
        _previewCloseTimer.Stop();
        _thumbnailPreview.Hide();
        ResetMagnification(immediate: true);
        button.Opacity = 0.55;
        try
        {
            DragDrop.DoDragDrop(button, token, DragDropEffects.Move);
        }
        finally
        {
            CancelLiveDrag();
            button.Opacity = 1.0;
        }
    }

    // ---- Start Button press tracking (Start itself is pinned and never dragged) ----

    private void StartButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Snapshot at press: the press itself light-dismisses an open Start menu before the click runs.
        var pressTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        _startMenuOpenAtPress = _startMenu.WasOpenAtPress(_startMenu.IsStartMenuOpenNow(), pressTimestamp);
    }

    private static bool TryGetDockItemToken(DragEventArgs e, out string token)
    {
        if (e.Data.GetData(typeof(DockApp)) is DockApp app)
        {
            token = "app:" + app.ExecutablePath;
            return true;
        }
        if (e.Data.GetData(typeof(DockAppGroup)) is DockAppGroup group)
        {
            token = "group:" + group.Id;
            return true;
        }
        if (e.Data.GetData(typeof(DockStack)) is DockStack stack)
        {
            token = "stack:" + stack.Path;
            return true;
        }
        if (e.Data.GetData(typeof(WidgetCoordinator.WidgetDragPayload)) is WidgetCoordinator.WidgetDragPayload widget)
        {
            token = "widget:" + widget.WidgetId;
            return true;
        }
        if (e.Data.GetData(typeof(string)) is string shellToken)
        {
            if (ShellDockTokens.Contains(shellToken, StringComparer.OrdinalIgnoreCase)
                && !DockItemOrdering.IsPinned(shellToken))
            {
                token = shellToken;
                return true;
            }
        }
        token = string.Empty;
        return false;
    }

    // Collapsed or zero-size widgets do not occupy a drop slot.
    private IEnumerable<KeyValuePair<string, FrameworkElement>> VisibleDockItems() =>
        _dockItemElements.Where(item => !DockItemOrdering.IsPinned(item.Key)
            && item.Value.IsVisible
            && (IsVertical ? item.Value.ActualHeight : item.Value.ActualWidth) > 0);

    private int GetDockInsertionIndex(DragEventArgs e)
    {
        var point = e.GetPosition(DockContents);
        var cursor = IsVertical ? point.Y : point.X;
        var index = 0;
        foreach (var (_, element) in VisibleDockItems())
        {
            var origin = element.TranslatePoint(default, DockContents);
            var center = (IsVertical ? origin.Y : origin.X)
                + (IsVertical ? element.ActualHeight : element.ActualWidth) / 2;
            if (cursor > center) index++;
            else break;
        }
        return index;
    }

    private void DockContents_DragOver(object sender, DragEventArgs e)
    {
        CancelDragHoverApp();
        if (TryGetDockItemToken(e, out _))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = ResolveDockBackgroundDropEffect(e);
            e.Handled = true;
        }
    }

    private void DockContents_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDockItemToken(e, out var token))
        {
            ResetDisplacements();
            DropDockItem(token, e);
            e.Handled = true;
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => HandleDockBackgroundFileDropAsync(paths));
        }
    }

    private void DockFrame_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = ResolveDockBackgroundDropEffect(e);
            e.Handled = true;
        }
    }

    private void DockFrame_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => HandleDockBackgroundFileDropAsync(paths));
        }
    }

    // ---- File drag-hover activation and drop-to-open on DockApp buttons ----

    private DragDropEffects ResolveAppFileDropEffect(DragEventArgs e, DockApp app)
    {
        var canOpen = CanOpenFilesWithExecutable(LaunchTargetResolver.Resolve(app.ExecutablePath).Path);
        var hasRunningWindow = FindWindowForExecutable(app.ExecutablePath) is not null;
        if (!canOpen && !hasRunningWindow) return DragDropEffects.None;

        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Move)) return DragDropEffects.Move;
        return DragDropEffects.None;
    }

    private void BeginOrContinueDragHoverApp(Button button, DockApp app)
    {
        _dragHoverAppLeavePending = false;
        if (ReferenceEquals(_dragHoverAppButton, button) && ReferenceEquals(_dragHoverApp, app))
        {
            if (!_dragHoverAppActivated && _dragHoverAppTimer is { IsEnabled: false })
            {
                _dragHoverAppTimer.Start();
            }
            return;
        }

        CancelDragHoverApp();
        _dragHoverAppButton = button;
        _dragHoverApp = app;
        _dragHoverAppActivated = false;
        button.SetResourceReference(Control.BackgroundProperty, "WidgetSelectedFill");

        _dragHoverAppTimer ??= CreateOneShotTimer(DragHoverDwellMs, DragHoverAppTimer_Tick);
        _dragHoverAppTimer.Start();
    }

    private void DragHoverAppTimer_Tick(object? sender, EventArgs e)
    {
        _dragHoverAppTimer?.Stop();
        _dragHoverAppActivated = true;
        if (_dragDropDisposed || _dragHoverApp is not { } app) return;

        var window = FindPreferredWindowForApp(app.ExecutablePath);
        if (window is null) return;

        if (NativeWindowTracker.Activate(window.Handle))
        {
            _lastSelectedWindows[app.ExecutablePath] = window.Handle;
        }
    }

    private TrackedWindow? FindPreferredWindowForApp(string executablePath)
    {
        if (_lastSelectedWindows.TryGetValue(executablePath, out var lastHandle))
        {
            var last = VisibleSnapshot.FirstOrDefault(window =>
                window.Handle == lastHandle
                && string.Equals(window.ExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase));
            if (last is not null) return last;
        }

        return FindWindowForExecutable(executablePath);
    }

    private void CancelDragHoverApp()
    {
        _dragHoverAppLeavePending = false;
        _dragHoverAppTimer?.Stop();
        _dragHoverAppButton?.ClearValue(Control.BackgroundProperty);
        _dragHoverAppButton = null;
        _dragHoverApp = null;
        _dragHoverAppActivated = false;
    }

    internal static bool CanOpenFilesWithExecutable(string? executablePath, string? currentProcessPath = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;

        string full;
        try
        {
            full = Path.GetFullPath(executablePath.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return false;
        }

        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            return false;
        }

        var selfPath = currentProcessPath ?? Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(selfPath))
        {
            try
            {
                if (string.Equals(full, Path.GetFullPath(selfPath), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
            {
            }
        }

        return true;
    }

    internal static IReadOnlyList<string> FilterAppDropPaths(
        IEnumerable<string?> droppedPaths,
        string resolvedExecutablePath,
        string? originalExecutablePath = null)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedResolved = TryNormalizeFullPath(resolvedExecutablePath);
        var normalizedOriginal = TryNormalizeFullPath(originalExecutablePath);

        foreach (var rawPath in droppedPaths)
        {
            var full = TryNormalizeFullPath(rawPath);
            if (string.IsNullOrEmpty(full) || !seen.Add(full)) continue;
            if (!File.Exists(full) && !Directory.Exists(full)) continue;

            // Skip dropping an executable onto itself.
            if (string.Equals(full, normalizedResolved, StringComparison.OrdinalIgnoreCase)
                || string.Equals(full, normalizedOriginal, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(full);
        }

        return results;
    }

    /// <summary>
    /// Quotes paths according to Windows CommandLineToArgvW rules (including embedded quotes and trailing
    /// backslashes on drive roots like C:\) and optionally prepends existing launch arguments.
    /// </summary>
    internal static string BuildQuotedArguments(IEnumerable<string> paths, string? prefixArguments = null)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prefixArguments))
        {
            builder.Append(prefixArguments.Trim());
        }

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (builder.Length > 0) builder.Append(' ');
            AppendQuotedWindowsArgument(builder, path);
        }

        return builder.ToString();
    }

    private static void AppendQuotedWindowsArgument(StringBuilder builder, string value)
    {
        builder.Append('"');
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
            }
            else if (ch == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
            }
            else
            {
                if (backslashes > 0)
                {
                    builder.Append('\\', backslashes);
                    backslashes = 0;
                }
                builder.Append(ch);
            }
        }

        if (backslashes > 0)
        {
            builder.Append('\\', backslashes * 2);
        }
        builder.Append('"');
    }

    private void OpenFilesWithApp(DockApp app, IReadOnlyList<string> droppedPaths)
    {
        if (_dragDropDisposed) return;

        var (resolvedExe, dropArgs) = LaunchTargetResolver.Resolve(app.ExecutablePath);
        if (!CanOpenFilesWithExecutable(resolvedExe)) return;

        var validPaths = FilterAppDropPaths(droppedPaths, resolvedExe, app.ExecutablePath);
        if (validPaths.Count == 0) return;

        var arguments = BuildQuotedArguments(validPaths, dropArgs ? null : app.LaunchArguments);
        var workingDirectory = Path.GetDirectoryName(resolvedExe) ?? string.Empty;

        _ = Task.Run(() =>
        {
            try
            {
                using var _ = Process.Start(new ProcessStartInfo(resolvedExe)
                {
                    Arguments = arguments,
                    UseShellExecute = true,
                    WorkingDirectory = workingDirectory
                });
                return (string?)null;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelledByUser)
            {
                return null;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
            {
                AppLog.Warning($"Failed to open dropped files with {resolvedExe}: {ex.Message}");
                return "ErrorAppLaunchFailed";
            }
        }).ContinueWith(task =>
        {
            if (!_dragDropDisposed && task.IsCompletedSuccessfully && task.Result is { } error)
            {
                _ = Dispatcher.BeginInvoke(() => ReportError(error));
            }
        }, TaskScheduler.Default);
    }

    // ---- ShowDesktopButton drag-hover & drop-to-Desktop ----

    private void ShowDesktopButton_DragOver(object sender, DragEventArgs e)
    {
        CancelDragHoverApp();
        if (TryGetDockItemToken(e, out _))
        {
            CancelShowDesktopDragHover();
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            CancelShowDesktopDragHover();
            e.Effects = DragDropEffects.Move;
            e.Handled = false;
            return;
        }

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            _showDesktopDragLeavePending = false;
            var effect = DesktopFileTransfer.ResolveFileDropEffect(e);
            e.Effects = effect;
            e.Handled = true;

            if (effect != DragDropEffects.None)
            {
                _showDesktopFeedbackTimer?.Stop();
                ShowDesktopButton.SetResourceReference(Control.BackgroundProperty, "AccentBrush");
                if (!_showDesktopDragHoverTriggered)
                {
                    _showDesktopDragHoverTimer ??= CreateOneShotTimer(DragHoverDwellMs, ShowDesktopDragHoverTimer_Tick);
                    if (!_showDesktopDragHoverTimer.IsEnabled)
                    {
                        _showDesktopDragHoverTimer.Start();
                    }
                }
            }
            return;
        }

        CancelShowDesktopDragHover();
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private void ShowDesktopButton_DragLeave(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        _showDesktopDragLeavePending = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_dragDropDisposed && _showDesktopDragLeavePending)
            {
                CancelShowDesktopDragHover();
            }
        });
    }

    private void ShowDesktopButton_Drop(object sender, DragEventArgs e)
    {
        CancelShowDesktopDragHover();

        if (TryGetDockItemToken(e, out var droppedToken))
        {
            ResetDisplacements();
            DropDockItem(droppedToken, e);
            e.Handled = true;
            return;
        }

        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;

        var copy = DesktopFileTransfer.ShouldCopy(e);
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () => TransferToDesktopFromButtonAsync(paths, copy));
    }

    private void ShowDesktopButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        e.Handled = true;
        ReportResult(DesktopFileTransfer.OpenDesktopFolder());
    }

    private void ShowDesktopDragHoverTimer_Tick(object? sender, EventArgs e)
    {
        _showDesktopDragHoverTimer?.Stop();
        _showDesktopDragHoverTriggered = true;
        if (_dragDropDisposed) return;
        ReportResult(ShellShortcuts.ShowDesktopForDrag());
    }

    private void CancelShowDesktopDragHover()
    {
        _showDesktopDragLeavePending = false;
        _showDesktopDragHoverTimer?.Stop();
        _showDesktopDragHoverTriggered = false;
        if (_showDesktopFeedbackTimer is not { IsEnabled: true })
        {
            ShowDesktopButton.ClearValue(Control.BackgroundProperty);
        }
    }

    private async void TransferToDesktopFromButtonAsync(string[] paths, bool copy)
    {
        var result = await DesktopFileTransfer.TransferToDesktopAsync(paths, copy);
        if (_dragDropDisposed) return;

        if (result.ErrorKey is { } errorKey)
        {
            ReportError(errorKey);
            return;
        }

        if (result.Transferred)
        {
            ShowDesktopButton.SetResourceReference(Control.BackgroundProperty, "WidgetGreen");
            _showDesktopFeedbackTimer ??= CreateOneShotTimer(ShowDesktopFeedbackMs, ShowDesktopFeedbackTimer_Tick);
            _showDesktopFeedbackTimer.Stop();
            _showDesktopFeedbackTimer.Start();
        }
    }

    private void ShowDesktopFeedbackTimer_Tick(object? sender, EventArgs e)
    {
        _showDesktopFeedbackTimer?.Stop();
        if (!_dragDropDisposed && _showDesktopDragHoverTimer is not { IsEnabled: true })
        {
            ShowDesktopButton.ClearValue(Control.BackgroundProperty);
        }
    }

    // ---- Dock background file drops (.exe / .lnk -> pin, directory -> DockStack) ----

    private static DragDropEffects ResolveDockBackgroundDropEffect(DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths)
        {
            return DragDropEffects.None;
        }

        if (!paths.Any(WindowsTaskbarPinImporter.IsSupportedDockDropPath))
        {
            return DragDropEffects.None;
        }

        if (e.AllowedEffects.HasFlag(DragDropEffects.Link)) return DragDropEffects.Link;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Copy)) return DragDropEffects.Copy;
        if (e.AllowedEffects.HasFlag(DragDropEffects.Move)) return DragDropEffects.Move;
        return DragDropEffects.None;
    }

    private async void HandleDockBackgroundFileDropAsync(string[] paths)
    {
        if (_dragDropDisposed) return;

        WindowsTaskbarPinImporter.DroppedPinTarget[] targets;
        try
        {
            targets = await StaTask.Run(() => paths
                .Select(WindowsTaskbarPinImporter.TryResolveDroppedTarget)
                .OfType<WindowsTaskbarPinImporter.DroppedPinTarget>()
                .ToArray());
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Resolving dropped dock targets failed: {ex.Message}");
            return;
        }

        if (_dragDropDisposed || targets.Length == 0) return;

        var folders = ConfiguredStackFolders().ToList();
        var existingFolderKeys = folders
            .Select(TryNormalizeDirectoryKey)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var foldersChanged = false;
        var pinsChanged = false;

        foreach (var target in targets)
        {
            if (target.IsDirectory)
            {
                var key = TryNormalizeDirectoryKey(target.TargetPath);
                if (key is not null && existingFolderKeys.Add(key))
                {
                    folders.Add(target.TargetPath);
                    foldersChanged = true;
                }
                continue;
            }

            if (TryPinDroppedApp(target))
            {
                pinsChanged = true;
            }
        }

        if (foldersChanged)
        {
            SaveStackFolders(folders);
        }

        if (pinsChanged)
        {
            SavePinOrder();
            if (_windowTracker is not null) RefreshRunningApps();
            RebuildDockOrder();
            ResizeDockToContent();
            _replacementSession?.RefreshPosition();
        }
    }

    private bool TryPinDroppedApp(WindowsTaskbarPinImporter.DroppedPinTarget target)
    {
        var existingPinned = _pinnedApps.FirstOrDefault(app =>
            string.Equals(app.ExecutablePath, target.TargetPath, StringComparison.OrdinalIgnoreCase));
        if (existingPinned is not null)
        {
            _ = RevealHiddenPin(existingPinned);
            return false;
        }

        var existingRunning = _runningApps.FirstOrDefault(app =>
            string.Equals(app.ExecutablePath, target.TargetPath, StringComparison.OrdinalIgnoreCase));
        if (existingRunning is not null)
        {
            PinRunningApp(existingRunning);
            return false;
        }

        var (shortcutPath, error) = WindowsTaskbarPinImporter.AddPin(
            target.Name,
            target.TargetPath,
            target.LaunchArguments);
        if (error is not null || shortcutPath is null)
        {
            ReportError(error ?? "ErrorAppPinFailed");
            return false;
        }

        var pinnedApp = new DockApp(target.Name, target.TargetPath)
        {
            LaunchArguments = target.LaunchArguments,
            ShortcutPath = shortcutPath,
            IconLocation = target.IconLocation,
            AppUserModelId = ApplicationIconProvider.TryResolvePackagedAppUserModelId(target.TargetPath)
        };
        LoadPinnedIconAsync(pinnedApp);
        _pinnedApps.Add(pinnedApp);
        return true;
    }

    private static string? TryNormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }

    private static string? TryNormalizeDirectoryKey(string? path)
    {
        var full = TryNormalizeFullPath(path);
        return full is null ? null : Path.TrimEndingDirectorySeparator(full);
    }

    private DispatcherTimer CreateOneShotTimer(int milliseconds, EventHandler handler)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds)
        };
        timer.Tick += handler;
        return timer;
    }

    private void DisposeDragDrop()
    {
        _dragDropDisposed = true;
        CancelDragHoverApp();
        if (_dragHoverAppTimer is not null)
        {
            _dragHoverAppTimer.Tick -= DragHoverAppTimer_Tick;
            _dragHoverAppTimer = null;
        }

        _showDesktopFeedbackTimer?.Stop();
        if (_showDesktopFeedbackTimer is not null)
        {
            _showDesktopFeedbackTimer.Tick -= ShowDesktopFeedbackTimer_Tick;
            _showDesktopFeedbackTimer = null;
        }

        CancelShowDesktopDragHover();
        if (_showDesktopDragHoverTimer is not null)
        {
            _showDesktopDragHoverTimer.Tick -= ShowDesktopDragHoverTimer_Tick;
            _showDesktopDragHoverTimer = null;
        }
    }

    private void DockLeadControls_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void DockLeadControls_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartButtonDragData))) return;

        if (_preferences.Current.StartPlacement != StartButtonPlacement.Start)
        {
            _preferences.Save(_preferences.Current with { StartPlacement = StartButtonPlacement.Start });
            ApplyStartPlacement();
            AnimateDropSettle(StartButton);
        }

        e.Handled = true;
    }

    private void DockTrailControls_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void DockTrailControls_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartButtonDragData))) return;

        if (_preferences.Current.StartPlacement != StartButtonPlacement.End)
        {
            _preferences.Save(_preferences.Current with { StartPlacement = StartButtonPlacement.End });
            ApplyStartPlacement();
            AnimateDropSettle(StartButton);
        }

        e.Handled = true;
    }

    private void WidgetStrip_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void WidgetStrip_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartButtonDragData))) return;

        var dropPos = e.GetPosition(WidgetStrip);
        var dropCoord = IsVertical ? dropPos.Y : dropPos.X;

        var targetIndex = 0;
        var validIndex = 0;
        for (var i = 0; i < WidgetStrip.Children.Count; i++)
        {
            if (WidgetStrip.Children[i] is not FrameworkElement child || ReferenceEquals(child, StartButton))
            {
                continue;
            }

            var childPos = child.TranslatePoint(new Point(0, 0), WidgetStrip);
            var childCenter = IsVertical
                ? childPos.Y + (child.ActualHeight / 2)
                : childPos.X + (child.ActualWidth / 2);

            if (dropCoord > childCenter)
            {
                targetIndex = validIndex + 1;
            }
            validIndex++;
        }

        _preferences.Save(_preferences.Current with
        {
            StartPlacement = StartButtonPlacement.InWidgets,
            StartWidgetIndex = targetIndex
        });
        ApplyStartPlacement();
        AnimateDropSettle(StartButton);
        e.Handled = true;
    }
}

public sealed class StartButtonDragData;
