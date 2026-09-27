# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
    Trim a TERA 100.02 Datasheet folder down to a keep-list of continent ids, so the real
    ArbiterServer + WorldServer boot with only those continents loaded.

.DESCRIPTION
    T63. The mechanism is NOT ServerConfig.xml's <WorldServer loadAllContinents="false"> with
    <Continent> children - that list can only ever select continents whose channelType is not
    "dungeon"/"battleField", so intro instances are silently dropped and you get a server with no
    instances. The mechanism that works is loadAllContinents="true" plus a trimmed
    Datasheet\ContinentData.xml: PlanetInfo::LoadPlanetInfo builds the per-server continent list
    from the continents that survived the sheet.

    This script does three kinds of thing, all reversible with restore-datasheets.ps1:

      1. ROW TRIMS   - deletes XML rows that name a dropped continent, from the seven sheets that
                       resolve continent / dungeon ids at boot.
      2. FILE MOVES  - moves per-continent datasheet files (AreaData_<id>_*.xml,
                       DungeonData_<id>.xml, ...) into an aside folder OUTSIDE the Datasheet tree,
                       because the loader walks that tree recursively and would still read them.
      3. STUBS       - writes a minimal ShieldTerritory file for a kept continent that would
                       otherwise fail ShieldTerritory::PostProcess.

    Every layout rule here is cited to the decompiled validator in status\WORLD-PARTIAL-LOAD.md.

.PARAMETER DatasheetRoot
    The Datasheet folder, e.g. D:\v100\TERA_SERVER.100\Executable\Datasheet

.PARAMETER KeepContinents
    The continent ids to keep, e.g. 5,9827,9828,9829 (Island of Dawn plus the intro instances).

.PARAMETER AsideRoot
    Where moved files go. MUST be outside DatasheetRoot - the server's file walk is recursive, so
    an aside folder inside Datasheet would still be loaded. Also holds the manifest and the
    pre-edit backups of every sheet this script rewrites.

.PARAMETER Destination
    Optional. When given, DatasheetRoot is copied here first and the trim is applied to the copy;
    the original is never touched. When omitted the trim is applied in place.

.PARAMETER ServerConfig
    Optional path to ServerConfig.xml. When given, the wildcard datasheet families are read from
    its <Datasheet> blocks instead of the built-in table, so a changed mapping is picked up.

.PARAMETER Aggressiveness
    Minimal    - only the families whose loader resolves continentId fatally. The safe default.
    Standard   - adds the territory families that self-filter via DoIHaveThisContinent (moving
                 them saves disk walk time but is not required).
    Aggressive - adds hunting-zone-keyed families, resolved through ContinentData's <HuntingZone>
                 children. Never touches NpcData_*, the skill sheets or S1ActionScripts_*, which
                 are cross-sheet resolution targets.

.PARAMETER StubShieldTerritory
    Write a 4-fence ShieldTerritory stub for any kept continent left without one. Only needed for a
    continent whose AreaList <Area> entries carry no <Zone>; harmless otherwise.

