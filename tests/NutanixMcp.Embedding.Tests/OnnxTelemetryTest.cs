using System.Runtime.InteropServices;
using FluentAssertions;
using Xunit;

namespace NutanixMcp.Embedding.Tests;

public sealed partial class OnnxTelemetryTest
{
    [SkippableFact]
    public void DisableForProcess_SetsOrtDisableTelemetryInNativeEnvironment()
    {
        Skip.If(OperatingSystem.IsWindows(), "Windows telemetry is switched off through the OrtEnv API");

        OnnxTelemetry.DisableForProcess();

        // The managed Environment API does not see native setenv on Unix, so read it natively too.
        Marshal.PtrToStringUTF8(GetEnv("ORT_DISABLE_TELEMETRY")).Should().Be("1");
    }

    [LibraryImport("libc", EntryPoint = "getenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr GetEnv(string name);
}
