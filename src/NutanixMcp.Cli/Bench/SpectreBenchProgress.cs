using System.Globalization;
using Spectre.Console;

namespace NutanixMcp.Cli.Bench;

public sealed class SpectreBenchProgress(IAnsiConsole console) : IBenchProgress
{
    // Plain-text widths for the rank marker, so column alignment matches what
    // the user actually sees. Spectre format width specifiers count markup
    // characters, not rendered characters, so we have to widen by hand.
    private const int MarkerColumnWidth = 12;

    private readonly IAnsiConsole _console = console ?? throw new ArgumentNullException(nameof(console));
    private int _seen;
    private int _total;

    public void Start(int total)
    {
        _seen = 0;
        _total = total;
        _console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"[grey]bench start[/]: {total} queries");
    }

    public void Query(string queryId, int firstHitRank, double latencyMs)
    {
        _seen++;
        var (rendered, plainLength) = firstHitRank switch
        {
            0 => ("[red]miss[/]", 4),
            1 => ("[green]hit@1[/]", 5),
            <= 5 => ("[green]hit@5[/]", 5),
            <= 10 => ("[yellow]hit@10[/]", 6),
            _ => ("[grey]rank>10[/]", 7),
        };
        string marker = rendered + new string(' ', Math.Max(0, MarkerColumnWidth - plainLength));
        // queryId can contain `[` (`ahv-[v11]-001`); Markup.Escape neutralises
        // any literal that Spectre would otherwise misread as a markup tag.
        string safeId = Markup.Escape(queryId);
        _console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"  ({_seen,4}/{_total,-4}) {marker} {safeId} [grey]({latencyMs,7:F0} ms)[/]");
    }

    public void Finish()
    {
        _console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"[bold]bench done[/]: {_seen} queries");
    }
}
