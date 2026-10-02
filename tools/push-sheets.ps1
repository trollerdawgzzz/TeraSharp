# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
  T237 - copy a named set of sheets to another folder, taking a .bak per file first.

.DESCRIPTION
  The thing that keeps getting hand-written after a sheet job: "copy these six files to the box".
  Give it the names, or the text a script printed, and it does the copy with one backup per file
  named for the job tag - the same convention the sheet tools use (.t220.bak, .t236b.bak, .m5.bak).

  -ListFile reads a FILE OF TEXT, not a tidy list: it pulls every token that looks like a sheet
  name out of whatever is in there, so the tail of a check-box run, a section of OPERATIONS.md or
  a pasted console log all work. Order is kept and duplicates are dropped.

  Dry run unless -Apply. Nothing is copied if any named file is missing from -From.

.PARAMETER Files
  Sheet names, comma-separated or as an array. Names only - the folders come from -From / -To.

.PARAMETER ListFile
  A text file to scrape names out of instead.

.PARAMETER From
  Where the good copies are. Default D:\v100\TERA_SERVER.100\Executable\Datasheet.

.PARAMETER To
  Where they go - the box's Datasheet, or a staging folder.

.PARAMETER Tag
  Backup suffix, so a file becomes <name>.<tag>.bak. Default 't237'.

.PARAMETER Force
  Overwrite an existing <name>.<tag>.bak. Off by default: the FIRST backup is the one worth having.

.EXAMPLE
  .\push-sheets.ps1 -To E:\box\Datasheet -Files AnimationData.xml,UserSkillData_Engineer_Popori_M.xml
  .\check-box.ps1 -Box E:\box\Datasheet > drift.txt ; .\push-sheets.ps1 -To E:\box\Datasheet -ListFile drift.txt -Tag t236c -Apply
#>
[CmdletBinding()]
param(
    [string[]] $Files,
    [string] $ListFile,
    [string] $From = 'D:\v100\TERA_SERVER.100\Executable\Datasheet',
    [Parameter(Mandatory = $true)] [string] $To,
    [string] $Tag = 't237',
    [switch] $Force,
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
foreach ($d in @($From, $To)) { if (-not (Test-Path -LiteralPath $d)) { throw "not a directory: $d" } }
if ($Tag -notmatch '^[A-Za-z0-9_.\-]+$') { throw "not a usable backup tag: '$Tag'" }

# A sheet name, anywhere in the text: the extensions the Datasheet actually holds. Backup names are
# dropped - pushing a .bak to the box is never what was meant.
$NamePattern = '[A-Za-z0-9_\-\.]+\.(?:xml|condition|quest|dat|bin)'

$wanted = New-Object System.Collections.ArrayList
$seen = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
function Add-Name([string] $n) {
    if (-not $n) { return }
    $n = Split-Path $n -Leaf
    if ($n -match '\.(bak|orig|stock)$') { return }
    if ($seen.Add($n)) { [void]$wanted.Add($n) }
}
$typed = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($chunk in $Files) { foreach ($one in ($chunk -split '[,;\s]+')) { Add-Name $one; if ($one) { [void]$typed.Add((Split-Path $one -Leaf)) } } }
if ($ListFile) {
    if (-not (Test-Path -LiteralPath $ListFile)) { throw "not found: $ListFile" }
    foreach ($m in [regex]::Matches([IO.File]::ReadAllText($ListFile), $NamePattern)) { Add-Name $m.Value }
}
if ($wanted.Count -eq 0) { throw 'nothing to push - pass -Files or a -ListFile that names some sheets' }

Write-Host ("from : {0}" -f $From)
Write-Host ("to   : {0}" -f $To)
Write-Host ("tag  : .{0}.bak" -f $Tag)
Write-Host ''

$plan = @()
$missing = @()
$skipped = @()
foreach ($name in $wanted) {
    $src = Join-Path $From $name
    if (-not (Test-Path -LiteralPath $src)) {
        # A name you typed and got wrong stops the run. A name scraped out of -ListFile may simply
        # be noise from the surrounding text (a BOX-only row, a sentence), so it is reported and
        # skipped rather than treated as a mistake.
        if ($typed.Contains($name)) { $missing += $name } else { $skipped += $name }
        continue
    }
    $dst = Join-Path $To $name
    $bak = "$dst.$Tag.bak"
    $exists = Test-Path -LiteralPath $dst
    $same = $false
    if ($exists) {
        $s = Get-Item -LiteralPath $src; $d = Get-Item -LiteralPath $dst
        if ($s.Length -eq $d.Length) {
            $same = ((Get-FileHash -LiteralPath $src -Algorithm SHA256).Hash -eq
                     (Get-FileHash -LiteralPath $dst -Algorithm SHA256).Hash)
        }
    }
    $note = if (-not $exists) { 'new file, no backup needed' }
            elseif ($same) { 'already identical - skipped' }
            elseif (Test-Path -LiteralPath $bak) { if ($Force) { ".$Tag.bak exists - will be OVERWRITTEN (-Force)" } else { ".$Tag.bak exists - kept" } }
            else { ".$Tag.bak will be written" }
    $plan += [pscustomobject]@{ Name = $name; Src = $src; Dst = $dst; Bak = $bak; Exists = $exists; Same = $same; Note = $note
                                Bytes = (Get-Item -LiteralPath $src).Length }
}

foreach ($s2 in $skipped) { Write-Host ("  not in -From, skipped : {0}" -f $s2) }
if ($missing.Count -gt 0) {
    foreach ($m in $missing) { Write-Host ("  MISSING in -From : {0}" -f $m) }
    throw ("{0} named file(s) are not in {1} - nothing copied" -f $missing.Count, $From)
}

foreach ($p in $plan) { Write-Host ("  {0,-46} {1,10}  {2}" -f $p.Name, $p.Bytes, $p.Note) }

$todo = @($plan | Where-Object { -not $_.Same })
Write-Host ''
if ($todo.Count -eq 0) { Write-Host 'nothing to do - every file is already identical at the target.'; exit 0 }
if (-not $Apply) {
    Write-Host ("{0} file(s), {1:N1} MB would be copied. Add -Apply." -f $todo.Count, (($todo | Measure-Object -Property Bytes -Sum).Sum / 1MB))
    exit 0
}
foreach ($p in $todo) {
    if ($p.Exists -and ((-not (Test-Path -LiteralPath $p.Bak)) -or $Force)) {
        Copy-Item -LiteralPath $p.Dst -Destination $p.Bak -Force
    }
    Copy-Item -LiteralPath $p.Src -Destination $p.Dst -Force
    Write-Host ("  copied {0}" -f $p.Name)
}
Write-Host ''
Write-Host ("{0} file(s) copied. World must be stopped for a Datasheet push; restart it, then run" -f $todo.Count)
Write-Host 'tools\check-class-rows.ps1 <the target Datasheet> - it should come back 0.'
exit 0
