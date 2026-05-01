using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;

namespace NutanixMcp.Core.Bench;

// Append-only JSONL history writer. One BenchRun per line. Ensures the
// parent directory exists before the first write.
//
// Concurrency: a per-path SemaphoreSlim guards the append so two simultaneous
// callers in the same process do not interleave bytes mid-line. FileShare is
// `ReadWrite` so a sibling reader can tail the history file while bench is
// running without blocking the writer.
public static class BenchHistoryWriter
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> s_locks =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task AppendAsync(string path, BenchRun run, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(run);

        string fullPath = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(run, s_jsonOptions);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");

        var gate = s_locks.GetOrAdd(fullPath, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var stream = new FileStream(
                fullPath,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite);
            await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }
}
