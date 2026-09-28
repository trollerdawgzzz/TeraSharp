# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

#Requires -Version 5.1
<#
.SYNOPSIS
    One command from an unzipped TERA 100.02 server to a running TeraSharp.

.DESCRIPTION
    Asks four questions, writes ONE settings file, makes the server's own XML agree with it, and
    leaves start.ps1 / stop.ps1 behind. Run it again after changing anything - it is idempotent:
    existing answers become the defaults, existing secrets are never regenerated, and an XML
    attribute that is already right is not rewritten.

    What it does, in order:

      1. finds the TERA_SERVER.100 tree and checks the four things that must already exist
      2. writes teras.json (ports, paths, GM accounts, a fresh admin token and Alt+A secret)
      3. patches Executable\DeploymentConfig.xml  - planet id, client/World ports, ExternalIp,
         and <Topography folderName> - and Executable\ServerConfig.xml, BOM-safely, with one
         backup per file per run
      4. points tera-server-proxy\config.json at this server
      5. runs TeraSharp.Arbiter --check-config and reports what it resolved, datasheets included
      6. creates the first GM account through tera-api, when tera-api answers
      7. writes start.ps1 and stop.ps1

    It never touches a SqlServer element. Those hold credentials; this script does not read,
    print, copy or rewrite them.

.PARAMETER Check
    Change nothing. Print every action it would take and every mismatch it found, and exit
    non-zero if the tree is not ready. Safe to run on a live server.

.PARAMETER Root
    The TERA_SERVER.100 folder. Skips the first question.

.PARAMETER PublicHost
    The public IP or hostname players connect to. Skips the second question.

.PARAMETER PlanetId
    Planet id. Default 2800, and the databases are named after it - see the warning it prints
    if you change it on an existing tree.

.PARAMETER ServerName
    The name the client's server list shows. Default "TeraSharp".

.PARAMETER GmAccount
    tera-api accountDBID to make the first GM. A number, not a display name.

.PARAMETER NonInteractive
    Never prompt: take -Root/-PublicHost/etc and the existing teras.json, and fail if a required
    answer is still missing.

.EXAMPLE
    .\tools\setup.ps1

.EXAMPLE
    .\tools\setup.ps1 -Check

.EXAMPLE
    .\tools\setup.ps1 -Root C:\TERA_SERVER.100 -PublicHost play.example.com -GmAccount 1 -NonInteractive
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [switch] $Check,
    [string] $Root,
    [string] $PublicHost,
    [int]    $PlanetId = 0,
    [string] $ServerName,
    [string] $GmAccount,
    [switch] $NonInteractive
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

# -WhatIf and -Check are the same request; two words for it would only mean two behaviours.
if ($WhatIfPreference) { $Check = $true }
$WhatIfPreference = $false

# ------------------------------------------------------------------ output helpers

$script:Problems = 0
$script:Planned  = 0

function Write-Step  { param([string] $Text) Write-Host ""; Write-Host ("== " + $Text) -ForegroundColor Cyan }
function Write-Ok    { param([string] $Text) Write-Host ("   ok    " + $Text) }
function Write-Plan  { param([string] $Text) $script:Planned++;  Write-Host ("   would " + $Text) -ForegroundColor Yellow }
function Write-Did   { param([string] $Text) Write-Host ("   set   " + $Text) -ForegroundColor Green }
function Write-Note  { param([string] $Text) Write-Host ("   note  " + $Text) -ForegroundColor DarkGray }
function Write-Warn2 { param([string] $Text) Write-Host ("   warn  " + $Text) -ForegroundColor Yellow }
function Write-Bad   { param([string] $Text) $script:Problems++; Write-Host ("   FAIL  " + $Text) -ForegroundColor Red }

# ------------------------------------------------------------------ small utilities

<# 48 URL-safe characters from the OS CSPRNG. Get-Random is not one and must not be used here. #>
function New-Secret {
    param([int] $Length = 48)
    $bytes = New-Object byte[] ($Length)
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
    $sb = New-Object System.Text.StringBuilder
    foreach ($b in $bytes) { [void] $sb.Append($alphabet[$b % $alphabet.Length]) }
    return $sb.ToString()
}

<# A prompt that respects -Check and -NonInteractive: both take the default and move on. #>
function Read-Answer {
    param([string] $Prompt, [string] $Default, [switch] $Required)
    if ($Check -or $NonInteractive) {
        if ($Required -and [string]::IsNullOrWhiteSpace($Default)) {
            Write-Bad ($Prompt + ": no value, and not prompting")
        }
        return $Default
    }
    $suffix = ''
    if (-not [string]::IsNullOrWhiteSpace($Default)) { $suffix = " [$Default]" }
    while ($true) {
        $answer = Read-Host ($Prompt + $suffix)
        if ([string]::IsNullOrWhiteSpace($answer)) { $answer = $Default }
        if (-not [string]::IsNullOrWhiteSpace($answer) -or -not $Required) { return $answer }
        Write-Host "   (required)" -ForegroundColor Yellow
    }
}

<#
Read a text file keeping what a byte-exact rewrite needs: the BOM and the newline style. Every
XML in the TERA tree is UTF-8 with a BOM and CRLF, and a rewrite that drops either is a file the
server may still load but a diff nobody can read.
#>
function Get-TextFile {
    param([Parameter(Mandatory = $true)][string] $Path)
    $full = (Resolve-Path -LiteralPath $Path).ProviderPath
    $bytes = [IO.File]::ReadAllBytes($full)
    $bom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $offset = 0
    if ($bom) { $offset = 3 }
    $text = [Text.Encoding]::UTF8.GetString($bytes, $offset, $bytes.Length - $offset)
    return [pscustomobject] @{ Path = $full; Text = $text; Bom = $bom; Crlf = $text.Contains("`r`n") }
}

