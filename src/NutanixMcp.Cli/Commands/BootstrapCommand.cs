using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Reflection;
using Spectre.Console;

namespace NutanixMcp.Cli.Commands;

// Single-command bootstrap of a working nutanix-mcp install:
//
//   1. Detect platform (Windows / Linux / macOS) and container runtime
//      (podman preferred, docker fallback).
//   2. Stage the Postgres container (or verify a running one).
//   3. Stream the corpus dump from a GitHub release attachment and
//      pg_restore inside the container.
//   4. (Linux + --quadlet) Drop Quadlet unit files into
//      ~/.config/containers/systemd/, then `systemctl --user
//      daemon-reload && enable --now`.
//
// Goal: zero manual steps between `nutanix-mcp serve` and a working
// instance with a populated corpus. The only remaining requirement is
// model fetch (handled by `scripts/fetch-models.ps1` or, on Linux, by
// the bootstrap-models subcommand).
public static class BootstrapCommand
{
    private const string DefaultPgImage = "docker.io/tensorchord/vchord-suite:pg18-latest";
    private const string DefaultContainerName = "nutanix-mcp-postgres";
    private const string DefaultDatabase = "nutanix";
    private const string DefaultUser = "nutanix";
    // Default first-boot password for the PG container. The bootstrap flow
    // is dev-grade — operators rotate via `ALTER USER nutanix PASSWORD '…'`
    // before exposing the listener beyond 127.0.0.1. The value lives here
    // so the constant is grep-able for security review (not in a hidden
    // env-default).
#pragma warning disable S2068 // Hard-coded credential — see docstring above.
    private const string DefaultPgPassword = "nutanix";
#pragma warning restore S2068
    private const int DefaultHostPort = 5435;

    // Release-attachment naming. Asset name follows the
    // `nutanix-corpus-<tag>.dump` convention used by the release
    // pipeline (matches the `v` prefix every other release artefact
    // carries: nutanix-mcp-<tag>-<rid>.tar.gz, etc.). The actual
    // download flow goes through the GitHub REST API rather than the
    // browser-style html URL because private repositories do not
    // serve `releases/download/...` paths even with a Bearer token —
    // see https://docs.github.com/en/rest/releases/assets.
    private const string ReleaseRepoOwner = "dantte-lp";
    private const string ReleaseRepoName = "nutanix-mcp";
    private static readonly System.Text.CompositeFormat s_releaseDumpAssetName =
        System.Text.CompositeFormat.Parse("nutanix-corpus-{0}.dump");

    public static Command Build()
    {
        var release = new Option<string>("--release")
        {
            Description = "Release tag whose `nutanix-corpus-<tag>.dump` asset to restore (default: skip restore).",
        };
        var quadlet = new Option<bool>("--quadlet")
        {
            Description = "Linux: install Podman Quadlet unit files for auto-restart on boot.",
        };
        var pgImage = new Option<string>("--pg-image")
        {
            Description = "Override the Postgres image reference.",
            DefaultValueFactory = _ => DefaultPgImage,
        };
        var containerName = new Option<string>("--container-name")
        {
            Description = "Postgres container name.",
            DefaultValueFactory = _ => DefaultContainerName,
        };
        var hostPort = new Option<int>("--host-port")
        {
            Description = "Host port to publish 5432 on.",
            DefaultValueFactory = _ => DefaultHostPort,
        };
        var skipPg = new Option<bool>("--skip-pg")
        {
            Description = "Don't touch the Postgres container — assume it's already running.",
        };
        var skipRestore = new Option<bool>("--skip-restore")
        {
            Description = "Provision Postgres but skip the corpus restore.",
        };

        var cmd = new Command("bootstrap",
            "One-shot install: provision Postgres, restore corpus dump from a release attachment, optionally wire systemd Quadlet for auto-restart.")
        {
            release,
            quadlet,
            pgImage,
            containerName,
            hostPort,
            skipPg,
            skipRestore,
        };

        cmd.SetAction(async (pr, ct) =>
        {
            var console = AnsiConsole.Console;
            var args = new BootstrapArgs(
                Release: pr.GetValue(release),
                Quadlet: pr.GetValue(quadlet),
                PgImage: pr.GetValue(pgImage) ?? DefaultPgImage,
                ContainerName: pr.GetValue(containerName) ?? DefaultContainerName,
                HostPort: pr.GetValue(hostPort),
                SkipPg: pr.GetValue(skipPg),
                SkipRestore: pr.GetValue(skipRestore));

            return await RunAsync(console, args, ct).ConfigureAwait(false);
        });

        return cmd;
    }

