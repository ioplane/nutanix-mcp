#!/usr/bin/env pwsh
# Stratified-sample chunks from the live nutanix DB and emit a bench v2
# fixture with heuristically-templated queries. First-cut path for Sprint 006
# acceptance — replaces the LLM-generation pipeline used by arista-mcp until
# a Qwen-class sidecar is wired up against the remote GPU host.
#
# Output schema matches NutanixMcp.Core.Bench.BenchQueryFile (schema_version=2):
#   { schema_version, corpus_sha, queries: [...] }
# Each query carries chunk_ids resolved at sample time, plus the (doc, section)
# anchor so a future re-pin via `validate-bench-queries` can reattach after a
# reingest.
#
# Usage:
#   $env:PGPASSWORD='nutanix'
#   pwsh scripts/seed-bench-queries.ps1 -OutPath tests/fixtures/bench-queries.json

param(
    [string]$DbHost = '127.0.0.1', # ignored if $env:PGHOST is set
    [int]$Port = 5436,
    [string]$Db = 'nutanix',
    [string]$User = 'nutanix',
    [string]$OutPath = 'tests/fixtures/bench-queries.json',
    [int]$Seed = 42
)

$ErrorActionPreference = 'Stop'

$psql = 'C:\Program Files\DBeaverUltimate\plugins\com.dbeaver.resources.clients.postgresql.win32_10.6.0\clients\postgresql\win\17\psql.exe'
if (-not (Test-Path $psql)) {
    $psql = (Get-Command psql -ErrorAction SilentlyContinue)?.Source
    if (-not $psql) {
        throw 'psql not found. Install PostgreSQL client or DBeaver.'
    }
}

# Boilerplate section titles that are too generic to anchor a query against.
# Mirrors the filter list in the execution plan.
$BoilerplateSections = @(
    '**Procedure**', '**About this task**', '**Before you begin**',
    '**Note:**', '**What to do next**', '**Warning:**',
    '**Required arguments**', '**Optional arguments**',
    '**Tools and supplies required:**', '**Contents**', '**Template**',
    'Procedure', 'About this task'
)

# Per-category quotas. Adds up to 32 — enough headroom for a couple of
# accidental drops while still meeting the ≥30-query floor.
$Quotas = [ordered]@{
    'kb'                    = 14
    'hardware-replacement'  = 8
    'bible'                 = 1
    'ahv'                   = 2
    'aos'                   = 1
    'prism-central'         = 3
    'prism-element'         = 2
    'files'                 = 2
    'api'                   = 1
    'other'                 = 4
}

function Invoke-PsqlJson {
    param([string]$Sql)
    # `-A -t -F '|'` => unaligned, tuples-only, pipe-separated. Cheap parse.
    $tmp = [System.IO.Path]::GetTempFileName()
    $Sql | Set-Content -LiteralPath $tmp -Encoding utf8
    try {
        & $psql -h $DbHost -p $Port -U $User -d $Db -A -t -F '|' -f $tmp 2>&1
    }
    finally {
        Remove-Item -LiteralPath $tmp -Force
    }
}

function Get-CorpusSha {
    $rows = Invoke-PsqlJson -Sql @"
SELECT COALESCE(catalog_sha256, 'unknown')
FROM ingest_runs
WHERE status = 'success'
ORDER BY started_at DESC
LIMIT 1;
"@
    $line = ($rows | Where-Object { $_ -and $_ -notmatch '^\(' } | Select-Object -First 1)
    if (-not $line) { return 'unknown' }
    return $line.Trim()
}

function Clean-Title {
    param([string]$T)
    $t = $T
    $t = $t -replace '^\*\*(.+)\*\*$', '$1'
    $t = $t -replace '<span[^>]*></span>\s*\**', ''
    $t = $t -replace '\**$', ''
    return $t.Trim()
}

function Templated-Query {
    # Build a query from the document title, refining with section_title when
    # it is more specific than boilerplate. Doc title is the strongest signal
    # for KB / hardware-replacement docs because it states the user-facing
    # topic directly ("Steps to mount Windows OS disk when VM BSODs ...",
    # "Replacing the SATA SSD on NX-3035-G10").
    param(
        [string]$DocTitle,
        [string]$SectionTitle,
        [string]$Category
    )
    $doc = Clean-Title -T $DocTitle
    $sec = Clean-Title -T $SectionTitle

    # 1) doc title is already a question
    if ($doc -match '\?\s*$') { return $doc }

    # 2) "Steps to <verb-phrase>" → "How do I <verb-phrase>?"
    if ($doc -match '^Steps to\s+(.+)$') {
        return "How do I $($Matches[1])?"
    }
    # 3) "How to|Configuring|... <X>" → "How do I <verb-form X>?"
    if ($doc -match '^(How to|Configuring|Configure|Enabling|Enable|Replacing|Replace|Removing|Remove|Adding|Add|Installing|Install|Upgrading|Upgrade)\s+(.+)$') {
        $verbLower = $Matches[1].ToLower()
        $rest = $Matches[2]
        $map = @{
            'how to'      = "How do I $rest`?"
            'configuring' = "How do I configure $rest`?"
            'configure'   = "How do I configure $rest`?"
            'enabling'    = "How do I enable $rest`?"
            'enable'      = "How do I enable $rest`?"
            'replacing'   = "How do I replace $rest`?"
            'replace'     = "How do I replace $rest`?"
            'removing'    = "How do I remove $rest`?"
            'remove'      = "How do I remove $rest`?"
            'adding'      = "How do I add $rest`?"
            'add'         = "How do I add $rest`?"
            'installing'  = "How do I install $rest`?"
            'install'     = "How do I install $rest`?"
            'upgrading'   = "How do I upgrade $rest`?"
            'upgrade'     = "How do I upgrade $rest`?"
        }
        if ($map.ContainsKey($verbLower)) {
            return $map[$verbLower]
        }
    }
    # 4) doc title looks like a problem statement → "Why is <problem>?" / "How do I troubleshoot <problem>?"
    if ($doc -match '\b(failing|fails|not working|cannot|unable to|error|issue|problem|stuck|hung|crash|hang)\b') {
        return "How do I troubleshoot ""$doc""?"
    }
    # 5) doc starts with a noun phrase → "What is <noun phrase>?"
    if ($doc.Length -gt 0 -and $doc.Length -le 90) {
        return "What is ""$doc""?"
    }
    # 6) over-long titles → use section anchor when it's not boilerplate
    if ($sec -and $sec.Length -le 60) {
        return "What does ""$sec"" cover for $Category?"
    }
    return "What is ""$($doc.Substring(0, [Math]::Min(80, $doc.Length)))""?"
}

