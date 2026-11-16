using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

public readonly record struct BatteryStatus(bool Present, int Percent, bool Charging, bool PluggedIn);

/// <summary>
/// Cheap Win32 readings for the system meters. CPU usage is the busy share between two successive samples.
/// </summary>
public sealed class SystemStats
{
    private const byte BatteryFlagNoBattery = 128;
    private const byte BatteryFlagCharging = 8;
    private const byte BatteryFlagUnknown = 255;
    private const byte BatteryPercentUnknown = 255;

    private ulong _lastIdle;
    private ulong _lastTotal;

    /// <summary>Returns processor usage (0–1) since the previous call, or null on the first sample.</summary>
    public double? SampleCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        // Kernel time already includes idle time.
        var total = kernel + user;
        double? usage = null;
        if (_lastTotal != 0 && total > _lastTotal)
        {
            var totalDelta = total - _lastTotal;
            var idleDelta = idle - _lastIdle;
            usage = Math.Clamp(1.0 - (double)idleDelta / totalDelta, 0, 1);
        }

        _lastIdle = idle;
        _lastTotal = total;
        return usage;
    }

    public static double? SampleMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status) ? status.MemoryLoad / 100.0 : null;
    }

    public static BatteryStatus SampleBattery()
    {
        if (!GetSystemPowerStatus(out var power)
            || power.BatteryFlag is BatteryFlagUnknown
            || (power.BatteryFlag & BatteryFlagNoBattery) != 0
            || power.BatteryLifePercent == BatteryPercentUnknown)
        {
            return new BatteryStatus(false, 0, false, false);
        }

        return new BatteryStatus(true, power.BatteryLifePercent,
            (power.BatteryFlag & BatteryFlagCharging) != 0, power.AcLineStatus == 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idleTime, out ulong kernelTime, out ulong userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
