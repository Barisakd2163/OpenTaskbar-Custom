using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenTaskbar.Services;

/// <summary>
/// Optional hardware readings for the system meters. Every reading returns null when the machine does not expose
/// it: GPU usage comes from the Windows GPU Engine counters, CPU temperature from the ACPI thermal zone counters,
/// and GPU temperature from the NVIDIA driver (NVML). No administrator rights or kernel drivers are used.
/// </summary>
public sealed partial class SystemSensors : IDisposable
{
    private const double KelvinOffset = 273.15;
    private const double MaxPlausibleCelsius = 150;

    private readonly PdhCounterArray? _gpuEngines = PdhCounterArray.TryCreate(@"\GPU Engine(*)\Utilization Percentage");
    private readonly PdhCounterArray? _thermalZones = PdhCounterArray.TryCreate(@"\Thermal Zone Information(*)\Temperature");
    private readonly bool _nvmlReady = Nvml.TryInitialize();

    /// <summary>Busiest engine of the busiest GPU (0–1), matching how Task Manager reports GPU usage.</summary>
    public double? SampleGpuUsage()
    {
        if (_gpuEngines?.Read() is not { } samples) return null;
        var busiestEngine = samples
            .Select(sample => (Match: EngineInstance().Match(sample.Instance), sample.Value))
            .Where(item => item.Match.Success)
            .GroupBy(item => (Gpu: item.Match.Groups["luid"].Value, Engine: item.Match.Groups["engine"].Value))
            .Select(group => (group.Key.Gpu, Load: group.Sum(item => item.Value)));
        var busiestGpu = busiestEngine.GroupBy(item => item.Gpu).Select(group => group.Max(item => item.Load)).ToList();
        return busiestGpu.Count == 0 ? null : Math.Clamp(busiestGpu.Max() / 100, 0, 1);
    }

    /// <summary>Hottest ACPI thermal zone in °C. Many desktops do not expose any zone.</summary>
    public double? SampleCpuTemperature()
    {
        if (_thermalZones?.Read() is not { } zones) return null;
        var celsius = zones.Select(zone => zone.Value - KelvinOffset).Where(value => value is > 0 and < MaxPlausibleCelsius).ToList();
        return celsius.Count == 0 ? null : celsius.Max();
    }

    /// <summary>Hottest NVIDIA GPU in °C; null for other vendors or when the NVIDIA driver is missing.</summary>
    public double? SampleGpuTemperature() => _nvmlReady ? Nvml.HottestGpu() : null;

    public void Dispose()
    {
        _gpuEngines?.Dispose();
        _thermalZones?.Dispose();
        if (_nvmlReady) Nvml.Shutdown();
    }

    // Instance names look like pid_1234_luid_0x00000000_0x0000F3A1_phys_0_eng_0_engtype_3D.
    [GeneratedRegex(@"_luid_(?<luid>.+?)_phys_\d+_eng_\d+_engtype_(?<engine>.+)$")]
    private static partial Regex EngineInstance();

    private static class Nvml
    {
        public static bool TryInitialize()
        {
            try
            {
                return nvmlInit_v2() == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                AppLog.Info($"NVIDIA management library unavailable: {ex.Message}");
                return false;
            }
        }

        public static void Shutdown() => nvmlShutdown();

        public static double? HottestGpu()
        {
            if (nvmlDeviceGetCount_v2(out var count) != 0) return null;
            double? hottest = null;
            for (uint index = 0; index < count; index++)
            {
                if (nvmlDeviceGetHandleByIndex_v2(index, out var device) != 0
                    || nvmlDeviceGetTemperature(device, 0, out var temperature) != 0) continue;
                hottest = Math.Max(hottest ?? 0, temperature);
            }

            return hottest;
        }

        // Loaded from System32 only so a library next to the executable cannot replace the driver's.
        [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlInit_v2();

        [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlShutdown();

        [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlDeviceGetCount_v2(out uint count);

        [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);

        [DllImport("nvml.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int nvmlDeviceGetTemperature(nint device, int sensor, out uint temperature);
    }
}