    private sealed record BootstrapArgs(
        string? Release,
        bool Quadlet,
        string PgImage,
        string ContainerName,
        int HostPort,
        bool SkipPg,
        bool SkipRestore);

    private static async Task<int> RunAsync(IAnsiConsole console, BootstrapArgs args, CancellationToken ct)
    {
        console.MarkupLine("[bold]nutanix-mcp bootstrap[/]");

        // Step 1: detect runtime.
        string? runtime = await DetectRuntimeAsync(ct).ConfigureAwait(false);
        if (runtime is null)
        {
            console.MarkupLine("[red]error[/] neither `podman` nor `docker` found in PATH");
            return 2;
        }
        console.MarkupLineInterpolated(CultureInfo.InvariantCulture, $"  runtime  [green]{runtime}[/]");
        console.MarkupLineInterpolated(CultureInfo.InvariantCulture, $"  platform [green]{RuntimePlatform()}[/]");

        // Step 2: provision PG.
        if (!args.SkipPg)
        {
            int rc = await EnsurePostgresAsync(runtime, args, console, ct).ConfigureAwait(false);
            if (rc != 0)
            {
                return rc;
            }
        }
        else
        {
            console.MarkupLine("  pg       [yellow]skipped[/] (--skip-pg)");
        }

        // Step 3: restore corpus.
        if (!args.SkipRestore && args.Release is not null)
        {
            int rc = await RestoreCorpusAsync(runtime, args, console, ct).ConfigureAwait(false);
            if (rc != 0)
            {
                return rc;
            }
        }
        else if (args.Release is null)
        {
            console.MarkupLine("  corpus   [yellow]skipped[/] (no --release given; ingest manually with `nutanix-mcp ingest`)");
        }
        else
        {
            console.MarkupLine("  corpus   [yellow]skipped[/] (--skip-restore)");
        }

        // Step 4: Quadlet (Linux only).
        if (args.Quadlet)
        {
            if (!OperatingSystem.IsLinux())
            {
                console.MarkupLine("[yellow]warn[/] --quadlet is Linux-only; skipping (use deploy/windows/Install-NutanixMcpService.ps1 on Windows).");
            }
            else
            {
                int rc = await InstallQuadletAsync(console, ct).ConfigureAwait(false);
                if (rc != 0)
                {
                    return rc;
                }
            }
        }

        console.MarkupLine("[bold green]bootstrap done.[/]");
        return 0;
    }

    private static async Task<string?> DetectRuntimeAsync(CancellationToken ct)
    {
        foreach (string candidate in new[] { "podman", "docker" })
        {
            int code = await RunSilentAsync(candidate, "--version", ct).ConfigureAwait(false);
            if (code == 0)
            {
                return candidate;
            }
        }
        return null;
    }

    private static string RuntimePlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return "Windows";
        }
        if (OperatingSystem.IsLinux())
        {
            return "Linux";
        }
        if (OperatingSystem.IsMacOS())
        {
            return "macOS";
        }
        return "unknown";
    }

    private static async Task<int> EnsurePostgresAsync(
        string runtime, BootstrapArgs args, IAnsiConsole console, CancellationToken ct)
    {
        // `podman container exists` returns exit 0/1; the Docker CLI has no such
        // subcommand. Use `ps -aq --filter name=^X$` instead — works on both
        // runtimes; non-empty stdout means the container exists.
        string existing = await RunSilentReadStdoutAsync(
            runtime, $"ps -aq --filter name=^{args.ContainerName}$", ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"  pg       [green]container exists[/]: {args.ContainerName} (starting if stopped)");
            await RunSilentAsync(runtime, $"start {args.ContainerName}", ct).ConfigureAwait(false);
            return 0;
        }

        console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
            $"  pg       creating container [green]{args.ContainerName}[/] from {args.PgImage}");
        // Pull first so the run step doesn't time out on slow links.
        await RunStreamingAsync(runtime, $"pull {args.PgImage}", console, ct).ConfigureAwait(false);

        // Mount the repo's init.sql if available alongside the binary
        // (release artefacts include docker/init.sql at $publishdir/docker/).
        string? initSql = LocateInitSql();
        string initMount = initSql is null
            ? string.Empty
            : $" -v \"{initSql}:/docker-entrypoint-initdb.d/01-init.sql:ro\"";

