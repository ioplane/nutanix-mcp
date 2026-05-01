using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using NutanixMcp.Core.Chunking;
using NutanixMcp.Core.Models;

namespace NutanixMcp.Core.Catalog;

// Loads a converted MD/JSON pair into a `LoadedDocument`. The MD file supplies
// section bodies (split on ATX headings); the per-doc `<stem>.json` supplies
// page spans per section when enriched. Marker page-break tokens are stripped
// before sectioning, empty image references are dropped, the table-of-
// contents L2 section is removed when present.
//
// Heading titles are matched between MD and JSON via the same `CleanHeading`
// pass — strip emphasis markers and inline HTML, collapse whitespace.
public static partial class DocumentLoader
{
    [GeneratedRegex(@"\{\d+\}-+", RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex PageMarkerRegex();

    [GeneratedRegex(
        @"^!\[\]\(_page_\d+_(?:Picture|Figure|Image)_\d+\.[a-z]+\)$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex EmptyImageRegex();

    [GeneratedRegex(
        @"^(?<level>\#{1,6})[ \t]+(?<title>[^\r\n]+)$",
        RegexOptions.Multiline | RegexOptions.ExplicitCapture)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.ExplicitCapture)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\*\*(?<inner>[^*]+)\*\*", RegexOptions.ExplicitCapture)]
    private static partial Regex BoldRegex();

    [GeneratedRegex(@"(?<!\*)\*(?<inner>[^*]+)\*(?!\*)", RegexOptions.ExplicitCapture)]
    private static partial Regex ItalicRegex();

    [GeneratedRegex(@"(?<!_)_(?<inner>[^_]+)_(?!_)", RegexOptions.ExplicitCapture)]
    private static partial Regex UnderscoreRegex();

    [GeneratedRegex(@"\s+", RegexOptions.ExplicitCapture)]
    private static partial Regex WhitespaceRegex();

    private static readonly HashSet<string> TocTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Contents",
        "Table of Contents",
        "Содержание",
    };

    public static async Task<LoadedDocument> LoadAsync(
        CatalogEntry entry,
        string catalogBaseDir,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrEmpty(catalogBaseDir);

        var baseFull = Path.GetFullPath(catalogBaseDir);
        var mdFull = ResolveInside(baseFull, entry.MdPath, nameof(entry.MdPath));
        if (!File.Exists(mdFull))
        {
            throw new FileNotFoundException($"MD file missing for doc {entry.Id}", mdFull);
        }

        var md = await File.ReadAllTextAsync(mdFull, ct).ConfigureAwait(false);

        var jsonFull = ResolveInside(baseFull, entry.JsonPath, nameof(entry.JsonPath));
        var enriched = await TryLoadEnrichedJsonAsync(jsonFull, ct).ConfigureAwait(false);

        var mdSections = ExtractSectionsFromMarkdown(md, fallbackTitle: entry.Title);
        IReadOnlyList<JsonSection> jsonSections;
        if (enriched is null)
        {
            jsonSections = [];
        }
        else if (enriched.Toc.Count > 0)
        {
            jsonSections = enriched.Toc;
        }
        else
        {
            jsonSections = enriched.Sections;
        }
        var sections = jsonSections.Count == 0
            ? mdSections
            : StampPagesFromJson(mdSections, jsonSections);

        var metadata = new NutanixDocument
        {
            Id = entry.Id,
            Url = entry.Url ?? "",
            Category = entry.Category,
            Product = entry.Product,
            Version = entry.Version,
            Title = entry.Title,
            Slug = entry.Slug,
            Tags = entry.Tags,
            Pages = entry.Pages,
            SizeBytes = entry.SizeBytes,
            PdfSha256 = entry.PdfSha256,
            MdPath = entry.MdPath,
            JsonPath = entry.JsonPath,
            ConvertMode = entry.ConvertMode,
            ImageCount = entry.ImageCount,
            SectionCount = entry.SectionCount,
            Level1SectionCount = entry.Level1SectionCount,
            TocCount = entry.TocCount,
            DownloadedAt = entry.DownloadedAt,
            ConvertedAt = entry.ConvertedAt,
            KbNumber = entry.KbNumber,
        };

        return new LoadedDocument
        {
            Metadata = metadata,
            Sections = sections,
        };
    }

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    // Resolves a catalog-relative path against `baseFull` and refuses any
    // traversal that lands outside the base directory. Catalog entries are
    // typically authored, but the loader is reachable from CLI input so we
    // treat the relative path as untrusted.
    private static string ResolveInside(string baseFull, string relative, string argumentName)
    {
        if (string.IsNullOrEmpty(relative))
        {
            throw new ArgumentException($"{argumentName} must not be empty", argumentName);
        }

        var normalised = NormalizePath(relative);
        if (Path.IsPathRooted(normalised))
        {
            throw new InvalidOperationException(
                $"{argumentName} must be relative to the catalog base; got '{relative}'.");
        }

        var combined = Path.GetFullPath(Path.Combine(baseFull, normalised));
        var baseWithSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;
        if (!combined.StartsWith(baseWithSep, StringComparison.Ordinal)
            && !string.Equals(combined, baseFull, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{argumentName} '{relative}' escapes the catalog base directory.");
        }

        return combined;
    }

    public static IReadOnlyList<Section> ExtractSectionsFromMarkdown(string md, string fallbackTitle)
    {
        // Normalise CRLF to LF first. .NET regex `$` in Multiline mode matches
        // only before `\n`, never at `\r`, so on Windows-written MD files
        // (81 % of the local corpus on the 2026-05-01 snapshot) the heading
        // regex `[^\r\n]+$` would otherwise fail and the whole document would
        // collapse into a single fallback-titled section.
        var lf = md.Replace("\r\n", "\n", StringComparison.Ordinal)
                   .Replace("\r", "\n", StringComparison.Ordinal);

        // Strip marker page-break tokens so they do not bleed into section
        // bodies. About a quarter of the local corpus carries them.
        var noPageMarkers = PageMarkerRegex().Replace(lf, string.Empty);

        // Strip empty marker image references — they have no semantic content
        // and pollute embeddings.
        var cleaned = EmptyImageRegex().Replace(noPageMarkers, string.Empty);

        var matches = HeadingRegex().Matches(cleaned);
        if (matches.Count == 0)
        {
            var body = cleaned.Trim();
            return body.Length == 0
                ? []
                : [new Section { Title = fallbackTitle, Level = 1, Content = body }];
        }

        var sections = new List<Section>(matches.Count);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var level = (short)m.Groups["level"].Value.Length;
            var title = m.Groups["title"].Value.Trim();

            var bodyStart = m.Index + m.Length;
            var bodyEnd = i + 1 < matches.Count ? matches[i + 1].Index : cleaned.Length;
            var body = cleaned[bodyStart..bodyEnd].Trim();

            if (body.Length == 0)
            {
                continue;
            }

            sections.Add(new Section { Title = title, Level = level, Content = body });
        }

        // Drop the table-of-contents section when it appears as the first
        // level-2 heading directly under the document's level-1 title. The
        // TOC body is a pipe-delimited table that does not chunk meaningfully.
        sections = DropTocSection(sections);

        if (sections.Count == 0)
        {
            var body = cleaned.Trim();
            return body.Length == 0
                ? []
                : [new Section { Title = fallbackTitle, Level = 1, Content = body }];
        }

        return sections;
    }

    private static List<Section> DropTocSection(List<Section> sections)
    {
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Level == 2 && TocTitles.Contains(CleanHeading(s.Title)))
            {
                sections.RemoveAt(i);
                return sections;
            }

            // The TOC sits among the very first sections. Stop scanning once
            // a real content L2 (or deeper) heading is seen.
            if (s.Level >= 2)
            {
                return sections;
            }
        }

        return sections;
    }

    // Mirrors the converter's `_clean_heading` normalisation: strip emphasis
    // markers, inline HTML, and collapse whitespace.
    public static string CleanHeading(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var s = raw;
        s = HtmlTagRegex().Replace(s, string.Empty);
        s = BoldRegex().Replace(s, "${inner}");
        s = ItalicRegex().Replace(s, "${inner}");
        s = UnderscoreRegex().Replace(s, "${inner}");
        s = WhitespaceRegex().Replace(s, " ").Trim();
        return s;
    }

    // Pairs MD-derived sections to JSON-derived sections by cleaned title in
    // sequence with a small lookahead. LEVEL IS IGNORED: the converter's
    // enrichment pass emits flat level-1 entries while the MD walker keeps
    // real `#`/`##`/`###` depth (MD L3 to JSON L1 is common).
    //
    // The forward scan with lookahead handles a handful of extra MD or JSON
    // headings without losing alignment for the rest of the document.
    public static IReadOnlyList<Section> StampPagesFromJson(
        IReadOnlyList<Section> mdSections,
        IReadOnlyList<JsonSection> jsonSections)
    {
        ArgumentNullException.ThrowIfNull(mdSections);
        ArgumentNullException.ThrowIfNull(jsonSections);

        if (mdSections.Count == 0 || jsonSections.Count == 0)
        {
            return mdSections;
        }

        var jsonCleaned = new (string Cleaned, JsonSection Section)[jsonSections.Count];
        for (var i = 0; i < jsonSections.Count; i++)
        {
            jsonCleaned[i] = (CleanHeading(jsonSections[i].Title), jsonSections[i]);
        }

        const int Lookahead = 3;
        var result = new List<Section>(mdSections.Count);
        var jsonIdx = 0;

        foreach (var md in mdSections)
        {
            var mdClean = CleanHeading(md.Title);
            var matchAt = -1;
            var bound = Math.Min(jsonCleaned.Length, jsonIdx + Lookahead + 1);
            for (var probe = jsonIdx; probe < bound; probe++)
            {
                if (string.Equals(jsonCleaned[probe].Cleaned, mdClean, StringComparison.Ordinal))
                {
                    matchAt = probe;
                    break;
                }
            }

            if (matchAt >= 0)
            {
                var js = jsonCleaned[matchAt].Section;
                result.Add(md with { PageStart = js.PageStart, PageEnd = js.PageEnd });
                jsonIdx = matchAt + 1;
            }
            else
            {
                // Extra MD heading the JSON side does not know about. Leave
                // null-paged and try to re-sync on the next MD heading
                // against the same cursor.
                result.Add(md);
            }
        }

        return result;
    }

    private static async Task<EnrichedDocumentJson?> TryLoadEnrichedJsonAsync(
        string jsonFull,
        CancellationToken ct)
    {
        if (!File.Exists(jsonFull))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(jsonFull);
            // The converter shape is loose: `sections` is sometimes a list of
            // strings, sometimes a list of `{title, page}` objects, sometimes
            // empty. Read into an intermediate `JsonElement` and project to
            // a strict shape.
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            return ProjectEnrichedJson(doc.RootElement);
        }
        catch (JsonException)
        {
            // Corrupt or partial JSON — fall back to MD-only behaviour
            // rather than abort the whole ingest.
            return null;
        }
    }