.EXAMPLE
    .\trim-datasheets.ps1 -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
                          -KeepContinents 5,9827,9828,9829 `
                          -AsideRoot D:\v100\TERA_SERVER.100\DatasheetAside -WhatIf

.EXAMPLE
    .\trim-datasheets.ps1 -DatasheetRoot D:\v100\TERA_SERVER.100\Executable\Datasheet `
                          -KeepContinents 5,9827,9828,9829 `
                          -Destination D:\v100\scratch\Datasheet `
                          -AsideRoot D:\v100\scratch\aside
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)] [string]   $DatasheetRoot,
    [Parameter(Mandatory = $true)] [int[]]    $KeepContinents,
    [Parameter(Mandatory = $true)] [string]   $AsideRoot,
    [string] $Destination,
    [string] $ServerConfig,
    [ValidateSet('Minimal', 'Standard', 'Aggressive')] [string] $Aggressiveness = 'Minimal',
    [switch] $StubShieldTerritory,
    [string] $Manifest
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# =================================================================================================
# 0. The family table.
#
#    IdKind says what the number in the file name is. That is the whole game: a file named for a
#    hunting zone cannot be selected by a continent keep-list without the <HuntingZone> map in
#    ContinentData.xml, and a file named for something else cannot be selected at all.
#
#    Tier says when the file is moved. "Never" means the sheet is a cross-sheet resolution target
#    (NpcData_*) or is not continent-scoped (skills, action scripts) - moving it breaks validators
#    that have nothing to do with continents.
# =================================================================================================
$script:Families = @(
    # ---- fatal if left behind: the loader resolves continentId against ContinentData ----
    [pscustomobject]@{ Sheet = 'AreaData';                    Pattern = 'AreaData_*.xml';              IdKind = 'Continent';   Tier = 'Minimal' }
    [pscustomobject]@{ Sheet = 'DungeonTemplate';             Pattern = 'DungeonData_*.xml';           IdKind = 'Continent';   Tier = 'Minimal' }
    [pscustomobject]@{ Sheet = 'DungeonRankRecorderTemplate'; Pattern = 'DungeonRankRecorder_*.xml';   IdKind = 'Continent';   Tier = 'Minimal' }
    [pscustomobject]@{ Sheet = 'FieldTemplate';               Pattern = 'FieldData_*.xml';             IdKind = 'Continent';   Tier = 'Minimal' }
    [pscustomobject]@{ Sheet = 'DynamicGeoTemplate';          Pattern = 'DynamicGeo_*.xml';            IdKind = 'Continent';   Tier = 'Minimal' }
    [pscustomobject]@{ Sheet = 'CollectionTerritory';         Pattern = 'CollectionTerritory_*.xml';   IdKind = 'Continent';   Tier = 'Minimal' }
    [pscustomobject]@{ Sheet = 'ShieldTerritory';             Pattern = 'ShieldTerritory_*.xml';       IdKind = 'Continent';   Tier = 'Minimal' }
    # ---- self-filtering (DoIHaveThisContinent): optional, moving them only saves walk time ----
    [pscustomobject]@{ Sheet = 'ClimbingTemplate';            Pattern = 'ClimbingTerritory_*.xml';     IdKind = 'Continent';   Tier = 'Standard' }
    [pscustomobject]@{ Sheet = 'SwimmingTerritoryTemplate';   Pattern = 'SwimmingTerritory_*.xml';     IdKind = 'Continent';   Tier = 'Standard' }
    [pscustomobject]@{ Sheet = 'TeleportTerritoryTemplate';   Pattern = 'TeleportTerritory_*.xml';     IdKind = 'Continent';   Tier = 'Standard' }
    [pscustomobject]@{ Sheet = 'TerritoryTemplate';           Pattern = 'TerritoryData_*.xml';         IdKind = 'HuntingZone'; Tier = 'Standard' }
    [pscustomobject]@{ Sheet = 'FishingTerritoryTemplate';    Pattern = 'FishingTerritory_*.xml';      IdKind = 'HuntingZone'; Tier = 'Standard' }
    # ---- hunting-zone keyed: resolvable only through ContinentData's <HuntingZone> children ----
    [pscustomobject]@{ Sheet = 'AiTemplate';                  Pattern = 'AiData_*.xml';                IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'ActiveMoveTemplate';          Pattern = 'ActiveMove_*.xml';            IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'ActiveRotateTemplate';        Pattern = 'ActiveRotate_*.xml';          IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'DynamicSpawnTemplate';        Pattern = 'DynamicSpawn_*.xml';          IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'FormationData';               Pattern = 'FormationData_*.xml';         IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'BonfireSpawnData';            Pattern = 'BonfireData_*.xml';           IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'WorkObjectSpawnData';         Pattern = 'WorkObjectTerritory_*.xml';   IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'QuestCompensationData';       Pattern = 'QuestCompensationData_*.xml'; IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'NpcPartTemplate';             Pattern = 'NpcPartData_*.xml';           IdKind = 'HuntingZone'; Tier = 'Aggressive' }
    [pscustomobject]@{ Sheet = 'BehaviorRecorderTemplate';    Pattern = 'BehaviorRecorder_*.xml';      IdKind = 'Unused';      Tier = 'Aggressive' }
    # ---- never moved ----
    [pscustomobject]@{ Sheet = 'NpcTemplate';                 Pattern = 'NpcData_*.xml';               IdKind = 'HuntingZone'; Tier = 'Never' }
    [pscustomobject]@{ Sheet = 'NpcSkillTemplate';            Pattern = 'NpcSkillData_*.xml';          IdKind = 'Unverified';  Tier = 'Never' }
    [pscustomobject]@{ Sheet = 'UserSkillTemplate';           Pattern = 'UserSkillData_*.xml';         IdKind = 'NotAnId';     Tier = 'Never' }
    [pscustomobject]@{ Sheet = 'HeroSkillTemplate';           Pattern = 'HeroSkillData_*.xml';         IdKind = 'NotAnId';     Tier = 'Never' }
    [pscustomobject]@{ Sheet = 'S1ActionScript';              Pattern = 'S1ActionScripts_*.xml';       IdKind = 'NotAnId';     Tier = 'Never' }
    [pscustomobject]@{ Sheet = 'PegasusPath';                 Pattern = 'PegasusPath_*.xml';           IdKind = 'NotAnId';     Tier = 'Never' }
)

$script:TierRank = @{ 'Minimal' = 1; 'Standard' = 2; 'Aggressive' = 3; 'Never' = 99 }

# =================================================================================================
# 1. Helpers
# =================================================================================================

function Write-Step([string] $Text) { Write-Host ''; Write-Host "== $Text" -ForegroundColor Cyan }
function Write-Note([string] $Text) { Write-Host "   $Text" }

function Get-FullPathSafe([string] $Path) {
    # Resolve-Path fails on a path that does not exist yet; this does not.
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).ProviderPath $Path))
}

function Test-IsUnder([string] $Child, [string] $Parent) {
    $c = (Get-FullPathSafe $Child).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $p = (Get-FullPathSafe $Parent).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    return $c.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)
}

function Read-XmlPreserving([string] $Path) {
    # PreserveWhitespace keeps the file diffable: comments, indentation and line breaks survive.
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.Load($Path)
    return $doc
}

