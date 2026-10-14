using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;

namespace NutanixMcp.Embedding;

// ONNX Runtime 1.31 native builds default to 1DS telemetry that uploads to
// mobile.events.data.microsoft.com (ORT 1.29 / 1.31 release notes; endpoint string in
// libonnxruntime.so). ORT_DISABLE_TELEMETRY=1 switches it off for the process, but only
// if it is in the native environment before ORT initializes — on Unix,
// Environment.SetEnvironmentVariable does not reach native libraries, hence libc setenv.
// An explicit ORT_DISABLE_TELEMETRY already set by the operator is left as is.
public static partial class OnnxTelemetry
{
    private static int s_disabled;

    public static void DisableForProcess()
    {
        if (Interlocked.Exchange(ref s_disabled, 1) == 1)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows uses TraceLogging, controlled through the environment object instead.
            OrtEnv.Instance().DisableTelemetryEvents();
            return;
        }

        try
        {
            _ = SetEnv("ORT_DISABLE_TELEMETRY", "1", overwrite: 0);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) //-V3163 intentional: narrow best-effort handler
        {
            // No libc setenv available: nothing else can switch it off before initialization.
        }
    }

    [LibraryImport("libc", EntryPoint = "setenv", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int SetEnv(string name, string value, int overwrite);
}
