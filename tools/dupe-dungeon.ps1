# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
    T177 - duplicate a dungeon continent (default 9781, Velik's Sanctuary) under a new continent id
    and hunting zone, optionally with another member count. -Revert takes it out again.
    status/DUPLICATE-CONTENT.md has the reasoning and the decompile citations.

.DESCRIPTION
    A dungeon IS a continent: DungeonData's continentId is the dungeon id, and it must be unique
    (World 'duplicated dungeon continent Id!'). A hunting zone belongs to exactly one continent
    ('duplicated huntingZoneId'), and NPCs, spawns, AI and skills are keyed by hunting zone, so the
    copy gets its own hunting zone with copied per-zone files - which is exactly how the shipped
    hard mode 9981 / 981 was made from 9781 / 781. The map is reused: Topology tiles are keyed by
    AreaList zone coordinates and pathdata by area name, never by continent id.

    Required (boot or matching fails without them):
      AreaList.xml              <Continent id=Source> cloned as NewId (same Area and Zones).
      ContinentData.xml         <Continent id=Source> cloned as NewId, HuntingZone -> NewHuntingZone.
      DungeonData_<NewId>.xml   from DungeonData_<Source>: continentId, every huntingZoneId and
                                every "<zone>,<id>" pair moved to the new zone; maxMemberCount set
                                when -MaxMembers is given.
      per-zone files            every <family>_<zone>.xml of the source zone copied as the new
                                zone (TerritoryData, NpcData, NpcSkillData, AIData, ActiveMove,
                                ActiveRotate, DynamicSpawn, FormationData, WorkObjectTerritory,
                                ?Compensation, ...), same rewrite.
      DungeonMatching.xml       <Dungeon id=Source> cloned as NewId (skip with -NoMatching - the
                                copy then loads and can be entered but never matched).
      MatchingRoleTemplate.xml  when -MaxMembers differs from the rule's totalUser: the role cloned
                                with totalUser = MaxMembers. World fails Validation unless the
                                role's totalUser equals DungeonData maxMemberCount.
    Optional, done by default:
      DungeonConstraint.xml     <Constraint continentId=Source> cloned (skip with -NoConstraint).
      AreaData_<NewId>_*.xml, DynamicGeo_<NewId>.xml and other continent-keyed files that exist
                                for the source.
    Not touched: EventMatching, Leaderboards, CompetitionDungeon, DungeonRankRecorder,
    DungeonNewbieBonus, achievements, and the client DataCenter (section 5 of the doc).

    Rows go in fenced by <!-- dupe:dungeon:<NewId> begin/end -->, created files carry a
    <!-- dupe:dungeon:<NewId> created from ... --> line, and -Revert removes exactly those.
    Changed sheets are copied to <Datasheet>\..\dupe-backup\ first.

    Datasheets are read at boot: stop ArbiterServer, TopographyServer, every WorldServer,
    MatchServer and TeraSharp first. TopographyServer must restart too - it has to build the new
    continent's map, or World logs 'Load Topo Fail, continent=<NewId>'.

.PARAMETER Datasheet
    The Datasheet folder (a copy is fine - that is how this was tested).
.PARAMETER Source
    Dungeon continent id to copy. Default 9781.
.PARAMETER NewId
    New continent id: 1000..9998, not in AreaList / ContinentData / DungeonData_*, not a
    battlefield id. 9784-9789 are free on the shipped sheets.
.PARAMETER NewHuntingZone
    Hunting zone for the copy. Default NewId - 9000 when that fits the 9xyz -> xyz convention.
    Must be 1..4095, in no ContinentData row and in no per-zone file name.
.PARAMETER MaxMembers
    Party size. Omit to keep the source's maxMemberCount and matching role.
.PARAMETER RoleId
    Use this existing MatchingRoleTemplate role (its totalUser must equal the member count).
.PARAMETER NewRoleId
    Id for the cloned role. Default: the next id after the highest role id below 100.
.PARAMETER NoMatching
    Leave DungeonMatching and MatchingRoleTemplate alone.
.PARAMETER NoConstraint
    Leave DungeonConstraint alone.
.PARAMETER DryRun
    List every edit and run the checks; write nothing. Same as -WhatIf.
.PARAMETER Revert
    Remove every row and file this script added for NewId.
.PARAMETER Force
    Accept a -RoleId whose totalUser differs (World will refuse to boot - only for testing a copy).
.EXAMPLE
    .\dupe-dungeon.ps1 -NewId 9785 -MaxMembers 3 -DryRun
    .\dupe-dungeon.ps1 -NewId 9785 -MaxMembers 3
    .\dupe-dungeon.ps1 -NewId 9785 -Revert
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $Datasheet = 'D:\v100\TERA_SERVER.100\Executable\Datasheet',
    [int]    $Source = 9781,
    [Parameter(Mandatory = $true)] [int] $NewId,
    [int]    $NewHuntingZone = 0,
    [int]    $MaxMembers = 0,
    [int]    $RoleId = 0,
    [int]    $NewRoleId = 0,
    [switch] $NoMatching,
    [switch] $NoConstraint,
    [switch] $DryRun,
    [switch] $Revert,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'dupe-common.ps1')
