using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace NutanixMcp.Embedding.Tests;

public sealed partial class OnnxTelemetryTest
{
    [Fact]
    public void DisableForProcess_SetsOrtDisableTelemetryInNativeEnvironment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows telemetry is switched off through the OrtEnv API");

        // An operator-set value (e.g. the documented opt-in ORT_DISABLE_TELEMETRY=0) is
        // inherited at start-up, so the managed copy still shows it, and it must survive.
        string expected = Environment.GetEnvironmentVariable("ORT_DISABLE_TELEMETRY") ?? "1";

        OnnxTelemetry.DisableForProcess();

        // The managed Environment API does not see native setenv on Unix, so read it natively too.
        Marshal.PtrToStringUTF8(GetEnv("ORT_DISABLE_TELEMETRY")).Should().Be(expected);
    }

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetEnv(string name);
}
