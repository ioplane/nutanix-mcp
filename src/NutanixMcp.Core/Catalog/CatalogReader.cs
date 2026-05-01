using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NutanixMcp.Core.Catalog;

// Discovers catalog entries on disk by walking `<base>/md/` and pairing each
// `<stem>.md` with the optional `<base>/json/<stem>.json`. The local corpus
// has no central `catalog.json`; this walker is the catalog.
//
// Category, KB number, and other fields come from the JSON when present.
// When the JSON is empty (about a quarter of the local corpus) the reader
// falls back to filename heuristics. Documents without any JSON metadata
// still ingest — they just lose page numbers and structured tags.
public static partial class CatalogReader
{
    [GeneratedRegex(@"^KB-(?<num>\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex KbStemRegex();

    [GeneratedRegex(@"^(?<slug>.+?)(?:-v(?<version>\d+(?:[._]\d+)*))?$")]
    private static partial Regex SlugVersionRegex();

    public sealed record ReadOptions
    {
        public required string CatalogBaseDir { get; init; }
        public string MdSubdir { get; init; } = "md";
        public string JsonSubdir { get; init; } = "json";
    }

    public static IReadOnlyList<CatalogEntry> Read(ReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        string baseDir = Path.GetFullPath(options.CatalogBaseDir);
        string mdDir = Path.Combine(baseDir, options.MdSubdir);
        string jsonDir = Path.Combine(baseDir, options.JsonSubdir);
        if (!Directory.Exists(mdDir))
        {
            throw new DirectoryNotFoundException(
                $"Catalog md directory missing: {mdDir}");
        }

        var entries = new List<CatalogEntry>();
        foreach (string mdPath in Directory.EnumerateFiles(mdDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            string stem = Path.GetFileNameWithoutExtension(mdPath);
            string jsonPath = Path.Combine(jsonDir, $"{stem}.json");
            var entry = BuildEntry(stem, mdPath, jsonPath, options);
            entries.Add(entry);
        }

        entries.Sort(static (a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
        return entries;
    }

    private static CatalogEntry BuildEntry(
        string stem, string mdPath, string jsonPath, ReadOptions options)
    {
        var (title, category, product, version, tags, pages, sectionCount, tocCount,
             imageCount, kbNumber, sourceType) = TryReadJsonMetadata(jsonPath);

        // Filename-heuristic fallback for missing metadata.
        if (string.IsNullOrEmpty(category))
        {
            category = InferCategoryFromStem(stem, sourceType);
        }

        if (string.IsNullOrEmpty(title))
        {
            title = stem;
        }

        if (string.IsNullOrEmpty(version))
        {
            version = InferVersionFromStem(stem);
        }

        long sizeBytes = new FileInfo(mdPath).Length;
        string relMd = Path.Combine(options.MdSubdir, Path.GetFileName(mdPath));
        string relJson = Path.Combine(options.JsonSubdir, Path.GetFileName(jsonPath));

        return new CatalogEntry
        {
            Id = stem,
            Slug = stem,
            Title = title,
            Category = category,
            Product = product,
            Version = version,
            Tags = tags,
            Pages = pages,
            SizeBytes = sizeBytes,
            PdfSha256 = null,
            MdPath = relMd,
            JsonPath = relJson,
            ConvertMode = null,
            ImageCount = imageCount,
            SectionCount = sectionCount,
            Level1SectionCount = 0,
            TocCount = tocCount,
            DownloadedAt = null,
            ConvertedAt = null,
            KbNumber = kbNumber,
        };
    }

    private static (
        string Title, string Category, string? Product, string? Version,
        IReadOnlyList<string> Tags, int? Pages, int SectionCount, int TocCount,
        int ImageCount, int? KbNumber, string? SourceType
    ) TryReadJsonMetadata(string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            return ("", "", null, null, [], null, 0, 0, 0, null, null);
        }

        try
        {
            using var stream = File.OpenRead(jsonPath);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;

            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string category = root.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "";
            string? product = root.TryGetProperty("product", out var p) ? p.GetString() : null;
            string? version = ReadVersionField(root);
            var tags = ParseTags(root);
            int? pages = TryGetInt(root, "num_pages");
            int sectionCount = TryGetInt(root, "section_count")
                ?? CountArrayOrZero(root, "sections");
            int tocCount = TryGetInt(root, "toc_count")
                ?? CountArrayOrZero(root, "toc");
            int imageCount = TryGetInt(root, "image_count") ?? 0;
            int? kbNumber = TryGetInt(root, "kb_number");
            string? sourceType = root.TryGetProperty("source_type", out var st) ? st.GetString() : null;

            return (title, category, product, version?.Length > 0 ? version : null,
                    tags, pages, sectionCount, tocCount, imageCount, kbNumber, sourceType);
        }
        catch (JsonException)
        {
            return ("", "", null, null, [], null, 0, 0, 0, null, null);
        }
    }

    private static IReadOnlyList<string> ParseTags(JsonElement root)
    {
        if (!root.TryGetProperty("tags", out var tagsEl))
        {
            return [];
        }

        return tagsEl.ValueKind switch
        {
            JsonValueKind.Array =>
                tagsEl.EnumerateArray()
                    .Select(static x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                    .Where(static x => !string.IsNullOrEmpty(x))
                    .Cast<string>()
                    .ToList(),
            JsonValueKind.String => SplitTagsString(tagsEl.GetString()),
            _ => [],
        };
    }

    private static string[] SplitTagsString(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? ReadVersionField(JsonElement root)
    {
        if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
        {
            return v.GetString();
        }

        if (root.TryGetProperty("versions_affected", out var va) && va.ValueKind == JsonValueKind.String)
        {
            return va.GetString();
        }

        return null;
    }

    private static int? TryGetInt(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var el))
        {
            return null;
        }

        return el.ValueKind == JsonValueKind.Number ? el.GetInt32() : null;
    }

    private static int CountArrayOrZero(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var el))
        {
            return 0;
        }

        return el.ValueKind == JsonValueKind.Array ? el.GetArrayLength() : 0;
    }

    private static string InferCategoryFromStem(string stem, string? sourceType)
    {
        if (KbStemRegex().IsMatch(stem))
        {
            return "kb";
        }

        if (!string.IsNullOrEmpty(sourceType))
        {
            return sourceType.ToLowerInvariant();
        }

        if (stem.StartsWith("bible-", StringComparison.OrdinalIgnoreCase))
        {
            return "bible";
        }

        if (stem.Contains("Replacement", StringComparison.OrdinalIgnoreCase))
        {
            return "hardware-replacement";
        }

        if (stem.StartsWith("AHV-", StringComparison.OrdinalIgnoreCase))
        {
            return "ahv";
        }

        if (stem.StartsWith("Prism-Central", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("PC-", StringComparison.OrdinalIgnoreCase))
        {
            return "prism-central";
        }

        if (stem.StartsWith("Prism-Element", StringComparison.OrdinalIgnoreCase))
        {
            return "prism-element";
        }

        if (stem.StartsWith("Files-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("AFS-", StringComparison.OrdinalIgnoreCase))
        {
            return "files";
        }

        if (stem.StartsWith("NDB-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("Era-", StringComparison.OrdinalIgnoreCase))
        {
            return "ndb";
        }

        if (stem.StartsWith("Move-", StringComparison.OrdinalIgnoreCase))
        {
            return "move";
        }

        if (stem.StartsWith("LCM-", StringComparison.OrdinalIgnoreCase))
        {
            return "lcm";
        }

        if (stem.StartsWith("API-", StringComparison.OrdinalIgnoreCase))
        {
            return "api";
        }

        if (stem.StartsWith("Acropolis-", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("Advanced-Admin-AOS", StringComparison.OrdinalIgnoreCase)
            || stem.StartsWith("AOS-", StringComparison.OrdinalIgnoreCase))
        {
            return "aos";
        }

        return "other";
    }

    private static string? InferVersionFromStem(string stem)
    {
        var m = SlugVersionRegex().Match(stem);
        if (!m.Success || !m.Groups["version"].Success)
        {
            return null;
        }

        return m.Groups["version"].Value.Replace('_', '.');
    }
}
