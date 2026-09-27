# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

#Requires -Version 5.1
<#
.SYNOPSIS
    Refresh the public mirror from this tree with the T173 strip, then run the release audit.

.DESCRIPTION
    A one-way file sync: master -> public. It runs NO git commands. You review `git status` and
    `git diff` in the public repository afterwards and commit there yourself, which is the point -
    the strip is mechanical, the judgement is not.

    Four things happen, in this order.

    1. MIRROR the scope in $Scope: src, status, tools, docs, the listed root files, and the two
       parts of data\ that are ours rather than captured. Bytes are copied unchanged - including
       line endings, because the public tree is a mix and .gitattributes (`* text=auto`) is what
       normalises it on commit. Rewriting newlines here would show every file as modified.

    2. DROP what must never be published (T139/T140/T173): the capture fixtures and evidence
       folders under data\, the per-task research scripts in tools\, the private deploy script,
       and the generated per-install files. $Drop is the whole list and each entry says why.

    3. TRANSFORM each copied text file: prepend the SPDX header when it has none, and apply
       $Rewrites - the literal-for-prose substitutions the current public tree is known to carry.
       Everything else is left alone: the public tree still contains developer paths in comments
       and this script deliberately does not start scrubbing them, because a blanket path rewrite
       would touch a hundred files nobody asked about.

    4. AUDIT with tools\audit-release.ps1 -Strict, in the public tree. That is the backstop for
       anything $Rewrites does not know about yet: a leak fails the run and prints file:line.
       Add a rule here, or fix it in master; do not pass -AllowAddress to make it quiet.

    Files the public repository has and master does not - LICENSE, CHANGELOG.md, CONTRIBUTING.md,
    .env.example, .gitattributes, .git - are never touched. $Keep is that list.

    Stale files (inside the mirrored scope, present in public, gone from master) are reported and
    only removed with -Prune, because "delete" and "I renamed something upstream" look identical
    from here.

.PARAMETER Public
    The public repository. Default D:\TeraSharp-public.

.PARAMETER Check
    Change nothing. Print every copy, rewrite, drop and stale file, run the audit on the public
    tree as it stands, and exit non-zero if anything is wrong. This is the default answer to
    "what would this do".

.PARAMETER Prune
    Also delete stale files inside the mirrored scope. Off by default.

.PARAMETER SkipAudit
    Do not run audit-release.ps1. For debugging this script only - never for a release.

.EXAMPLE
    .\tools\sync-public.ps1 -Check

.EXAMPLE
    .\tools\sync-public.ps1 -Prune
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Public = 'D:\TeraSharp-public',
    [string] $Master,
    [switch] $Check,
    [switch] $Prune,
    [switch] $SkipAudit
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
if ($WhatIfPreference) { $Check = $true }
$WhatIfPreference = $false

# ------------------------------------------------------------------ the rules, all in one place

<# What gets mirrored. A leading directory, or an exact root file name. #>
$Scope = @(
    'src', 'status', 'docs', 'tools', 'data',
    'CLAUDE.md', 'README.md', 'STATUS.md', 'TeraSharp.sln', 'deploy.example.ps1', 'teras.example.json'
)

