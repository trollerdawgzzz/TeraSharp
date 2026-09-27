# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
    T177 - duplicate a battlefield (default 37, Champions' Skyring) under a new id, optionally with
    another team size. -Revert takes it out again. status/DUPLICATE-CONTENT.md has the reasoning.

.DESCRIPTION
    Required (without them the id fails World's Validation or cannot be matched):
      BattleFieldData.xml       <BattleField id=Source> cloned as NewId. CommonData@maxTeamMember
                                = TeamSize, RuleData@ruleId = the role below.
      MatchingRoleTemplate.xml  only when -TeamSize differs from the source rule's totalUser:
                                <Role> cloned as NewRuleId with RoleData@totalUser = TeamSize.
                                World fails Validation on a BG ruleId with no Role, and the real
                                matcher (MatchServer) sizes a match from totalUser, not maxTeamMember.
    Optional:
      ItemEquipRestriction.xml  -EqualizedFrom <bf>: that BG's <AllowedItems> (the unified-gear
                                set) cloned for NewId. 38 and 40 have one; 37 does not.
      BattleFieldData.xml       NewId added to BFMatchingUISortPriority after Source (skip with
                                -NoSortPriority).
    The clone keeps continentId, doors, spawns and rewards - BGs 37-40 already share continent 115.
    RankingCompetition is set active="false" unless -KeepRanking: the Arbiter's leaderboard takes
    the lowest id per type anyway, and TeraSharp lists every active one as a PvP board.

    Nothing is written to the client DataCenter. The client's battlefield tab needs its own
    BattleFieldData row (DUPLICATE-CONTENT.md section 5).

    Rows go in fenced by <!-- dupe:battlefield:<NewId> begin/end --> and -Revert removes exactly
    those. The sheets it changes are copied to <Datasheet>\..\dupe-backup\ first (outside
    Datasheet\, which the servers walk recursively).

    Datasheets are read at boot: stop ArbiterServer, every WorldServer, MatchServer and TeraSharp
    before applying or reverting.

.PARAMETER Datasheet
    The Datasheet folder (a copy is fine - that is how this was tested).
.PARAMETER Source
    Battlefield id to copy. Default 37.
.PARAMETER NewId
    The new battlefield id: 1..999 (TeraSharp treats instance ids under 1000 as battlegrounds),
    unused, not a DungeonData / DungeonMatching id, and above the lowest id of its type.
.PARAMETER TeamSize
    Players per team. Omit to keep the source's size and rule.
.PARAMETER RuleId
    Use this existing MatchingRoleTemplate role instead of cloning one.
.PARAMETER NewRuleId
    Id for the cloned role. Default: the next id after the highest role id below 900.
.PARAMETER NameId
    StrSheet_BattleField string id for the clone's name (the client shows its own DC string).
.PARAMETER EqualizedFrom
    Battlefield whose ItemEquipRestriction <AllowedItems> the clone should use.
.PARAMETER DryRun
    List every edit and run the checks; write nothing. Same as -WhatIf.
.PARAMETER Revert
    Remove every row and list entry this script added for NewId.
.PARAMETER Force
    Accept an id below the lowest of its type, or a -RuleId whose totalUser is not TeamSize.
.EXAMPLE
    .\dupe-battlefield.ps1 -NewId 41 -TeamSize 2 -DryRun
    .\dupe-battlefield.ps1 -NewId 41 -TeamSize 2 -EqualizedFrom 38
    .\dupe-battlefield.ps1 -NewId 41 -Revert
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Datasheet = 'D:\v100\TERA_SERVER.100\Executable\Datasheet',
    [int]    $Source = 37,
    [Parameter(Mandatory = $true)] [int] $NewId,
    [int]    $TeamSize = 0,
    [int]    $RuleId = 0,
    [int]    $NewRuleId = 0,
    [int]    $NameId = 0,
    [int]    $EqualizedFrom = 0,
    [switch] $KeepRanking,
    [switch] $NoSortPriority,
    [switch] $DryRun,
    [switch] $Revert,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dupe-common.ps1')
if ($DryRun) { $WhatIfPreference = $true }
$kind = 'battlefield'
$dsDir = (Resolve-Path -LiteralPath $Datasheet).ProviderPath
$bfSheet   = Read-DupeSheet (Join-Path $dsDir 'BattleFieldData.xml')
$roleSheet = Read-DupeSheet (Join-Path $dsDir 'MatchingRoleTemplate.xml')
$eqFile    = Join-Path $dsDir 'ItemEquipRestriction.xml'
$eqSheet   = $null; if (Test-Path -LiteralPath $eqFile) { $eqSheet = Read-DupeSheet $eqFile }
$sortMarker = Get-DupeMarker $kind $NewId 'sort'

function Get-SortSpan($sheet) {
    $masked = Get-DupeMasked $sheet.Text
    $m = [regex]::Match($masked, '<BFMatchingUISortPriority\b[^>]*?\sbattleFieldId\s*=\s*"([^"]*)"[^>]*>')
    if ($m.Success) { return $m } else { return $null }
}

if ($Revert) {
    Write-Host ('Revert battlefield {0} in {1}' -f $NewId, $dsDir)
    $n = Remove-DupeBlocks $bfSheet $kind $NewId
    Write-DupePlan 'remove' $bfSheet.Name ('{0} fenced block(s)' -f $n)
    $i = $bfSheet.Text.IndexOf(' ' + $sortMarker)
    if ($i -ge 0) {
        $bfSheet.Text = $bfSheet.Text.Remove($i, $sortMarker.Length + 1)
        $sm = Get-SortSpan $bfSheet
        $ids = @($sm.Groups[1].Value.Split([char[]]@(','), [StringSplitOptions]::RemoveEmptyEntries) | Where-Object { $_.Trim() -ne [string]$NewId })
        $g = $sm.Groups[1]
        $bfSheet.Text = $bfSheet.Text.Substring(0, $g.Index) + ($ids -join ',') + $bfSheet.Text.Substring($g.Index + $g.Length)
        $bfSheet.Changed = $true
        Write-DupePlan 'edit' $bfSheet.Name ('BFMatchingUISortPriority: {0} taken out' -f $NewId)
    }
    $n = Remove-DupeBlocks $roleSheet $kind $NewId
    Write-DupePlan 'remove' $roleSheet.Name ('{0} fenced block(s)' -f $n)
    if ($eqSheet) { $n = Remove-DupeBlocks $eqSheet $kind $NewId; Write-DupePlan 'remove' $eqSheet.Name ('{0} fenced block(s)' -f $n) }
    $changed = @(@($bfSheet, $roleSheet, $eqSheet) | Where-Object { $_ -and $_.Changed })
    if ($changed.Count -eq 0) { Write-Host 'nothing to revert'; return }
    if ($PSCmdlet.ShouldProcess(($changed | ForEach-Object { $_.Name }) -join ', ', 'write reverted sheets')) {
        $bak = Backup-DupeSheets $dsDir @($changed | ForEach-Object { $_.Path }) ('battlefield-{0}-revert' -f $NewId)
        foreach ($s in $changed) { Write-DupeSheet $s }
        Write-Host ('reverted; previous copies in {0}' -f $bak)
    }
    return
}

# ---- checks before anything is planned --------------------------------------------------------
$problems = New-Object System.Collections.Generic.List[string]
if ($NewId -le 0 -or $NewId -ge 1000) { $problems.Add(('NewId {0}: must be 1..999 (TeraSharp: instance ids under 1000 are battlegrounds)' -f $NewId)) }
$bfIds = @(Get-DupeAttrValues $bfSheet.Text 'BattleField' 'id')
if ($bfIds -contains [string]$NewId) { $problems.Add(('NewId {0}: already a battlefield (the Arbiter drops a duplicate id silently)' -f $NewId)) }
if ($bfSheet.Text.Contains((Get-DupeMarker $kind $NewId 'begin'))) { $problems.Add(('NewId {0}: already added by this script - run -Revert first' -f $NewId)) }
if (Test-Path -LiteralPath (Join-Path $dsDir ('DungeonData_{0}.xml' -f $NewId))) { $problems.Add(('NewId {0}: DungeonData_{0}.xml exists - the Arbiter would list it as a dungeon' -f $NewId)) }
$dmFile = Join-Path $dsDir 'DungeonMatching.xml'
if (Test-Path -LiteralPath $dmFile) {
    if (@(Get-DupeAttrValues (Read-DupeSheet $dmFile).Text 'Dungeon' 'id') -contains [string]$NewId) {
        $problems.Add(('NewId {0}: a DungeonMatching id - World takes the dungeon rule first (InitMatchRuleTemplate)' -f $NewId))
    }
}
$srcSpan = Find-DupeElement $bfSheet.Text 'BattleField' 'id' ([string]$Source)
if (-not $srcSpan) { throw ('Source battlefield {0} not found in BattleFieldData.xml' -f $Source) }
$clone = $bfSheet.Text.Substring($srcSpan.Start, $srcSpan.End - $srcSpan.Start)
$type = Get-DupeAttr $clone 'BattleField' 'type'
$sameType = @()
foreach ($m in [regex]::Matches((Get-DupeMasked $bfSheet.Text), '<BattleField\b[^>]*>')) {
    if ((Get-DupeAttr $m.Value 'BattleField' 'type') -eq $type) { $sameType += [int](Get-DupeAttr $m.Value 'BattleField' 'id') }
}
$lowest = ($sameType | Measure-Object -Minimum).Minimum
if ($NewId -lt $lowest -and -not $Force) { $problems.Add(('NewId {0} is below {1}, the lowest {2} id: the Arbiter would make it the {2} leaderboard (-Force to accept)' -f $NewId, $lowest, $type)) }

$srcRule = Get-DupeAttr $clone 'RuleData' 'ruleId'
$srcTeam = Get-DupeAttr $clone 'CommonData' 'maxTeamMember'
$roleIds = @(Get-DupeAttrValues $roleSheet.Text 'Role' 'id')
function Get-RoleTotal([string] $id) {
    $sp = Find-DupeElement $roleSheet.Text 'Role' 'id' $id
    if (-not $sp) { return $null }
    return Get-DupeAttr ($roleSheet.Text.Substring($sp.Start, $sp.End - $sp.Start)) 'RoleData' 'totalUser'
}
$useRule = $srcRule; $cloneRole = $false
if ($RuleId -gt 0) {
    if ($roleIds -notcontains [string]$RuleId) { $problems.Add(('RuleId {0}: no such MatchingRoleTemplate role' -f $RuleId)) }
    elseif ($TeamSize -gt 0 -and [int](Get-RoleTotal ([string]$RuleId)) -ne $TeamSize -and -not $Force) {
        $problems.Add(('RuleId {0}: totalUser {1} is not TeamSize {2} (-Force to accept)' -f $RuleId, (Get-RoleTotal ([string]$RuleId)), $TeamSize))
    }
    $useRule = [string]$RuleId
} elseif ($TeamSize -gt 0 -and [string]$TeamSize -ne (Get-RoleTotal $srcRule)) {
    $cloneRole = $true
    if ($NewRuleId -le 0) {
        $below = @($roleIds | ForEach-Object { [int]$_ } | Where-Object { $_ -lt 900 })
        $NewRuleId = (($below | Measure-Object -Maximum).Maximum) + 1
    }
    if ($roleIds -contains [string]$NewRuleId) { $problems.Add(('NewRuleId {0}: role already exists' -f $NewRuleId)) }
    $useRule = [string]$NewRuleId
}
if ($EqualizedFrom -gt 0) {
    if (-not $eqSheet) { $problems.Add('ItemEquipRestriction.xml not found') }
    else {
        if (-not (Find-DupeElement $eqSheet.Text 'AllowedItems' 'battleFieldId' ([string]$EqualizedFrom))) { $problems.Add(('EqualizedFrom {0}: no <AllowedItems battleFieldId="{0}">' -f $EqualizedFrom)) }
        if (Find-DupeElement $eqSheet.Text 'AllowedItems' 'battleFieldId' ([string]$NewId)) { $problems.Add(('NewId {0} already has <AllowedItems>' -f $NewId)) }
    }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Host ('  refused: ' + $_) }; throw 'nothing written' }

