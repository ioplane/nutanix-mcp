using System.IO;
using System.Text;
using System.Text.Json;

namespace NutanixMcp.Core.Bench;

// Append-only JSONL history writer. One BenchRun per line. Ensures the
// parent directory exists before the first write.
public static class BenchHistoryWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task AppendAsync(string path, BenchRun run, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(run);

        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var json = JsonSerializer.Serialize(run, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json + "\n");
        await using var stream = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
    }
}
