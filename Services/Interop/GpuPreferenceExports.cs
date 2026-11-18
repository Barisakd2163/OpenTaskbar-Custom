using System.Runtime.InteropServices;

namespace OpenTaskbar.Services;

/// <summary>
/// Exports standard GPU preference symbols recognized by NVIDIA and AMD display drivers.
/// Returning 0 signals to hybrid-GPU laptops to prefer the integrated power-saving GPU,
/// preventing the discrete 3D GPU driver from injecting ~150 MB of shader compilers into a 2D taskbar.
/// </summary>
public static class GpuPreferenceExports
{
    [UnmanagedCallersOnly(EntryPoint = "NvOptimusEnablement")]
    public static uint NvOptimusEnablement() => 0;

    [UnmanagedCallersOnly(EntryPoint = "AmdPowerXpressRequestHighPerformance")]
    public static int AmdPowerXpressRequestHighPerformance() => 0;
}