# ---- plan -------------------------------------------------------------------------------------
Write-Host ('Battlefield {0} ({1}) -> {2} in {3}' -f $Source, $type, $NewId, $dsDir)
$r = Set-DupeAttr $clone 'BattleField' 'id' ([string]$NewId); $clone = $r.Text
Write-DupePlan 'clone' $bfSheet.Name ('<BattleField id="{0}"> (line {1}) as id="{2}"' -f $Source, (Get-DupeLineNo $bfSheet.Text $srcSpan.Start), $NewId)
if ($TeamSize -gt 0) {
    $r = Set-DupeAttr $clone 'CommonData' 'maxTeamMember' ([string]$TeamSize); $clone = $r.Text
    Write-DupePlan '' '' ('  CommonData@maxTeamMember {0} -> {1}' -f $r.Old, $TeamSize)
    $minT = Get-DupeAttr $clone 'CommonData' 'minTeamMember'
    if ($minT -and [int]$minT -gt $TeamSize) {
        $r = Set-DupeAttr $clone 'CommonData' 'minTeamMember' ([string]$TeamSize); $clone = $r.Text
        Write-DupePlan '' '' ('  CommonData@minTeamMember {0} -> {1}' -f $r.Old, $TeamSize)
    }
}
if ($useRule -ne $srcRule) {
    $r = Set-DupeAttr $clone 'RuleData' 'ruleId' $useRule; $clone = $r.Text
    Write-DupePlan '' '' ('  RuleData@ruleId {0} -> {1}' -f $r.Old, $useRule)
}
if ($NameId -gt 0) {
    $r = Set-DupeAttr $clone 'BattleField' 'name' ([string]$NameId); $clone = $r.Text
    Write-DupePlan '' '' ('  @name {0} -> {1}' -f $r.Old, $NameId)
}
if (-not $KeepRanking -and (Get-DupeAttr $clone 'RankingCompetition' 'active') -eq 'true') {
    $r = Set-DupeAttr $clone 'RankingCompetition' 'active' 'false'; $clone = $r.Text
    Write-DupePlan '' '' '  RankingCompetition@active true -> false'
}
Add-DupeBlock $bfSheet $srcSpan $clone $kind $NewId