#pragma warning disable S2068 // Const credential — see DefaultPgPassword docstring.
        // pg18 images use major-version-specific data dirs and refuse to
        // start when the volume lands on the legacy /var/lib/postgresql/data.
        // Mount on /var/lib/postgresql/18/data and pin PGDATA to the same
        // path — same approach as docker/compose.yaml and the Quadlet
        // template (deploy/quadlet/nutanix-mcp-postgres.container). Keeping
        // all three in sync is a hard requirement: the bench/ingest paths
        // assume the dump restored on the bootstrap PG matches the schema
        // produced by the compose stack.
        // --shm-size=2g matches docker/compose.yaml: HNSW index build and
        // pg_restore -j 4 burn shared-memory; the default 64 MB triggers
        // ENOSPC on /dev/shm.
        string runArgs = string.Join(' ',
            "run -d",
            $"--name {args.ContainerName}",
            "--restart unless-stopped",
            "--shm-size=2g",
            $"-p 127.0.0.1:{args.HostPort.ToString(CultureInfo.InvariantCulture)}:5432",
            $"-e POSTGRES_DB={DefaultDatabase}",
            $"-e POSTGRES_USER={DefaultUser}",
            $"-e POSTGRES_PASSWORD={DefaultPgPassword}",
            "-e PGDATA=/var/lib/postgresql/18/data",
            "-e POSTGRES_INITDB_ARGS=\"--data-checksums --encoding=UTF-8 --locale=C.UTF-8\"",
            $"-v {args.ContainerName}-data:/var/lib/postgresql/18/data",
            initMount,
            args.PgImage);
