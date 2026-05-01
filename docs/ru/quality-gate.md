# Гейт качества

Пять слоёв гейтят каждое изменение в кодовой базе.

## Слой 1: Roslyn-анализаторы

`Directory.Build.props` принудительно ставит:

- `TreatWarningsAsErrors=true`
- `AnalysisLevel=latest-recommended`
- `EnforceCodeStyleInBuild=true`
- SARIF-лог анализа в `analyzers.sarif`

Набор анализаторов через `<GlobalPackageReference>`:

| Анализатор | Версия |
|---|---|
| `Meziantou.Analyzer` | 3.0.50 |
| `Roslynator.Analyzers` | 4.15.0 |
| `SonarAnalyzer.CSharp` | 10.23.0.137933 |
| `AsyncFixer` | 2.1.0 |
| `Microsoft.CodeAnalysis.BannedApiAnalyzers` | 4.14.0 |

`BannedSymbols.txt` запрещает `DateTime.Now`, `WebClient`,
`Thread.Abort`, `BinaryFormatter`, `MD5.Create`, `SHA1.Create`.

## Слой 2: Semgrep

Локально:

```bash
semgrep --config=p/csharp --config=p/security-audit \
        --config=p/owasp-top-ten --config=p/secrets src/
```

CI запускает то же в отдельном job; находки роняют пайплайн. Override
правил живут в `.semgrep/rules.yaml`, когда находка нерелевантна.

## Слой 3: PVS-Studio

`pvs-studio.cfg` в корне репо хранит путь к лицензии и режим анализа.
Локально:

```bash
PVS-Studio_Cmd.exe -t nutanix-mcp.slnx -o pvs-results.plog -c Release
HtmlGenerator.exe -t pvs-results.plog -o pvs-report -r
```

CI-job — информационный против baseline, но build падает на новых
W-level warnings относительно `main`.

## Слой 4: Format

`dotnet format --verify-no-changes` запускается в pre-commit-хуке и в
CI-job. Style-нарушения роняют сборку.

## Слой 5: Линтинг документации

`markdownlint-cli2` гоняется по каждому Markdown в `docs/` плюс
`README*.md`, `CHANGELOG.md`, `CONTRIBUTING.md`. Конфиг
`.markdownlint.jsonc` включает recommended-набор с проектными
override’ами. CI падает при любом нарушении.

## Гейтинг тестов

Sprint-каданс waterfall. Sprint N+1 стартует только когда Sprint N
прошёл:

- `dotnet test` зелёный по всем пяти проектам тестов.
- `psql -c '\dx'` показывает шесть PostgreSQL-расширений.
- Дельта бенча против предыдущего спринта залоганаф в
  `tests/fixtures/bench-history.jsonl` (с Sprint 006).
- Тег `git tag sprint-N-review` существует.

## Reference checks

| Инструмент | Когда использовать | Почему |
|---|---|---|
| `pg-aiguide` MCP | Пишем DDL, миграции или запросы. | Сверка с PostgreSQL best practices до мерджа. |
| Microsoft Learn MCP | Трогаем .NET, EF Core или MCP SDK. | Защита от галлюцинаций API и устаревших паттернов. |
| Context7 MCP | Добавляем или апгрейдим библиотеку. | Подтянуть актуальную документацию вместо устаревшей памяти. |

Эти три — consult-only — в CI не запускаются. Это ожидаемый шаг до
авторства кода в их области.