if ($DryRun) { $WhatIfPreference = $true }
$kind = 'dungeon'
$dsDir = (Resolve-Path -LiteralPath $Datasheet).ProviderPath

# Per-hunting-zone families (the number in the name is a hunting zone) and per-continent ones.
$zoneFamilies = @('AiData', 'ActiveMove', 'ActiveRotate', 'BonfireData', 'DynamicSpawn', 'FishingTerritory',
                  'FormationData', 'NpcData', 'NpcPartData', 'NpcSkillData', 'QuestCompensationData',
                  'TerritoryData', 'WorkObjectTerritory', 'ECompensation', 'CCompensation', 'ICompensation', 'FCompensation')
$contFamilies = @('DynamicGeo', 'ShieldTerritory', 'CollectionTerritory', 'SwimmingTerritory', 'TeleportTerritory', 'FieldData')
$contNamed    = @('AreaData', 'ClimbingTerritory')      # <family>_<continent>_<AreaName>.xml
$skipFamilies = @('DungeonRankRecorder')                 # would add a PvE leaderboard

$sheetNames = @('AreaList.xml', 'ContinentData.xml', 'DungeonMatching.xml', 'MatchingRoleTemplate.xml', 'DungeonConstraint.xml')
$sheets = @{}
foreach ($n in $sheetNames) { $p = Join-Path $dsDir $n; if (Test-Path -LiteralPath $p) { $sheets[$n] = Read-DupeSheet $p } }
foreach ($n in @('AreaList.xml', 'ContinentData.xml')) { if (-not $sheets.ContainsKey($n)) { throw ('{0} not found in {1}' -f $n, $dsDir) } }
$allFiles = @(Get-ChildItem -LiteralPath $dsDir -Recurse -File -Filter '*.xml')

function Find-ZoneFiles([int] $hz) {
    foreach ($f in $allFiles) {
        foreach ($fam in $zoneFamilies) {
            $m = [regex]::Match($f.Name, '^(?i:' + $fam + ')_(0*)' + $hz + '\.xml$')
            if ($m.Success) { [pscustomobject]@{ File = $f; Family = $fam; Pad = $m.Groups[1].Value.Length + ([string]$hz).Length } }
        }
    }
}
function Find-ContFiles([int] $c) {
    foreach ($f in $allFiles) {
        foreach ($fam in ($contFamilies + $skipFamilies)) {
            if ($f.Name -match ('^(?i:' + $fam + ')_' + $c + '\.xml$')) { [pscustomobject]@{ File = $f; Family = $fam; Suffix = '' } }
        }
        foreach ($fam in $contNamed) {
            $m = [regex]::Match($f.Name, '^(?i:' + $fam + ')_' + $c + '(_.+)\.xml$')
            if ($m.Success) { [pscustomobject]@{ File = $f; Family = $fam; Suffix = $m.Groups[1].Value } }
        }
    }
}