#pragma warning restore S2068

        int code = await RunStreamingAsync(runtime, runArgs, console, ct).ConfigureAwait(false);
        if (code != 0)
        {
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"[red]error[/] {runtime} run failed (exit {code})");
            return code;
        }

        // Wait for pg_isready. First boot runs initdb + the init.sql path
        // (CREATE EXTENSION vchord/vchord_bm25/pg_tokenizer + analyzer
        // build) which together can take ~90s on cold storage; allow up to
        // 180s before failing.
        for (int attempt = 1; attempt <= 60; attempt++)
        {
            int rc = await RunSilentAsync(runtime,
                $"exec {args.ContainerName} pg_isready -U {DefaultUser} -d {DefaultDatabase}",
                ct).ConfigureAwait(false);
            if (rc == 0)
            {
                console.MarkupLine("  pg       [green]ready[/]");
                return 0;
            }
            await Task.Delay(3000, ct).ConfigureAwait(false);
        }
        console.MarkupLine("[red]error[/] postgres did not become ready in 180 s");
        return 3;
    }

    private static async Task<int> RestoreCorpusAsync(
        string runtime, BootstrapArgs args, IAnsiConsole console, CancellationToken ct)
    {
        if (args.Release is null)
        {
            return 0;
        }

        string tag = args.Release;
        string assetName = string.Format(CultureInfo.InvariantCulture, s_releaseDumpAssetName, tag);

        console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
            $"  corpus   resolving asset [green]{assetName}[/] in [green]{ReleaseRepoOwner}/{ReleaseRepoName}@{tag}[/]");

        string tmpHostPath = Path.Combine(Path.GetTempPath(), $"nutanix-corpus-{tag}.dump");
        try
        {
            await DownloadReleaseAssetAsync(tag, assetName, tmpHostPath, console, ct).ConfigureAwait(false);

            // Copy into the container.
            await RunStreamingAsync(runtime, $"cp \"{tmpHostPath}\" \"{args.ContainerName}:/tmp/nutanix-corpus.dump\"",
                console, ct).ConfigureAwait(false);

            console.MarkupLine("  corpus   running pg_restore inside the container");
            // --clean drops existing schema, --if-exists tolerates a fresh DB.
            // -j 4 parallelises restore; HNSW index rebuild may still serialize.
#pragma warning disable S2068 // Const credential — see DefaultPgPassword docstring.
            string restoreArgs = string.Join(' ',
                $"exec -e PGPASSWORD={DefaultPgPassword} {args.ContainerName}",
                $"pg_restore -h 127.0.0.1 -U {DefaultUser} -d {DefaultDatabase}",
                "--no-owner --no-acl --clean --if-exists -j 4",
                "/tmp/nutanix-corpus.dump");
#pragma warning restore S2068
            int code = await RunStreamingAsync(runtime, restoreArgs, console, ct).ConfigureAwait(false);
            if (code != 0)
            {
                console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                    $"[yellow]warn[/] pg_restore exit {code} — typical for HNSW shared-mem ENOSPC; retrying serially");
                // Drop+recreate the HNSW index serially with no parallel
                // workers (avoids /dev/shm exhaustion on small containers).
                string hnswSql = "SET maintenance_work_mem = '4GB'; SET max_parallel_maintenance_workers = 0; " +
                    "DROP INDEX IF EXISTS \\\"IX_chunks_embedding\\\"; " +
                    "CREATE INDEX \\\"IX_chunks_embedding\\\" ON chunks USING hnsw (embedding halfvec_cosine_ops) " +
                    "WITH (ef_construction=200, m=16);";
#pragma warning disable S2068
                int hnswRc = await RunStreamingAsync(runtime,
                    $"exec -e PGPASSWORD={DefaultPgPassword} {args.ContainerName} " +
                    $"psql -U {DefaultUser} -d {DefaultDatabase} -c \"{hnswSql}\"",
                    console, ct).ConfigureAwait(false);
#pragma warning restore S2068
                if (hnswRc != 0)
                {
                    console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                        $"[red]error[/] serial HNSW index rebuild failed (exit {hnswRc}); dense search will return no results until the index is rebuilt manually.");
                    return hnswRc;
                }
            }

            // pg_tokenizer's tokenizer/model/text_analyzer registry tables are
            // extension-owned (deptype='e') and pg_dump skips their contents by
            // default — only `pg_extension_config_dump()` opt-in (called from
            // CREATE EXTENSION script, which we don't control) would dump them.
            // After pg_restore the chunks_tokenizer/chunks_model/english_analyzer
            // entries are gone, so HybridRetriever.RunSparseAsync throws
            // "Tokenizer not found: chunks_tokenizer". Replay init.sql + the
            // bm25 migration's tokenizer setup, then re-tokenize all chunks so
            // the bm25v column is consistent with the freshly built vocab.
            int rebuildRc = await RebuildTokenizerStateAsync(runtime, args, console, ct).ConfigureAwait(false);
            if (rebuildRc != 0)
            {
                return rebuildRc;
            }
            console.MarkupLine("  corpus   [green]restored[/]");
            return 0;
        }
        finally
        {
            // Cleanup must run even on download / pg_restore failure — otherwise
            // a partial dump leaks into Path.GetTempPath() and into the container's
            // /tmp on repeated bootstrap attempts. Use a fresh CancellationToken so
            // a cancelled outer operation does not block cleanup itself.
            await RunSilentAsync(runtime,
                $"exec {args.ContainerName} rm -f /tmp/nutanix-corpus.dump", CancellationToken.None).ConfigureAwait(false);
            if (File.Exists(tmpHostPath))
            {
                try { File.Delete(tmpHostPath); }
                catch (IOException) { /* best-effort cleanup */ }
                catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
            }
        }
    }

    private static async Task<int> InstallQuadletAsync(IAnsiConsole console, CancellationToken ct)
    {
        // Quadlet's systemd generator (podman-system-generator) ships with
        // Podman 4.4+. Without it, `.container/.network/.volume` files in
        // ~/.config/containers/systemd/ are inert — daemon-reload won't
        // synthesize services and `enable --now` fails with 'unit not
        // found'. Refuse early with a useful message.
        int podmanCheck = await RunSilentAsync("podman", "--version", ct).ConfigureAwait(false);
        if (podmanCheck != 0)
        {
            console.MarkupLine("[red]error[/] --quadlet requires Podman 4.4+; not found in PATH (install with `dnf/apt install podman`)");
            return 5;
        }

        string home = Environment.GetEnvironmentVariable("HOME")
                      ?? throw new InvalidOperationException("HOME unset");
        string quadletDir = Path.Combine(home, ".config", "containers", "systemd");
        Directory.CreateDirectory(quadletDir);

        string? repoQuadlet = LocateRepoFile(Path.Combine("deploy", "quadlet"));
        if (repoQuadlet is null || !Directory.Exists(repoQuadlet))
        {
            console.MarkupLine("[red]error[/] deploy/quadlet/ not found alongside the binary");
            return 4;
        }
        foreach (string src in Directory.EnumerateFiles(repoQuadlet)
                     .Where(f => f.EndsWith(".container", StringComparison.Ordinal)
                              || f.EndsWith(".network", StringComparison.Ordinal)
                              || f.EndsWith(".volume", StringComparison.Ordinal)))
        {
            string dst = Path.Combine(quadletDir, Path.GetFileName(src));
            File.Copy(src, dst, overwrite: true);
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"  quadlet  installed [green]{Path.GetFileName(dst)}[/]");
        }

        await RunStreamingAsync("systemctl", "--user daemon-reload", console, ct).ConfigureAwait(false);
        await RunStreamingAsync("systemctl",
            "--user enable --now nutanix-mcp-postgres.service", console, ct).ConfigureAwait(false);

        console.MarkupLine("  quadlet  [green]enabled[/]");
        console.MarkupLine("  next: `systemctl --user enable --now nutanix-mcp.service` once the server image lands on ghcr.io.");
        return 0;
    }

    private static string? LocateInitSql()
    {
        string? repoFile = LocateRepoFile(Path.Combine("docker", "init.sql"));
        return repoFile;
    }

    private static string? LocateRepoFile(string relative)
    {
        string baseDir = AppContext.BaseDirectory;
        var probe = new DirectoryInfo(baseDir);
        while (probe is not null)
        {
            string candidate = Path.Combine(probe.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
            probe = probe.Parent;
        }
        return null;
    }

    // Resolve the release-asset id by name, then download via the GitHub
    // REST API endpoint with `Accept: application/octet-stream`. Required
    // for private repos: `https://github.com/.../releases/download/...`
    // returns 404 even with a Bearer token; only the API path
    // (`/releases/assets/{id}`) issues the 302 to objects.githubusercontent.com
    // (or release-assets.githubusercontent.com).
    //
    // SocketsHttpHandler strips the Authorization header on cross-origin
    // redirects by default (per Microsoft docs), so the token does not leak
    // across the api.github.com → release-assets.githubusercontent.com hop.
    private static async Task DownloadReleaseAssetAsync(
        string tag, string assetName, string destination,
        IAnsiConsole console, CancellationToken ct)
    {
        string? token = Environment.GetEnvironmentVariable("GH_TOKEN")
                        ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");

        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(30),
            DefaultRequestVersion = new Version(2, 0),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"nutanix-mcp/{Assembly.GetExecutingAssembly().GetName().Version}");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(token))
        {
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        // Step 1: GET /repos/{owner}/{repo}/releases/tags/{tag} → find asset id.
        string releaseUrl = $"https://api.github.com/repos/{ReleaseRepoOwner}/{ReleaseRepoName}/releases/tags/{Uri.EscapeDataString(tag)}";
        using (var releaseReq = new HttpRequestMessage(HttpMethod.Get, releaseUrl))
        {
            releaseReq.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var releaseResp = await http.SendAsync(releaseReq, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
            if (releaseResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"release {tag} not found at {ReleaseRepoOwner}/{ReleaseRepoName}"
                    + (string.IsNullOrEmpty(token)
                        ? " — set GH_TOKEN or GITHUB_TOKEN if the source repo is private"
                        : " — verify the tag exists and the token has `repo` read scope"));
            }
            releaseResp.EnsureSuccessStatusCode();

            await using var stream = await releaseResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await System.Text.Json.JsonDocument.ParseAsync(stream, default, ct).ConfigureAwait(false);

            long? assetId = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                if (string.Equals(asset.GetProperty("name").GetString(),
                                  assetName, StringComparison.Ordinal))
                {
                    assetId = asset.GetProperty("id").GetInt64();
                    break;
                }
            }
            if (assetId is null)
            {
                throw new InvalidOperationException(
                    $"release {tag} has no asset named {assetName}");
            }

            // Step 2: GET /repos/{owner}/{repo}/releases/assets/{id} with
            // Accept: application/octet-stream. GitHub returns 302 → S3
            // (release-assets.githubusercontent.com); HttpClient follows
            // automatically, dropping our Authorization header before the
            // S3 hop (GETs to S3 with the GitHub bearer would 401).
            string assetUrl = $"https://api.github.com/repos/{ReleaseRepoOwner}/{ReleaseRepoName}/releases/assets/{assetId}";
            using var assetReq = new HttpRequestMessage(HttpMethod.Get, assetUrl);
            assetReq.Headers.Accept.ParseAdd("application/octet-stream");
            using var assetResp = await http.SendAsync(assetReq, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            assetResp.EnsureSuccessStatusCode();

            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"  corpus   downloading [green]{assetName}[/] (asset id {assetId})");

            long? total = assetResp.Content.Headers.ContentLength;
            await using var input = await assetResp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var output = File.Create(destination);
            var buf = new byte[256 * 1024];
            long copied = 0;
            long lastReport = 0;
            // With HttpCompletionOption.ResponseHeadersRead, HttpClient.Timeout covers only the header
            // exchange — the body copy is unbounded. Guard each read with an idle timeout so a stalled
            // connection (headers received, then no bytes) fails fast instead of hanging.
            var idleTimeout = TimeSpan.FromSeconds(100);
            while (true)
            {
                int n;
                using (var idle = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    idle.CancelAfter(idleTimeout);
                    try
                    {
                        n = await input.ReadAsync(buf, idle.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(
                            $"download stalled: no data received for {idleTimeout.TotalSeconds:F0}s");
                    }
                }

                if (n <= 0)
                {
                    break;
                }

                await output.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                copied += n;
                if (copied - lastReport > 50 * 1024 * 1024 || (total.HasValue && copied == total))
                {
                    lastReport = copied;
                    if (total.HasValue)
                    {
                        double pct = copied * 100.0 / total.Value;
                        console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                            $"           {copied / 1024 / 1024} / {total / 1024 / 1024} MB ({pct:F1}%)");
                    }
                    else
                    {
                        console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                            $"           {copied / 1024 / 1024} MB");
                    }
                }
            }
        }
    }

    // Restore the pg_tokenizer registry state that pg_dump skipped. After
    // pg_restore --clean we have:
    //   ✅ tokenizer_catalog.model_chunks_model (vocab table + rows)
    //   ✅ chunks.bm25v column (with bm25vector values)
    //   ✅ idx_chunks_bm25 BM25 index
    //   ❌ tokenizer_catalog.{tokenizer,model,text_analyzer} registry rows
    //   ❌ BEFORE INSERT/UPDATE triggers on chunks
    // The simplest reliable recovery is to drop the orphan model_chunks_model
    // (so create_custom_model_tokenizer_and_trigger's CREATE TABLE step does
    // not collide), re-register, then force a re-tokenize of all rows so
    // the bm25v values match the freshly built vocab. This is slow on a
    // 489k-row corpus (~5-15 min) but produces a queryable system.
    private static async Task<int> RebuildTokenizerStateAsync(
        string runtime, BootstrapArgs args, IAnsiConsole console, CancellationToken ct)
    {
        console.MarkupLine("  tokenizer recreating chunks_tokenizer + model + trigger (pg_dump skips extension-owned registry tables)");

        // Stage init.sql + post-restore SQL inside the container.
        string? initSql = LocateInitSql();
        if (initSql is null)
        {
            console.MarkupLine("[red]error[/] docker/init.sql not found alongside the binary; cannot rebuild tokenizer state");
            return 6;
        }

        await RunStreamingAsync(runtime,
            $"cp \"{initSql}\" \"{args.ContainerName}:/tmp/init.sql\"",
            console, ct).ConfigureAwait(false);

        // Step 1: replay init.sql to get english_analyzer back. The
        // CREATE EXTENSION statements are no-ops on second run.
#pragma warning disable S2068
        int initRc = await RunStreamingAsync(runtime,
            $"exec -e PGPASSWORD={DefaultPgPassword} {args.ContainerName} " +
            $"psql -U {DefaultUser} -d {DefaultDatabase} -v ON_ERROR_STOP=1 -f /tmp/init.sql",
            console, ct).ConfigureAwait(false);
#pragma warning restore S2068
        if (initRc != 0)
        {
            // Hitting "text analyzer already exists" is fine — the registry
            // can survive between runs. Other errors fall through.
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"[yellow]warn[/] init.sql replay returned {initRc}; assuming idempotent and continuing");
        }

        // Step 2: drop the orphan model_chunks_model + triggers that the
        // restored dump left behind, then re-register via the public
        // function. Step 3: re-tokenize.
        string rebuildSql =
            "DROP TRIGGER IF EXISTS model_chunks_model_trigger ON chunks; " +
            "DROP TRIGGER IF EXISTS model_chunks_model_trigger_insert ON chunks; " +
            "DROP TABLE IF EXISTS tokenizer_catalog.model_chunks_model; " +
            "DELETE FROM tokenizer_catalog.tokenizer WHERE name = 'chunks_tokenizer'; " +
            "DELETE FROM tokenizer_catalog.model WHERE name = 'chunks_model'; " +
            "SELECT tokenizer_catalog.create_custom_model_tokenizer_and_trigger(" +
                "tokenizer_name => 'chunks_tokenizer', " +
                "model_name => 'chunks_model', " +
                "text_analyzer_name => 'english_analyzer', " +
                "table_name => 'chunks', " +
                "source_column => 'content', " +
                "target_column => 'bm25v'); " +
            "SET maintenance_work_mem = '4GB'; " +
            "SET max_parallel_maintenance_workers = 0;";
