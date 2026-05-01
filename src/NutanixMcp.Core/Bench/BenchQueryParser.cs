using System.IO;
using System.Text.Json;

namespace NutanixMcp.Core.Bench;

// Reads bench-queries.json and validates each row. Bails out on any
// missing required field rather than silently dropping bad rows — a
// silent drop is exactly the failure mode that hid arista's three v1
// regressions.
public static class BenchQueryParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<IReadOnlyList<BenchQuery>> LoadAsync(string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        await using var stream = File.OpenRead(path);
        var file = await JsonSerializer
            .DeserializeAsync<BenchQueryFile>(stream, JsonOptions, ct)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"bench query file is empty: {path}");

        Validate(file);
        return file.Queries;
    }

    private static void Validate(BenchQueryFile file)
    {
        if (file.Queries.Count == 0)
        {
            throw new InvalidDataException("bench query file contains zero queries");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < file.Queries.Count; i++)
        {
            var q = file.Queries[i];
            if (string.IsNullOrWhiteSpace(q.QueryId))
            {
                throw new InvalidDataException($"query at index {i} has empty query_id");
            }

            if (!seen.Add(q.QueryId))
            {
                throw new InvalidDataException($"duplicate query_id: '{q.QueryId}'");
            }

            if (string.IsNullOrWhiteSpace(q.Query))
            {
                throw new InvalidDataException($"query '{q.QueryId}' has empty query text");
            }

            if (q.ExpectChunkIds.Count == 0)
            {
                throw new InvalidDataException($"query '{q.QueryId}' has empty expect_chunk_ids");
            }
        }
    }
}
