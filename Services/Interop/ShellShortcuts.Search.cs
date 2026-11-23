using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public static partial class ShellShortcuts
{
    private const ushort VkEscape = 0x1B;
    private const int DwmwaExtendedFrameBounds = 9;
    private static readonly TimeSpan SearchProbeTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly object SearchProbeGate = new();
    private static readonly PropertyCondition SearchEditCondition =
        new(AutomationElement.ControlTypeProperty, ControlType.Edit);
    private static readonly OrCondition SearchInputCondition = new(SearchEditCondition,
        new PropertyCondition(AutomationElement.IsValuePatternAvailableProperty, true),
        new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Custom),
            new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)));
    private static Task<SearchWindow?>? _nativeSearchProbe;
    private static int _searchQueryVersion;

    internal sealed record SearchWindow(nint Handle, uint ProcessId, string ProcessName, string ClassName);

    public static async Task<bool> IsSearchFlyoutOpenAsync(CancellationToken cancellation = default)
    {
        var search = await FindSearchAsync(cancellation);
        return search.Native is not null || search.Legacy.Count > 0;
    }

    public static async Task CloseWindowsSearch()
    {
        var search = await FindSearchAsync();
        if (search.Native is { } native)
        {
            if (IsCurrentSearchWindow(native) && TryFocusSearchWindow(native.Handle)
                && SyntheticKeyboard.SendFocusedNavigationKey(VkEscape))
                return;

            AppLog.Warning("Windows Search could not be closed with Escape.");
        }
        else if (search.Legacy.Count > 0)
        {
            SendWindowsShortcut(VkS);
            EnsureNativeTaskbarHidden();
        }
    }

    public static async Task ForwardKeyToSearch(ushort vk)
    {
        var search = await FindSearchAsync();
        if (search.Native is { } native && IsCurrentSearchWindow(native)
            && TryFocusSearchWindow(native.Handle) && SyntheticKeyboard.SendFocusedNavigationKey(vk))
            return;

        if (search.Legacy.Count > 0)
        {
            foreach (var hwnd in search.Legacy)
            {
                PostMessage(hwnd, WmKeyDown, (nint)vk, 0);
                PostMessage(hwnd, WmKeyUp, (nint)vk, 0);
            }
            return;
        }

        AppLog.Warning("Windows Search navigation key could not be forwarded.");
    }

    public static async Task SyncSearchQuery(string query)
    {
        var version = Interlocked.Increment(ref _searchQueryVersion);
        var search = await FindSearchAsync();
        IReadOnlyList<nint> handles = search.Native is { } native ? [native.Handle] : search.Legacy;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var cancellation = timeout.Token;
        bool IsCurrent() => !cancellation.IsCancellationRequested && version == Volatile.Read(ref _searchQueryVersion);
        var applied = false;
        try
        {
            applied = handles.Count > 0 && await StaTask.RunSearch(() => TryApplySearchQuery(
                () => TrySetSearchValues(handles, query, IsCurrent, search.Native, byIdentity: false),
                () => TrySetSearchValues(handles, query, IsCurrent, search.Native, byIdentity: true),
                () => TryTypeSearchQuery(handles, query, IsCurrent, search.Native))).WaitAsync(cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Ignore a late UIA result once the query has expired.
        }

        if (!applied && version == Volatile.Read(ref _searchQueryVersion))
            AppLog.Warning("Windows Search query could not be synchronized through UI Automation or the keyboard.");
    }

    internal static bool TryApplySearchQuery(Func<bool> setValue, Func<bool> setIdentifiedValue, Func<bool> type) =>
        setValue() || setIdentifiedValue() || type();

    private static bool TryTypeSearchQuery(IReadOnlyList<nint> handles, string query, Func<bool> isCurrent,
        SearchWindow? native)
    {
        foreach (var hwnd in handles)
        {
            if (!isCurrent()) return true;
            if (native is not null && !IsCurrentSearchWindow(native)) continue;
            if (!IsWindowVisible(hwnd) || IsWindowCloaked(hwnd)) continue;
            if (TryReadSearchAutomation(() =>
            {
                var root = AutomationElement.FromHandle(hwnd);
                var edit = FindSearchEditByIdentity(root) ?? root.FindFirst(TreeScope.Descendants, SearchEditCondition);
                if (edit is null || !isCurrent() || !TryFocusSearchWindow(hwnd)) return false;
                if (!edit.Current.HasKeyboardFocus) edit.SetFocus();
                return isCurrent() && edit.Current.HasKeyboardFocus && GetForegroundWindow() == hwnd
                    && SyntheticKeyboard.ReplaceFocusedText(query);
            }, "keyboard fallback"))
                return true;
        }
        return false;
    }

    private static bool TrySetSearchValues(IReadOnlyList<nint> handles, string query, Func<bool> isCurrent,
        SearchWindow? native, bool byIdentity)
    {
        foreach (var hwnd in handles)
        {
            if (!isCurrent()) return true;
            if (native is not null && !IsCurrentSearchWindow(native)) continue;
            if (TryReadSearchAutomation(() =>
            {
                var root = AutomationElement.FromHandle(hwnd);
                var edit = byIdentity ? FindSearchEditByIdentity(root)
                    : root.FindFirst(TreeScope.Descendants, SearchEditCondition);
                if (edit is null || !edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
                    || pattern is not ValuePattern value || value.Current.IsReadOnly || !isCurrent())
                    return false;
                value.SetValue(query);
                return true;
            }, "ValuePattern"))
                return true;
        }
        return false;
    }

    private static AutomationElement? FindSearchEditByIdentity(AutomationElement root)
    {
        foreach (AutomationElement element in root.FindAll(TreeScope.Descendants, SearchInputCondition))
        {
            var properties = element.Current;
            if (properties.IsEnabled && !properties.IsOffscreen
                && properties.ControlType != ControlType.Button
                && (HasSearchIdentity(properties.AutomationId, properties.Name)
                    || properties.Name.StartsWith("Search ", StringComparison.OrdinalIgnoreCase)))
                return element;
        }
        return null;
    }

    internal static bool HasSearchIdentity(string automationId, string name) =>
        automationId.Contains("Search", StringComparison.OrdinalIgnoreCase)
        || IsSearchLabel(name);

    private static bool IsSearchLabel(string name) =>
        name.Equals("Search", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Windows Search", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Arama", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Ara", StringComparison.OrdinalIgnoreCase);

    internal static (SearchWindow? Native, List<nint> Legacy) SelectSearchWindows(
        SearchWindow? native, Func<List<nint>> findLegacy) =>
        native is not null ? (native, []) : (null, findLegacy());

    private static async Task<(SearchWindow? Native, List<nint> Legacy)> FindSearchAsync(
        CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        Task<SearchWindow?> probe;
        lock (SearchProbeGate)
        {
            probe = _nativeSearchProbe ??= StaTask.RunSearch(ProbeNativeSearch);
        }

        SearchWindow? native = null;
        try
        {
            native = await probe.WaitAsync(SearchProbeTimeout, cancellation);
        }
        catch (TimeoutException) when (!probe.IsFaulted)
        {
            // A slow provider can outlive the wait. Subsequent callers reuse the same in-flight scan.
            return SelectSearchWindows(null, FindLegacySearchWindows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Warning($"Windows Search detection failed: {ex.GetType().Name}.");
        }
        lock (SearchProbeGate)
        {
            if (_nativeSearchProbe == probe) _nativeSearchProbe = null;
        }
        return SelectSearchWindows(native is not null && IsCurrentSearchWindow(native) ? native : null,
            FindLegacySearchWindows);
    }

    private static SearchWindow? ProbeNativeSearch()
    {
        var candidates = new List<(nint Handle, uint ProcessId)>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != 0 && pid != Environment.ProcessId && IsWindowVisible(hwnd) && !IsWindowCloaked(hwnd)
                && GetWindowRect(hwnd, out var rect) && rect.Right - rect.Left > 40 && rect.Bottom - rect.Top > 20)
                candidates.Add((hwnd, pid));
            return true;
        }, 0);

        var foreground = GetForegroundWindow();
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (windows.Length == 0) return null;
        var systemApps = Path.Combine(windows, "SystemApps") + Path.DirectorySeparatorChar;
        var system32 = Path.Combine(windows, "System32") + Path.DirectorySeparatorChar;
        var started = Stopwatch.GetTimestamp();
        foreach (var (hwnd, pid) in candidates.OrderByDescending(candidate => candidate.Handle == foreground))
        {
            if (Stopwatch.GetElapsedTime(started) > SearchProbeTimeout) break;
            var path = NativeWindowTracker.GetExecutablePath(hwnd);
            if (path is null || (!path.StartsWith(systemApps, StringComparison.OrdinalIgnoreCase)
                && !path.StartsWith(system32, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(Path.GetDirectoryName(path), windows, StringComparison.OrdinalIgnoreCase)))
                continue;
            var processName = Path.GetFileNameWithoutExtension(path);
            var className = ReadSearchWindowClass(hwnd);
            // Keep the established CoreWindow Search and Start paths on their existing implementations.
            if (StartMenuState.IsStartMenuProcessName(processName)
                || (className == "Windows.UI.Core.CoreWindow" && (processName is "SearchHost" or "SearchApp")))
                continue;
            var candidate = new SearchWindow(hwnd, pid, processName, className);
            if (!IsCurrentSearchWindow(candidate)) continue;
            if (TryReadSearchAutomation(() =>
            {
                var root = AutomationElement.FromHandle(hwnd);
                var properties = root.Current;
                // The native Search tree has not been sampled yet. Require a Search-specific root or
                // app identity plus a visible editor so an ordinary shell window with a search box is ignored.
                var hasRootIdentity = HasSearchIdentity(properties.AutomationId, properties.Name);
                var edit = hasRootIdentity
                    ? root.FindFirst(TreeScope.Descendants, SearchEditCondition) ?? FindSearchEditByIdentity(root)
                    : FindSearchEditByIdentity(root);
                return edit is not null && !edit.Current.IsOffscreen
                    && (hasRootIdentity || (NativeWindowTracker.GetPackagedAppId(hwnd) is { } appId
                        && HasSearchIdentity(appId, string.Empty)));
            }, "detection"))
                return candidate;
        }

        return null;
    }

    private static bool TryFocusSearchWindow(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return true;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == Environment.ProcessId && SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd;
    }

    private static bool IsCurrentSearchWindow(SearchWindow window)
    {
        GetWindowThreadProcessId(window.Handle, out var pid);
        return pid == window.ProcessId && IsWindowVisible(window.Handle) && !IsWindowCloaked(window.Handle);
    }

    private static T TryReadSearchAutomation<T>(Func<T> action, string operation) where T : struct
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or ElementNotEnabledException
            or InvalidOperationException or ArgumentException or COMException or UnauthorizedAccessException
            or Win32Exception or NotSupportedException or TimeoutException)
        {
            AppLog.Debug($"Windows Search UI Automation {operation} failed: {ex.GetType().Name}.");
            return default;
        }
    }

    private static async Task ObserveUnanchoredSearchAsync()
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            await Task.Delay(50);
            var search = await FindSearchAsync();
            if (search.Native is { } native)
            {
                await LogSearchWindowAsync(native);
                return;
            }
            if (search.Legacy.Count > 0)
            {
                await LogLegacySearchAsync(search.Legacy[0]);
                return;
            }
        }

        AppLog.Warning("Windows Search flyout could not be identified after Win+S.");
    }

    private static Task LogLegacySearchAsync(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        var image = NativeWindowTracker.GetExecutablePath(hwnd);
        return LogSearchWindowAsync(new SearchWindow(hwnd, pid,
            image is null ? "unavailable" : Path.GetFileNameWithoutExtension(image), ReadSearchWindowClass(hwnd)));
    }

    private static Task LogSearchWindowAsync(SearchWindow window) => StaTask.RunSearch(() =>
    {
        if (!IsCurrentSearchWindow(window)) return false;
        var title = new StringBuilder(128);
        GetWindowText(window.Handle, title, title.Capacity);
        var safeTitle = SafeSearchLabel(title.ToString());
        var rootInfo = "unavailable";
        TryReadSearchAutomation(() =>
        {
            var root = AutomationElement.FromHandle(window.Handle).Current;
            rootInfo = $"{root.ControlType.ProgrammaticName} ({root.FrameworkId}); name={SafeSearchLabel(root.Name)}";
            return true;
        }, "diagnostics");
        AppLog.Info($"Windows Search detected: process={window.ProcessName}, class={window.ClassName}, title={safeTitle}, UIA root={rootInfo}.");
        return true;
    });

    // Window titles and UIA names can include the query. Only log static Search labels.
    private static string SafeSearchLabel(string name) => IsSearchLabel(name) ? name
        : name.Length == 0 ? "(empty)" : "(redacted)";

    private static string ReadSearchWindowClass(nint hwnd)
    {
        var name = new StringBuilder(256);
        GetClassName(hwnd, name, name.Capacity);
        return name.ToString();
    }

    private static async Task RepositionNativeSearchAsync(SearchWindow window, NativeRect work,
        Point topLeft, Point bottomRight, DockEdge edge, int generation, CancellationToken cancellation)
    {
        _ = LogSearchWindowAsync(window);
        var hwnd = window.Handle;
        var missingMeasurements = 0;
        for (var frame = 0; CanMoveShellWindow(generation, cancellation) && IsCurrentSearchWindow(window); frame++)
        {
            if (frame >= 5 && !IsSearchOrDockForeground(hwnd)) return;
            (Rect Host, Rect Panel) measurement;
            try
            {
                measurement = await StaTask.RunSearch(() => cancellation.IsCancellationRequested ? default
                    : MeasureNativeSearch(window)).WaitAsync(TimeSpan.FromMilliseconds(400), cancellation);
            }
            catch (TimeoutException)
            {
                AppLog.Warning("Windows Search panel measurement timed out.");
                return;
            }
            if (!CanMoveShellWindow(generation, cancellation) || !IsCurrentSearchWindow(window)) return;
            if (measurement.Panel.Width <= 0 || measurement.Panel.Height <= 0)
            {
                if (++missingMeasurements == 5)
                {
                    AppLog.Warning("Windows Search panel bounds were unavailable.");
                    return;
                }
                await Task.Delay(50, cancellation);
                continue;
            }
            missingMeasurements = 0;

            if (!GetWindowRect(hwnd, out var rect)) return;
            var dpi = GetDpiForWindow(hwnd);
            var (x, y) = ComputeMeasuredSearchPosition(measurement.Host, measurement.Panel, AsRect(work),
                new Rect(topLeft, bottomRight), edge, dpi == 0 ? 1 : dpi / 96.0);
            if (rect.Left != x || rect.Top != y)
            {
                FlyoutPositions.Remember(hwnd, rect.Left, rect.Top);
                if (!CanMoveShellWindow(generation, cancellation)) return;
                if (!SetWindowPos(hwnd, 0, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate))
                {
                    AppLog.Warning($"Windows Search panel could not be moved: {Marshal.GetLastPInvokeError()}.");
                    return;
                }
            }

            // Search can resize when results arrive or the query changes.
            await Task.Delay(frame < 5 ? 30 : 250, cancellation);
        }
    }

    private static bool IsSearchOrDockForeground(nint hwnd)
    {
        var foreground = GetForegroundWindow();
        if (foreground == hwnd) return true;
        GetWindowThreadProcessId(foreground, out var pid);
        return pid == Environment.ProcessId;
    }

    private static (Rect Host, Rect Panel) MeasureNativeSearch(SearchWindow window)
    {
        if (!IsCurrentSearchWindow(window) || !GetWindowRect(window.Handle, out var rect)) return default;
        var host = AsRect(rect);
        var panel = TryReadSearchAutomation(() =>
        {
            var root = AutomationElement.FromHandle(window.Handle);
            var edit = FindSearchEditByIdentity(root) ?? root.FindFirst(TreeScope.Descendants, SearchEditCondition);
            if (edit is null) return Rect.Empty;
            var measured = Rect.Empty;
            var rootBounds = root.Current.BoundingRectangle;
            var editBounds = edit.Current.BoundingRectangle;
            // Walk up from the editor, excluding the host itself so a transparent canvas is not
            // mistaken for the panel. A compact host can use its DWM frame when no panel is exposed.
            for (var current = TreeWalker.RawViewWalker.GetParent(edit); current is not null
                && !Automation.Compare(current, root); current = TreeWalker.RawViewWalker.GetParent(current))
            {
                var properties = current.Current;
                var bounds = properties.BoundingRectangle;
                if (!properties.IsOffscreen && bounds.Width > 0 && bounds.Height > editBounds.Height
                    && host.Contains(bounds) && bounds != rootBounds)
                    measured = bounds;
            }
            return measured;
        }, "measurement");

        if (panel.Width <= 0 || panel.Height <= 0)
        {
            if (DwmGetSearchFrame(window.Handle, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<NativeRect>()) != 0)
                return default;
            panel = AsRect(frame);
            var monitor = MonitorFromWindow(window.Handle, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info)
                && (panel.Contains(AsRect(info.Work)) || panel.Contains(AsRect(info.Monitor))))
                return default;
        }
        return (host, panel);
    }

    private static Rect AsRect(NativeRect rect) => new(
        rect.Left, rect.Top, Math.Max(0, rect.Right - rect.Left), Math.Max(0, rect.Bottom - rect.Top));

    internal static (int X, int Y) ComputeMeasuredSearchPosition(Rect host, Rect panel, Rect work,
        Rect anchor, DockEdge edge, double dpiScale)
    {
        var (x, y) = ComputeSearchPanelPosition(
            (int)Math.Ceiling(panel.Width), (int)Math.Ceiling(panel.Height),
            (int)work.Left, (int)work.Top, (int)work.Right, (int)work.Bottom,
            anchor.Left, anchor.Top, anchor.Right, anchor.Bottom, edge, dpiScale, measured: true);
        return (x - (int)Math.Round(panel.Left - host.Left), y - (int)Math.Round(panel.Top - host.Top));
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder title, int capacity);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetSearchFrame(nint hwnd, int attribute, out NativeRect rect, int size);
}