#pragma warning disable S2068
        int rebuildRc = await RunStreamingAsync(runtime,
            $"exec -e PGPASSWORD={DefaultPgPassword} {args.ContainerName} " +
            $"psql -U {DefaultUser} -d {DefaultDatabase} -v ON_ERROR_STOP=1 -c \"{rebuildSql}\"",
            console, ct).ConfigureAwait(false);
#pragma warning restore S2068
        if (rebuildRc != 0)
        {
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"[red]error[/] tokenizer rebuild psql exit {rebuildRc}");
            return rebuildRc;
        }

        // Step 3: force the trigger to recompute bm25v for every row. On a
        // 489k-row corpus this is the slow step (5-15 min on Win+podman),
        // run as a single statement so the user sees one psql line and
        // the trigger batches efficiently.
        console.MarkupLine("  tokenizer re-tokenizing all chunks (bm25v rebuild — slow path, ~5-15 min on a 489k-row corpus)");
        string retokenizeSql = "UPDATE chunks SET content = content; REINDEX INDEX idx_chunks_bm25;";
#pragma warning disable S2068
        int retokenizeRc = await RunStreamingAsync(runtime,
            $"exec -e PGPASSWORD={DefaultPgPassword} {args.ContainerName} " +
            $"psql -U {DefaultUser} -d {DefaultDatabase} -v ON_ERROR_STOP=1 -c \"{retokenizeSql}\"",
            console, ct).ConfigureAwait(false);