    private static EnrichedDocumentJson ProjectEnrichedJson(JsonElement root)
    {
        var sections = ProjectSectionsArray(root, "sections");
        var toc = ProjectSectionsArray(root, "toc");
        return new EnrichedDocumentJson { Sections = sections, Toc = toc };
    }

    private static List<JsonSection> ProjectSectionsArray(JsonElement root, string field)
    {
        if (!root.TryGetProperty(field, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<JsonSection>(arr.GetArrayLength());
        foreach (var item in arr.EnumerateArray())
        {
            switch (item.ValueKind)
            {
                case JsonValueKind.String:
                    var title = item.GetString() ?? "";
                    if (title.Length > 0)
                    {
                        result.Add(new JsonSection(title, null, null));
                    }

                    break;
                case JsonValueKind.Object:
                    var t = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                    if (t.Length == 0) continue;
                    int? page = item.TryGetProperty("page", out var pEl) && pEl.ValueKind == JsonValueKind.Number
                        ? pEl.GetInt32()
                        : null;
                    int? pageStart = item.TryGetProperty("page_start", out var psEl) && psEl.ValueKind == JsonValueKind.Number
                        ? psEl.GetInt32()
                        : page;
                    int? pageEnd = item.TryGetProperty("page_end", out var peEl) && peEl.ValueKind == JsonValueKind.Number
                        ? peEl.GetInt32()
                        : pageStart;
                    result.Add(new JsonSection(t, pageStart, pageEnd));
                    break;
                default:
                    break;
            }
        }

        return result;
    }
}
