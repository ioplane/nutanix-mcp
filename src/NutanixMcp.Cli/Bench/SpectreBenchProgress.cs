using System.Globalization;
using Spectre.Console;

namespace NutanixMcp.Cli.Bench;

public sealed class SpectreBenchProgress(IAnsiConsole console) : IBenchProgress
{
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
        var marker = firstHitRank switch
        {
            0 => "[red]miss[/]",
            1 => "[green]hit@1[/]",
            <= 5 => "[green]hit@5[/]",
            <= 10 => "[yellow]hit@10[/]",
            _ => "[grey]rank>10[/]",
        };
        _console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"  ({_seen,4}/{_total,-4}) {marker,-12} {queryId} [grey]({latencyMs,7:F0} ms)[/]");
    }

    public void Finish()
    {
        _console.MarkupLineInterpolated(
            CultureInfo.InvariantCulture,
            $"[bold]bench done[/]: {_seen} queries");
    }
}