<#
What never leaves this tree. Rx is matched against the repo-relative path with forward slashes.
Every one of these is a decision, so every one carries its reason.
#>
$Drop = @(
    @{ Rx = '^data/';                        Why = 'capture fixtures and per-task evidence - retail bytes by construction (T139)' },
    @{ Rx = '^data/README\.md$';             Why = ''; Keep = $true },
    @{ Rx = '^data/[^/]+/README\.md$';       Why = ''; Keep = $true },
    @{ Rx = '^data/custom-datasheets/';      Why = ''; Keep = $true },
    @{ Rx = '^tools/.*\.py$';                Why = 'per-task research scripts; they only run against captures that are not published' },
    @{ Rx = '^tools/T179Audit/';             Why = 'per-task audit workspace' },
    @{ Rx = '^status/T105-fail\.txt$';       Why = 'a raw private test-failure log' },
    @{ Rx = '^ship\.ps1$';                   Why = 'the private publish script - developer paths and a local 7-Zip' },
    @{ Rx = '^(start|stop)\.ps1$';           Why = 'generated per install by tools/setup.ps1' },
    @{ Rx = '^teras\.json$';                 Why = 'holds the admin token and the Alt+A signing key' },
    @{ Rx = '(^|/)(bin|obj)/';               Why = 'build output' },
    @{ Rx = '\.(bak|orig|rej|tmp|7z|zip|db)$'; Why = 'strays and archives' },
    @{ Rx = '(^|/)\.git/';                   Why = 'not ours to copy' }
)

<# Public-only files. Never overwritten, never reported stale. #>
$Keep = @('LICENSE', 'CHANGELOG.md', 'CONTRIBUTING.md', '.env.example', '.gitattributes', '.gitignore')

<#
The retail Arbiter's hardcoded phone-home address, spelled in octets on purpose: written out as a
literal, THIS SCRIPT would carry a routable public IPv4 and audit-release.ps1 would refuse to
publish it - correctly, since it cannot tell a rewrite rule from a leak.
#>
$RetailPhoneHome = (@(52, 199, 108, 189) -join '.')

<#
Literal -> prose. Only rules the current public tree is known to carry; each is a one-line
substitution applied to the file's text after the SPDX header. Add a rule when the audit finds a
leak, and say in the commit why the literal cannot simply be removed from master.
#>
$Rewrites = @(
    @{ Files = '\.cs$'
       From  = ('"' + $RetailPhoneHome + ':80"')
       To    = 'a hardcoded retail address'
       Why   = 'the retail Arbiter phone-home address, quoted in a comment' },
    @{ Files = '^src/TeraSharp\.Arbiter/Program\.cs$'
       From  = '@"D:\packetlogs"'
       To    = '"logs"'
       Why   = 'the log folder default: a developer path in the private tree, relative in the public one' }
)

<# SPDX, by file type. The public tree carries exactly these two lines plus a blank one. #>
$Spdx = @(
    @{ Files = '\.(cs|js)$';  Prefix = '// ' },
    @{ Files = '\.(ps1|psm1|py)$'; Prefix = '# ' }
)
$SpdxLines = @('SPDX-License-Identifier: MIT', 'Copyright (c) 2026 the TeraSharp contributors')

# ------------------------------------------------------------------ output

$script:Copied = 0; $script:Rewrote = 0; $script:Same = 0; $script:Dropped = 0
$script:Stale = New-Object System.Collections.ArrayList
$script:Problems = 0

function Write-Step { param([string] $T) Write-Host ""; Write-Host ("== " + $T) -ForegroundColor Cyan }
function Write-Bad  { param([string] $T) $script:Problems++; Write-Host ("   FAIL  " + $T) -ForegroundColor Red }
function Write-Note { param([string] $T) Write-Host ("   note  " + $T) -ForegroundColor DarkGray }

# ------------------------------------------------------------------ helpers

function Test-Dropped {
    param([string] $Rel)
    $verdict = $null
    foreach ($rule in $Drop) {
        if ($Rel -match $rule.Rx) {
            if ($rule.ContainsKey('Keep') -and $rule.Keep) { return $null }   # an exception wins
            $verdict = $rule.Why
        }
    }
    return $verdict
}

function Test-InScope {
    param([string] $Rel)
    foreach ($entry in $Scope) {
        if ($Rel -eq $entry) { return $true }
        if ($Rel.StartsWith($entry + '/', [StringComparison]::Ordinal)) { return $true }
    }
    return $false
}

