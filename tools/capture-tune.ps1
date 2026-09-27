# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
    T149 - temporary, capture-only datasheet tweaks so two or three GM characters can drive guild
    war, guild quests and Civil Unrest end to end on the real 100.02 servers. -Revert undoes them.

.DESCRIPTION
    Every edit is one attribute on one element, checked against the value this project read out
    of the shipped sheet before it is changed (status/CAPTURE-T149-GUILD.md has the table and
    the reasoning). A value that is not the expected original is reported and left alone unless
    -Force is given, so a sheet somebody already edited is never silently overwritten.

    BOM-safe: each file is read as bytes, a UTF-8 BOM is kept if it was there, and the text is
    written back with the same bytes everywhere except the attribute values. Line endings are
    untouched. Comments are skipped - GuildConfig.xml carries a commented-out GuildWarTime line
    that must not be the one edited.

    The originals are copied to <Executable>\capture-tune-backup\ before anything is written,
    with a manifest of SHA-256 hashes. That folder is deliberately OUTSIDE Datasheet\: both
    servers walk the Datasheet tree recursively, so a backup inside it would be loaded too.

    Datasheets are read at boot. Stop ArbiterServer and WorldServer before applying or
    reverting, and start them again afterwards.

.PARAMETER Executable
    The server folder holding Datasheet\ and DeploymentConfig.xml.

.PARAMETER Revert
    Put every file back from the backup, verify each against its original hash, and retire the
    backup folder (renamed with a timestamp, not deleted).

.PARAMETER Part
    Which groups to apply: GuildWar, GuildQuest, CityWar. Default all three.

.PARAMETER CityWarLeadMinutes
    Civil Unrest starts at the first full hour at least this many minutes from now (local time,
    the clock the servers use). With the sheet's 120-minute advance entry, entry opens at once.

.PARAMETER CityWarPlayMinutes
    Civil Unrest battle length (OpenTime@playTimeMin). Default 30.

.PARAMETER BattleChipAllDay
    Also widen every GuildWar.xml BattleChipTime window to 00-24. Off by default: what the window
    gates is not traced, only named.

.PARAMETER QaServer
    Also flip DeploymentConfig.xml ArbiterServerConfig@qaServer to true, which makes the loaders
    pick every *Qa attribute (GuildWar, BattleChipData, matching rules, floating castle). Off by
    default - it is global and its other side effects are not verified.

.PARAMETER Force
    Apply over an existing backup, over unexpected original values, and revert over files changed
    since tuning.

.EXAMPLE
    .\capture-tune.ps1 -WhatIf
    .\capture-tune.ps1
    .\capture-tune.ps1 -Part CityWar -CityWarLeadMinutes 20
    .\capture-tune.ps1 -Revert
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]   $Executable = 'D:\v100\TERA_SERVER.100\Executable',
    [switch]   $Revert,
    [ValidateSet('GuildWar', 'GuildQuest', 'CityWar')]
    [string[]] $Part = @('GuildWar', 'GuildQuest', 'CityWar'),
    [int]      $CityWarLeadMinutes = 15,
    [int]      $CityWarPlayMinutes = 30,
    [switch]   $BattleChipAllDay,
    [switch]   $QaServer,
    [switch]   $Force
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$backupRoot   = Join-Path $Executable 'capture-tune-backup'
$manifestPath = Join-Path $backupRoot 'manifest.json'
$utf8NoBom    = New-Object System.Text.UTF8Encoding($false)
$singleLine   = [System.Text.RegularExpressions.RegexOptions]::Singleline

# ------------------------------------------------------------------------------------------ io

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
    } else {
        $all = $body
    }
    [System.IO.File]::WriteAllBytes($path, $all)
}