# Move a hunting zone (and optionally the root continentId / Area id) inside one file's text.
function Convert-DupeText([string] $text, [string] $oldHz, [string] $newHz, [string] $oldC, [string] $newC, [switch] $AreaRoot) {
    $n = 0
    $rx1 = New-Object regex ('(\shuntingZoneId\s*=\s*")' + $oldHz + '(")')
    $n += $rx1.Matches($text).Count; $text = $rx1.Replace($text, '${1}' + $newHz + '${2}')
    $rx2 = New-Object regex ('(\s\w+\s*=\s*")' + $oldHz + '(,\d+")')
    $n += $rx2.Matches($text).Count; $text = $rx2.Replace($text, '${1}' + $newHz + '${2}')
    $root = [regex]::Match((Get-DupeMasked $text), '<(?!\?)(\w+)\b[^>]*>')
    if ($root.Success) {
        $head = $text.Substring($root.Index, $root.Length); $h2 = $head
        if ($oldC) { $h2 = [regex]::Replace($h2, '(\scontinentId\s*=\s*")' + $oldC + '(")', '${1}' + $newC + '${2}') }
        if ($AreaRoot) { $h2 = [regex]::Replace($h2, '(\sid\s*=\s*")' + $oldHz + '(")', '${1}' + $newHz + '${2}') }
        if ($h2 -ne $head) { $n++; $text = $text.Substring(0, $root.Index) + $h2 + $text.Substring($root.Index + $root.Length) }
    }
    @{ Text = $text; Count = $n }
}

$created = New-Object System.Collections.Generic.List[object]   # @{ Path; Sheet }

# ---- revert -----------------------------------------------------------------------------------
if ($Revert) {
    Write-Host ('Revert dungeon {0} in {1}' -f $NewId, $dsDir)
    foreach ($n in $sheetNames) {
        if (-not $sheets.ContainsKey($n)) { continue }
        $k = Remove-DupeBlocks $sheets[$n] $kind $NewId
        if ($k -gt 0) { Write-DupePlan 'remove' $n ('{0} fenced block(s)' -f $k) }
    }
    $del = @($allFiles | Where-Object { Test-DupeFileMarker $_.FullName $kind $NewId })
    foreach ($f in $del) { Write-DupePlan 'delete' $f.Name 'created by this script' }
    $changed = @($sheets.Values | Where-Object { $_.Changed })
    if ($changed.Count -eq 0 -and $del.Count -eq 0) { Write-Host 'nothing to revert'; return }
    if ($PSCmdlet.ShouldProcess(('{0} sheet(s), {1} file(s)' -f $changed.Count, $del.Count), 'revert')) {
        if ($changed.Count -gt 0) {
            $bak = Backup-DupeSheets $dsDir @($changed | ForEach-Object { $_.Path }) ('dungeon-{0}-revert' -f $NewId)
            foreach ($s in $changed) { Write-DupeSheet $s }
            Write-Host ('previous copies in {0}' -f $bak)
        }
        foreach ($f in $del) { Remove-Item -LiteralPath $f.FullName }
        Write-Host 'reverted'
    }
    return
}