function Save-XmlPreserving([System.Xml.XmlDocument] $Doc, [string] $Path) {
    # The shipped sheets are UTF-8 WITH a BOM and both servers read them that way, so keep it.
    $settings = New-Object System.Xml.XmlWriterSettings
    $settings.Encoding = New-Object System.Text.UTF8Encoding($true)
    $settings.Indent = $false                   # PreserveWhitespace already carries the layout
    $settings.OmitXmlDeclaration = $false
    $writer = [System.Xml.XmlWriter]::Create($Path, $settings)
    try { $Doc.Save($writer) } finally { $writer.Close() }
}

function Remove-NodeWithLeadingWhitespace([System.Xml.XmlNode] $Node) {
    # Take the indentation in front of the node with it, or the file fills up with blank lines,
    # and take a trailing comment that sits on the SAME line, because these sheets label rows that
    # way ( <Constraint ... /><!-- Malakai's laboratory --> ) and an orphaned label is worse than
    # no label: it drifts up and describes the wrong row.
    $parent = $Node.ParentNode

    $next = $Node.NextSibling
    $gap  = $null
    if ($null -ne $next -and $next.NodeType -eq [System.Xml.XmlNodeType]::Whitespace -and $next.Value -notmatch "`n") {
        $gap = $next
        $next = $next.NextSibling
    }
    if ($null -ne $next -and $next.NodeType -eq [System.Xml.XmlNodeType]::Comment) {
        if ($null -ne $gap) { [void] $parent.RemoveChild($gap) }
        [void] $parent.RemoveChild($next)
    }

    $prev = $Node.PreviousSibling
    if ($null -ne $prev -and $prev.NodeType -eq [System.Xml.XmlNodeType]::Whitespace) {
        [void] $parent.RemoveChild($prev)
    }
    [void] $parent.RemoveChild($Node)
}

function Get-IntAttr([System.Xml.XmlNode] $Node, [string] $Name) {
    $v = $Node.GetAttribute($Name)
    if ([string]::IsNullOrWhiteSpace($v)) { return $null }
    $parsed = 0
    if ([int]::TryParse($v.Trim(), [ref] $parsed)) { return $parsed }
    return $null
}

