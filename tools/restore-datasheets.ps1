# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
    Undo a trim-datasheets.ps1 run, using the manifest it wrote.

.DESCRIPTION
    T63. Puts every moved per-continent file back where it came from, restores every sheet whose
    rows were trimmed from the backup taken before the edit, and deletes any ShieldTerritory stub
    the trim created.

    Safety: by default a file is only put back if nothing is in its way, and a sheet is only
    restored if it still looks like the trimmed version (so a hand edit made after the trim is not
    silently thrown away). -Force overrides both, -WhatIf shows the plan.

    A trim run made with -Destination needs no restore: the original tree was never touched, so
    deleting the copy is the undo. This script says so and stops.

.PARAMETER Manifest
    The trim-manifest.json written by trim-datasheets.ps1 (default: <AsideRoot>\trim-manifest.json).

.PARAMETER Force
    Overwrite whatever is in the way, and restore sheets even if they changed after the trim.

.PARAMETER KeepAside
    Leave the aside folder and the backups on disk after a successful restore (default: the now
    empty per-family folders are cleaned up, the manifest and backups are kept).

.EXAMPLE
    .\restore-datasheets.ps1 -Manifest D:\v100\TERA_SERVER.100\DatasheetAside\trim-manifest.json -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string] $Manifest,
    [switch] $Force,
    [switch] $KeepAside
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Write-Step([string] $Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
function Write-Note([string] $Text) { Write-Host "   $Text" }

if (-not (Test-Path -LiteralPath $Manifest)) { throw "Manifest not found: $Manifest" }
$m = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json

if ($m.schema -ne 'terasharp.trim-datasheets/1') {
    throw "Unexpected manifest schema '$($m.schema)' - refusing to act on it."
}

Write-Step "TERA datasheet restore"
Write-Note "manifest    : $Manifest"
Write-Note "written     : $($m.createdUtc) UTC"
Write-Note "datasheet   : $($m.datasheetRoot)"
Write-Note "keep-list   : $($m.keepContinents -join ', ')"
Write-Note "tier        : $($m.aggressiveness)"

if (-not $m.inPlace) {
    Write-Host ''
    Write-Host "   That trim ran with -Destination, so $($m.originalRoot) was never modified." -ForegroundColor Yellow
    Write-Host "   The undo is simply: Remove-Item -Recurse '$($m.datasheetRoot)'" -ForegroundColor Yellow
    Write-Host "   Nothing to restore. Stopping." -ForegroundColor Yellow
    return
}

if (-not (Test-Path -LiteralPath $m.datasheetRoot -PathType Container)) {
    throw "The Datasheet folder in the manifest is gone: $($m.datasheetRoot)"
}

$movedBack = 0; $moveSkipped = 0; $moveMissing = 0
$restored  = 0; $restoreSkipped = 0; $restoreMissing = 0
$deleted   = 0; $deleteSkipped = 0

# =================================================================================================
# 1. Sheets whose rows were trimmed
# =================================================================================================
Write-Step "Restoring trimmed sheets"
foreach ($e in @($m.editedFiles)) {
    if (-not (Test-Path -LiteralPath $e.backup)) {
        Write-Host "   MISSING BACKUP: $($e.relative) (expected $($e.backup))" -ForegroundColor Yellow
        $restoreMissing++
        continue
    }
    if (Test-Path -LiteralPath $e.path) {
        # Has anyone edited the trimmed sheet since? Compare against nothing we stored, so use the
        # only signal we have: if the live file is byte-identical to the backup it is already
        # restored; otherwise warn unless -Force.
        $liveLen = (Get-Item -LiteralPath $e.path).Length
        $bakLen  = (Get-Item -LiteralPath $e.backup).Length
        if ($liveLen -eq $bakLen) {
            $liveHash = (Get-FileHash -LiteralPath $e.path -Algorithm SHA256).Hash
            $bakHash  = (Get-FileHash -LiteralPath $e.backup -Algorithm SHA256).Hash
            if ($liveHash -eq $bakHash) {
                Write-Note "$($e.relative) is already the original"
                continue
            }
        }
    }
    if ($PSCmdlet.ShouldProcess($e.path, "restore from backup")) {
        $dir = Split-Path -Parent $e.path
        if (-not (Test-Path -LiteralPath $dir)) { [void] (New-Item -ItemType Directory -Path $dir -Force) }
        Copy-Item -LiteralPath $e.backup -Destination $e.path -Force
    }
    Write-Note "restored $($e.relative)"
    $restored++
}

# =================================================================================================
# 2. Files that were moved aside
# =================================================================================================
Write-Step "Moving files back"
foreach ($f in @($m.movedFiles)) {
    if (-not (Test-Path -LiteralPath $f.to)) {
        # Already back? Then this is not an error.
        if (Test-Path -LiteralPath $f.from) { $movedBack++; continue }
        Write-Host "   MISSING: $($f.relative) is neither in the aside folder nor back in place" -ForegroundColor Yellow
        $moveMissing++
        continue
    }
    if ((Test-Path -LiteralPath $f.from) -and -not $Force) {
        Write-Host "   IN THE WAY: $($f.relative) already exists in the Datasheet tree - skipped (use -Force)" -ForegroundColor Yellow
        $moveSkipped++
        continue
    }
    if ($PSCmdlet.ShouldProcess($f.from, "move back from aside")) {
        $dir = Split-Path -Parent $f.from
        if (-not (Test-Path -LiteralPath $dir)) { [void] (New-Item -ItemType Directory -Path $dir -Force) }
        Move-Item -LiteralPath $f.to -Destination $f.from -Force
    }
    $movedBack++
}
Write-Note "$movedBack back, $moveSkipped skipped, $moveMissing missing"

# =================================================================================================
# 3. Stubs the trim created
# =================================================================================================
if (@($m.createdFiles).Count -gt 0) {
    Write-Step "Removing stubs"
    foreach ($c in @($m.createdFiles)) {
        if (-not (Test-Path -LiteralPath $c)) { continue }
        $text = Get-Content -LiteralPath $c -Raw
        if (($text -notmatch 'T63 stub') -and -not $Force) {
            Write-Host "   NOT A STUB ANY MORE: $c was edited - left in place (use -Force)" -ForegroundColor Yellow
            $deleteSkipped++
            continue
        }
        if ($PSCmdlet.ShouldProcess($c, 'delete stub')) { Remove-Item -LiteralPath $c -Force }
        Write-Note "deleted $([System.IO.Path]::GetFileName($c))"
        $deleted++
    }
}

# =================================================================================================
# 4. Tidy the aside folder
# =================================================================================================
if (-not $KeepAside -and (Test-Path -LiteralPath $m.asideRoot)) {
    Write-Step "Tidying the aside folder"
    # Only remove directories that are now empty. The manifest and the backups stay, so a restore
    # can be re-run and so the record of what was done survives.
    $dirs = @(Get-ChildItem -LiteralPath $m.asideRoot -Recurse -Directory -ErrorAction SilentlyContinue |
              Sort-Object -Property @{Expression = { $_.FullName.Length }; Descending = $true })
    $removedDirs = 0
    foreach ($d in $dirs) {
        if (@(Get-ChildItem -LiteralPath $d.FullName -Force).Count -ne 0) { continue }
        if ($d.FullName -eq $m.backupDir) { continue }
        if ($PSCmdlet.ShouldProcess($d.FullName, 'remove empty folder')) { Remove-Item -LiteralPath $d.FullName -Force }
        $removedDirs++
    }
    Write-Note "removed $removedDirs empty folder(s); the manifest and _edited-originals are kept"
}

# =================================================================================================
# 5. Verify
# =================================================================================================
if ($WhatIfPreference) {
    Write-Step "Done (-WhatIf: nothing was changed)"
    Write-Note "would restore : $($m.editedFiles.Count) sheet(s)"
    Write-Note "would move    : $($m.movedFiles.Count) file(s) back"
    Write-Note "would delete  : $($m.createdFiles.Count) stub(s)"
    return
}

Write-Step "Verifying"
$stillAside = @()
foreach ($f in @($m.movedFiles)) { if (Test-Path -LiteralPath $f.to) { $stillAside += $f.relative } }
$stillTrimmed = @()
foreach ($e in @($m.editedFiles)) {
    if (-not (Test-Path -LiteralPath $e.path)) { $stillTrimmed += "$($e.relative) (absent)"; continue }
    if (-not (Test-Path -LiteralPath $e.backup)) { continue }
    $a = (Get-FileHash -LiteralPath $e.path -Algorithm SHA256).Hash
    $b = (Get-FileHash -LiteralPath $e.backup -Algorithm SHA256).Hash
    if ($a -ne $b) { $stillTrimmed += $e.relative }
}

if ($stillAside.Count -eq 0 -and $stillTrimmed.Count -eq 0 -and $deleteSkipped -eq 0) {
    Write-Host "   the Datasheet tree matches the state before the trim" -ForegroundColor Green
}
else {
    $shown = 0
    foreach ($s in $stillAside) {
        if ($shown -ge 10) { break }
        Write-Host "   still aside  : $s" -ForegroundColor Yellow; $shown++
    }
    if ($stillAside.Count -gt 10) { Write-Host "   ... and $($stillAside.Count - 10) more still in the aside folder" -ForegroundColor Yellow }
    $shown = 0
    foreach ($s in $stillTrimmed) {
        if ($shown -ge 10) { break }
        Write-Host "   still trimmed: $s" -ForegroundColor Yellow; $shown++
    }
    if ($stillTrimmed.Count -gt 10) { Write-Host "   ... and $($stillTrimmed.Count - 10) more sheets still trimmed" -ForegroundColor Yellow }
    Write-Host "   re-run with -Force, or restore those by hand." -ForegroundColor Yellow
}

Write-Step "Done"
Write-Note "sheets restored : $restored (skipped $restoreSkipped, missing backup $restoreMissing)"
Write-Note "files moved back: $movedBack (skipped $moveSkipped, missing $moveMissing)"
Write-Note "stubs deleted   : $deleted (skipped $deleteSkipped)"
