using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

public sealed class NativeWindowTracker : IDisposable
{
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectDestroy = 0x8001;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectNameChange = 0x800C;
    private const uint WineventOutOfContext = 0x0000;
    private const int ObjectWindow = 0;
    private const int ChildIdSelf = 0;
    private const int WindowOwner = 4;
    private const int ShowRestore = 9;

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _refreshTimer;
    private readonly WinEventDelegate _eventCallback;
    private readonly EnumWindowsDelegate _enumCallback;
    private nint _windowHook;
    private nint _nameChangeHook;
    private nint _excludedWindow;
    private int _refreshQueued;
    private bool _disposed;

    public NativeWindowTracker(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180)
        };
        _refreshTimer.Tick += RefreshTimer_Tick;
        _eventCallback = OnWinEvent;
        _enumCallback = CollectWindow;
    }

    public event EventHandler<IReadOnlyList<TrackedWindow>>? WindowsChanged;

    public IReadOnlyList<TrackedWindow> Snapshot { get; private set; } = [];

    public void Start(nint excludedWindow)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _excludedWindow = excludedWindow;
        _windowHook = SetWinEventHook(EventObjectCreate, EventObjectHide, 0, _eventCallback, 0, 0, WineventOutOfContext);
        if (_windowHook == 0)
        {
            throw new InvalidOperationException("Windows pencere bildirimleri dinlenemedi.");
        }

        _nameChangeHook = SetWinEventHook(EventObjectNameChange, EventObjectNameChange, 0, _eventCallback, 0, 0, WineventOutOfContext);
        if (_nameChangeHook == 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
            throw new InvalidOperationException("Pencere başlığı değişiklikleri dinlenemedi.");
        }

        RefreshWindows();
    }

    private void OnWinEvent(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (_disposed || objectId != ObjectWindow || childId != ChildIdSelf
            || eventType is not (EventObjectCreate or EventObjectDestroy or EventObjectShow or EventObjectHide or EventObjectNameChange))
        {
            return;
        }

        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0)
        {
            return;
        }

        _dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!_disposed)
            {
                _refreshTimer.Stop();
                _refreshTimer.Start();
            }
        }, DispatcherPriority.Background);
    }

    private void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        RefreshWindows();
    }

    private void RefreshWindows()
    {
        var windows = new List<TrackedWindow>();
        var previousWindows = Snapshot.ToDictionary(window => window.Handle);
        var state = GCHandle.Alloc(new WindowCollectionState(windows, previousWindows));
        try
        {
            EnumWindows(_enumCallback, GCHandle.ToIntPtr(state));
        }
        finally
        {
            state.Free();
        }

        if (Snapshot.SequenceEqual(windows))
        {
            return;
        }

        Snapshot = windows;
        WindowsChanged?.Invoke(this, windows);
    }

    private bool CollectWindow(nint windowHandle, nint state)
    {
        if (windowHandle == _excludedWindow || !IsWindowVisible(windowHandle)
            || GetWindow(windowHandle, WindowOwner) != 0)
        {
            return true;
        }

        var titleLength = GetWindowTextLength(windowHandle);
        if (titleLength <= 0)
        {
            return true;
        }

        var title = new StringBuilder(Math.Min(titleLength + 1, 1024));
        GetWindowText(windowHandle, title, title.Capacity);
        var value = title.ToString().Trim();
        if (value.Length > 0)
        {
            if (GCHandle.FromIntPtr(state).Target is WindowCollectionState collection)
            {
                if (collection.Previous.TryGetValue(windowHandle, out var previous))
                {
                    collection.Windows.Add(previous with { Title = value });
                }
                else
                {
                    collection.Windows.Add(new TrackedWindow(windowHandle, value,
                        ApplicationIconProvider.GetForWindow(windowHandle)));
                }
            }
        }

        return true;
    }

    public static void Activate(nint windowHandle)
    {
        if (IsIconic(windowHandle))
        {
            ShowWindow(windowHandle, ShowRestore);
        }

        SetForegroundWindow(windowHandle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _refreshTimer.Stop();
        _refreshTimer.Tick -= RefreshTimer_Tick;
        if (_windowHook != 0)
        {
            UnhookWinEvent(_windowHook);
            _windowHook = 0;
        }

        if (_nameChangeHook != 0)
        {
            UnhookWinEvent(_nameChangeHook);
            _nameChangeHook = 0;
        }
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint windowHandle, int objectId, int childId, uint eventThread, uint eventTime);
    private delegate bool EnumWindowsDelegate(nint windowHandle, nint state);

    private sealed record WindowCollectionState(List<TrackedWindow> Windows, IReadOnlyDictionary<nint, TrackedWindow> Previous);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint eventHookModule, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsDelegate callback, nint state);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint windowHandle, int command);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint windowHandle, StringBuilder text, int maximumCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint windowHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);
}