function Get-IdFromFileName([string] $FileName, [string] $Pattern) {
    # 'AreaData_*.xml' + 'AreaData_9827_A_Island_02_P.xml' -> 9827
    # The number is always the first run of digits after the fixed prefix.
    $prefix = $Pattern.Substring(0, $Pattern.IndexOf('*'))
    if (-not $FileName.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { return $null }
    $rest = $FileName.Substring($prefix.Length)
    $m = [regex]::Match($rest, '^(\d+)')
    if (-not $m.Success) { return $null }
    return [int] $m.Groups[1].Value
}

function Import-FamiliesFromServerConfig([string] $Path) {
    # Union of every <Datasheet><SheetName fileName="..."/> across all server sections, filtered to
    # the families we know how to classify. A sheet we have no verdict on is reported, never moved.
    $doc = New-Object System.Xml.XmlDocument
    $doc.Load($Path)
    $seen = @{}
    foreach ($entry in $doc.SelectNodes('//Datasheet/*')) {
        $fn = $entry.GetAttribute('fileName')
        if ([string]::IsNullOrWhiteSpace($fn)) { continue }
        if ($fn -notlike '*`**') { continue }
        if (-not $seen.ContainsKey($entry.Name)) { $seen[$entry.Name] = $fn }
    }
    $out = @()
    $unknown = @()
    foreach ($name in ($seen.Keys | Sort-Object)) {
        $known = $script:Families | Where-Object { $_.Sheet -eq $name }
        if ($known) {
            # Trust ServerConfig for the pattern, our table for the classification.
            $out += [pscustomobject]@{ Sheet = $name; Pattern = $seen[$name]; IdKind = $known.IdKind; Tier = $known.Tier }
        }
        else { $unknown += "$name = $($seen[$name])" }
    }
    # Families we classify but ServerConfig did not mention are kept, so a stale config cannot
    # silently stop us moving something that is actually on disk.
    foreach ($f in $script:Families) {
        if (-not ($out | Where-Object { $_.Sheet -eq $f.Sheet })) { $out += $f }
    }
    return [pscustomobject]@{ Families = $out; Unknown = $unknown }
}

# =================================================================================================
# 2. Set up
# =================================================================================================

$DatasheetRoot = Get-FullPathSafe $DatasheetRoot
if (-not (Test-Path -LiteralPath $DatasheetRoot -PathType Container)) {
    throw "DatasheetRoot not found: $DatasheetRoot"
}
$AsideRoot = Get-FullPathSafe $AsideRoot

$workRoot = $DatasheetRoot
if ($Destination) {
    $Destination = Get-FullPathSafe $Destination
    if (Test-IsUnder $Destination $DatasheetRoot) {
        throw "Destination must not be inside DatasheetRoot - the server's file walk is recursive."
    }
    $workRoot = $Destination
}

if (Test-IsUnder $AsideRoot $workRoot) {
    throw "AsideRoot ($AsideRoot) is inside the Datasheet tree ($workRoot). The loader walks that tree recursively (DataXmlManager::SearchFilesInDirectory recurses into every subfolder under rootFolder), so files moved there would still be loaded. Pick a folder outside it."
}

if (-not $Manifest) { $Manifest = Join-Path $AsideRoot 'trim-manifest.json' }
$backupDir = Join-Path $AsideRoot '_edited-originals'

$keep = [System.Collections.Generic.HashSet[int]]::new()
foreach ($k in $KeepContinents) { [void] $keep.Add($k) }

Write-Step "TERA datasheet trim"
Write-Note "source      : $DatasheetRoot"
Write-Note "working on  : $workRoot$(if ($Destination) { '  (copy - the original is not touched)' } else { '  (IN PLACE)' })"
Write-Note "aside       : $AsideRoot"
Write-Note "keep        : $(($KeepContinents | Sort-Object) -join ', ')"
Write-Note "tier        : $Aggressiveness"

$families = $script:Families
if ($ServerConfig) {
    $ServerConfig = Get-FullPathSafe $ServerConfig
    $imported = Import-FamiliesFromServerConfig $ServerConfig
    $families = $imported.Families
    Write-Note "families    : $($families.Count) (patterns taken from $([System.IO.Path]::GetFileName($ServerConfig)))"
    if ($imported.Unknown.Count -gt 0) {
        Write-Note "unclassified wildcard sheets in ServerConfig - left alone, listed for review:"
        foreach ($u in $imported.Unknown) { Write-Note "    $u" }
    }
}

# ---- the copy, if asked for -------------------------------------------------------------------
if ($Destination) {
    if ($PSCmdlet.ShouldProcess($Destination, "copy $DatasheetRoot")) {
        if (Test-Path -LiteralPath $Destination) {
            throw "Destination already exists: $Destination (refusing to merge into it)"
        }
        Write-Step "Copying the Datasheet tree"
        $t0 = Get-Date
        Copy-Item -LiteralPath $DatasheetRoot -Destination $Destination -Recurse -Force
        $n = (Get-ChildItem -LiteralPath $Destination -Recurse -File).Count
        Write-Note ("{0} files in {1:n1}s" -f $n, ((Get-Date) - $t0).TotalSeconds)
    }
}

foreach ($d in @($AsideRoot, $backupDir)) {
    if (-not (Test-Path -LiteralPath $d)) {
        if ($PSCmdlet.ShouldProcess($d, 'create folder')) { [void] (New-Item -ItemType Directory -Path $d -Force) }
    }
}

$manifestData = [ordered]@{
    schema          = 'terasharp.trim-datasheets/1'
    createdUtc      = (Get-Date).ToUniversalTime().ToString('o')
    datasheetRoot   = $workRoot
    originalRoot    = $DatasheetRoot
    asideRoot       = $AsideRoot
    backupDir       = $backupDir
    keepContinents  = @($KeepContinents | Sort-Object)
    aggressiveness  = $Aggressiveness
    inPlace         = (-not $Destination)
    movedFiles      = @()
    editedFiles     = @()
    createdFiles    = @()
}

# =================================================================================================
# 3. Read AreaList + ContinentData first - they define what a continent IS.
#
#    AreaList is the only creator of continent records (PlanetInfo::LoadAreaList). It holds
#    top-level <Continent id> AND nested <ChannelContinent id> children; on the shipped 100.02 data
#    the 18 channel continents (2000/2050/2052/2054, 7001-7005, 7011-7015, 7021-7023, 7031) exist
#    ONLY as nested <ChannelContinent>, which is why a naive root-level parse says they are missing.
#
#    ContinentData requires, never creates: a <Continent id> with no AreaList record is a fatal
#    PreLoad error. So ContinentData ids must stay a subset of AreaList ids.
# =================================================================================================
Write-Step "Reading AreaList.xml and ContinentData.xml"

$areaListPath      = Join-Path $workRoot 'AreaList.xml'
$continentDataPath = Join-Path $workRoot 'ContinentData.xml'
foreach ($p in @($areaListPath, $continentDataPath)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Required sheet missing: $p" }
}

$alDoc = Read-XmlPreserving $areaListPath
$cdDoc = Read-XmlPreserving $continentDataPath

$areaListIds = [System.Collections.Generic.HashSet[int]]::new()
foreach ($c in $alDoc.SelectNodes('/*/Continent')) {
    $id = Get-IntAttr $c 'id'; if ($null -ne $id) { [void] $areaListIds.Add($id) }
    foreach ($cc in $c.SelectNodes('ChannelContinent')) {
        $ccid = Get-IntAttr $cc 'id'; if ($null -ne $ccid) { [void] $areaListIds.Add($ccid) }
    }
}
Write-Note "AreaList continents          : $($areaListIds.Count) (top-level + nested ChannelContinent)"

# hunting zone -> continent, straight out of ContinentData's <HuntingZone id> children
$hzToContinent = @{}
$continentDataIds = [System.Collections.Generic.HashSet[int]]::new()
foreach ($c in $cdDoc.SelectNodes('/*/Continent')) {
    $id = Get-IntAttr $c 'id'; if ($null -eq $id) { continue }
    [void] $continentDataIds.Add($id)
    foreach ($hz in $c.SelectNodes('.//HuntingZone')) {
        $h = Get-IntAttr $hz 'id'
        if ($null -ne $h -and -not $hzToContinent.ContainsKey($h)) { $hzToContinent[$h] = $id }
    }
}
Write-Note "ContinentData continents     : $($continentDataIds.Count)"
Write-Note "hunting zones mapped         : $($hzToContinent.Count)"

$missingKeep = @($KeepContinents | Where-Object { -not $areaListIds.Contains($_) })
if ($missingKeep.Count -gt 0) {
    throw "These kept continents have no AreaList record and cannot be kept: $($missingKeep -join ', '). PlanetInfo::LoadAreaList is the only creator of continent records; ContinentData requires one."
}

$keptHuntingZones = [System.Collections.Generic.HashSet[int]]::new()
foreach ($kv in $hzToContinent.GetEnumerator()) {
    if ($keep.Contains($kv.Value)) { [void] $keptHuntingZones.Add($kv.Key) }
}
Write-Note "hunting zones kept           : $(($keptHuntingZones | Sort-Object) -join ', ')"

# =================================================================================================
# 4. Row trims
# =================================================================================================

$script:EditedAny = @{}

function Backup-Once([string] $Path) {
    if ($script:EditedAny.ContainsKey($Path)) { return }
    $rel = $Path.Substring($workRoot.Length).TrimStart('\', '/')
    $dest = Join-Path $backupDir $rel
    $destDir = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $destDir)) { [void] (New-Item -ItemType Directory -Path $destDir -Force) }
    Copy-Item -LiteralPath $Path -Destination $dest -Force
    $script:EditedAny[$Path] = $dest
    $manifestData.editedFiles += [ordered]@{ path = $Path; backup = $dest; relative = $rel }
}