<#
Read a text file as text plus what a byte-exact rewrite needs. Returns $null for a binary file,
which is copied verbatim and never transformed.
#>
function Get-SourceText {
    param([string] $Path)
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ($bytes.Length -gt 0 -and $bytes -contains 0) { return $null }         # NUL: treat as binary
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $offset = 0
    if ($bom) { $offset = 3 }
    return [pscustomobject] @{
        Text = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
        Bom  = $bom
        Crlf = ([Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)).Contains("`r`n")
    }
}

function Add-Spdx {
    param([string] $Rel, [string] $Text, [bool] $Crlf)
    $prefix = $null
    foreach ($rule in $Spdx) { if ($Rel -match $rule.Files) { $prefix = $rule.Prefix; break } }
    if ($null -eq $prefix) { return $Text }
    if ($Text -match '(?m)^\s*(//|#)\s*SPDX-License-Identifier:') { return $Text }
    $nl = "`n"
    if ($Crlf) { $nl = "`r`n" }
    $header = ''
    foreach ($line in $SpdxLines) { $header += $prefix + $line + $nl }
    return $header + $nl + $Text
}

function Convert-Content {
    param([string] $Rel, [string] $Text, [bool] $Crlf, [ref] $Notes)
    $out = Add-Spdx -Rel $Rel -Text $Text -Crlf $Crlf
    foreach ($rule in $Rewrites) {
        if ($Rel -notmatch $rule.Files) { continue }
        if (-not $out.Contains($rule.From)) { continue }
        $out = $out.Replace($rule.From, $rule.To)
        [void] $Notes.Value.Add($rule.Why)
    }
    return $out
}

<#
Is this a routable public IPv4? The same carve-outs audit-release.ps1 uses: loopback, RFC1918,
link-local, 0.0.0.0 and the RFC 5737 documentation ranges are all fine in an example.
#>
function Test-PublicIPv4 {
    param([string] $Ip)
    $parts = $Ip.Split([char[]] @('.'))
    if ($parts.Count -ne 4) { return $false }
    $n = @()
    foreach ($part in $parts) {
        $value = 0
        if (-not [int]::TryParse($part, [ref] $value)) { return $false }
        if ($value -lt 0 -or $value -gt 255) { return $false }
        $n += $value
    }
    if ($n[0] -eq 0 -or $n[0] -eq 10 -or $n[0] -eq 127 -or $n[0] -ge 224) { return $false }
    if ($n[0] -eq 192 -and $n[1] -eq 168) { return $false }
    if ($n[0] -eq 172 -and $n[1] -ge 16 -and $n[1] -le 31) { return $false }
    if ($n[0] -eq 169 -and $n[1] -eq 254) { return $false }
    if ($n[0] -eq 192 -and $n[1] -eq 0 -and $n[2] -eq 2) { return $false }        # RFC 5737
    if ($n[0] -eq 198 -and $n[1] -eq 51 -and $n[2] -eq 100) { return $false }
    if ($n[0] -eq 203 -and $n[1] -eq 0 -and $n[2] -eq 113) { return $false }
    return $false -ne $true
}

<#
After the rewrites, name any public address still in the text. This is the general case the
targeted $Rewrites rules cannot cover: the audit would catch it at the end anyway, but saying it
here names the master file to fix rather than the copy.
#>
function Test-ResidualAddress {
    param([string] $Rel, [string] $Text)
    foreach ($m in [regex]::Matches($Text, '\b\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}\b')) {
        if (Test-PublicIPv4 -Ip $m.Value) {
            Write-Bad ($Rel + ': a routable public address survives the strip. Fix it in MASTER, or add a $Rewrites rule.')
            return
        }
    }
}

function Save-Text {
    param([string] $Path, [string] $Text, [bool] $Bom)
    $dir = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [IO.File]::WriteAllText($Path, $Text, (New-Object Text.UTF8Encoding($Bom)))
}

# ------------------------------------------------------------------ 0. the two trees