# ---- checks -----------------------------------------------------------------------------------
$problems = New-Object System.Collections.Generic.List[string]
$al = $sheets['AreaList.xml']; $cd = $sheets['ContinentData.xml']
$srcCd = Find-DupeElement $cd.Text 'Continent' 'id' ([string]$Source)
if (-not $srcCd) { throw ('continent {0} not in ContinentData.xml' -f $Source) }
$srcCdText = $cd.Text.Substring($srcCd.Start, $srcCd.End - $srcCd.Start)
$srcZones = @(Get-DupeAttrValues $srcCdText 'HuntingZone' 'id')
if ($srcZones.Count -ne 1) { throw ('continent {0} has {1} hunting zones; only single-zone dungeons are supported' -f $Source, $srcZones.Count) }
$oldHz = [int]$srcZones[0]
if ($NewHuntingZone -le 0) {
    if ($Source - 9000 -eq $oldHz -and $NewId -ge 9000) { $NewHuntingZone = $NewId - 9000 }
    else { throw 'pass -NewHuntingZone (the source does not follow the 9xyz -> xyz convention)' }
}
if ($NewId -lt 1000 -or $NewId -ge 9999) { $problems.Add(('NewId {0}: must be 1000..9998 (World TopoMap has 9999 slots; TeraSharp treats ids under 1000 as battlegrounds)' -f $NewId)) }
$alIds = @(Get-DupeAttrValues $al.Text 'Continent' 'id') + @(Get-DupeAttrValues $al.Text 'ChannelContinent' 'id')
if ($alIds -contains [string]$NewId) { $problems.Add(('NewId {0}: already in AreaList.xml' -f $NewId)) }
if (@(Get-DupeAttrValues $cd.Text 'Continent' 'id') -contains [string]$NewId) { $problems.Add(('NewId {0}: already in ContinentData.xml' -f $NewId)) }
if (Test-Path -LiteralPath (Join-Path $dsDir ('DungeonData_{0}.xml' -f $NewId))) { $problems.Add(('DungeonData_{0}.xml already exists' -f $NewId)) }
if ($cd.Text.Contains((Get-DupeMarker $kind $NewId 'begin'))) { $problems.Add(('NewId {0}: already added by this script - run -Revert first' -f $NewId)) }
if ($NewHuntingZone -le 0 -or $NewHuntingZone -gt 4095) { $problems.Add(('hunting zone {0}: must be 1..4095' -f $NewHuntingZone)) }
if (@(Get-DupeAttrValues $cd.Text 'HuntingZone' 'id') -contains [string]$NewHuntingZone) { $problems.Add(('hunting zone {0}: already owned by a continent (World: duplicated huntingZoneId)' -f $NewHuntingZone)) }
$taken = @(Find-ZoneFiles $NewHuntingZone)
if ($taken.Count -gt 0) { $problems.Add(('hunting zone {0}: files already exist ({1})' -f $NewHuntingZone, (($taken | ForEach-Object { $_.File.Name }) -join ', '))) }
$srcAl = Find-DupeElement $al.Text 'Continent' 'id' ([string]$Source)
if (-not $srcAl) { $problems.Add(('continent {0} is not a top-level AreaList <Continent>' -f $Source)) }
$srcDd = Join-Path $dsDir ('DungeonData_{0}.xml' -f $Source)
if (-not (Test-Path -LiteralPath $srcDd)) { $problems.Add(('DungeonData_{0}.xml not found' -f $Source)) }

$dm = $null; $srcDm = $null; $useRole = $null; $srcRole = $null; $cloneRole = $false
$ddText = $null; $srcMax = $null
if (Test-Path -LiteralPath $srcDd) {
    $ddText = (Read-DupeSheet $srcDd).Text
    $mm = [regex]::Match((Get-DupeMasked $ddText), '<Condition\b[^>]*\stype\s*=\s*"maxMemberCount"[^>]*>')
    if ($mm.Success) { $srcMax = Get-DupeAttr $ddText.Substring($mm.Index, $mm.Length) 'Condition' 'value' }
}
$members = $srcMax; if ($MaxMembers -gt 0) { $members = [string]$MaxMembers }
if (-not $NoMatching) {
    if (-not $sheets.ContainsKey('DungeonMatching.xml')) { $problems.Add('DungeonMatching.xml not found (or pass -NoMatching)') }
    else {
        $dm = $sheets['DungeonMatching.xml']
        $srcDm = Find-DupeElement $dm.Text 'Dungeon' 'id' ([string]$Source)
        if (-not $srcDm) { $problems.Add(('{0} has no DungeonMatching row to copy (pass -NoMatching)' -f $Source)) }
        else {
            $srcRole = Get-DupeAttr $dm.Text.Substring($srcDm.Start, $srcDm.End - $srcDm.Start) 'Dungeon' 'matchingRoleId'
            if (-not $srcRole) { $srcRole = '1' }                     # World's default (257713)
            $rt = $sheets['MatchingRoleTemplate.xml']
            $roleIds = @(Get-DupeAttrValues $rt.Text 'Role' 'id')
            $useRole = $srcRole
            if ($RoleId -gt 0) { $useRole = [string]$RoleId }
            elseif ($members -and $MaxMembers -gt 0) {
                $sp = Find-DupeElement $rt.Text 'Role' 'id' $srcRole
                if ($sp -and (Get-DupeAttr $rt.Text.Substring($sp.Start, $sp.End - $sp.Start) 'RoleData' 'totalUser') -ne $members) {
                    $cloneRole = $true
                    if ($NewRoleId -le 0) { $NewRoleId = ((@($roleIds | ForEach-Object { [int]$_ } | Where-Object { $_ -lt 100 }) | Measure-Object -Maximum).Maximum) + 1 }
                    if ($roleIds -contains [string]$NewRoleId) { $problems.Add(('NewRoleId {0}: role already exists' -f $NewRoleId)) }
                    $useRole = [string]$NewRoleId
                }
            }
            if (-not $cloneRole) {
                if ($roleIds -notcontains $useRole) { $problems.Add(('role {0}: not in MatchingRoleTemplate.xml' -f $useRole)) }
                else {
                    $sp = Find-DupeElement $rt.Text 'Role' 'id' $useRole
                    $tu = Get-DupeAttr $rt.Text.Substring($sp.Start, $sp.End - $sp.Start) 'RoleData' 'totalUser'
                    if ($tu -ne $members -and -not $Force) { $problems.Add(('role {0}: totalUser {1} <> maxMemberCount {2} - World refuses to boot (DungeonMatchingDataSheet::Validate)' -f $useRole, $tu, $members)) }
                }
            }
        }
    }
}
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Host ('  refused: ' + $_) }; throw 'nothing written' }

