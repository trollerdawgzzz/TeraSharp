# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors
<#
.SYNOPSIS
    T160/T162 - data-only level-70 start: lift the ClassException teleport gate and the
    RestrictionOpen CBT row, and optionally put a level-70 jump scroll in every new character's
    starting bag. -Revert undoes all of it.

.DESCRIPTION
    Gate. World's User::CheckTeleportClassException looks up ClassException.xml by
    (class, level) and refuses any teleport (@3301) to a continent other than
    TeleportRestriction@continentId until questId/taskId is complete. In this server's sheet the
    only such rows are the two Reaper ("soulless") conditions, levels 1-57 and 58-70:
        <TeleportRestriction continentId="7087" questId="8708" taskId="3"/>
    They are commented out, not deleted. No other class has a TeleportRestriction row, so this
    does NOT affect the Valkyrie ("glaiver") characters of cap_play1 - see status/QUEST-DESIGN.md
    T160.

    RestrictionOpen (T162). ServerConfig.xml maps RestrictionOpenData to Datasheet\RestrictionOpen.xml.
    Its only travel row is the CBT block <RestrictionOpen level="1"> (PegasusException closes ten
    pegasus routes; HuntingZone/BuyList/Territory/Collection exceptions). World applies a level-N
    block only when <WorldServerConfig restrictionOpenLevel="N"> (IsAppliableRestriction: level 0
    always, else == restrictionOpenLevel), so the lift is restrictionOpenLevel="0". This server
    ships 0 already: 0 edits. No RestrictionOpen row gates teleport - see QUEST-DESIGN.md T162.

    Scroll (-StarterScroll). Adds <InitItem itemTemplateId="207631" ... amount="1"/> (the
    "70 level jump scroll", PERFECT_LEVEL_JUMPING_UP, combatItemArg1=70, usable at levels 1-64)
    to every <Char> in CreateCharData.xml. Both servers read it: ArbiterServer.exe, and TeraSharp
    since T159/T162 (starter items and createdLevel). TeraSharp answers the scroll's
    SDB_INCREMENT_CHARACTER_LEVEL_PERFECT_JUMP (0x28DD) since T162; World then levels the
    character itself. Opt-in: it changes every new character.

    BOM-safe: each file is read as bytes, a UTF-8 BOM is kept if it was there, line endings are
    kept, and only the edited spans change. Comments are skipped. Originals go to
    <Executable>\level70-start-backup\ (outside Datasheet\, which the servers load recursively)
    with a SHA-256 manifest. Stop the servers first; sheets are read at boot.

.PARAMETER Executable
    The server folder holding Datasheet\.

.PARAMETER Revert
    Restore every file from the backup, check it against the original hash, retire the backup.

.PARAMETER StarterScroll
    Also add the jump scroll to CreateCharData.xml (read by both servers, see above).

.PARAMETER ScrollItemId
    The item to hand out. Default 207631. It is checked against ItemTemplate.xml first.

.PARAMETER Force
    Apply over an existing backup, revert over files changed since the edit.

.EXAMPLE
    .\level70-start.ps1 -WhatIf
    .\level70-start.ps1
    .\level70-start.ps1 -StarterScroll
    .\level70-start.ps1 -Revert
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Executable = 'D:\v100\TERA_SERVER.100\Executable',
    [switch] $Revert,
    [switch] $StarterScroll,
    [int]    $ScrollItemId = 207631,
    [switch] $Force
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$Executable   = (Resolve-Path -LiteralPath $Executable).Path
$backupRoot   = Join-Path $Executable 'level70-start-backup'
$manifestPath = Join-Path $backupRoot 'manifest.json'
$utf8NoBom    = New-Object System.Text.UTF8Encoding($false)
$singleLine   = [System.Text.RegularExpressions.RegexOptions]::Singleline