$corpusSha = Get-CorpusSha
Write-Host "[seed] corpus_sha = $corpusSha"

$queries = New-Object System.Collections.Generic.List[hashtable]
$qid = 0
$boilerplateLiteral = ($BoilerplateSections | ForEach-Object { "'" + ($_ -replace "'", "''") + "'" }) -join ','

foreach ($cat in $Quotas.Keys) {
    $quota = $Quotas[$cat]
    $sql = @"
-- Two-step pick: first dedup documents by base-title (strips trailing version
-- like "v11.0", "v6.10", "v1.1.5.0"), then pick one varied chunk per surviving
-- doc. Avoids the multi-version-collision bias where three "AHV Administration
-- Guide" docs each contribute a query with an identical "What is ...?" prompt.
WITH dedup_docs AS (
    SELECT DISTINCT ON (regexp_replace(d.title, '\s*v\d+([._]\d+)*\.?$', '', 'i'))
           d.id, d.slug, d.title, d.category
    FROM documents d
    WHERE d.category = '$cat'
    ORDER BY regexp_replace(d.title, '\s*v\d+([._]\d+)*\.?$', '', 'i'),
             md5(d.id || '$Seed')
)
SELECT DISTINCT ON (c.document_id)
       c.id, c.document_id, COALESCE(c.section_title,''), c.parent_chunk_id, dd.slug, dd.title, dd.category, c.token_count
FROM chunks c
JOIN dedup_docs dd ON dd.id = c.document_id
WHERE c.chunk_kind = 'leaf'
  AND c.token_count BETWEEN 200 AND 700
  AND COALESCE(c.section_title,'') NOT IN ($boilerplateLiteral)
  AND c.raw_content NOT LIKE '\`\`\`%'
ORDER BY c.document_id, md5(c.id::text || '$Seed')
LIMIT $quota;
"@
    $rows = Invoke-PsqlJson -Sql $sql | Where-Object { $_ -match '^\d+\|' }
    Write-Host "[seed] $cat -> $($rows.Count) chunks"

    foreach ($line in $rows) {
        $parts = $line -split '\|'
        if ($parts.Count -lt 7) { continue }
        $chunkId = [int64]$parts[0]
        $docId = $parts[1]
        $section = $parts[2]
        $parentId = $parts[3]
        $slug = $parts[4]
        $docTitle = $parts[5]
        $category = $parts[6]

        $qtext = Templated-Query -DocTitle $docTitle -SectionTitle $section -Category $category
        if (-not $qtext) { continue }

        # Multi-positive set:
        #   - source chunk (always valid by construction)
        #   - source's parent chunk (parent is the section-level summary)
        #   - up to 4 sibling leaves in the same document by lowest token_count
        #     ranks first (overview-style chunks tend to be shorter)
        # Keeps "What is X?" queries forgiving — any of the doc's overview
        # chunks should satisfy a doc-level question.
        $expectIds = New-Object System.Collections.Generic.List[int64]
        [void]$expectIds.Add($chunkId)
        if ($parentId -and $parentId -ne '') {
            [void]$expectIds.Add([int64]$parentId)
        }
        $sibQuery = @"
SELECT c.id
FROM chunks c
WHERE c.document_id = '$($docId -replace "'", "''")'
  AND c.chunk_kind = 'leaf'
  AND c.id <> $chunkId
ORDER BY c.token_count ASC, c.chunk_index ASC
LIMIT 20;
"@
        $sibLines = Invoke-PsqlJson -Sql $sibQuery
        foreach ($s in $sibLines) {
            $sid = $s.Trim()
            if ($sid -match '^\d+$') {
                [void]$expectIds.Add([int64]$sid)
            }
        }

        $qid += 1
        $queries.Add(@{
            query_id          = "$cat-{0:D3}" -f $qid
            query             = $qtext
            expect_chunk_ids  = $expectIds.ToArray()
            expect_doc        = $slug
            expect_section    = $section
            expect_product    = $null
            category          = $category
            difficulty        = 'easy'
        })
    }
}

if ($queries.Count -lt 30) {
    Write-Warning "Only $($queries.Count) queries sampled — below the 30-query floor"
}

$payload = [ordered]@{
    schema_version = 2
    corpus_sha     = $corpusSha
    queries        = $queries
}

$json = $payload | ConvertTo-Json -Depth 6
Set-Content -LiteralPath $OutPath -Value $json -Encoding utf8 -NoNewline
Write-Host "[seed] wrote $($queries.Count) queries -> $OutPath"