# ---- plan -------------------------------------------------------------------------------------
Write-Host ('Dungeon {0} (zone {1}) -> {2} (zone {3}) in {4}' -f $Source, $oldHz, $NewId, $NewHuntingZone, $dsDir)
$c = $al.Text.Substring($srcAl.Start, $srcAl.End - $srcAl.Start)
$c = (Set-DupeAttr $c 'Continent' 'id' ([string]$NewId)).Text
$d = Get-DupeAttr $c 'Continent' 'desc'; if ($d -ne $null) { $c = (Set-DupeAttr $c 'Continent' 'desc' ($d + (' [dupe of {0}]' -f $Source))).Text }
Add-DupeBlock $al $srcAl $c $kind $NewId
Write-DupePlan 'clone' 'AreaList.xml' ('<Continent id="{0}"> (line {1}) as {2}, same Area and Zones' -f $Source, (Get-DupeLineNo $al.Text $srcAl.Start), $NewId)

$srcCd = Find-DupeElement $cd.Text 'Continent' 'id' ([string]$Source)
$c = $cd.Text.Substring($srcCd.Start, $srcCd.End - $srcCd.Start)
$c = (Set-DupeAttr $c 'Continent' 'id' ([string]$NewId)).Text
$c = (Set-DupeAttr $c 'HuntingZone' 'id' ([string]$NewHuntingZone)).Text
Add-DupeBlock $cd $srcCd $c $kind $NewId
Write-DupePlan 'clone' 'ContinentData.xml' ('<Continent id="{0}"> as {1}, HuntingZone {2} -> {3}' -f $Source, $NewId, $oldHz, $NewHuntingZone)

$ddSheet = Read-DupeSheet $srcDd
$r = Convert-DupeText $ddSheet.Text ([string]$oldHz) ([string]$NewHuntingZone) ([string]$Source) ([string]$NewId)
$ddSheet.Text = $r.Text
$note = ('continentId {0} -> {1}, {2} zone reference(s) {3} -> {4}' -f $Source, $NewId, ($r.Count - 1), $oldHz, $NewHuntingZone)
if ($MaxMembers -gt 0) {
    $mm = [regex]::Match((Get-DupeMasked $ddSheet.Text), '<Condition\b[^>]*\stype\s*=\s*"maxMemberCount"[^>]*>')
    if (-not $mm.Success) { throw ('DungeonData_{0}.xml has no <Condition type="maxMemberCount">' -f $Source) }
    $cond = (Set-DupeAttr $ddSheet.Text.Substring($mm.Index, $mm.Length) 'Condition' 'value' ([string]$MaxMembers))
    $ddSheet.Text = $ddSheet.Text.Substring(0, $mm.Index) + $cond.Text + $ddSheet.Text.Substring($mm.Index + $mm.Length)
    $note += (', maxMemberCount {0} -> {1}' -f $cond.Old, $MaxMembers)
}
$ddSheet.Text = Add-DupeFileMarker $ddSheet.Text $kind $NewId ('DungeonData_{0}.xml' -f $Source) $ddSheet.NewLine
$created.Add(@{ Path = (Join-Path $dsDir ('DungeonData_{0}.xml' -f $NewId)); Sheet = $ddSheet })
Write-DupePlan 'create' ('DungeonData_{0}.xml' -f $NewId) $note

