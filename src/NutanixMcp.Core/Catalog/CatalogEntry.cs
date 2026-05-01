namespace NutanixMcp.Core.Catalog;

// One row of the on-disk catalog. The reader synthesises this from the
// `data/converted/md/<stem>.md` plus optional `data/converted/json/<stem>.json`
// pair on the developer machine. Optional fields are best-effort: the JSON may
// be empty (about a quarter of the corpus on the 2026-05-01 snapshot), in
// which case page numbers and structured tags are missing and the loader
// falls back to MD-only behaviour.
public sealed record CatalogEntry
{
    public required string Id { get; init; }
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public required string Category { get; init; }
    public string? Product { get; init; }
    public string? Version { get; init; }
    public string? Url { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public int? Pages { get; init; }
    public long? SizeBytes { get; init; }
    public string? PdfSha256 { get; init; }
    public required string MdPath { get; init; }
    public required string JsonPath { get; init; }
    public string? ConvertMode { get; init; }
    public int ImageCount { get; init; }
    public int SectionCount { get; init; }
    public int Level1SectionCount { get; init; }
    public int TocCount { get; init; }
    public DateTimeOffset? DownloadedAt { get; init; }
    public DateTimeOffset? ConvertedAt { get; init; }
    public int? KbNumber { get; init; }
}