function Invoke-SheetTrim {
    param(
        [string] $FileName,
        [string] $Why,
        [scriptblock] $Body        # receives the XmlDocument, returns a summary string, or $null to skip
    )
    $path = Join-Path $workRoot $FileName
    if (-not (Test-Path -LiteralPath $path)) {
        Write-Note ("{0,-24} (absent - skipped)" -f $FileName)
        return
    }
    $doc = Read-XmlPreserving $path
    $summary = & $Body $doc
    if ($null -eq $summary) {
        Write-Note ("{0,-24} nothing to remove" -f $FileName)
        return
    }
    if ($PSCmdlet.ShouldProcess($path, "trim rows ($Why)")) {
        Backup-Once $path
        Save-XmlPreserving $doc $path
    }
    Write-Note ("{0,-24} {1}" -f $FileName, $summary)
}

Write-Step "Trimming rows"

# ---- AreaList.xml --------------------------------------------------------------------------
# Keep only kept continents. A kept continent keeps only its kept <ChannelContinent> children:
# each nested one becomes its own ContinentInfo with its own TopoMap, so an unkept one is pure cost.
Invoke-SheetTrim 'AreaList.xml' 'continents not in the keep-list' {
    param($doc)
    $removedC = 0; $removedCC = 0
    foreach ($c in @($doc.SelectNodes('/*/Continent'))) {
        $id = Get-IntAttr $c 'id'
        if ($null -eq $id -or -not $keep.Contains($id)) { Remove-NodeWithLeadingWhitespace $c; $removedC++; continue }
        foreach ($cc in @($c.SelectNodes('ChannelContinent'))) {
            $ccid = Get-IntAttr $cc 'id'
            if ($null -eq $ccid -or -not $keep.Contains($ccid)) { Remove-NodeWithLeadingWhitespace $cc; $removedCC++ }
        }
    }
    if ($removedC -eq 0 -and $removedCC -eq 0) { return $null }
    return "removed $removedC <Continent>, $removedCC nested <ChannelContinent>"
}

# ---- ContinentData.xml ---------------------------------------------------------------------
# Keep only kept rows, then repair inChannelingContinent: every id it lists is resolved against the
# continent map by PlanetInfo::PostProcess and an unresolvable one is fatal
# ("Invalid Channeling Continent Info. ContinentId [%d], Channeling ContinentId [%d]").
# This is why keeping continent 1 dragged in 7001-7005: that is its inChannelingContinent value.
Invoke-SheetTrim 'ContinentData.xml' 'continents not in the keep-list' {
    param($doc)
    $removed = 0; $rewritten = @()
    foreach ($c in @($doc.SelectNodes('/*/Continent'))) {
        $id = Get-IntAttr $c 'id'
        if ($null -eq $id -or -not $keep.Contains($id)) { Remove-NodeWithLeadingWhitespace $c; $removed++; continue }
        $chan = $c.GetAttribute('inChannelingContinent')
        if ([string]::IsNullOrWhiteSpace($chan)) { continue }
        $ids = @()
        foreach ($piece in $chan.Split(',')) {
            $t = $piece.Trim()
            if ($t -eq '') { continue }
            $n = 0
            if ([int]::TryParse($t, [ref] $n) -and $keep.Contains($n)) { $ids += $n }
        }
        if ($ids.Count -eq 0) { $c.RemoveAttribute('inChannelingContinent'); $rewritten += "$id -> (removed)" }
        elseif (($ids -join ',') -ne ($chan -replace '\s', '')) {
            $c.SetAttribute('inChannelingContinent', ($ids -join ','))
            $rewritten += "$id -> $($ids -join ',')"
        }
    }
    if ($removed -eq 0 -and $rewritten.Count -eq 0) { return $null }
    $s = "removed $removed <Continent>"
    if ($rewritten.Count -gt 0) { $s += "; inChannelingContinent $($rewritten -join '; ')" }
    return $s
}

