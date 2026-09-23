using System.Management;
using System.Runtime.InteropServices;
using ExtormSub.Core.ASR;
using Microsoft.Extensions.Logging;

namespace ExtormSub.Infrastructure.ASR;

public static class HardwareDetector
{
    private static HardwareInfo? _cached;

    /// <summary>Probes CPU, GPUs (WMI) and the Vulkan/CUDA drivers once; never throws.</summary>
    public static HardwareInfo Detect(ILogger? log = null)
    {
        if (_cached is not null) return _cached;

        string cpu = "Unknown CPU";
        int physical = Math.Max(1, Environment.ProcessorCount / 2);
        long memory = 0;
        var gpus = new List<GpuInfo>();
        try
        {
            using (var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores FROM Win32_Processor"))
                foreach (var o in s.Get())
                {
                    cpu = o["Name"]?.ToString()?.Trim() ?? cpu;
                    physical = Convert.ToInt32(o["NumberOfCores"] ?? physical);
                }
            using (var s = new ManagementObjectSearcher("SELECT TotalPhysicalMemory FROM Win32_ComputerSystem"))
                foreach (var o in s.Get())
                    memory = Convert.ToInt64(o["TotalPhysicalMemory"] ?? 0L);
            using (var s = new ManagementObjectSearcher("SELECT Name, AdapterRAM FROM Win32_VideoController"))
                foreach (var o in s.Get())
                {
                    var name = o["Name"]?.ToString() ?? "";
                    if (name.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Remote", StringComparison.OrdinalIgnoreCase)) continue;
                    // AdapterRAM is a uint32 in WMI and saturates at 4 GB; good enough to rank adapters.
                    long vram = Convert.ToInt64(o["AdapterRAM"] ?? 0L);
                    gpus.Add(new GpuInfo(name, VendorOf(name), vram));
                }
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Hardware probe via WMI failed; assuming CPU only");
        }

        bool vulkan = CanLoad("vulkan-1.dll");
        bool cuda = CanLoad("nvcuda.dll");
        _cached = new HardwareInfo(cpu, physical, Environment.ProcessorCount, memory, gpus, vulkan, cuda);
        log?.LogInformation("Hardware: {Cpu} ({Cores} cores), GPUs: [{Gpus}], Vulkan {Vulkan}, CUDA driver {Cuda}",
            cpu, physical, string.Join("; ", gpus.Select(g => g.Name)), vulkan, cuda);
        return _cached;
    }

    private static GpuVendor VendorOf(string name) => name.ToUpperInvariant() switch
    {
        var n when n.Contains("NVIDIA") || n.Contains("GEFORCE") || n.Contains("QUADRO") || n.Contains("RTX") => GpuVendor.Nvidia,
        var n when n.Contains("AMD") || n.Contains("RADEON") => GpuVendor.Amd,
        var n when n.Contains("INTEL") => GpuVendor.Intel,
        _ => GpuVendor.Other,
    };

    private static bool CanLoad(string library)
    {
        if (!NativeLibrary.TryLoad(library, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }
}