foreach ($z in @(Find-ZoneFiles $oldHz)) {
    $s = Read-DupeSheet $z.File.FullName
    $r = Convert-DupeText $s.Text ([string]$oldHz) ([string]$NewHuntingZone) '' ''
    $s.Text = Add-DupeFileMarker $r.Text $kind $NewId $z.File.Name $s.NewLine
    $num = ([string]$NewHuntingZone).PadLeft($z.Pad, '0')
    $name = [regex]::Replace($z.File.Name, '_0*' + $oldHz + '\.xml$', '_' + $num + '.xml')
    $target = Join-Path $z.File.DirectoryName $name
    $created.Add(@{ Path = $target; Sheet = $s })
    Write-DupePlan 'create' $name ('from {0}: {1} zone reference(s) {2} -> {3}' -f $z.File.Name, $r.Count, $oldHz, $NewHuntingZone)
}
foreach ($z in @(Find-ContFiles $Source)) {
    if ($skipFamilies -contains $z.Family) { Write-DupePlan 'skip' $z.File.Name 'would add a PvE leaderboard (TeraSharp ReadPveBoardIds)'; continue }
    $s = Read-DupeSheet $z.File.FullName
    $r = Convert-DupeText $s.Text ([string]$oldHz) ([string]$NewHuntingZone) ([string]$Source) ([string]$NewId) -AreaRoot:($z.Family -eq 'AreaData')
    $s.Text = Add-DupeFileMarker $r.Text $kind $NewId $z.File.Name $s.NewLine
    $name = '{0}_{1}{2}.xml' -f ([regex]::Match($z.File.Name, '^[^_]+').Value), $NewId, $z.Suffix
    $created.Add(@{ Path = (Join-Path $z.File.DirectoryName $name); Sheet = $s })
    Write-DupePlan 'create' $name ('from {0}: {1} id reference(s) moved' -f $z.File.Name, $r.Count)
}

if ($srcDm) {
    $c = $dm.Text.Substring($srcDm.Start, $srcDm.End - $srcDm.Start)
    $c = (Set-DupeAttr $c 'Dungeon' 'id' ([string]$NewId)).Text
    if ($useRole -ne $srcRole -or -not (Get-DupeAttr $c 'Dungeon' 'matchingRoleId')) { $c = (Set-DupeAttr $c 'Dungeon' 'matchingRoleId' $useRole).Text }
    Add-DupeBlock $dm $srcDm $c $kind $NewId
    Write-DupePlan 'clone' 'DungeonMatching.xml' ('<Dungeon id="{0}"> as {1}, matchingRoleId {2}' -f $Source, $NewId, $useRole)
}
if ($cloneRole) {
    $rt = $sheets['MatchingRoleTemplate.xml']
    $sp = Find-DupeElement $rt.Text 'Role' 'id' $srcRole
    $c = $rt.Text.Substring($sp.Start, $sp.End - $sp.Start)
    $old = Get-DupeAttr $c 'RoleData' 'totalUser'
    $c = (Set-DupeAttr $c 'Role' 'id' $useRole).Text
    $c = (Set-DupeAttr $c 'RoleData' 'totalUser' $members).Text
    $notes = @(('totalUser {0} -> {1}' -f $old, $members))
    $qa = Get-DupeAttr $c 'RoleData' 'totalUserQa'
    if ($qa -and [int]$qa -gt [int]$members) { $c = (Set-DupeAttr $c 'RoleData' 'totalUserQa' $members).Text; $notes += ('totalUserQa {0} -> {1}' -f $qa, $members) }
    $fit = Resize-DupeRole $c ([int]$members); $c = $fit.Text; $notes += $fit.Notes
    if ((Get-DupeAttr $c 'Role' 'changeRoleId')) { $c = [regex]::Replace($c, '\s+changeRoleId\s*=\s*"[^"]*"', ''); $notes += 'changeRoleId dropped' }
    Add-DupeBlock $rt $sp $c $kind $NewId
    Write-DupePlan 'clone' 'MatchingRoleTemplate.xml' ('<Role id="{0}"> as {1}: {2}' -f $srcRole, $useRole, ($notes -join ', '))
}
if (-not $NoConstraint -and $sheets.ContainsKey('DungeonConstraint.xml')) {
    $dc = $sheets['DungeonConstraint.xml']
    $sp = Find-DupeElement $dc.Text 'Constraint' 'continentId' ([string]$Source)
    if ($sp) {
        $c = $dc.Text.Substring($sp.Start, $sp.End - $sp.Start)
        $c = (Set-DupeAttr $c 'Constraint' 'continentId' ([string]$NewId)).Text
        if (Get-DupeAttr $c 'Constraint' 'huntingZoneId') { $c = (Set-DupeAttr $c 'Constraint' 'huntingZoneId' ([string]$NewHuntingZone)).Text }
        Add-DupeBlock $dc $sp $c $kind $NewId
        Write-DupePlan 'clone' 'DungeonConstraint.xml' ('<Constraint continentId="{0}"> as {1}' -f $Source, $NewId)
    } else { Write-DupePlan 'skip' 'DungeonConstraint.xml' ('no row for {0}' -f $Source) }
}

