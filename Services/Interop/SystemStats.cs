using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

public readonly record struct BatteryStatus(bool Present, int Percent, bool Charging, bool PluggedIn);

public readonly record struct MemorySnapshot(double UsageRatio, ulong UsedBytes, ulong TotalBytes, ulong AvailableBytes);

public readonly record struct ProcessMemoryEntry(string Name, long WorkingSetBytes, int InstanceCount);

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

    public static double? SampleMemory() => SampleMemorySnapshot()?.UsageRatio;

    public static MemorySnapshot? SampleMemorySnapshot()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        var used = status.TotalPhys >= status.AvailPhys ? status.TotalPhys - status.AvailPhys : 0;
        return new MemorySnapshot(
            Math.Clamp(status.MemoryLoad / 100.0, 0, 1),
            used,
            status.TotalPhys,
            status.AvailPhys);
    }

    public static TimeSpan GetSystemUptime() =>
        TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64));

    /// <summary>
    /// Aggregates running processes by name and returns the top entries sorted by working set memory.
    /// Intended for off-UI-thread calls only while the telemetry flyout is open; disposes every process handle.
    /// </summary>
    public static IReadOnlyList<ProcessMemoryEntry> SampleTopProcesses(int maxCount = 5, CancellationToken cancellationToken = default)
    {
        if (maxCount <= 0 || cancellationToken.IsCancellationRequested) return [];

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            AppLog.Warning($"Top process enumeration failed: {ex.Message}");
            return [];
        }

        try
        {
            var grouped = new Dictionary<string, (string DisplayName, long WorkingSet, int Count)>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < processes.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested) return [];

                var process = processes[i];
                try
                {
                    if (process.Id == 0) continue;
                    var name = process.ProcessName;
                    if (string.IsNullOrWhiteSpace(name) || string.Equals(name, "Idle", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var workingSet = process.WorkingSet64;
                    if (workingSet <= 0) continue;

                    if (grouped.TryGetValue(name, out var existing))
                        grouped[name] = (existing.DisplayName, existing.WorkingSet + workingSet, existing.Count + 1);
                    else
                        grouped[name] = (name, workingSet, 1);
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Process exited or became inaccessible between snapshot and inspection.
                }
            }

            if (cancellationToken.IsCancellationRequested || grouped.Count == 0) return [];

            return grouped.Values
                .OrderByDescending(entry => entry.WorkingSet)
                .Take(maxCount)
                .Select(entry => new ProcessMemoryEntry(entry.DisplayName, entry.WorkingSet, entry.Count))
                .ToArray();
        }
        finally
        {
            for (var i = 0; i < processes.Length; i++)
            {
                try { processes[i].Dispose(); } catch { }
            }
        }
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