if (-not $NoSortPriority) {
    $sm = Get-SortSpan $bfSheet
    if ($sm) {
        $g = $sm.Groups[1]
        $ids = New-Object System.Collections.Generic.List[string]
        foreach ($x in $g.Value.Split([char[]]@(','), [StringSplitOptions]::RemoveEmptyEntries)) { $ids.Add($x.Trim()) }
        $at = $ids.IndexOf([string]$Source); if ($at -lt 0) { $at = $ids.Count - 1 }
        $ids.Insert($at + 1, [string]$NewId)
        $endTag = $sm.Index + $sm.Length
        $t = $bfSheet.Text
        $t = $t.Substring(0, $endTag) + ' ' + $sortMarker + $t.Substring($endTag)
        $bfSheet.Text = $t.Substring(0, $g.Index) + ($ids.ToArray() -join ',') + $t.Substring($g.Index + $g.Length)
        Write-DupePlan 'edit' $bfSheet.Name ('BFMatchingUISortPriority: {0} after {1}' -f $NewId, $Source)
    }
}

if ($cloneRole) {
    $rs = Find-DupeElement $roleSheet.Text 'Role' 'id' $srcRule
    if (-not $rs) { throw ('rule {0} of battlefield {1} has no MatchingRoleTemplate role' -f $srcRule, $Source) }
    $rc = $roleSheet.Text.Substring($rs.Start, $rs.End - $rs.Start)
    $old = [int](Get-DupeAttr $rc 'RoleData' 'totalUser')
    $rc = (Set-DupeAttr $rc 'Role' 'id' $useRule).Text
    $rc = (Set-DupeAttr $rc 'RoleData' 'totalUser' ([string]$TeamSize)).Text
    $notes = @(('totalUser {0} -> {1}' -f $old, $TeamSize))
    $qa = Get-DupeAttr $rc 'RoleData' 'totalUserQa'
    if ($qa -and [int]$qa -gt $TeamSize) { $rc = (Set-DupeAttr $rc 'RoleData' 'totalUserQa' ([string]$TeamSize)).Text; $notes += ('totalUserQa {0} -> {1}' -f $qa, $TeamSize) }
    foreach ($a in @('minMatchingMember', 'maxMatchingMember')) {
        $v = Get-DupeAttr $rc 'RoleData' $a
        if ($v -and [int]$v -eq $old) { $rc = (Set-DupeAttr $rc 'RoleData' $a ([string]$TeamSize)).Text; $notes += ('{0} {1} -> {2}' -f $a, $v, $TeamSize) }
    }
    $fit = Resize-DupeRole $rc $TeamSize
    $rc = $fit.Text; $notes += $fit.Notes
    Add-DupeBlock $roleSheet $rs $rc $kind $NewId
    Write-DupePlan 'clone' $roleSheet.Name ('<Role id="{0}"> as id="{1}": {2}' -f $srcRule, $useRule, ($notes -join ', '))
}