<# Write it back with the same BOM, taking one .bak per file per run the first time. #>
function Set-TextFile {
    param([Parameter(Mandatory = $true)] $File, [Parameter(Mandatory = $true)][string] $Text)
    $backup = $File.Path + '.bak'
    if (-not (Test-Path -LiteralPath $backup)) {
        Copy-Item -LiteralPath $File.Path -Destination $backup
        Write-Note ("backup " + (Split-Path -Leaf $backup))
    }
    $encoding = New-Object Text.UTF8Encoding($File.Bom)
    [IO.File]::WriteAllText($File.Path, $Text, $encoding)
}

<#
One attribute, inside one element, inside one named section. Text-level on purpose: loading these
files into [xml] and saving reformats every line in the file, and DeploymentConfig.xml is the last
place anyone wants a 200-line diff. Returns $null when the element is not there.
#>
function Get-SectionAttr {
    param([string] $Text, [string] $Section, [string] $Element, [string] $Name)
    $block = [regex]::Match($Text, ('<' + $Section + '\b[\s\S]*?</' + $Section + '>'))
    if (-not $block.Success) { $block = [regex]::Match($Text, ('<' + $Section + '\b[^>]*/>')) }
    if (-not $block.Success) { return $null }
    $tag = [regex]::Match($block.Value, ('<' + $Element + '\b[^>]*?/?>'))
    if (-not $tag.Success) { return $null }
    $attr = [regex]::Match($tag.Value, ($Name + '\s*=\s*"([^"]*)"'))
    if (-not $attr.Success) { return $null }
    return $attr.Groups[1].Value
}

function Set-SectionAttr {
    param(
        [Parameter(Mandatory = $true)] $File,
        [string] $Section, [string] $Element, [string] $Name, [string] $Value
    )
    $current = Get-SectionAttr -Text $File.Text -Section $Section -Element $Element -Name $Name
    $label = "$Section/$Element@$Name"
    if ($null -eq $current) { Write-Bad ($label + " is not in " + (Split-Path -Leaf $File.Path)); return $false }
    if ($current -eq $Value) { Write-Ok ($label + " = " + $Value); return $false }
    if ($Check) { Write-Plan ($label + ": " + $current + " -> " + $Value); return $false }

    $blockRx = '<' + $Section + '\b[\s\S]*?</' + $Section + '>'
    $block = [regex]::Match($File.Text, $blockRx)
    if (-not $block.Success) { $blockRx = '<' + $Section + '\b[^>]*/>'; $block = [regex]::Match($File.Text, $blockRx) }
    $tag = [regex]::Match($block.Value, ('<' + $Element + '\b[^>]*?/?>'))
    $newTag = [regex]::Replace($tag.Value, ($Name + '\s*=\s*"[^"]*"'), ($Name + '="' + $Value + '"'), 1)
    $newBlock = $block.Value.Remove($tag.Index, $tag.Length).Insert($tag.Index, $newTag)
    $File.Text = $File.Text.Remove($block.Index, $block.Length).Insert($block.Index, $newBlock)
    Write-Did ($label + ": " + $current + " -> " + $Value)
    return $true
}

<# Is anything listening there? Used for readiness, and to tell whether tera-api is up. #>
function Test-Listening {
    param([string] $Address = '127.0.0.1', [int] $Port, [int] $TimeoutMs = 400)
    $client = New-Object Net.Sockets.TcpClient
    try {
        $async = $client.BeginConnect($Address, $Port, $null, $null)
        if (-not $async.AsyncWaitHandle.WaitOne($TimeoutMs)) { return $false }
        $client.EndConnect($async)
        return $true
    } catch { return $false } finally { $client.Close() }
}

# ------------------------------------------------------------------ teras.json

