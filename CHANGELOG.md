# Changelog

## [Unreleased] — Sprint 001 (scaffold)

- Solution skeleton (5 src + 5 tests, layered Cli→Server→Core←Embedding,Data)
- Centralised package versions (Directory.Packages.props), TWaE, analyzers (Meziantou, Roslynator, Sonar, AsyncFixer, BannedApi)
- Postgres docker stack: tensorchord/vchord-suite:pg18-latest on port 5435, init.sql with english_analyzer
- BannedSymbols.txt mirroring arista-mcp
- Trivial Program.cs in CLI for build smoke
