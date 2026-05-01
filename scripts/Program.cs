// Standalone embedder throughput benchmark. Builds the same OnnxEmbedder
// the production CLI uses and times embedding of 1000 chunks pulled from
// a converted-corpus md file. Reports tokens-per-second.
//
// Usage:
//   dotnet run --project scripts/EmbedBench.csproj -c Release \
//     -- --models models --gpu false --count 1000 --md path/to/big.md

using System.Diagnostics;
using System.Globalization;
using NutanixMcp.Core.Settings;
using NutanixMcp.Embedding;

var modelsDir = "models";
var gpu = false;
var count = 1000;
var mdPath = "";
var batchSize = 8;

var inv = CultureInfo.InvariantCulture;
var argList = args.ToList();
for (var i = 0; i + 1 < argList.Count; i += 2)
{
    var flag = argList[i];
    var value = argList[i + 1];
    switch (flag)
    {
        case "--models": modelsDir = value; break;
        case "--gpu": gpu = bool.Parse(value); break;
        case "--count": count = int.Parse(value, inv); break;
        case "--md": mdPath = value; break;
        case "--batch": batchSize = int.Parse(value, inv); break;
    }
}

if (string.IsNullOrEmpty(mdPath))
{
    Console.Error.WriteLine("usage: --md <path> --models <dir> [--gpu true|false] [--count N] [--batch N]");
    return 1;
}

var settings = new NutanixMcpSettings { ModelsDir = modelsDir, Gpu = gpu };
var raw = (await File.ReadAllTextAsync(mdPath))
    .Replace("\r\n", "\n", StringComparison.Ordinal)
    .Replace('\r', '\n');
// Split on single newlines, group into ~512-char chunks (matching the
// production section-aware chunker target). Skip lines under 30 chars
// (table dividers, inline image refs, page markers).
var lines = raw.Split('\n')
    .Select(static s => s.Trim())
    .Where(static s => s.Length > 30)
    .ToList();
var paragraphs = new List<string>();
var current = new System.Text.StringBuilder();
foreach (var line in lines)
{
    if (current.Length + line.Length > 512 && current.Length > 0)
    {
        paragraphs.Add(current.ToString());
        if (paragraphs.Count >= count) break;
        current.Clear();
    }
    if (current.Length > 0) current.Append(' ');
    current.Append(line);
}
if (current.Length > 0 && paragraphs.Count < count) paragraphs.Add(current.ToString());

if (paragraphs.Count < count)
{
    Console.Error.WriteLine($"warning: only {paragraphs.Count} paragraphs available in {mdPath}, requested {count}");
    count = paragraphs.Count;
}

var totalChars = paragraphs.Sum(static p => p.Length);
Console.WriteLine($"loaded {paragraphs.Count} paragraphs, {totalChars} chars, {totalChars / paragraphs.Count} chars/para avg");
Console.WriteLine($"models={modelsDir} gpu={gpu} batch={batchSize}");

using var emb = new OnnxEmbedder(new EmbeddingOptions
{
    ModelPath = ModelPaths.EmbedderModel(settings),
    VocabPath = ModelPaths.EmbedderVocab(settings),
    Gpu = gpu,
    BatchSize = batchSize,
});

// Warm up — first call pays graph init cost.
Console.Write("warming up... ");
var swWarm = Stopwatch.StartNew();
_ = await emb.EmbedAsync([paragraphs[0]], isQuery: false, CancellationToken.None);
swWarm.Stop();
Console.WriteLine($"{swWarm.ElapsedMilliseconds} ms");

Console.Write($"embedding {paragraphs.Count} paragraphs... ");
var sw = Stopwatch.StartNew();
var vecs = await emb.EmbedAsync(paragraphs, isQuery: false, CancellationToken.None);
sw.Stop();
Console.WriteLine($"{sw.Elapsed.TotalSeconds:F1} s");

Console.WriteLine();
Console.WriteLine($"throughput: {paragraphs.Count / sw.Elapsed.TotalSeconds:F1} paragraphs/sec");
Console.WriteLine($"throughput: {totalChars / sw.Elapsed.TotalSeconds:F0} chars/sec");
Console.WriteLine($"per-paragraph: {sw.Elapsed.TotalMilliseconds / paragraphs.Count:F1} ms");
Console.WriteLine($"output dimensions: {vecs[0].Length}");
return 0;