<#
A hand-rolled emitter. ConvertTo-Json on 5.1 escapes every backslash into \u005c-adjacent noise,
reorders nothing predictably and cannot be told to indent two spaces, and this file is meant to be
opened and edited by hand.
#>
function ConvertTo-TerasJson {
    param([Parameter(Mandatory = $true)] $Settings)
    $sb = New-Object Text.StringBuilder
    [void] $sb.AppendLine('{')
    $sections = @($Settings.Keys)
    for ($s = 0; $s -lt $sections.Count; $s++) {
        $name = $sections[$s]
        $inner = $Settings[$name]
        $keys = @($inner.Keys)
        if ($keys.Count -eq 0) {
            $emptyClose = '  },'
            if ($s -eq $sections.Count - 1) { $emptyClose = '  }' }
            [void] $sb.AppendLine(('  "' + $name + '": {' + $emptyClose.TrimStart()))
            continue
        }
        [void] $sb.AppendLine(('  "' + $name + '": {'))
        for ($k = 0; $k -lt $keys.Count; $k++) {
            $key = $keys[$k]
            $value = $inner[$key]
            if ($value -is [bool])      { $text = $value.ToString().ToLowerInvariant() }
            elseif ($value -is [int] -or $value -is [long]) { $text = $value.ToString() }
            elseif ($null -eq $value)   { $text = '""' }
            else {
                $escaped = ([string] $value).Replace('\', '\\').Replace('"', '\"')
                $text = '"' + $escaped + '"'
            }
            $comma = ','
            if ($k -eq $keys.Count - 1) { $comma = '' }
            [void] $sb.AppendLine(('    "' + $key + '": ' + $text + $comma))
        }
        $close = '  },'
        if ($s -eq $sections.Count - 1) { $close = '  }' }
        [void] $sb.AppendLine($close)
    }
    [void] $sb.Append('}')
    return $sb.ToString()
}

<# Existing answers, so a second run defaults to the first one's. #>
function Read-TerasJson {
    param([string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return (Get-Content -LiteralPath $Path -Raw) | ConvertFrom-Json }
    catch { Write-Warn2 ("teras.json is not valid JSON (" + $_.Exception.Message + "); treating it as absent"); return $null }
}

function Get-Existing {
    param($Json, [string] $Section, [string] $Key, $Fallback = '')
    if ($null -eq $Json) { return $Fallback }
    if (-not ($Json.PSObject.Properties.Name -contains $Section)) { return $Fallback }
    $inner = $Json.$Section
    if ($null -eq $inner -or -not ($inner.PSObject.Properties.Name -contains $Key)) { return $Fallback }
    $value = $inner.$Key
    if ($null -eq $value -or ([string] $value) -eq '') { return $Fallback }
    return $value
}

# ------------------------------------------------------------------ 1. the tree

$RepoRoot = Split-Path -Parent $PSScriptRoot
Write-Host "TeraSharp setup" -ForegroundColor White
Write-Host ("repository: " + $RepoRoot)
if ($Check) { Write-Host "-Check: nothing will be written" -ForegroundColor Yellow }

$TerasPath = Join-Path $RepoRoot 'teras.json'
$existing = Read-TerasJson -Path $TerasPath
if ($null -ne $existing) { Write-Note ("reusing the answers in " + $TerasPath) }

Write-Step "1. the server tree"
if ([string]::IsNullOrWhiteSpace($Root)) {
    $guess = Get-Existing $existing 'deployment' 'root' ''
    if ([string]::IsNullOrWhiteSpace($guess)) {
        # The usual places: next to this clone, then the obvious drive root. No developer path.
        foreach ($candidate in @((Split-Path -Parent $RepoRoot), 'C:\TERA_SERVER.100', 'D:\TERA_SERVER.100')) {
            if ($candidate -and (Test-Path -LiteralPath (Join-Path $candidate 'Executable\WorldServer.exe'))) { $guess = $candidate; break }
        }
    }
    $Root = Read-Answer -Prompt 'TERA_SERVER.100 folder' -Default $guess -Required
}
if ([string]::IsNullOrWhiteSpace($Root) -or -not (Test-Path -LiteralPath $Root)) {
    Write-Bad ("no such folder: " + $Root)
    Write-Host ""; Write-Host "Nothing else can be checked without it." -ForegroundColor Red
    exit 1
}
$Root = (Resolve-Path -LiteralPath $Root).ProviderPath

$required = @(
    @{ Path = 'Executable\WorldServer.exe';                 What = 'WorldServer.exe' },
    @{ Path = 'Executable\DeploymentConfig.xml';            What = 'DeploymentConfig.xml' },
    @{ Path = 'Executable\ServerConfig.xml';                What = 'ServerConfig.xml' },
    @{ Path = 'Executable\Datasheet';                       What = 'Executable\Datasheet' },
    @{ Path = 'tera_v100_MASTER_FINAL';                     What = 'the .def folder' },
    @{ Path = 'tera-server-proxy\data\data.json';           What = 'the opcode map' }
)
foreach ($item in $required) {
    $path = Join-Path $Root $item.Path
    if (Test-Path -LiteralPath $path) { Write-Ok $item.What } else { Write-Bad ($item.What + " is missing (" + $path + ")") }
}
# The World ids this tree can actually boot. start.ps1 launches one process per id, and an id with
# no <WorldServer> row loads nothing and exits, which looks exactly like a crash.
$script:TreeWorldIds = @()
$serverCfgProbe = Join-Path $Root 'Executable\ServerConfig.xml'
if (Test-Path -LiteralPath $serverCfgProbe) {
    $probeText = Get-Content -LiteralPath $serverCfgProbe -Raw
    $script:TreeWorldIds = @([regex]::Matches($probeText, '<WorldServer\s+id="(\d+)"') | ForEach-Object { $_.Groups[1].Value })
    Write-Ok ('WorldServerList ids: ' + ($script:TreeWorldIds -join ', '))
}

$topology = Join-Path $Root 'Topology'
if (Test-Path -LiteralPath $topology) { Write-Ok 'Topology' } else { Write-Warn2 ("no Topology folder at " + $topology + " - TopographyServer will have nothing to serve") }

# The opcode map has to carry this protocol or nothing logs in.
$dataJson = Join-Path $Root 'tera-server-proxy\data\data.json'
if (Test-Path -LiteralPath $dataJson) {
    if ((Select-String -LiteralPath $dataJson -Pattern '"376012"' -SimpleMatch -Quiet)) { Write-Ok 'opcode map has protocol 376012' }
    else { Write-Bad 'tera-server-proxy\data\data.json has no "376012" key' }
}

# ------------------------------------------------------------------ 2. the answers

Write-Step "2. settings"
if ([string]::IsNullOrWhiteSpace($PublicHost)) {
    $PublicHost = Read-Answer -Prompt 'public IP or hostname players connect to' `
        -Default (Get-Existing $existing 'deployment' 'publicHost' '') -Required
}
if ($PlanetId -le 0) {
    $PlanetId = [int] (Read-Answer -Prompt 'planet id' -Default ([string] (Get-Existing $existing 'deployment' 'planetId' '2800')))
}
if ([string]::IsNullOrWhiteSpace($ServerName)) {
    $ServerName = Read-Answer -Prompt 'server name shown in the client' -Default (Get-Existing $existing 'deployment' 'serverName' 'TeraSharp')
}
if ([string]::IsNullOrWhiteSpace($GmAccount)) {
    $GmAccount = Read-Answer -Prompt 'first GM accountDBID (a number; blank for none)' -Default (Get-Existing $existing 'auth' 'gmAccounts' '')
}
if (-not [string]::IsNullOrWhiteSpace($GmAccount)) {
    foreach ($part in $GmAccount.Split([char[]] @(',', ';', ' '), [StringSplitOptions]::RemoveEmptyEntries)) {
        $parsed = 0
        if (-not [long]::TryParse($part.Trim(), [ref] $parsed)) {
            Write-Warn2 ("GM account '" + $part.Trim() + "' is not a number. The launcher puts the tera-api accountDBID in C_LOGIN_ARBITER.name, so a display name never matches and that account silently gets a normal login.")
        }
    }
}

# Secrets: generated once, then left alone for ever. Rotating the Alt+A key on every setup run
# would break the panel; rotating the admin token would lock the operator out of their own web.
$adminToken = Get-Existing $existing 'admin' 'token' ''
$jwtSecret  = Get-Existing $existing 'gateway' 'jwtSecret' ''
if ([string]::IsNullOrWhiteSpace($adminToken)) {
    if ($Check) { Write-Plan 'generate TERASHARP_ADMIN_TOKEN (48 chars)'; $adminToken = '' }
    else { $adminToken = New-Secret 48; Write-Did 'generated an admin token' }
} else { Write-Ok 'admin token already set (kept)' }
if ([string]::IsNullOrWhiteSpace($jwtSecret)) {
    if ($Check) { Write-Plan 'generate TERASHARP_API_JWT_SECRET (48 chars)'; $jwtSecret = '' }
    else { $jwtSecret = New-Secret 48; Write-Did 'generated an Alt+A signing key' }
    Write-Note 'set tera-api API_PORTAL_SECRET to the same value, or Alt+A breaks on every restart'
} else { Write-Ok 'Alt+A signing key already set (kept)' }

$logs = Get-Existing $existing 'paths' 'logs' (Join-Path $Root 'logs')
$clientPort = [int] (Get-Existing $existing 'deployment' 'clientPort' 7701)
$worldPort  = [int] (Get-Existing $existing 'deployment' 'worldPort'  7802)
$proxyPort  = [int] (Get-Existing $existing 'deployment' 'proxyPort'  7801)
$adminPort  = [int] (Get-Existing $existing 'admin'      'port'       8051)

# Which Worlds start.ps1 boots. An existing answer is kept; otherwise it comes from this tree's own
# WorldServerList - the main world (id 1 where there is one, id 0 on a stock 100.02 tree) plus the
# dungeon World 13.
$worldIdsAnswer = [string] (Get-Existing $existing 'deployment' 'worldIds' '')
if ([string]::IsNullOrWhiteSpace($worldIdsAnswer)) {
    $picked = @()
    foreach ($candidate in @('1', '0')) {
        if ($script:TreeWorldIds -contains $candidate) { $picked += $candidate; break }
    }
    if ($script:TreeWorldIds -contains '13') { $picked += '13' }
    if ($picked.Count -eq 0) { $picked = @('1', '13') }
    $worldIdsAnswer = ($picked -join ',')
    Write-Note ('deployment.worldIds = ' + $worldIdsAnswer + ' (from this tree)')
}

# One ordered map in, one file out. MIN_MEMBERS is deliberately absent: it is a test knob that
# makes the matcher wrong on purpose, and a file that lists it invites someone to set it.
$settings = [ordered] @{
    paths = [ordered] @{
        data              = $Root
        logs              = $logs
        db                = ''
        datasheet         = ''
        serverConfig      = ''
        starterBlob       = (Join-Path $RepoRoot 'data\starter_blob.bin')
        starterInventory  = (Join-Path $RepoRoot 'data\starter_inventory.bin')
        itemStrSheet      = ''
        itemNames         = ''
    }
    listener    = [ordered] @{ bind = '127.0.0.1' }
    planet      = [ordered] @{ dbServerName = ('PlanetDB_' + $PlanetId) }
    # T215: enabled is FORCED true on every run - an open login is the one setting that turns
    # this from a server into a free account generator, and start.ps1 refuses to boot without it.
    # The url keeps an existing answer, so moving tera-api survives a re-run.
    auth        = [ordered] @{ enabled = $true; url = (Get-Existing $existing 'auth' 'url' 'http://127.0.0.1:8080'); gmAccounts = $GmAccount }
    admin       = [ordered] @{ token = $adminToken; port = $adminPort }
    gateway     = [ordered] @{ address = '127.0.0.1:8800'; serve = $false; bind = ''; jwtSecret = $jwtSecret }
    knobs       = [ordered] @{ logLevel = 'Warning'; rankingSeason = 15; startOverride = ''; standalone = '' }
    matchmaking = [ordered] @{ entrySeconds = 300; bgMaxHealers = 2; bgMaxTanks = 3 }
    economy     = [ordered] @{ brokerFeePercent = 10 }
    deployment  = [ordered] @{
        root = $Root; publicHost = $PublicHost; planetId = $PlanetId; serverName = $ServerName
        clientPort = $clientPort; worldPort = $worldPort; proxyPort = $proxyPort
        topologyFolder = '.\Topology'; worldIds = $worldIdsAnswer
    }
    env         = [ordered] @{}
}

$json = ConvertTo-TerasJson -Settings $settings
$jsonSame = $false
if (Test-Path -LiteralPath $TerasPath) {
    $onDisk = (Get-Content -LiteralPath $TerasPath -Raw)
    $jsonSame = ($onDisk.Replace("`r`n", "`n").Trim() -eq $json.Replace("`r`n", "`n").Trim())
}
if ($jsonSame) {
    Write-Ok ($TerasPath + ' up to date')
} elseif ($Check) {
    if (Test-Path -LiteralPath $TerasPath) { Write-Plan ('rewrite ' + $TerasPath) } else { Write-Plan ('write ' + $TerasPath) }
} else {
    if (Test-Path -LiteralPath $TerasPath) {
        $backup = $TerasPath + '.bak'
        Copy-Item -LiteralPath $TerasPath -Destination $backup -Force
        Write-Note 'backup teras.json.bak'
    }
    [IO.File]::WriteAllText($TerasPath, $json, (New-Object Text.UTF8Encoding($false)))
    Write-Did ('wrote ' + $TerasPath)
    Write-Note 'teras.json holds two secrets and is gitignored. Never commit it.'
}
if (-not (Test-Path -LiteralPath $logs)) {
    if ($Check) { Write-Plan ('create ' + $logs) }
    else { New-Item -ItemType Directory -Path $logs -Force | Out-Null; Write-Did ('created ' + $logs) }
}

# ------------------------------------------------------------------ 3. the server's own XML

Write-Step "3. Executable\DeploymentConfig.xml"
$deployPath = Join-Path $Root 'Executable\DeploymentConfig.xml'
if (Test-Path -LiteralPath $deployPath) {
    $deploy = Get-TextFile -Path $deployPath
    $changed = $false

    $currentPlanet = Get-SectionAttr -Text $deploy.Text -Section 'ArbiterServerConfig' -Element 'Planet' -Name 'id'
    if ($null -ne $currentPlanet -and $currentPlanet -ne ([string] $PlanetId)) {
        Write-Warn2 ("the databases in this tree are named after planet " + $currentPlanet + " (PlanetDB_" + $currentPlanet + ", SharedDB_" + $currentPlanet + ", CollectionDB_" + $currentPlanet + "). This script does not rename a database or touch a SqlServer element - change the planet id only on a tree whose databases you have already created for it.")
    }
    foreach ($section in @('ArbiterServerConfig', 'WorldServerConfig', 'MatchServerConfig')) {
        if (Set-SectionAttr -File $deploy -Section $section -Element 'Planet' -Name 'id' -Value ([string] $PlanetId)) { $changed = $true }
    }
    if (Set-SectionAttr -File $deploy -Section 'ArbiterServerConfig' -Element 'Listen' -Name 'portForClient' -Value ([string] $clientPort)) { $changed = $true }
    if (Set-SectionAttr -File $deploy -Section 'ArbiterServerConfig' -Element 'Listen' -Name 'portForWorld'  -Value ([string] $worldPort))  { $changed = $true }
    # ExternalIp is what the Arbiter hands the client as the address to come back to. A hostname
    # belongs in the proxy, not here - this field is read as an address.
    $ipOnly = $PublicHost
    $parsedIp = [Net.IPAddress]::None
    if (-not [Net.IPAddress]::TryParse($PublicHost, [ref] $parsedIp)) {
        try { $ipOnly = ([Net.Dns]::GetHostAddresses($PublicHost) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1).IPAddressToString }
        catch { $ipOnly = '' }
        if ([string]::IsNullOrWhiteSpace($ipOnly)) { Write-Warn2 ($PublicHost + " does not resolve to an IPv4 address; leaving ExternalIp alone") }
        else { Write-Note ($PublicHost + " resolves to " + $ipOnly) }
    }
    if (-not [string]::IsNullOrWhiteSpace($ipOnly)) {
        if (Set-SectionAttr -File $deploy -Section 'ArbiterServerConfig' -Element 'ExternalIp' -Name 'value' -Value $ipOnly) { $changed = $true }
        if (Set-SectionAttr -File $deploy -Section 'ArbiterServerConfig' -Element 'ExternalIp' -Name 'getFromNetworkDevice' -Value 'false') { $changed = $true }
    }
    if (Set-SectionAttr -File $deploy -Section 'WorldServerConfig' -Element 'Topography' -Name 'folderName' -Value '.\Topology') { $changed = $true }

    if ($changed) { Set-TextFile -File $deploy -Text $deploy.Text }
    elseif (-not $Check) { Write-Ok 'nothing to change' }
} else { Write-Bad ('missing ' + $deployPath) }

Write-Step "4. Executable\ServerConfig.xml"
$serverCfgPath = Join-Path $Root 'Executable\ServerConfig.xml'
if (Test-Path -LiteralPath $serverCfgPath) {
    $serverCfg = Get-TextFile -Path $serverCfgPath
    $changed = $false
    # The datasheet root is the one path in this file that a moved tree breaks, and it is the
    # folder the Arbiter reads too - status/DATASHEETS.md. Everything else here is game policy,
    # which belongs to the operator, not to a setup script.
    if (Set-SectionAttr -File $serverCfg -Section 'ArbiterServerConfig' -Element 'Datasheet' -Name 'rootFolder' -Value '.\Datasheet\') { $changed = $true }
    if ($changed) { Set-TextFile -File $serverCfg -Text $serverCfg.Text }

    # start.ps1 launches a World per id. An id with no <WorldServer> row loads nothing and the
    # process exits, which looks exactly like a crash.
    $worldIds = $settings.deployment.worldIds.Split([char[]] @(',', ' '), [StringSplitOptions]::RemoveEmptyEntries)
    foreach ($id in $worldIds) {
        if ($serverCfg.Text -match ('<WorldServer\s+id="' + $id.Trim() + '"')) { Write-Ok ('WorldServerList has id ' + $id.Trim()) }
        else { Write-Warn2 ('WorldServerList has no <WorldServer id="' + $id.Trim() + '"> - either add one or edit deployment.worldIds in teras.json. Ids present: ' + (([regex]::Matches($serverCfg.Text, '<WorldServer\s+id="(\d+)"') | ForEach-Object { $_.Groups[1].Value }) -join ', ')) }
    }
} else { Write-Bad ('missing ' + $serverCfgPath) }

# ------------------------------------------------------------------ 5. the proxy

Write-Step "5. tera-server-proxy"
$proxyRoot = Join-Path $Root 'tera-server-proxy'
if (Test-Path -LiteralPath $proxyRoot) {
    $proxyCfgPath = Join-Path $proxyRoot 'config.json'
    $proxyJson = @"
{
    "servers": [
        {
            "listenIp": "0.0.0.0",
            "listenPort": $proxyPort,
            "serverIp": "127.0.0.1",
            "serverPort": $clientPort,
            "name": "$ServerName",
            "serverId": $PlanetId,
            "publisher": "GF",
            "language": "eu",
            "patchVersion": "100.02",
            "protocolVersion": 376012,
            "integrity": false
        }
    ]
}
"@
    $same = $false
    if (Test-Path -LiteralPath $proxyCfgPath) {
        $current = (Get-Content -LiteralPath $proxyCfgPath -Raw)
        $same = ($current.Replace("`r`n", "`n").Trim() -eq $proxyJson.Replace("`r`n", "`n").Trim())
    }
    if ($same) { Write-Ok 'config.json already points at this server' }
    elseif ($Check) { Write-Plan ('rewrite ' + $proxyCfgPath + (' (listen {0}, forward to 127.0.0.1:{1}, serverId {2})' -f $proxyPort, $clientPort, $PlanetId)) }
    else {
        if (Test-Path -LiteralPath $proxyCfgPath) { Copy-Item -LiteralPath $proxyCfgPath -Destination ($proxyCfgPath + '.bak') -Force }
        [IO.File]::WriteAllText($proxyCfgPath, $proxyJson, (New-Object Text.UTF8Encoding($false)))
        Write-Did ('wrote ' + $proxyCfgPath)
    }

    # The mod set. Two of these live in this repository and are installed; the rest are separate
    # projects and are only reported, because vendoring somebody else's mod into this tree is how
    # a fork ends up shipping code it has no licence for.
    $modsDir = Join-Path $proxyRoot 'mods'
    if (-not (Test-Path -LiteralPath $modsDir)) {
        if ($Check) { Write-Plan ('create ' + $modsDir) } else { New-Item -ItemType Directory -Path $modsDir -Force | Out-Null }
    }
    $shipped = @(
        @{ Name = 'packet-logger';   From = (Join-Path $RepoRoot 'tools\packet-logger.js') },
        @{ Name = 'arbiter-tap';     From = (Join-Path $RepoRoot 'tools\arbiter-world-tap.js') }
    )
    foreach ($mod in $shipped) {
        if (-not (Test-Path -LiteralPath $mod.From)) { continue }
        $target = Join-Path $modsDir ($mod.Name + '\index.js')
        if ((Test-Path -LiteralPath $target) -and ((Get-FileHash -LiteralPath $target).Hash -eq (Get-FileHash -LiteralPath $mod.From).Hash)) {
            Write-Ok ('mod ' + $mod.Name + ' installed')
        } elseif ($Check) { Write-Plan ('install mod ' + $mod.Name) }
        else {
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $mod.From -Destination $target -Force
            Write-Did ('installed mod ' + $mod.Name)
        }
    }
    foreach ($name in @('command', 'exploit-fix', 'spoof-guard', 'fake-ping')) {
        if (Test-Path -LiteralPath (Join-Path $modsDir $name)) { Write-Ok ('mod ' + $name + ' present') }
        elseif ($name -eq 'command') { Write-Warn2 ('mod command is not installed - without it the client has no /@ chat commands. It is a separate project; drop it into ' + (Join-Path $modsDir $name)) }
        else { Write-Note ('optional mod ' + $name + ' is not installed (' + (Join-Path $modsDir $name) + ')') }
    }
} else {
    Write-Bad ('no tera-server-proxy at ' + $proxyRoot + ' - the client cannot reach the Arbiter without it')
}

# ------------------------------------------------------------------ 6. what the Arbiter resolved

Write-Step "6. --check-config"
$project = Join-Path $RepoRoot 'src\TeraSharp.Arbiter\TeraSharp.Arbiter.csproj'
$exe = Join-Path $RepoRoot 'TeraSharp.Arbiter.exe'
$report = $null
try {
    if (Test-Path -LiteralPath $exe) { $report = & $exe --check-config 2>&1 }
    elseif (Test-Path -LiteralPath $project) {
        Push-Location $RepoRoot
        try { $report = & dotnet run --project $project -- --check-config 2>&1 } finally { Pop-Location }
    } else { Write-Warn2 'neither TeraSharp.Arbiter.exe nor the project is here; skipping' }
} catch { Write-Warn2 ('could not run --check-config: ' + $_.Exception.Message) }
if ($null -ne $report) {
    foreach ($line in $report) {
        $text = [string] $line
        if ($text -match '^\s*!')            { Write-Warn2 $text.Trim() }
        elseif ($text -match 'built-in')     { Write-Warn2 $text.Trim() }
        elseif ($text -match 'error|Unhandled|Exception') { Write-Bad $text.Trim() }
    }
    $sheets = @($report | Where-Object { [string] $_ -match 'built-in' }).Count
    if ($sheets -eq 0) { Write-Ok 'every datasheet-backed value came from the server tree' }
    else { Write-Warn2 ("" + $sheets + " value(s) fell back to a built-in copy - the sheet is missing or unreadable") }
    $settingsLine = @($report | Where-Object { [string] $_ -match 'teras\.json' }) | Select-Object -First 1
    if ($settingsLine) { Write-Ok ([string] $settingsLine).Trim() }
}

# ------------------------------------------------------------------ 7. the first GM account

Write-Step "7. tera-api"
$apiUp = Test-Listening -Address '127.0.0.1' -Port 8080
if (-not $apiUp) {
    Write-Note 'tera-api is not answering on 127.0.0.1:8080. Start it and re-run, or create the account in its own panel; auth.enabled=true means the Arbiter validates every ticket against it.'
} elseif ([string]::IsNullOrWhiteSpace($GmAccount)) {
    Write-Note 'tera-api is up, but no GM accountDBID was given - nothing to grant.'
} else {
    Write-Ok ('tera-api is up; GM accountDBID(s) ' + $GmAccount + ' are in teras.json')
    Write-Note 'TeraSharp reads GM rights from auth.gmAccounts, not from tera-api, so there is nothing to create there. Make sure the account with that DBID exists in tera-api and that you can log in with it.'
}

# ------------------------------------------------------------------ 8. start.ps1 / stop.ps1

Write-Step "8. start.ps1 and stop.ps1"

$startBody = @'
#Requires -Version 5.1
<#
    Written by tools\setup.ps1. Boots the stack in the only order that works, waiting for each
    process to be reachable before starting the next one:

        TopographyServer (--sharedmemoryproducer=true)  ->  TeraSharp  ->  World 1  ->  World 13  ->  proxy

    World reads the shared memory Topography produces, so Topography must be first and must still
    be running. TeraSharp comes before World because World connects TO the Arbiter and retries;
    the other order works too but logs a minute of connection failures. The proxy is last because
    it is the only thing players can reach, and it must not accept a connection before the rest
    is up.

    -Only <name> starts one process. -WaitSeconds changes the per-step timeout.

    T215: it refuses to boot at all when teras.json says the login is open or the client port is
    off loopback. -Insecure starts anyway and is for a laptop, never for a public host.
#>
[CmdletBinding()]
param([string] $Only, [int] $WaitSeconds = 240, [switch] $Insecure)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$here = $PSScriptRoot
$cfg  = (Get-Content -LiteralPath (Join-Path $here 'teras.json') -Raw) | ConvertFrom-Json
$root = $cfg.deployment.root
$exe  = Join-Path $root 'Executable'

# ---------------------------------------------------------------- T215 preflight
# Two settings decide whether this stack is safe to have listening, and neither is worth a
# warning nobody reads. auth.enabled=false accepts every login; listener.bind off loopback puts
# the client port - which does NOT check GM privilege on C_ADMIN - on the network with only the
# proxy in front of it. Both refuse the boot; -Insecure overrides, and says so in the log.
function Get-Setting {
    param($Node, [string] $Name, $Fallback = $null)
    if ($null -eq $Node) { return $Fallback }
    if (-not ($Node.PSObject.Properties.Name -contains $Name)) { return $Fallback }
    $value = $Node.$Name
    if ($null -eq $value) { return $Fallback }
    return $value
}

$authNode = Get-Setting $cfg 'auth'
$authOn   = [bool] (Get-Setting $authNode 'enabled' $false)
$authUrl  = [string] (Get-Setting $authNode 'url' '')
$bind     = [string] (Get-Setting (Get-Setting $cfg 'listener') 'bind' '127.0.0.1')

$blockers = @()
if (-not $authOn) {
    $blockers += 'auth.enabled is false - every login would be accepted, whatever the name. Set it to true (TERASHARP_AUTH) and re-run tools\setup.ps1.'
} elseif ([string]::IsNullOrWhiteSpace($authUrl)) {
    $blockers += 'auth.enabled is true but auth.url is empty - there is nowhere to validate a ticket. Point it at tera-api (TERASHARP_AUTH_URL), e.g. http://127.0.0.1:8080.'
}
if ($bind.Trim() -ne '' -and $bind.Trim() -ne '127.0.0.1' -and $bind.Trim() -ne '::1' -and $bind.Trim() -ne 'localhost') {
    $blockers += ('listener.bind is ' + $bind + ', not loopback - the client port does not check GM privilege on C_ADMIN. Set listener.bind to 127.0.0.1 (TERASHARP_BIND) and let the proxy face the network.')
}

if ($blockers.Count -gt 0) {
    Write-Host '== refusing to start' -ForegroundColor Red
    foreach ($b in $blockers) { Write-Host ('   ' + $b) -ForegroundColor Red }
    if (-not $Insecure) {
        Write-Host '   nothing was started. -Insecure boots anyway; never use it on a public host.' -ForegroundColor Yellow
        exit 2
    }
    Write-Host '   -Insecure given: booting an UNSAFE configuration on purpose.' -ForegroundColor Yellow
}

function Wait-Port {
    param([int] $Port, [string] $What, [int] $Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $client = New-Object Net.Sockets.TcpClient
        try {
            $async = $client.BeginConnect('127.0.0.1', $Port, $null, $null)
            if ($async.AsyncWaitHandle.WaitOne(500)) { $client.EndConnect($async); Write-Host ("   up    " + $What + " (:" + $Port + ")") -ForegroundColor Green; return $true }
        } catch { } finally { $client.Close() }
        Start-Sleep -Seconds 2
    }
    Write-Host ("   TIMEOUT " + $What + " did not listen on " + $Port + " within " + $Seconds + "s") -ForegroundColor Red
    return $false
}

function Wait-Process2 {
    param([string] $Name, [string] $What, [int] $Seconds)
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (Get-Process -Name $Name -ErrorAction SilentlyContinue) { Write-Host ("   up    " + $What) -ForegroundColor Green; return $true }
        Start-Sleep -Seconds 2
    }
    Write-Host ("   TIMEOUT " + $What + " is not running") -ForegroundColor Red
    return $false
}

function Start-Step {
    param([string] $Name, [string] $Path, [string] $Arguments, [string] $WorkingDirectory)
    if ($Only -and $Only -ne $Name) { return $false }
    Write-Host ("== " + $Name) -ForegroundColor Cyan
    if (-not (Test-Path -LiteralPath $Path)) { Write-Host ("   missing " + $Path) -ForegroundColor Red; return $false }
    Start-Process -FilePath $Path -ArgumentList $Arguments -WorkingDirectory $WorkingDirectory | Out-Null
    return $true
}

if (Start-Step 'topography' (Join-Path $exe 'TopographyServer.exe') '--sharedmemoryproducer=true' $exe) {
    [void] (Wait-Process2 -Name 'TopographyServer' -What 'TopographyServer' -Seconds 30)
    Start-Sleep -Seconds 5      # it builds the shared memory after the process appears
}

$arbiter = Join-Path $here 'TeraSharp.Arbiter.exe'
if (Test-Path -LiteralPath $arbiter) {
    if (Start-Step 'arbiter' $arbiter '' $here) { [void] (Wait-Port -Port $cfg.deployment.clientPort -What 'TeraSharp' -Seconds 60) }
} else {
    if (-not $Only -or $Only -eq 'arbiter') {
        Write-Host '== arbiter' -ForegroundColor Cyan
        Write-Host '   no TeraSharp.Arbiter.exe here - run dotnet publish, or start it yourself with dotnet run' -ForegroundColor Yellow
    }
}

foreach ($id in ($cfg.deployment.worldIds.Split([char[]] @(',', ' '), [StringSplitOptions]::RemoveEmptyEntries))) {
    $trimmed = $id.Trim()
    if (Start-Step ('world' + $trimmed) (Join-Path $exe 'WorldServer.exe') ('--id=' + $trimmed) $exe) {
        [void] (Wait-Process2 -Name 'WorldServer' -What ('WorldServer ' + $trimmed) -Seconds $WaitSeconds)
        Start-Sleep -Seconds 10
    }
}

$proxyRoot = Join-Path $root 'tera-server-proxy'
if (Test-Path -LiteralPath (Join-Path $proxyRoot 'Start.bat')) {
    if (Start-Step 'proxy' (Join-Path $proxyRoot 'Start.bat') '' $proxyRoot) {
        [void] (Wait-Port -Port $cfg.deployment.proxyPort -What 'proxy' -Seconds 60)
    }
}

Write-Host ""
Write-Host ("Players connect to " + $cfg.deployment.publicHost + ":" + $cfg.deployment.proxyPort) -ForegroundColor White
'@

$stopBody = @'
#Requires -Version 5.1
<#
    Written by tools\setup.ps1. Stops the stack politely: announce, give players time to react,
    kick them so the Arbiter writes their rows, then stop the processes in reverse start order.

    -Seconds 0 skips the announcement. -Force skips the wait as well.
#>
[CmdletBinding()]
param([int] $Seconds = 60, [switch] $Force)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Continue'

$here = $PSScriptRoot
$cfg  = (Get-Content -LiteralPath (Join-Path $here 'teras.json') -Raw) | ConvertFrom-Json
$base = 'http://127.0.0.1:' + $cfg.admin.port
$headers = @{ 'X-Admin-Token' = $cfg.admin.token }

function Invoke-Admin {
    param([string] $Path, [string] $Method = 'POST', $Body = $null)
    try {
        $args2 = @{ Uri = ($base + $Path); Method = $Method; Headers = $headers; TimeoutSec = 10 }
        if ($null -ne $Body) { $args2['Body'] = ($Body | ConvertTo-Json -Compress); $args2['ContentType'] = 'application/json' }
        return Invoke-RestMethod @args2
    } catch { Write-Host ("   admin API: " + $_.Exception.Message) -ForegroundColor DarkGray; return $null }
}

if (-not [string]::IsNullOrWhiteSpace($cfg.admin.token)) {
    if ($Seconds -gt 0 -and -not $Force) {
        Write-Host ("== announcing a shutdown in " + $Seconds + "s") -ForegroundColor Cyan
        [void] (Invoke-Admin -Path '/api/announce' -Body @{ message = ("Server restarting in " + $Seconds + " seconds.") })
        Start-Sleep -Seconds $Seconds
    }
    Write-Host '== kicking players so their rows are written' -ForegroundColor Cyan
    $online = Invoke-Admin -Path '/api/online' -Method 'GET'
    if ($null -ne $online -and $online.PSObject.Properties.Name -contains 'players') {
        foreach ($p in $online.players) { [void] (Invoke-Admin -Path '/api/kick' -Body @{ id = $p.id; reason = 'shutdown' }) }
        Write-Host ("   kicked " + @($online.players).Count) -ForegroundColor Green
    }
} else {
    Write-Host '== no admin token in teras.json, so no announce or kick' -ForegroundColor Yellow
}

Write-Host '== stopping, reverse order' -ForegroundColor Cyan
foreach ($name in @('node', 'WorldServer', 'TeraSharp.Arbiter', 'TopographyServer')) {
    $procs = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $procs) { continue }
    if ($name -eq 'node') { Write-Host '   node: the proxy runs under node - close its window if this stops the wrong one' -ForegroundColor DarkGray }
    foreach ($p in $procs) {
        $p.CloseMainWindow() | Out-Null
        if (-not $p.WaitForExit(15000)) { Stop-Process -Id $p.Id -Force }
    }
    Write-Host ("   stopped " + $name) -ForegroundColor Green
}
'@

foreach ($script in @(@{ Name = 'start.ps1'; Body = $startBody }, @{ Name = 'stop.ps1'; Body = $stopBody })) {
    $path = Join-Path $RepoRoot $script.Name
    $same = (Test-Path -LiteralPath $path) -and (((Get-Content -LiteralPath $path -Raw).Replace("`r`n", "`n").Trim()) -eq ($script.Body.Replace("`r`n", "`n").Trim()))
    if ($same) { Write-Ok ($script.Name + ' up to date') }
    elseif ($Check) { Write-Plan ('write ' + $path) }
    else {
        [IO.File]::WriteAllText($path, $script.Body.Replace("`n", "`r`n"), (New-Object Text.UTF8Encoding($false)))
        Write-Did ('wrote ' + $path)
    }
}

# ------------------------------------------------------------------ summary

Write-Host ""
if ($Check) {
    Write-Host ("-Check: " + $script:Planned + " change(s) pending, " + $script:Problems + " problem(s)") -ForegroundColor White
    if ($script:Problems -gt 0) { exit 1 }
    exit 0
}
if ($script:Problems -gt 0) {
    Write-Host ($script:Problems.ToString() + " problem(s) - fix them and run setup.ps1 again") -ForegroundColor Red
    exit 1
}
Write-Host "Setup complete." -ForegroundColor Green
Write-Host "  .\start.ps1     boots Topography, TeraSharp, the Worlds and the proxy, in order"
Write-Host "                  it refuses to boot with auth off or the client port off loopback (-Insecure overrides)"
Write-Host "  .\stop.ps1      announces, kicks, stops"
Write-Host ("  players connect to " + $PublicHost + ":" + $proxyPort)
Write-Host "  docs\QUICKSTART.md is the five-step version of this; docs\GO-LIVE.md before anyone else logs in."
exit 0