function Read-Sheet([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $skip = 0
    if ($hasBom) { $skip = 3 }
    [pscustomobject]@{ Text = $utf8NoBom.GetString($bytes, $skip, $bytes.Length - $skip); Bom = $hasBom }
}

function Write-Sheet([string]$path, [string]$text, [bool]$hasBom) {
    $body = $utf8NoBom.GetBytes($text)
    if ($hasBom) {
        $all = New-Object byte[] ($body.Length + 3)
        $all[0] = 0xEF; $all[1] = 0xBB; $all[2] = 0xBF
        [Array]::Copy($body, 0, $all, 3, $body.Length)
    } else { $all = $body }
    [System.IO.File]::WriteAllBytes($path, $all)
}

function Get-Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

function Get-CommentRanges([string]$text) {
    $r = New-Object System.Collections.Generic.List[object]
    foreach ($m in [regex]::Matches($text, '<!--.*?-->', $singleLine)) { $r.Add(@($m.Index, ($m.Index + $m.Length))) }
    ,$r
}

function Test-InComment($ranges, [int]$index) {
    foreach ($c in $ranges) { if ($index -ge $c[0] -and $index -lt $c[1]) { return $true } }
    $false
}

# Replace matches right to left so earlier indexes stay valid.
function Edit-Matches([string]$text, [string]$pattern, [scriptblock]$replace) {
    $ranges = Get-CommentRanges $text
    $hits = @([regex]::Matches($text, $pattern, $singleLine) | Where-Object { -not (Test-InComment $ranges $_.Index) })
    [Array]::Reverse($hits)
    $n = 0
    foreach ($m in $hits) {
        $new = & $replace $m
        if ($null -eq $new -or $new -ceq $m.Value) { continue }
        $text = $text.Substring(0, $m.Index) + $new + $text.Substring($m.Index + $m.Length)
        $n++
    }
    [pscustomobject]@{ Text = $text; Count = $n }
}

# ------------------------------------------------------------------------------------- revert

if ($Revert) {
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "No $manifestPath - nothing to revert." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $bad = 0
    foreach ($f in @($manifest.Files)) {
        $target = Join-Path $Executable $f.File
        $backup = Join-Path $backupRoot $f.File
        if (-not (Test-Path -LiteralPath $backup)) { Write-Warning "missing backup $backup"; $bad++; continue }
        if ((Test-Path -LiteralPath $target) -and (Get-Sha $target) -ne $f.TunedSha256 -and -not $Force) {
            Write-Warning "$($f.File) changed since the edit - left as is (-Force to restore anyway)"
            $bad++; continue
        }
        if ($PSCmdlet.ShouldProcess($target, 'restore original')) {
            Copy-Item -LiteralPath $backup -Destination $target -Force
            if ((Get-Sha $target) -ne $f.OriginalSha256) { Write-Warning "$($f.File): restored hash does not match the original"; $bad++ }
            else { Write-Host ("restored  {0}" -f $f.File) }
        }
    }
    if ($bad -eq 0 -and -not $WhatIfPreference) {
        $retired = $backupRoot + '.reverted-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
        Rename-Item -LiteralPath $backupRoot -NewName (Split-Path $retired -Leaf)
        Write-Host "Backup retired to $retired. Restart ArbiterServer and WorldServer."
    } elseif ($bad -gt 0) { Write-Warning "$bad file(s) not restored cleanly; backup kept."; exit 1 }
    return
}

# -------------------------------------------------------------------------------------- apply

if ((Test-Path -LiteralPath $manifestPath) -and -not $Force) {
    throw "Already applied ($manifestPath). Run -Revert first, or -Force."
}
$running = @(Get-Process -Name ArbiterServer, WorldServer -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Warning ("Running now: " + (($running | ForEach-Object { $_.ProcessName }) -join ', ') + ". Restart them afterwards.")
}

$plan = New-Object System.Collections.Generic.List[object]

# 1. ClassException.xml - comment out every live TeleportRestriction element.
$plan.Add([pscustomobject]@{
    File = 'Datasheet\ClassException.xml'
    Edit = {
        param($text)
        Edit-Matches $text '<TeleportRestriction\b[^>]*/>' { param($m) '<!--T160 ' + $m.Value + ' -->' }
    }
})

# 2. ServerConfig.xml - restrictionOpenLevel back to 0, which retires the level="1" CBT row.
$plan.Add([pscustomobject]@{
    File = 'ServerConfig.xml'
    Edit = {
        param($text)
        Edit-Matches $text '(<WorldServerConfig\b[^>]*?\brestrictionOpenLevel=")(\d+)(")' {
            param($m)
            if ($m.Groups[2].Value -eq '0') { return $m.Value }
            $m.Groups[1].Value + '0' + $m.Groups[3].Value
        }
    }
})

# 3. CreateCharData.xml - one scroll per <Char>, before its <InitMoney>.
if ($StarterScroll) {
    $itemTemplate = Join-Path $Executable 'Datasheet\ItemTemplate.xml'
    $hit = Select-String -LiteralPath $itemTemplate -SimpleMatch ('<Item id="{0}" ' -f $ScrollItemId) -List
    if (-not $hit) { throw "Item $ScrollItemId is not in $itemTemplate." }
    if ($hit.Line -notmatch 'combatItemType="(PERFECT_)?LEVEL_JUMPING_UP"') {
        Write-Warning "Item $ScrollItemId is not a LEVEL_JUMPING_UP item - handing it out anyway."
    }
    $id = $ScrollItemId
    $plan.Add([pscustomobject]@{
        File = 'Datasheet\CreateCharData.xml'
        Edit = {
            param($text)
            $nl = "`n"; if ($text.Contains("`r`n")) { $nl = "`r`n" }
            Edit-Matches $text '<Char\b[^>]*>.*?</Char>' {
                param($m)
                $blk = $m.Value
                if ($blk -match ('itemTemplateId="{0}"' -f $id)) { return $blk }
                $im = [regex]::Match($blk, '(?m)^([ \t]*)<InitMoney\b')
                if (-not $im.Success) { return $blk }
                $line = ('{0}<InitItem itemTemplateId="{1}" initWear="false" amount="1" /><!--T160 level-70 jump scroll-->{2}' -f $im.Groups[1].Value, $id, $nl)
                $blk.Substring(0, $im.Index) + $line + $blk.Substring($im.Index)
            }
        }
    })
}

$files = New-Object System.Collections.Generic.List[object]
foreach ($p in $plan) {
    $path = Join-Path $Executable $p.File
    if (-not (Test-Path -LiteralPath $path)) { Write-Warning "missing $path"; continue }
    $sheet = Read-Sheet $path
    $r = & $p.Edit $sheet.Text
    Write-Host ("{0,-32} {1} edit(s)" -f $p.File, $r.Count)
    if ($r.Count -eq 0) { continue }
    $backup = Join-Path $backupRoot $p.File
    if ($PSCmdlet.ShouldProcess($path, "$($r.Count) edit(s)")) {
        $original = Get-Sha $path
        if (-not (Test-Path -LiteralPath $backup)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $backup -Parent) | Out-Null
            Copy-Item -LiteralPath $path -Destination $backup
        }
        Write-Sheet $path $r.Text $sheet.Bom
        $files.Add([pscustomobject]@{ File = $p.File; OriginalSha256 = $original; TunedSha256 = (Get-Sha $path) })
    }
}

if ($files.Count -gt 0 -and -not $WhatIfPreference) {
    [pscustomobject]@{ Applied = (Get-Date).ToString('s'); StarterScroll = [bool]$StarterScroll; Files = $files } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Host "Backups and manifest: $backupRoot"
}
Write-Host 'Restart ArbiterServer and WorldServer. Revert with: .\level70-start.ps1 -Revert'
