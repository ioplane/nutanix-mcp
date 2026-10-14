using System;
using FluentAssertions;
using Xunit;

namespace NutanixMcp.Cli.Tests;

public sealed class NativeMemoryTuningTests
{
    [SkippableFact]
    public void FixedMmapThreshold_IsAcceptedByGlibc()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "glibc mallopt is Linux-only");

        // mallopt returns 0 for an unknown parameter or a value glibc rejects, so this
        // fails on a wrong constant or entry point, not only on a missing libc.
        NativeMemoryTuning.TryUseFixedMmapThreshold().Should().BeTrue();
    }
}
