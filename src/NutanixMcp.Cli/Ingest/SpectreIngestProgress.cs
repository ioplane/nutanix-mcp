using System.Globalization;
using Spectre.Console;

namespace NutanixMcp.Cli.Ingest;

// IIngestProgress that prints Spectre.Console-styled status as documents
// flow through the pipeline. Kept simple — no live ProgressTask because the
// per-document latency varies wildly (KB articles take 50 ms, big admin
// guides take 30 s) and a flat status stream renders better in CI logs.
public sealed class SpectreIngestProgress(IAnsiConsole console) : IIngestProgress
{
    private int _seen;
    private int _total;

    public void Start(int total)
    {
        _total = total;
        _seen = 0;
        console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"[grey]ingest start[/]: {total} documents");
    }

    public void Document(string docId, string status, int chunkCount)
    {
        _seen++;
        string marker = status switch
        {
            "ok" => "[green]ok[/]",
            "skip" => "[yellow]skip[/]",
            "no-leaves" => "[yellow]no-leaves[/]",
            "dry-run" => "[blue]dry-run[/]",
            _ => $"[red]{Markup.Escape(status)}[/]",
        };
        // Spectre.Console treats `[…]` as a markup tag, so the running count
        // uses parentheses instead. The status marker, e.g. `[green]ok[/]`,
        // is the only place where Spectre markup is intentional.
        console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"  ({_seen,5}/{_total,-5}) {marker,-12} {docId} [grey]({chunkCount} leaves)[/]");
    }

    public void Finish(int succeeded, int skipped, int failed)
    {
        console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"[bold]ingest done[/]: ok=[green]{succeeded}[/] skip=[yellow]{skipped}[/] fail=[red]{failed}[/]");
    }
}