# ---- invariants on the result (also in a dry run) ---------------------------------------------
$bad = New-Object System.Collections.Generic.List[string]
$alTop = @(Get-DupeAttrValues $al.Text 'Continent' 'id') + @(Get-DupeAttrValues $al.Text 'ChannelContinent' 'id')
foreach ($id in @(Get-DupeAttrValues $cd.Text 'Continent' 'id')) { if ($alTop -notcontains $id) { $bad.Add(('ContinentData {0} has no AreaList continent (fatal: ContinentData Loading Error)' -f $id)) } }
$hzAll = @(Get-DupeAttrValues $cd.Text 'HuntingZone' 'id')
$hzDup = @($hzAll | Group-Object | Where-Object { $_.Count -gt 1 })
if ($hzDup.Count -gt 0) { $bad.Add(('hunting zone {0} in two continents (fatal: duplicated huntingZoneId)' -f $hzDup[0].Name)) }
if ($srcDm) {
    $rt = $sheets['MatchingRoleTemplate.xml']
    $sp = Find-DupeElement $rt.Text 'Role' 'id' $useRole
    if (-not $sp) { $bad.Add(('matchingRoleId {0} has no Role' -f $useRole)) }
    else {
        $tu = Get-DupeAttr $rt.Text.Substring($sp.Start, $sp.End - $sp.Start) 'RoleData' 'totalUser'
        $nm = [regex]::Match((Get-DupeMasked $ddSheet.Text), '<Condition\b[^>]*\stype\s*=\s*"maxMemberCount"[^>]*>')
        $mv = $null; if ($nm.Success) { $mv = Get-DupeAttr $ddSheet.Text.Substring($nm.Index, $nm.Length) 'Condition' 'value' }
        if ($tu -ne $mv -and -not $Force) { $bad.Add(('role {0} totalUser {1} <> maxMemberCount {2} (fatal: DungeonMatchingDataSheet::Validate)' -f $useRole, $tu, $mv)) }
    }
}
if (@(Get-DupeAttrValues $ddSheet.Text 'Dungeon' 'continentId') -notcontains [string]$NewId) { $bad.Add('DungeonData root continentId not moved') }
if ($bad.Count -gt 0) { $bad | ForEach-Object { Write-Host ('  check failed: ' + $_) }; throw 'invariants failed; nothing written' }
Write-Host '  check   ok: ContinentData in AreaList, zones unique, role totalUser = maxMemberCount'

$changed = @($sheets.Values | Where-Object { $_.Changed })
if ($PSCmdlet.ShouldProcess(('{0} sheet(s), {1} new file(s)' -f $changed.Count, $created.Count), 'write')) {
    $bak = Backup-DupeSheets $dsDir @($changed | ForEach-Object { $_.Path }) ('dungeon-{0}' -f $NewId)
    foreach ($s in $changed) { Write-DupeSheet $s }
    foreach ($cf in $created) { Write-DupeSheet $cf.Sheet $cf.Path }
    Write-Host ('written; originals in {0}.' -f $bak)
    Write-Host 'Restart TopographyServer, ArbiterServer, WorldServers, MatchServer, TeraSharp.'
    Write-Host ('Client: the DataCenter needs AreaList / ContinentData / StrSheet_Dungeon rows for {0} (DUPLICATE-CONTENT.md section 5).' -f $NewId)
}
