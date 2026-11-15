using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;

namespace OpenTaskbar.Services;

public sealed class TaskbarReplacementSession : IDisposable
{
    private const uint AbmNew = 0x00000000;
    private const uint AbmRemove = 0x00000001;
    private const uint AbmQueryPos = 0x00000002;
    private const uint AbmSetPos = 0x00000003;
    private const uint AbmGetState = 0x00000004;
    private const uint AbmActivate = 0x00000006;
    private const uint AbmSetState = 0x0000000A;
    private const uint AbmWindowPosChanged = 0x00000009;
    private const uint AbsAutoHide = 0x00000001;
    private const uint AbsAlwaysOnTop = 0x00000002;
    private const uint SupportedTaskbarState = AbsAutoHide | AbsAlwaysOnTop;
    private const uint AbeBottom = 3;
    private const uint AbnStateChange = 0;
    private const uint AbnPosChanged = 1;
    private const uint AbnFullScreenApp = 2;
    private const uint WmActivate = 0x0006;
    private const uint WmWindowPosChanged = 0x0047;
    private const uint WmDisplayChange = 0x007E;
    private const uint WmDpiChanged = 0x02E0;
    private const uint CallbackMessage = 0x8000 + 0x52;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;
    private static readonly nint HwndTopmost = new(-1);

    private readonly Window _window;
    private readonly nint _handle;
    private bool _registered;
    private bool _active;
    private bool _stateChangeStarted;
    private uint _previousTaskbarState;
    private bool _disposed;

    public TaskbarReplacementSession(Window window, nint handle)
    {
        _window = window;
        _handle = handle;
    }

    public bool IsActive => _active;

    public event Action<string>? OperationFailed;

    private static bool HasPendingRecovery(out string? error)
    {
        error = null;
        if (!File.Exists(JournalPath))
        {
            return false;
        }

        try
        {
            _ = ReadJournal();
            return true;
        }
        catch (InvalidDataException)
        {
            error = "Görev çubuğu kurtarma kaydı bozuk. Windows ayarlarını elle denetleyin.";
        }
        catch (IOException)
        {
            error = "Görev çubuğu kurtarma kaydı okunamadı.";
        }
        catch (UnauthorizedAccessException)
        {
            error = "Görev çubuğu kurtarma kaydına erişim izni yok.";
        }

        return false;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_active)
        {
            return;
        }

        var pending = HasPendingRecovery(out var recoveryError);
        if (recoveryError is not null)
        {
            throw new InvalidOperationException(recoveryError);
        }

        _previousTaskbarState = pending ? ReadJournal() : GetTaskbarState();
        try
        {
            if (!pending)
            {
                WriteJournal(_previousTaskbarState);
            }
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydı yazılamadı.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException("Görev çubuğu ayarı değiştirilmedi; kurtarma kaydına yazma izni yok.", ex);
        }