# ---- DungeonConstraint.xml ------------------------------------------------------------------
# <Constraint continentId="..."> is resolved against DungeonDataSheet; a row naming a dungeon whose
# DungeonData_<id>.xml we moved aside is fatal.
Invoke-SheetTrim 'DungeonConstraint.xml' 'constraints for dropped dungeons' {
    param($doc)
    $removed = 0
    foreach ($n in @($doc.SelectNodes('//Constraint'))) {
        $id = Get-IntAttr $n 'continentId'
        if ($null -eq $id -or -not $keep.Contains($id)) { Remove-NodeWithLeadingWhitespace $n; $removed++ }
    }
    if ($removed -eq 0) { return $null }
    return "removed $removed <Constraint>"
}

# ---- DungeonMatching.xml --------------------------------------------------------------------
# <Dungeon id> is a dungeon continent id. The Arbiter walks this list in PreLoad and
# EventMatching's validator resolves support-matching targets against it.
$script:KeptMatchingDungeons = [System.Collections.Generic.HashSet[int]]::new()
Invoke-SheetTrim 'DungeonMatching.xml' 'matching entries for dropped dungeons' {
    param($doc)
    $removed = 0
    foreach ($n in @($doc.SelectNodes('/*/Dungeon'))) {
        $id = Get-IntAttr $n 'id'
        if ($null -eq $id -or -not $keep.Contains($id)) { Remove-NodeWithLeadingWhitespace $n; $removed++ }
        else { [void] $script:KeptMatchingDungeons.Add($id) }
    }
    if ($removed -eq 0) { return $null }
    return "removed $removed <Dungeon>"
}

# ---- CompetitionDungeon.xml -----------------------------------------------------------------
# Must be trimmed BEFORE Leaderboards: every surviving Leaderboards dungeon row is resolved against
# this sheet as well as against DungeonDataSheet.
$script:KeptCompetitionDungeons = [System.Collections.Generic.HashSet[int]]::new()
Invoke-SheetTrim 'CompetitionDungeon.xml' 'competition entries for dropped dungeons' {
    param($doc)
    $removed = 0
    foreach ($n in @($doc.SelectNodes('/*/Dungeon'))) {
        $id = Get-IntAttr $n 'id'
        if ($null -eq $id -or -not $keep.Contains($id)) { Remove-NodeWithLeadingWhitespace $n; $removed++ }
        else { [void] $script:KeptCompetitionDungeons.Add($id) }
    }
    if ($removed -eq 0) { return $null }
    return "removed $removed <Dungeon>"
}

# ---- Leaderboards.xml -----------------------------------------------------------------------
# LeaderBoardDataSheet::PostProcess resolves every <ContentInfo id> under
# <ContentsTypeList type="dungeon"> against BOTH DungeonDataSheet and CompetitionDungeonDataSheet,
# and bumps the error counter on a miss - which is what makes the whole PostProcess stage return
# false ("[LeaderBoardDataSheet] Post-Process Error!"). battlefield rows resolve against
# BattleFieldTemplate, which is not continent-scoped, so they are left alone.
Invoke-SheetTrim 'Leaderboards.xml' 'dungeon rows with no surviving dungeon' {
    param($doc)
    $removed = 0; $emptied = 0
    foreach ($ctl in @($doc.SelectNodes('//ContentsTypeList'))) {
        if ($ctl.GetAttribute('type') -ne 'dungeon') { continue }
        foreach ($ci in @($ctl.SelectNodes('ContentInfo'))) {
            $id = Get-IntAttr $ci 'id'
            $ok = ($null -ne $id) -and $keep.Contains($id) -and $script:KeptCompetitionDungeons.Contains($id)
            if (-not $ok) { Remove-NodeWithLeadingWhitespace $ci; $removed++ }
        }
        if ($ctl.SelectNodes('ContentInfo').Count -eq 0) { $emptied++ }
    }
    if ($removed -eq 0) { return $null }
    return "removed $removed <ContentInfo> ($emptied dungeon list(s) now empty)"
}

# ---- EventMatching.xml ----------------------------------------------------------------------
# EventMatchingDataSheet::Validate resolves each <Target id> by the Event's type: Dungeon and
# SoloDungeon against DungeonDataSheet (so the id is a dungeon CONTINENT id), BattleField against
# BattleFieldTemplate, and support matching against DungeonMatching. teleportContinentId is not
# itself validated by the Arbiter, but a teleport into a continent that no longer exists is dead
# weight, so an Event that names one is dropped too.
# EnableTime rows quote eventId, so they follow their Event out.
Invoke-SheetTrim 'EventMatching.xml' 'events targeting dropped continents' {
    param($doc)
    $droppedIds = [System.Collections.Generic.HashSet[string]]::new()
    $removedEvents = 0
    foreach ($ev in @($doc.SelectNodes('//Event'))) {
        $type = $ev.GetAttribute('type')
        $drop = $false
        foreach ($t in $ev.SelectNodes('.//Target')) {
            $tid  = Get-IntAttr $t 'id'
            $tcid = Get-IntAttr $t 'teleportContinentId'
            if ($type -eq 'Dungeon' -or $type -eq 'SoloDungeon') {
                if ($null -eq $tid -or -not $keep.Contains($tid)) { $drop = $true }
            }
            if ($null -ne $tcid -and -not $keep.Contains($tcid)) { $drop = $true }
        }
        if (-not $drop) { continue }
        $evId = $ev.GetAttribute('id')
        if (-not [string]::IsNullOrWhiteSpace($evId)) { [void] $droppedIds.Add($evId.Trim()) }
        Remove-NodeWithLeadingWhitespace $ev
        $removedEvents++
    }
    $removedTimes = 0
    foreach ($t in @($doc.SelectNodes('//EnableTime'))) {
        $eid = $t.GetAttribute('eventId')
        if (-not [string]::IsNullOrWhiteSpace($eid) -and $droppedIds.Contains($eid.Trim())) {
            Remove-NodeWithLeadingWhitespace $t; $removedTimes++
        }
    }
    $removedDays = 0
    foreach ($d in @($doc.SelectNodes('//EnableDay'))) {
        if ($d.SelectNodes('EnableTime').Count -eq 0 -and $d.ChildNodes.Count -le 1) {
            Remove-NodeWithLeadingWhitespace $d; $removedDays++
        }
    }
    if ($removedEvents -eq 0 -and $removedTimes -eq 0) { return $null }
    return "removed $removedEvents <Event>, $removedTimes <EnableTime>, $removedDays empty <EnableDay>"
}

