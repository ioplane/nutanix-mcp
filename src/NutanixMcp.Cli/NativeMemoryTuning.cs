using System.Runtime.InteropServices;

namespace NutanixMcp.Cli;

// glibc raises its mmap threshold (up to 32 MiB) every time a large mmap'ed block is
// freed, so after the first model load the 8-16 MiB ONNX weight tensors land in heap
// arenas and stay resident once the session is disposed (measured: ~1.4 GB kept after an
// idle unload of both models). Pinning the threshold keeps every block >= 128 KiB on
// mmap, so disposing a model hands its memory back to the OS (measured: ~250 MB idle).
// Same effect as MALLOC_MMAP_THRESHOLD_=131072 without relying on every unit file and
// MCP client config to export it. Must run before the first large allocation.
public static partial class NativeMemoryTuning
{
    private const int MmapThresholdParam = -3;          // M_MMAP_THRESHOLD, glibc <malloc.h>
    private const int MmapThresholdBytes = 128 * 1024;  // glibc's own initial threshold

    public static bool TryUseFixedMmapThreshold()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return Mallopt(MmapThresholdParam, MmapThresholdBytes) == 1;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Non-glibc libc (musl): no mallopt tuning, keep the allocator defaults.
            return false;
        }
    }

    [LibraryImport("libc", EntryPoint = "mallopt")]
    private static partial int Mallopt(int param, int value);
}
