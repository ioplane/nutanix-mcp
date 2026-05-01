# Quality gate

Five layers gate every change to the codebase.

## Layer 1: Roslyn analyzers

`Directory.Build.props` enforces:

- `TreatWarningsAsErrors=true`
- `AnalysisLevel=latest-recommended`
- `EnforceCodeStyleInBuild=true`
- SARIF analyzer log at `analyzers.sarif`

Analyzer set, applied as `<GlobalPackageReference>`:

| Analyzer | Version |
|---|---|
| `Meziantou.Analyzer` | 3.0.50 |
| `Roslynator.Analyzers` | 4.15.0 |
| `SonarAnalyzer.CSharp` | 10.23.0.137933 |
| `AsyncFixer` | 2.1.0 |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 4.14.0 |

`BannedSymbols.txt` forbids `DateTime.Now`, `WebClient`, `Thread.Abort`,
`BinaryFormatter`, `MD5.Create`, and `SHA1.Create`.

## Layer 2: Semgrep

Local invocation:

```bash
semgrep --config=p/csharp --config=p/security-audit \
        --config=p/owasp-top-ten --config=p/secrets src/
```

Continuous integration runs the same command in a dedicated job;
findings fail the pipeline. Rule overrides live in `.semgrep/rules.yaml`
when a finding is irrelevant.

## Layer 3: PVS-Studio

`pvs-studio.cfg` at the repo root holds the licence path and analysis
mode. Local run:

```bash
PVS-Studio_Cmd.exe -t nutanix-mcp.slnx -o pvs-results.plog -c Release
HtmlGenerator.exe -t pvs-results.plog -o pvs-report -r
```

The CI job is informational against the baseline, but the build fails
on new W-level warnings versus `main`.

## Layer 4: Format

`dotnet format --verify-no-changes` runs in a pre-commit hook and as a
CI job. Style violations fail the build.

## Layer 5: Documentation linting

`markdownlint-cli2` runs against every Markdown file in `docs/` plus
`README*.md`, `CHANGELOG.md`, and `CONTRIBUTING.md`. Configuration in
`.markdownlint.jsonc` enables the recommended rule set with project
overrides. CI fails on any violation.

## Test gating

Sprint cadence is waterfall. Sprint N+1 starts only after Sprint N
passes:

- `dotnet test` green across all five test projects.
- `psql -c '\dx'` confirms the six PostgreSQL extensions.
- The bench delta versus the previous sprint is recorded in
  `tests/fixtures/bench-history.jsonl` (from Sprint 006 onwards).
- `git tag sprint-N-review` exists.

## Reference checks

| Tool | Use when | Reason |
|---|---|---|
| `pg-aiguide` MCP | Writing DDL, migrations, or queries. | Validate against PostgreSQL best practices before merging. |
| Microsoft Learn MCP | Touching .NET, EF Core, or MCP SDK signatures. | Prevent hallucinated APIs and deprecated patterns. |
| Context7 MCP | Adding or upgrading any library. | Pull current documentation rather than relying on stale memory. |

These three are consult-only — they do not run in CI. They are the
expected step before authoring code that touches their respective
surface areas.