# =================================================================================================
# 5. File moves
# =================================================================================================
Write-Step "Moving per-continent files aside"

$maxTier = $script:TierRank[$Aggressiveness]
$moveTotal = 0
$report = @()

foreach ($fam in ($families | Sort-Object Sheet)) {
    $rank = $script:TierRank[$fam.Tier]
    $files = @(Get-ChildItem -LiteralPath $workRoot -Recurse -File -Filter $fam.Pattern -ErrorAction SilentlyContinue)
    if ($files.Count -eq 0) { continue }

    if ($rank -gt $maxTier) {
        $report += [pscustomobject]@{ Sheet = $fam.Sheet; Pattern = $fam.Pattern; IdKind = $fam.IdKind; Tier = $fam.Tier; OnDisk = $files.Count; Moved = 0; Kept = $files.Count; Note = "tier $($fam.Tier) - left in place" }
        continue
    }

    $moved = 0; $kept = 0; $unparsed = 0
    foreach ($f in $files) {
        $id = Get-IdFromFileName $f.Name $fam.Pattern
        if ($null -eq $id) { $unparsed++; $kept++; continue }

        $isKept = $false
        switch ($fam.IdKind) {
            'Continent'   { $isKept = $keep.Contains($id) }
            'HuntingZone' {
                # A hunting zone we have never heard of belongs to no kept continent, so it goes -
                # but only at Aggressive, which is the only tier that reaches this branch.
                $isKept = $keptHuntingZones.Contains($id)
            }
            'Unused'      { $isKept = $false }
            default       { $isKept = $true }     # NotAnId / Unverified: never move
        }
        if ($isKept) { $kept++; continue }

        $rel = $f.FullName.Substring($workRoot.Length).TrimStart('\', '/')
        $dest = Join-Path $AsideRoot $rel
        if ($PSCmdlet.ShouldProcess($f.FullName, "move aside")) {
            $destDir = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $destDir)) { [void] (New-Item -ItemType Directory -Path $destDir -Force) }
            Move-Item -LiteralPath $f.FullName -Destination $dest -Force
        }
        $manifestData.movedFiles += [ordered]@{ from = $f.FullName; to = $dest; relative = $rel; sheet = $fam.Sheet; id = $id }
        $moved++; $moveTotal++
    }
    $note = ''
    if ($unparsed -gt 0) { $note = "$unparsed file(s) had no parsable id and were left" }
    $report += [pscustomobject]@{ Sheet = $fam.Sheet; Pattern = $fam.Pattern; IdKind = $fam.IdKind; Tier = $fam.Tier; OnDisk = $files.Count; Moved = $moved; Kept = $kept; Note = $note }
}

