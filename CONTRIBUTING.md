# Contributing

## Commit messages — Conventional Commits 1.0.0

Format: `<type>[optional scope]: <description>`. See <https://www.conventionalcommits.org/en/v1.0.0/>.

Types we use:

| type | when |
|--|--|
| `feat` | new user-visible capability (CLI verb, MCP tool, ingest stage) |
| `fix` | bug fix (regression, wrong DDL, broken serialisation) |
| `perf` | runtime / index / query speedup with no behaviour change |
| `refactor` | internal rework, no behaviour change |
| `test` | add or refine tests, no production code change |
| `docs` | README / CLAUDE.md / CHANGELOG / plan |
| `build` | csproj, Directory.Packages.props, global.json, Containerfile |
| `ci` | GitHub Actions, dependabot, semgrep, pvs-studio jobs |
| `chore` | uninteresting maintenance (gitignore, formatter pass) |

Breaking changes use `!` after type/scope **and** a `BREAKING CHANGE:` footer:

    feat(data)!: switch chunk PK to bigint snowflake

    BREAKING CHANGE: existing migrations cannot upgrade in-place;
    drop and re-ingest required.

Optional scopes that match the layered architecture: `core`, `data`, `embedding`,
`server`, `cli`, `docker`, `tests`. Cross-cutting changes can omit the scope.

## Versioning — SemVer 2.0.0

`MAJOR.MINOR.PATCH`. See <https://semver.org/spec/v2.0.0/>.

- Pre-1.0: minor bumps may break (we are here, v0.x).
- A version is bumped when a release tag is cut: `git tag v0.2.0 && git push --tags`.
- The `[Unreleased]` block in `CHANGELOG.md` becomes the bumped version's section.

## Changelog — Keep a Changelog 1.1.0

`CHANGELOG.md` follows <https://keepachangelog.com/en/1.1.0/>:
sections are `Added` / `Changed` / `Deprecated` / `Removed` / `Fixed` / `Security`.
Every PR that lands on `main` updates `[Unreleased]`.

## Container artifacts

- `docker/Containerfile` follows the OCI Dockerfile spec / Containerfile.5
  (<https://github.com/containers/common/blob/main/docs/Containerfile.5.md>).
- `docker/compose.yaml` follows compose-spec
  (<https://github.com/compose-spec/compose-spec/blob/main/spec.md>) — no `version:` key
  (deprecated), uses top-level `name:` for project disambiguation.
- `docker/.containerignore` keeps the build context to `Containerfile` + `init.sql` only
  (<https://github.com/containers/common/blob/main/docs/containerignore.5.md>).
- Engine config (`containers.conf`, `containers-mounts.conf`) is **not** vendored —
  developer-local. Document required mounts per ADR if any are needed.

## Code quality gate

See `CLAUDE.md` § *Quality gate (extended vs arista-mcp)*.