function Get-Sha([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }

# ------------------------------------------------------------------------------ attribute edit

function Get-CommentRanges([string]$text) {
    $ranges = New-Object System.Collections.Generic.List[int[]]
    foreach ($m in [regex]::Matches($text, '<!--.*?-->', $singleLine)) {
        $ranges.Add([int[]]@($m.Index, ($m.Index + $m.Length)))
    }
    , $ranges
}

function Test-InComment($ranges, [int]$index) {
    foreach ($r in $ranges) { if ($index -ge $r[0] -and $index -lt $r[1]) { return $true } }
    $false
}

# Returns the edited text and appends one result row per touched element to $results.
function Set-SheetAttribute([string]$text, $edit, $results) {
    $comments = Get-CommentRanges $text
    $regionStart = 0
    $regionEnd = $text.Length
    if ($edit.Scope) {
        $scope = $null
        foreach ($m in [regex]::Matches($text, $edit.Scope, $singleLine)) {
            if (-not (Test-InComment $comments $m.Index)) { $scope = $m; break }
        }
        if (-not $scope) {
            $results.Add([pscustomobject]@{ Edit = $edit; Old = '(scope not found)'; New = $edit.Value; Status = 'MISSING' })
            return $text
        }
        $regionStart = $scope.Index
        $regionEnd = $scope.Index + $scope.Length
    }

    $tagPattern = '<' + $edit.Tag + '\b[^>]*>'
    $attrPattern = '\b' + [regex]::Escape($edit.Attr) + '="([^"]*)"'
    $hits = @()
    foreach ($m in [regex]::Matches($text, $tagPattern, $singleLine)) {
        if ($m.Index -lt $regionStart -or ($m.Index + $m.Length) -gt $regionEnd) { continue }
        if (Test-InComment $comments $m.Index) { continue }
        if ($edit.Filter -and -not [regex]::IsMatch($m.Value, $edit.Filter)) { continue }
        $hits += $m
        if (-not $edit.All) { break }
    }
    if ($hits.Count -eq 0) {
        $results.Add([pscustomobject]@{ Edit = $edit; Old = '(element not found)'; New = $edit.Value; Status = 'MISSING' })
        return $text
    }

    # Splice from the last hit backwards so earlier offsets stay valid.
    for ($i = $hits.Count - 1; $i -ge 0; $i--) {
        $tag = $hits[$i]
        $a = [regex]::Match($tag.Value, $attrPattern)
        if (-not $a.Success) {
            $results.Add([pscustomobject]@{ Edit = $edit; Old = '(attribute not found)'; New = $edit.Value; Status = 'MISSING' })
            continue
        }
        $old = $a.Groups[1].Value
        if ($null -ne $edit.Expect -and $old -ne $edit.Expect -and -not $Force) {
            $results.Add([pscustomobject]@{ Edit = $edit; Old = $old; New = $edit.Value; Status = "SKIPPED (expected $($edit.Expect))" })
            continue
        }
        if ($old -eq $edit.Value) {
            $results.Add([pscustomobject]@{ Edit = $edit; Old = $old; New = $edit.Value; Status = 'already' })
            continue
        }
        $newTag = $tag.Value.Substring(0, $a.Groups[1].Index) + $edit.Value + $tag.Value.Substring($a.Groups[1].Index + $a.Groups[1].Length)
        $text = $text.Substring(0, $tag.Index) + $newTag + $text.Substring($tag.Index + $tag.Length)
        $results.Add([pscustomobject]@{ Edit = $edit; Old = $old; New = $edit.Value; Status = 'set' })
    }
    $text
}

function New-Edit([string]$group, [string]$file, [string]$tag, [string]$attr, $expect, [string]$value,
                  [string]$why, [string]$filter = $null, [string]$scope = $null, [bool]$all = $false) {
    [pscustomobject]@{ Group = $group; File = $file; Tag = $tag; Attr = $attr; Expect = $expect; Value = $value
                       Why = $why; Filter = $filter; Scope = $scope; All = $all }
}

# ------------------------------------------------------------------------------------ the edits

function Get-Edits {
    $gc  = 'Datasheet\GuildConfig.xml'
    $gw  = 'Datasheet\GuildWar.xml'
    $bc  = 'Datasheet\BattleChipData.xml'
    $gqc = 'Datasheet\GuildQuest\GuildQuestConfig.xml'
    $gqd = 'Datasheet\GuildQuest\GuildQuestData.xml'
    $cw  = 'Datasheet\CityWar.xml'
    $list = New-Object System.Collections.Generic.List[object]

    if ($Part -contains 'GuildWar') {
        # Arbiter GuildWarManager (100.02 declare / opposite-declare / give-up / withdraw). The
        # sheet's own commented-out QA line sets all five GuildWarTime values to 1.
        $list.Add((New-Edit GuildWar $gc GuildWarTime combatReadyMin '5' '1' 'declare -> combat (min)'))
        $list.Add((New-Edit GuildWar $gc GuildWarTime surrenderMin '30' '1' 'earliest give-up after being declared on (min)'))
        $list.Add((New-Edit GuildWar $gc GuildWarTime protectiMin '1440' '1' 'protection from new declares after a give-up (min)'))
        $list.Add((New-Edit GuildWar $gc GuildWarTime reDeclareAbleMin '1440' '1' 're-declare cooldown on the same guild (min)'))
        $list.Add((New-Edit GuildWar $gc GuildWarTime maintainCheckMin '60' '1' 'maintenance charge interval (min)'))
        $list.Add((New-Edit GuildWar $gc GuildSize declareCost '1500' '100' 'declare cost, small guild (guild money)' 'rank="0"'))
        $list.Add((New-Edit GuildWar $gc GuildSize limitDeclareCost '15000' '100' 'declare cost past declareLimitCount' 'rank="0"'))
        $list.Add((New-Edit GuildWar $gc GuildSize maintainCost '250' '10' 'maintenance per check' 'rank="0"'))
        $list.Add((New-Edit GuildWar $gc GuildSize limitMaintainCost '2500' '10' 'maintenance past the limit' 'rank="0"'))
        $list.Add((New-Edit GuildWar $gc GuildJoin reJoinCoolTime '24' '0' 'rejoin cooldown (hours) - lets characters swap guilds'))
        # GuildWar.xml, read by both binaries. Values are the sheet's own *Qa figures where it has one.
        $list.Add((New-Edit GuildWar $gw RuleVar coolTime '72000' '300' 'same-guild declare cooldown (s)'))
        $list.Add((New-Edit GuildWar $gw RuleVar preWarPeriod '1790' '60' 'pre-war wait (s)'))
        $list.Add((New-Edit GuildWar $gw RuleVar warPeriod '72000' '600' 'war length (s)'))
        $list.Add((New-Edit GuildWar $gw RuleVar declareWaitTime '1800' '120' 'wait for the declared guild to answer (s)'))
        $list.Add((New-Edit GuildWar $gw KillPoint winKillPoint '100' '3' 'kills to win'))
        $list.Add((New-Edit GuildWar $gw KillPoint underScoreDraw '30' '1' 'score under which the war is a draw'))
        $list.Add((New-Edit GuildWar $gw Guild minCountOfGuildMember '2' '1' 'members a guild needs to war'))
        $list.Add((New-Edit GuildWar $gw Acceptable toggleCoolTime '14400' '60' 'accept-wars toggle cooldown (s)'))
        $list.Add((New-Edit GuildWar $gw LeaderBoard warRestriction '8' '2' 'war-count restriction (the *Qa value)'))
        if ($BattleChipAllDay) {
            $list.Add((New-Edit GuildWar $gw BattleChipTime openHour $null '00' 'battle-chip window opens' $null $null $true))
            $list.Add((New-Edit GuildWar $gw BattleChipTime closeHour $null '24' 'battle-chip window closes' $null $null $true))
        }
        # BattleChipData.xml: the chip-betting war (accept / raise / give-up penalties).
        $list.Add((New-Edit GuildWar $bc Restriction minUserAccount '30' '1' 'accounts a guild needs to declare or be declared on'))
        $list.Add((New-Edit GuildWar $bc Restriction countLevel '65' '1' 'character level an account needs to count'))
    }

    if ($Part -contains 'GuildQuest') {
        $list.Add((New-Edit GuildQuest $gqc WeeklyLimit balderionCoin '900' '99999' 'weekly coin cap, small guild (full = no more quests)' 'guildSize="0"'))
        $list.Add((New-Edit GuildQuest $gqc ContributionPointconfig weeklyLimit '1000' '99999' 'weekly contribution cap'))
        $list.Add((New-Edit GuildQuest $gqd Collection count '600' '3' 'quest 10001: gather 3' $null '<Quest\s+id="10001".*?</Quest>'))
        $list.Add((New-Edit GuildQuest $gqd Npc count '300' '3' 'quest 10002: kill 3 monsters' $null '<Quest\s+id="10002".*?</Quest>'))
        $list.Add((New-Edit GuildQuest $gqd Npc count '15' '1' 'quest 10003: kill 1 boss' $null '<Quest\s+id="10003".*?</Quest>'))
        $list.Add((New-Edit GuildQuest $gqd condition limitMin '720' '5' 'quest 10003: 5-minute limit, to capture a timeout' $null '<Quest\s+id="10003".*?</Quest>'))
        $list.Add((New-Edit GuildQuest $gqd Fishing count '50' '1' 'quest 10004: catch 1' $null '<Quest\s+id="10004".*?</Quest>'))
    }

    if ($Part -contains 'CityWar') {
        $start = (Get-Date).AddMinutes($CityWarLeadMinutes)
        if ($start.Minute -ne 0 -or $start.Second -ne 0) { $start = $start.Date.AddHours($start.Hour + 1) }
        $script:cityWarStart = $start
        $day = $start.DayOfWeek.ToString().ToLowerInvariant()
        $list.Add((New-Edit CityWar $cw OpenTime day 'saturday' $day 'battle day'))
        $list.Add((New-Edit CityWar $cw OpenTime startHour '20' ([string]$start.Hour) 'battle start hour (local)'))
        $list.Add((New-Edit CityWar $cw OpenTime playTimeMin '120' ([string]$CityWarPlayMinutes) 'battle length (min)'))
        $list.Add((New-Edit CityWar $cw DestroyTowerWhenUserCountIsBelow active 'true' 'false' 'tower destroyed when a guild has fewer than 4 inside'))
        $list.Add((New-Edit CityWar $cw GuildSize amount '700' '10' 'entry cost, small guild (keeps the gate, so its DB check is captured)' 'rank="0"' '<UseLimitEntryWithGuildPoint\b.*?</UseLimitEntryWithGuildPoint>'))
        $list.Add((New-Edit CityWar $cw RankReward stayingTimeInGuild '168' '0' 'hours in the guild before rank rewards count'))
    }

    if ($QaServer) {
        $list.Add((New-Edit QaServer 'DeploymentConfig.xml' ArbiterServerConfig qaServer 'false' 'true' 'every *Qa attribute, all sheets'))
    }
    , $list
}

# ------------------------------------------------------------------------------------- revert

if ($Revert) {
    if (-not (Test-Path -LiteralPath $manifestPath)) { throw "No $manifestPath - nothing to revert." }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $bad = 0
    foreach ($f in $manifest.Files) {
        $target = Join-Path $Executable $f.File
        $backup = Join-Path $backupRoot $f.File
        if (-not (Test-Path -LiteralPath $backup)) { Write-Warning "missing backup $backup"; $bad++; continue }
        if ((Test-Path -LiteralPath $target) -and (Get-Sha $target) -ne $f.TunedSha256 -and -not $Force) {
            Write-Warning "$($f.File) changed since it was tuned - left as is (-Force to restore anyway)"
            $bad++
            continue
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
    } elseif ($bad -gt 0) {
        Write-Warning "$bad file(s) not restored cleanly; backup folder kept."
        exit 1
    }
    return
}

# -------------------------------------------------------------------------------------- apply

if ((Test-Path -LiteralPath $manifestPath) -and -not $Force) {
    throw "Already tuned ($manifestPath). Run -Revert first, or -Force to tune again on top."
}
$running = @(Get-Process -Name ArbiterServer, WorldServer -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Warning ("Running now: " + (($running | ForEach-Object { $_.ProcessName }) -join ', ') +
                   ". Sheets are read at boot - restart them after this.")
}

$edits = Get-Edits
$results = New-Object System.Collections.Generic.List[object]
$files = New-Object System.Collections.Generic.List[object]

foreach ($group in ($edits | Group-Object File)) {
    $path = Join-Path $Executable $group.Name
    if (-not (Test-Path -LiteralPath $path)) { Write-Warning "missing $path"; continue }
    $sheet = Read-Sheet $path
    $text = $sheet.Text
    foreach ($e in $group.Group) { $text = Set-SheetAttribute $text $e $results }
    if ($text -ceq $sheet.Text) { continue }

    $backup = Join-Path $backupRoot $group.Name
    if ($PSCmdlet.ShouldProcess($path, 'tune')) {
        $original = Get-Sha $path
        if (-not (Test-Path -LiteralPath $backup)) {
            New-Item -ItemType Directory -Force -Path (Split-Path $backup -Parent) | Out-Null
            Copy-Item -LiteralPath $path -Destination $backup
        }
        Write-Sheet $path $text $sheet.Bom
        $files.Add([pscustomobject]@{ File = $group.Name; OriginalSha256 = $original; TunedSha256 = (Get-Sha $path) })
    }
}

$results | ForEach-Object {
    [pscustomobject]@{
        File    = $_.Edit.File -replace '^Datasheet\\', ''
        Element = $_.Edit.Tag + $(if ($_.Edit.Filter) { '[' + $_.Edit.Filter + ']' } else { '' }) +
                  $(if ($_.Edit.Scope) { ' in ' + ($_.Edit.Scope -replace '\\s\+', ' ' -replace '\.\*\?.*$', '') } else { '' })
        Attr    = $_.Edit.Attr
        From    = $_.Old
        To      = $_.New
        Status  = $_.Status
    }
} | Format-Table -AutoSize | Out-String -Width 220 | Write-Host

if ($files.Count -gt 0 -and -not $WhatIfPreference) {
    $old = @()
    if (Test-Path -LiteralPath $manifestPath) { $old = @((Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).Files) }
    $keep = @($old | Where-Object { $n = $_.File; -not ($files | Where-Object { $_.File -eq $n }) })
    $merged = @($keep) + @($files | ForEach-Object {
        $f = $_
        $prev = $old | Where-Object { $_.File -eq $f.File } | Select-Object -First 1
        if ($prev) { $f.OriginalSha256 = $prev.OriginalSha256 }   # a second -Force run keeps the true original
        $f
    })
    [pscustomobject]@{ Applied = (Get-Date).ToString('s'); Parts = $Part; Files = $merged } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
    Write-Host "Backups and manifest: $backupRoot"
}

$skipped = @($results | Where-Object { $_.Status -ne 'set' -and $_.Status -ne 'already' })
if ($skipped.Count -gt 0) { Write-Warning "$($skipped.Count) edit(s) not applied - see the table." }
if (Get-Variable -Name cityWarStart -Scope Script -ErrorAction SilentlyContinue) {
    Write-Host ("Civil Unrest: battle {0:dddd HH:00} for {1} min; entry opens {0:HH:mm} minus 120 min, i.e. at the next World tick." -f $script:cityWarStart, $CityWarPlayMinutes)
}
Write-Host 'Start ArbiterServer and WorldServer now. Revert with: .\capture-tune.ps1 -Revert'