foreach ($r in ($report | Sort-Object -Property @{Expression = 'Moved'; Descending = $true}, Sheet)) {
    Write-Host ("   {0,-28} {1,-12} {2,-10} on disk {3,5}  moved {4,5}  kept {5,5}  {6}" -f `
        $r.Sheet, $r.IdKind, $r.Tier, $r.OnDisk, $r.Moved, $r.Kept, $r.Note)
}
Write-Note "moved $moveTotal file(s) to $AsideRoot"

# =================================================================================================
# 6. ShieldTerritory stubs
#
#    ShieldTerritory::PostProcess runs for EVERY continent. With fewer than 3 <Fence> points it
#    copies the continent's own area bounding box into the shield box, and those bounds start at
#    min=999999 / max=-1 and are only narrowed by <Area><Zones><Zone>. A continent whose areas carry
#    no <Zone> therefore keeps an inverted box and fails. A continent with real zones passes with no
#    ShieldTerritory file at all - which is why the shipped data has so few of them.
# =================================================================================================
if ($StubShieldTerritory) {
    Write-Step "ShieldTerritory stubs"
    $existing = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $workRoot -Recurse -File -Filter 'ShieldTerritory_*.xml' -ErrorAction SilentlyContinue)) {
        $id = Get-IdFromFileName $f.Name 'ShieldTerritory_*.xml'
        if ($null -ne $id) { $existing[$id] = $f.FullName }
    }
    foreach ($id in ($KeepContinents | Sort-Object)) {
        if ($existing.ContainsKey($id)) { Write-Note "continent $id already has $([System.IO.Path]::GetFileName($existing[$id]))"; continue }
        # Does it have zoned areas? If so it needs no stub.
        $zones = 0
        foreach ($c in $alDoc.SelectNodes('/*/Continent')) {
            $cid = Get-IntAttr $c 'id'
            if ($cid -eq $id) { $zones += $c.SelectNodes('.//Zone').Count }
            foreach ($cc in $c.SelectNodes('ChannelContinent')) {
                if ((Get-IntAttr $cc 'id') -eq $id) {
                    # a channel continent inherits its parent's zones through the named areas
                    $zones += $c.SelectNodes('.//Zone').Count
                }
            }
        }
        if ($zones -gt 0) { Write-Note "continent $id has $zones <Zone> - no stub needed"; continue }
        $stub = Join-Path $workRoot ("ShieldTerritory_{0}_stub.xml" -f $id)
        $xml = @"
<?xml version="1.0" encoding="utf-8"?>
<!-- T63 stub: continent $id has no zoned <Area>, so its area bounding box stays inverted
     (min 999999 / max -1) and ShieldTerritory::PostProcess would fail. Four fences is the
     minimum that works; the post-process needs at least three. -->
<ShieldTerritory continentId="$id">
  <Territory>
    <Fence x="0" y="0" z="0" />
    <Fence x="1000000" y="0" z="0" />
    <Fence x="1000000" y="1000000" z="0" />
    <Fence x="0" y="1000000" z="0" />
  </Territory>
</ShieldTerritory>
"@
        if ($PSCmdlet.ShouldProcess($stub, 'write ShieldTerritory stub')) {
            [System.IO.File]::WriteAllText($stub, $xml, (New-Object System.Text.UTF8Encoding($true)))
        }
        $manifestData.createdFiles += $stub
        Write-Note "wrote $([System.IO.Path]::GetFileName($stub))"
    }
}

# =================================================================================================
# 7. Manifest + a last consistency check
# =================================================================================================
Write-Step "Consistency check"

$alAfter = Read-XmlPreserving $areaListPath
$cdAfter = Read-XmlPreserving $continentDataPath
$alIds = [System.Collections.Generic.HashSet[int]]::new()
foreach ($c in $alAfter.SelectNodes('/*/Continent')) {
    $id = Get-IntAttr $c 'id'; if ($null -ne $id) { [void] $alIds.Add($id) }
    foreach ($cc in $c.SelectNodes('ChannelContinent')) { $i = Get-IntAttr $cc 'id'; if ($null -ne $i) { [void] $alIds.Add($i) } }
}
$problems = @()
foreach ($c in $cdAfter.SelectNodes('/*/Continent')) {
    $id = Get-IntAttr $c 'id'
    if ($null -ne $id -and -not $alIds.Contains($id)) { $problems += "ContinentData keeps $id but AreaList does not (fatal: ContinentData Loading Error)" }
    $chan = $c.GetAttribute('inChannelingContinent')
    if (-not [string]::IsNullOrWhiteSpace($chan)) {
        foreach ($piece in $chan.Split(',')) {
            $t = $piece.Trim(); if ($t -eq '') { continue }
            $n = 0
            if ([int]::TryParse($t, [ref] $n) -and -not $alIds.Contains($n)) {
                $problems += "continent $id channels into $n, which no longer exists (fatal: Invalid Channeling Continent Info)"
            }
        }
    }
    $ct = $c.GetAttribute('channelType')
    if ($ct -eq 'channelingZone' -or $ct -eq 'none' -or $ct -eq 'field') {
        $icc = Get-IntAttr $c 'initChannelCount'
        if ($null -eq $icc -or $icc -eq 0) { $problems += "continent $id has channelType=$ct with initChannelCount=$(if ($null -eq $icc) { 'unset' } else { $icc }) - the loader rejects zero for these types" }
    }
}
foreach ($sf in @(Get-ChildItem -LiteralPath $workRoot -Recurse -File -Filter 'ShieldTerritory_*.xml' -ErrorAction SilentlyContinue)) {
    $doc = New-Object System.Xml.XmlDocument; $doc.Load($sf.FullName)
    $cid = Get-IntAttr $doc.DocumentElement 'continentId'
    if ($null -ne $cid -and -not $alIds.Contains($cid)) { $problems += "$($sf.Name) names continent $cid, which no longer exists (fatal in PreLoad)" }
    if ($doc.SelectNodes('//Territory').Count -eq 0) { $problems += "$($sf.Name) has no <Territory> node (fatal in PreLoad)" }
}

if ($problems.Count -eq 0) { Write-Host "   no problems found" -ForegroundColor Green }
else { foreach ($p in $problems) { Write-Host "   PROBLEM: $p" -ForegroundColor Yellow } }
$manifestData.problems = @($problems)

if ($PSCmdlet.ShouldProcess($Manifest, 'write manifest')) {
    ($manifestData | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $Manifest -Encoding UTF8
}
Write-Step "Done"
Write-Note "manifest    : $Manifest"
Write-Note "rows edited : $($manifestData.editedFiles.Count) sheet(s)"
Write-Note "files moved : $($manifestData.movedFiles.Count)"
Write-Note "stubs       : $($manifestData.createdFiles.Count)"
if (-not $Destination) { Write-Note "restore with: .\restore-datasheets.ps1 -Manifest `"$Manifest`"" }