        try
        {
            _stateChangeStarted = true;
            var replacementState = _previousTaskbarState & AbsAlwaysOnTop;
            if (!SetTaskbarState(replacementState, _handle))
            {
                throw new InvalidOperationException("Windows görev çubuğunun kenar tetiklemesi kapatılamadı.");
            }

            if (!HideNativeTaskbar())
            {
                throw new InvalidOperationException("Windows görev çubuğu gizlenemedi.");
            }

            var data = CreateData();
            data.uCallbackMessage = CallbackMessage;
            if (SHAppBarMessage(AbmNew, ref data) == 0)
            {
                throw new InvalidOperationException("OpenTaskbar Windows'a AppBar olarak kaydedilemedi.");
            }

            _registered = true;
            _active = true;
            PositionAppBar();
        }
        catch
        {
            Stop();
            throw;
        }
    }

    public bool HandleMessage(uint message, nint wParam, nint lParam)
    {
        if (!_active)
        {
            return false;
        }

        if (message == CallbackMessage)
        {
            switch (unchecked((uint)wParam.ToInt64()))
            {
                case AbnPosChanged:
                    EnforceNativeTaskbarHidden();
                    PositionAppBar();
                    return true;
                case AbnFullScreenApp:
                    if (lParam != 0)
                    {
                        ShowWindow(_handle, SwHide);
                    }
                    else
                    {
                        ShowWindow(_handle, SwShowNoActivate);
                        PositionAppBar();
                    }
                    return true;
                case AbnStateChange:
                    EnforceNativeTaskbarHidden();
                    return true;
            }
        }

        if (message == WmWindowPosChanged)
        {
            var data = CreateData();
            SHAppBarMessage(AbmWindowPosChanged, ref data);
        }
        else if (message == WmActivate)
        {
            var data = CreateData();
            data.lParam = (wParam.ToInt64() & 0xFFFF) == 0 ? 0 : 1;
            SHAppBarMessage(AbmActivate, ref data);
        }
        else if (message is WmDisplayChange or WmDpiChanged)
        {
            PositionAppBar();
        }

        return false;
    }

    public string? Stop()
    {
        if (!_active && !_registered && !_stateChangeStarted)
        {
            return null;
        }

        if (_registered)
        {
            var data = CreateData();
            SHAppBarMessage(AbmRemove, ref data);
            _registered = false;
        }

        var stateRestored = SetTaskbarState(_previousTaskbarState, _handle);
        var taskbarShown = ShowNativeTaskbar();
        if (!stateRestored || !taskbarShown)
        {
            _active = true;
            return "Windows görev çubuğu ayarı geri yüklenemedi. Kapatmadan önce Windows ayarlarından otomatik gizlemeyi düzeltin.";
        }

        _active = false;
        _stateChangeStarted = false;
        return DeleteJournal();
    }

    public void RefreshPosition()
    {
        PositionAppBar();
    }

    private void PositionAppBar()
    {
        if (!_registered)
        {
            return;
        }

        var monitor = MonitorFromWindow(_handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { cbSize = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            throw new InvalidOperationException("Ekran boyutları alınamadı.");
        }

        var dpi = GetDpiForWindow(_handle);
        var height = Math.Max(1, (int)Math.Round(_window.Height * dpi / 96d));
        var data = CreateData();
        data.uEdge = AbeBottom;
        data.rc = new Rect(
            monitorInfo.rcMonitor.Left,
            monitorInfo.rcMonitor.Bottom - height,
            monitorInfo.rcMonitor.Right,
            monitorInfo.rcMonitor.Bottom);

        SHAppBarMessage(AbmQueryPos, ref data);
        data.rc.Top = data.rc.Bottom - height;
        SHAppBarMessage(AbmSetPos, ref data);
        var left = monitorInfo.rcMonitor.Left;
        var top = monitorInfo.rcMonitor.Bottom - height;
        var width = monitorInfo.rcMonitor.Right - left;
        if (!SetWindowPos(_handle, HwndTopmost, left, top,
                width, height,
                SwpNoActivate | SwpNoOwnerZOrder))
        {
            throw new InvalidOperationException("OpenTaskbar ekranın alt kenarına yerleştirilemedi.");
        }
    }

    private static uint GetTaskbarState()
    {
        var data = new AppBarData { cbSize = (uint)Marshal.SizeOf<AppBarData>() };
        return unchecked((uint)SHAppBarMessage(AbmGetState, ref data));
    }

    private static bool HideNativeTaskbar()
    {
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == 0)
        {
            return false;
        }

        if (IsWindowVisible(taskbarHandle))
        {
            ShowWindow(taskbarHandle, SwHide);
        }

        return !IsWindowVisible(taskbarHandle);
    }

    private static bool ShowNativeTaskbar()
    {
        var taskbarHandle = FindWindow("Shell_TrayWnd", null);
        if (taskbarHandle == 0)
        {
            return false;
        }

        ShowWindow(taskbarHandle, SwShowNoActivate);
        return IsWindowVisible(taskbarHandle);
    }

    private void EnforceNativeTaskbarHidden()
    {
        var replacementState = _previousTaskbarState & AbsAlwaysOnTop;
        if (!SetTaskbarState(replacementState, _handle))
        {
            OperationFailed?.Invoke("Windows görev çubuğunun otomatik açılması yeniden etkinleşti.");
            return;
        }

        if (!HideNativeTaskbar())
        {
            OperationFailed?.Invoke("Windows görev çubuğu yeniden gizlenemedi.");
        }
    }

    private static bool SetTaskbarState(uint state, nint windowHandle)
    {
        var data = new AppBarData
        {
            cbSize = (uint)Marshal.SizeOf<AppBarData>(),
            hWnd = windowHandle,
            lParam = unchecked((nint)state)
        };
        SHAppBarMessage(AbmSetState, ref data);
        return (GetTaskbarState() & SupportedTaskbarState) == (state & SupportedTaskbarState);
    }

    private AppBarData CreateData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<AppBarData>(),
        hWnd = _handle
    };

    private static string JournalPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "OpenTaskbar",
        "taskbar-state.txt");

    private static void WriteJournal(uint state)
    {
        var path = JournalPath;
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new IOException("Görev çubuğu kurtarma kaydı için yol oluşturulamadı.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, state.ToString(CultureInfo.InvariantCulture));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static uint ReadJournal()
    {
        var text = File.ReadAllText(JournalPath).Trim();
        if (!uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var state)
            || (state & ~SupportedTaskbarState) != 0)
        {
            throw new InvalidDataException("Görev çubuğu kurtarma kaydı geçersiz.");
        }

        return state;
    }

    private static string? DeleteJournal()
    {
        try
        {
            if (File.Exists(JournalPath))
            {
                File.Delete(JournalPath);
            }

            return null;
        }
        catch (IOException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydı silinemedi.";
        }
        catch (UnauthorizedAccessException)
        {
            return "Görev çubuğu ayarı geri yüklendi ancak kurtarma kaydını silme izni yok.";
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public nint lParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect(int left, int top, int right, int bottom)
    {
        public int Left = left;
        public int Top = top;
        public int Right = right;
        public int Bottom = bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [DllImport("shell32.dll")]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW")]
    private static extern nint FindWindow(string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint windowHandle);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint windowHandle, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint windowHandle, int command);
}