#pragma warning restore S2068
        if (retokenizeRc != 0)
        {
            console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                $"[red]error[/] re-tokenize psql exit {retokenizeRc}");
            return retokenizeRc;
        }

        await RunSilentAsync(runtime,
            $"exec {args.ContainerName} rm -f /tmp/init.sql", ct).ConfigureAwait(false);
        console.MarkupLine("  tokenizer [green]rebuilt[/]");
        return 0;
    }

    private static async Task<int> RunSilentAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return -1;
            }
            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Don't leave a half-finished podman exec / pg_isready process
                // running in the background — kill the whole tree so the next
                // bootstrap attempt isn't blocked by a stale child.
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                throw;
            }
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Executable not found.
            return -1;
        }
    }

    // Variant of RunSilentAsync that captures stdout — needed because we use
    // `docker ps -q --filter name=…` for portable container-existence checks:
    // both podman and docker exit 0 when ps succeeds, and we have to inspect
    // stdout (empty == not found, container-id == found) to tell the cases apart.
    private static async Task<string> RunSilentReadStdoutAsync(string file, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p is null)
            {
                return string.Empty;
            }
            // Read stdout before waiting for exit so we don't deadlock if the
            // child writes more than the pipe buffer.
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            try
            {
                await p.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                throw;
            }
            return p.ExitCode == 0
                ? (await stdoutTask.ConfigureAwait(false)).Trim()
                : string.Empty;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private static async Task<int> RunStreamingAsync(
        string file, string args, IAnsiConsole console, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to launch {file}");
        p.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                    $"           {Markup.Escape(e.Data)}");
            }
        };
        p.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                console.MarkupLineInterpolated(CultureInfo.InvariantCulture,
                    $"           [yellow]{Markup.Escape(e.Data)}[/]");
            }
        };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Kill the whole tree so an interrupted pg_restore / podman pull
            // doesn't keep running past the abandoned bootstrap.
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            throw;
        }
        return p.ExitCode;
    }
}
