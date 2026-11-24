using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace OpenTaskbar.Services;

/// <summary>Raises changes observed in Explorer's per-user Taskband pin data.</summary>
public sealed class WindowsTaskbarPinMonitor : IDisposable
{
    private const string TaskbandKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband";
    private const uint RegistryNotifyFilter = 0x00000001 | 0x00000002 | 0x00000004;
    private readonly object _gate = new();
    private RegistryKey? _key;
    private AutoResetEvent? _changeEvent;
    private RegisteredWaitHandle? _waitHandle;
    private HashSet<string> _aumids = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public event EventHandler<TaskbarPinChangedEventArgs>? PinChanged;
    public event EventHandler? PinsChanged;

    public WindowsTaskbarPinMonitor() => Start();

    private void Start()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
            _aumids = ReadAumids(key);
            if (key is null) return;
            var watchKey = Registry.CurrentUser.OpenSubKey(TaskbandKey);
            if (watchKey is null) return;
            var changeEvent = new AutoResetEvent(false);
            var status = RegNotifyChangeKeyValue(watchKey.Handle, true, RegistryNotifyFilter,
                changeEvent.SafeWaitHandle, true);
            if (status != 0) throw new Win32Exception(status);
            _key = watchKey;
            _changeEvent = changeEvent;
            _waitHandle = ThreadPool.RegisterWaitForSingleObject(changeEvent, OnChanged, null,
                Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException or ArgumentException)
        {
            AppLog.Warning($"Taskbar pin monitor could not start: {ex.Message}");
            Stop();
        }
    }

    private void OnChanged(object? state, bool timedOut)
    {
        if (_disposed) return;
        HashSet<string> current;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(TaskbandKey);
            current = ReadAumids(key);
            lock (_gate)
            {
                if (_disposed) return;
                var added = current.Except(_aumids, StringComparer.OrdinalIgnoreCase).ToArray();
                var removed = _aumids.Except(current, StringComparer.OrdinalIgnoreCase).ToArray();
                _aumids = current;
                foreach (var aumid in added) RaisePinChanged(new(TaskbarPinChangeKind.Pinned, aumid));
                foreach (var aumid in removed) RaisePinChanged(new(TaskbarPinChangeKind.Unpinned, aumid));
            }
            PinsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            AppLog.Warning($"Taskbar pin monitor could not read Taskband: {ex.Message}");
        }
        Rearm();
    }

    private static HashSet<string> ReadAumids(RegistryKey? key) =>
        WindowsTaskbarPinImporter.ExtractAumidsFromTaskbandBytes(key?.GetValue("Favorites") as byte[] ?? [])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void RaisePinChanged(TaskbarPinChangedEventArgs args)
    {
        var handlers = PinChanged;
        if (handlers is null) return;
        foreach (EventHandler<TaskbarPinChangedEventArgs> handler in handlers.GetInvocationList())
        {
            try { handler(this, args); }
            catch (Exception ex) { AppLog.Warning($"Taskbar pin listener failed: {ex.Message}"); }
        }
    }

    private void Rearm()
    {
        lock (_gate)
        {
            if (_disposed || _key is null || _changeEvent is null) return;
            var status = RegNotifyChangeKeyValue(_key.Handle, true, RegistryNotifyFilter,
                _changeEvent.SafeWaitHandle, true);
            if (status != 0) AppLog.Warning($"Taskbar pin monitor could not rearm: {new Win32Exception(status).Message}");
        }
    }

    private void Stop()
    {
        lock (_gate)
        {
            _waitHandle?.Unregister(null);
            _waitHandle = null;
            _changeEvent?.Dispose();
            _changeEvent = null;
            _key?.Dispose();
            _key = null;
        }
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        Stop();
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree, uint notifyFilter, SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);
}

public enum TaskbarPinChangeKind { Pinned, Unpinned }

public sealed class TaskbarPinChangedEventArgs(TaskbarPinChangeKind kind, string appUserModelId) : EventArgs
{
    public TaskbarPinChangeKind Kind { get; } = kind;
    public string AppUserModelId { get; } = appUserModelId;
}