if ($EqualizedFrom -gt 0) {
    $es = Find-DupeElement $eqSheet.Text 'AllowedItems' 'battleFieldId' ([string]$EqualizedFrom)
    $ec = (Set-DupeAttr $eqSheet.Text.Substring($es.Start, $es.End - $es.Start) 'AllowedItems' 'battleFieldId' ([string]$NewId)).Text
    Add-DupeBlock $eqSheet $es $ec $kind $NewId
    Write-DupePlan 'clone' $eqSheet.Name ('<AllowedItems battleFieldId="{0}"> as {1} (unified gear)' -f $EqualizedFrom, $NewId)
}

# ---- invariants on the result (also in a dry run) ---------------------------------------------
$bad = New-Object System.Collections.Generic.List[string]
$roleIds2 = @(Get-DupeAttrValues $roleSheet.Text 'Role' 'id')
foreach ($rid in @(Get-DupeAttrValues $bfSheet.Text 'RuleData' 'ruleId')) {
    if ($roleIds2 -notcontains $rid) { $bad.Add(('ruleId {0} has no Role (World Validation: MatchingRole Datasheet)' -f $rid)) }
}
$all = @(Get-DupeAttrValues $bfSheet.Text 'BattleField' 'id')
if (@($all | Select-Object -Unique).Count -ne $all.Count) { $bad.Add('duplicate BattleField id') }
$dupRoles = @($roleIds2 | Group-Object | Where-Object { $_.Count -gt 1 })
if ($dupRoles.Count -gt 0) { $bad.Add(('duplicate Role id {0}' -f ($dupRoles[0].Name))) }
$newSpan = Find-DupeElement $bfSheet.Text 'BattleField' 'id' ([string]$NewId)
$nb = $bfSheet.Text.Substring($newSpan.Start, $newSpan.End - $newSpan.Start)
$tu = Get-RoleTotal $useRule
if ($cloneRole) { $tu = [string]$TeamSize }
$mt = Get-DupeAttr $nb 'CommonData' 'maxTeamMember'
if ($tu -ne $mt) { Write-Host ('  note: role {0} totalUser {1} vs maxTeamMember {2} - MatchServer uses totalUser, TeraSharp uses maxTeamMember' -f $useRule, $tu, $mt) }
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host ('  check failed: ' + $_) }; throw 'invariants failed; nothing written' }
Write-Host '  check   ok: ruleIds resolve, ids unique'

$changed = @(@($bfSheet, $roleSheet, $eqSheet) | Where-Object { $_ -and $_.Changed })
if ($PSCmdlet.ShouldProcess(($changed | ForEach-Object { $_.Name }) -join ', ', 'write')) {
    $bak = Backup-DupeSheets $dsDir @($changed | ForEach-Object { $_.Path }) ('battlefield-{0}' -f $NewId)
    foreach ($s in $changed) { Write-DupeSheet $s }
    Write-Host ('written; originals in {0}. Restart ArbiterServer, WorldServers, MatchServer, TeraSharp.' -f $bak)
    Write-Host 'Client: add a BattleFieldData row for the new id to the DataCenter, or the tab will not show it.'
}
