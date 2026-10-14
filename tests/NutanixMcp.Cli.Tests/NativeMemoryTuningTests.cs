using System;
using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace NutanixMcp.Cli.Tests;

public sealed class NativeMemoryTuningTests
{
    [Fact]
    public void FixedMmapThreshold_IsAcceptedByGlibc()
    {
        Assert.SkipUnless(IsGlibc(), "mallopt tuning applies to glibc only (musl keeps allocator defaults)");

        // mallopt returns 0 for an unknown parameter or a value glibc rejects, so this
        // fails on a wrong constant or entry point, not only on a missing libc.
        NativeMemoryTuning.TryUseFixedMmapThreshold().Should().BeTrue();
    }

    private static bool IsGlibc() =>
        OperatingSystem.IsLinux()
        && NativeLibrary.TryLoad("libc.so.6", out IntPtr libc)
        && NativeLibrary.TryGetExport(libc, "gnu_get_libc_version", out _);
}