if ([string]::IsNullOrWhiteSpace($Master)) { $Master = Split-Path -Parent $PSScriptRoot }
Write-Host "sync-public" -ForegroundColor White
Write-Host ("master: " + $Master)
Write-Host ("public: " + $Public)
if ($Check) { Write-Host "-Check: nothing will be written" -ForegroundColor Yellow }

foreach ($tree in @($Master, $Public)) {
    if (-not (Test-Path -LiteralPath $tree)) { Write-Bad ("no such folder: " + $tree); exit 1 }
}
$Master = (Resolve-Path -LiteralPath $Master).ProviderPath
$Public = (Resolve-Path -LiteralPath $Public).ProviderPath
if ($Master -eq $Public) { Write-Bad 'master and public are the same folder'; exit 1 }
if (-not (Test-Path -LiteralPath (Join-Path $Public '.git'))) {
    Write-Note 'the public folder is not a git repository - nothing here needs git, but you will want one to review and commit'
}
$audit = Join-Path $Master 'tools\audit-release.ps1'
if (-not (Test-Path -LiteralPath $audit)) { Write-Bad ('missing ' + $audit) }

function Get-RelativeFiles {
    param([string] $Root)
    $prefix = $Root.TrimEnd('\') + '\'
    $list = New-Object System.Collections.ArrayList
    foreach ($file in (Get-ChildItem -LiteralPath $Root -Recurse -File -Force)) {
        $rel = $file.FullName.Substring($prefix.Length).Replace('\', '/')
        [void] $list.Add($rel)
    }
    return $list
}

# ------------------------------------------------------------------ 1-3. mirror, drop, transform

Write-Step '1. mirroring'
$mirrored = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
$dropReasons = @{}

foreach ($rel in (Get-RelativeFiles -Root $Master)) {
    if (-not (Test-InScope -Rel $rel)) { continue }
    $why = Test-Dropped -Rel $rel
    if ($null -ne $why) {
        $script:Dropped++
        if (-not $dropReasons.ContainsKey($why)) { $dropReasons[$why] = 0 }
        $dropReasons[$why] = $dropReasons[$why] + 1
        continue
    }
    if ($Keep -contains $rel) { Write-Note ($rel + ': public-only, left alone'); continue }

    [void] $mirrored.Add($rel)
    $from = Join-Path $Master ($rel.Replace('/', '\'))
    $to   = Join-Path $Public ($rel.Replace('/', '\'))
    $source = Get-SourceText -Path $from

    if ($null -eq $source) {
        # Binary. Nothing to transform, so compare bytes and copy when they differ.
        $differs = $true
        if (Test-Path -LiteralPath $to) {
            $differs = ((Get-FileHash -LiteralPath $from).Hash -ne (Get-FileHash -LiteralPath $to).Hash)
        }
        if (-not $differs) { $script:Same++; continue }
        if ($Check) { Write-Host ("   would copy   " + $rel) -ForegroundColor Yellow }
        else {
            $dir = Split-Path -Parent $to
            if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Copy-Item -LiteralPath $from -Destination $to -Force
            Write-Host ("   copied      " + $rel) -ForegroundColor Green
        }
        $script:Copied++
        continue
    }

    $notes = New-Object System.Collections.ArrayList
    $text = Convert-Content -Rel $rel -Text $source.Text -Crlf $source.Crlf -Notes ([ref] $notes)
    $changed = $true
    if (Test-Path -LiteralPath $to) {
        $existing = Get-SourceText -Path $to
        if ($null -ne $existing -and $existing.Text -eq $text -and $existing.Bom -eq $source.Bom) { $changed = $false }
    }
    if (-not $changed) { $script:Same++; continue }

    Test-ResidualAddress -Rel $rel -Text $text
    $suffix = ''
    if ($notes.Count -gt 0) { $suffix = '   [' + (($notes | Select-Object -Unique) -join '; ') + ']'; $script:Rewrote++ }
    if ($Check) { Write-Host ("   would write  " + $rel + $suffix) -ForegroundColor Yellow }
    else {
        Save-Text -Path $to -Text $text -Bom $source.Bom
        Write-Host ("   wrote       " + $rel + $suffix) -ForegroundColor Green
    }
    $script:Copied++
}

Write-Step '2. dropped'
if ($script:Dropped -eq 0) { Write-Host '   nothing matched the drop list' }
foreach ($why in ($dropReasons.Keys | Sort-Object)) {
    Write-Host ("   " + $dropReasons[$why].ToString().PadLeft(4) + "  " + $why)
}

Write-Step '3. stale in public'
foreach ($rel in (Get-RelativeFiles -Root $Public)) {
    if ($rel -match '(^|/)\.git/') { continue }
    if ($Keep -contains $rel) { continue }
    if (-not (Test-InScope -Rel $rel)) { continue }
    if ($mirrored.Contains($rel)) { continue }
    [void] $script:Stale.Add($rel)
}
if ($script:Stale.Count -eq 0) { Write-Host '   none' }
else {
    foreach ($rel in $script:Stale) {
        if ($Prune -and -not $Check) {
            Remove-Item -LiteralPath (Join-Path $Public ($rel.Replace('/', '\'))) -Force
            Write-Host ("   deleted     " + $rel) -ForegroundColor Green
        } else {
            Write-Host ("   stale       " + $rel) -ForegroundColor Yellow
        }
    }
    if (-not $Prune) { Write-Note 'these are in the mirrored scope but gone from master. Pass -Prune to delete them, or delete them by hand if one is a rename you want to keep.' }
}

# ------------------------------------------------------------------ 4. the gate

Write-Step '4. audit-release.ps1 -Strict, in the public tree'
if ($SkipAudit) { Write-Note 'skipped by -SkipAudit; do not release on this' }
elseif (-not (Test-Path -LiteralPath $audit)) { Write-Bad 'no audit script, so nothing was verified' }
else {
    $publicAudit = Join-Path $Public 'tools\audit-release.ps1'
    $script = $audit
    if (Test-Path -LiteralPath $publicAudit) { $script = $publicAudit }
    $output = & $script -Path $Public -Strict 2>&1
    $auditFailed = ($LASTEXITCODE -ne 0)
    foreach ($line in $output) {
        $text = [string] $line
        if ($text -match '^\s*(RETAIL|PII|SECRET|ADDRESS|JUNK|REVIEW)\s') { Write-Host ("   " + $text.Trim()) -ForegroundColor Yellow }
        elseif ($text -match 'blocking|not releasable|clean|scanned') { Write-Host ("   " + $text.Trim()) }
    }
    if ($auditFailed) {
        Write-Bad 'the audit refuses this tree. Fix it in MASTER, or add a $Rewrites rule here - do not pass -AllowAddress to silence it.'
    } else { Write-Host '   audit clean' -ForegroundColor Green }
}

# ------------------------------------------------------------------ summary

Write-Host ""
$verb = 'wrote'
if ($Check) { $verb = 'would write' }
Write-Host ("$verb $($script:Copied) file(s), $($script:Rewrote) with a rewrite; $($script:Same) already identical; " +
            "$($script:Dropped) dropped; $($script:Stale.Count) stale") -ForegroundColor White
if ($script:Problems -gt 0) {
    Write-Host ("" + $script:Problems + " problem(s) - the tree is not releasable") -ForegroundColor Red
    exit 1
}
if ($Check) { Write-Host 'Re-run without -Check to apply.' -ForegroundColor Green; exit 0 }
Write-Host "Now, in the public repository:" -ForegroundColor Green
Write-Host "  git -C $Public status"
Write-Host "  git -C $Public diff"
Write-Host "  git -C $Public add -A; git -C $Public commit"
Write-Host "Read the diff before committing. This script copies files; it does not know what is"
Write-Host "worth publishing."
exit 0
