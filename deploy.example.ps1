# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors
#Requires -Version 5.1
<#
.SYNOPSIS
    Example deploy script for a TeraSharp box. Copy to deploy.ps1 and edit.

.DESCRIPTION
    deploy.ps1 is gitignored; deploy.example.ps1 is not. Keep every real path, host and
    secret in .env and in your copy - never in this file.

    What it does, in order:
      1. reads .env into the process environment
      2. announce -> kick -> stop: tells the players, waits -GraceSeconds, kicks everyone still
         in world (so World's leave path saves them), then stops the Arbiter. Needs
         TERASHARP_ADMIN_TOKEN; without it the Arbiter is stopped with a warning.
      3. backs up the database
      4. installs the new build from -From (a folder, a .zip, or a URL)
      5. runs --check-config and --selftest, and refuses to start on a failure
      6. restarts WorldServer.exe if -WorldExe is given, then starts the Arbiter with output
         tee'd to a dated log

    World does NOT survive the Arbiter link dropping - it does not reconnect. Pass -WorldExe, or
    restart World yourself after the Arbiter is back (docs/SETUP.md section 12).

.PARAMETER From
    Where the new build comes from. A folder holding TeraSharp.Arbiter.exe, a .zip of one, or
    an http(s) URL to such a .zip. Omit to redeploy whatever is already in -InstallTo.

.PARAMETER InstallTo
    Where the build lives on this box.

.PARAMETER EnvFile
    The .env to load. Defaults to .env beside this script.

.PARAMETER NoStart
    Do everything except start the Arbiter (and World). Useful for a dry run on a live box.

.PARAMETER GraceSeconds
    How long players get between the announcement and the kick. Default 300.

.PARAMETER AnnounceText
    The in-game notice. {0} is replaced by the grace period in minutes.

.PARAMETER WorldExe
    Full path to WorldServer.exe. When given, World is stopped and started again right before
    the new Arbiter starts; World then takes its ~3 minutes to connect.

.EXAMPLE
    .\deploy.ps1 -From \\build\share\TeraSharp -WhatIf

.EXAMPLE
    .\deploy.ps1 -From https://your-build-host.example/TeraSharp-bin.zip

.EXAMPLE
    .\deploy.ps1 -From C:\builds\TeraSharp -GraceSeconds 120 -WorldExe C:\TERA\Executable\WorldServer.exe
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $From,
    [string] $InstallTo = 'C:\TeraSharp',
    [string] $EnvFile   = (Join-Path $PSScriptRoot '.env'),
    [string] $LogDir    = 'C:\TeraSharp\logs',
    [switch] $NoStart,
    [int]    $GraceSeconds = 300,
    [string] $AnnounceText = 'Server restart in {0} minute(s). Please log out now.',
    [string] $WorldExe
)

$ErrorActionPreference = 'Stop'
$exeName = 'TeraSharp.Arbiter.exe'

function Write-Step { param([string] $Text) Write-Host ''; Write-Host ("== " + $Text) -ForegroundColor Cyan }

# ---------------------------------------------------------------- 1. environment
Write-Step "environment"
if (-not (Test-Path -LiteralPath $EnvFile)) {
    throw "no $EnvFile. Copy .env.example to .env and fill it in first."
}
$loaded = 0
foreach ($line in Get-Content -LiteralPath $EnvFile) {
    $t = $line.Trim()
    if ($t.Length -eq 0 -or $t.StartsWith('#')) { continue }
    $eq = $t.IndexOf('=')
    if ($eq -lt 1) { continue }
    $name  = $t.Substring(0, $eq).Trim()
    $value = $t.Substring($eq + 1).Trim()
    if ($value.Length -ge 2 -and $value.StartsWith('"') -and $value.EndsWith('"')) {
        $value = $value.Substring(1, $value.Length - 2)
    }
    [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    $loaded++
}
Write-Host ("  {0} setting(s) from {1}" -f $loaded, $EnvFile)

if (-not $env:TERASHARP_AUTH -or $env:TERASHARP_AUTH -notmatch '^(1|true)$') {
    Write-Warning 'TERASHARP_AUTH is not on. Any login will be accepted. Do not leave this on a public box.'
}
if (-not $env:TERASHARP_API_JWT_SECRET) {
    Write-Warning 'TERASHARP_API_JWT_SECRET is unset - the Alt+A token changes on every restart.'
}

# ---------------------------------------------------------------- 2. announce -> kick -> stop
Write-Step "announce -> kick"
$running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    if (-not $env:TERASHARP_ADMIN_TOKEN) {
        Write-Warning 'TERASHARP_ADMIN_TOKEN is unset - cannot announce or kick. Anyone still in world loses what World has not saved.'
    } else {
        $adminPort = if ($env:TERASHARP_ADMIN_PORT) { $env:TERASHARP_ADMIN_PORT } else { '8050' }
        $api = "http://127.0.0.1:$adminPort/api"
        $hdr = @{ 'X-Admin-Token' = $env:TERASHARP_ADMIN_TOKEN }
        function Invoke-Admin {
            param([string] $Path, [hashtable] $Body)
            $json = $Body | ConvertTo-Json -Compress
            Invoke-RestMethod -Uri "$api/$Path" -Method Post -Headers $hdr `
                -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json))
        }
        function Get-InWorld {
            @((Invoke-RestMethod -Uri "$api/online" -Headers $hdr).online | Where-Object { $_.playerId -gt 0 })
        }
        try {
            $inWorld = @(Get-InWorld)
            if ($inWorld.Count -eq 0) {
                Write-Host '  nobody in world'
            } elseif ($PSCmdlet.ShouldProcess(("{0} player(s)" -f $inWorld.Count), 'announce, wait, kick')) {
                $minutes = [int][Math]::Max(1, [Math]::Ceiling($GraceSeconds / 60.0))
                Invoke-Admin 'announce' @{ text = ($AnnounceText -f $minutes); reason = 'deploy' } | Out-Null
                Write-Host ("  announced to {0} player(s); waiting {1} s" -f $inWorld.Count, $GraceSeconds)
                Start-Sleep -Seconds $GraceSeconds
                foreach ($p in @(Get-InWorld)) {
                    try {
                        Invoke-Admin 'kick' @{ id = [int]$p.playerId; reason = 'deploy' } | Out-Null
                        Write-Host ("  kicked {0}" -f $p.name)
                    } catch {
                        Write-Warning ("  kick {0}: {1}" -f $p.name, $_.Exception.Message)
                    }
                }
                Start-Sleep -Seconds 10   # World's leave path writes each character back
            }
        } catch {
            Write-Warning ("  admin API on {0} did not answer: {1}" -f $api, $_.Exception.Message)
        }
    }
}

Write-Step "stop"
if ($running.Count -eq 0) {
    Write-Host '  not running'
} elseif ($PSCmdlet.ShouldProcess($exeName, 'stop')) {
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
    Write-Host ("  stopped {0} process(es)" -f $running.Count)
}

# ---------------------------------------------------------------- 3. back up the database
Write-Step "backup"
$backup = Join-Path $PSScriptRoot 'tools\backup-db.ps1'
if (Test-Path -LiteralPath $backup) {
    if ($PSCmdlet.ShouldProcess('database', 'back up')) { & $backup }
} else {
    Write-Warning "  tools\backup-db.ps1 not found beside this script - skipping the backup"
}

# ---------------------------------------------------------------- 4. install
Write-Step "install"
if (-not $From) {
    Write-Host ("  -From not given; using what is already in {0}" -f $InstallTo)
} else {
    $staging = Join-Path ([IO.Path]::GetTempPath()) ('terasharp-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $staging -Force | Out-Null
    try {
        if ($From -match '^https?://') {
            $zip = Join-Path $staging 'build.zip'
            Write-Host ("  downloading {0}" -f $From)
            if ($PSCmdlet.ShouldProcess($From, 'download')) {
                Invoke-WebRequest -Uri $From -OutFile $zip -UseBasicParsing
                Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $staging 'x') -Force
            }
            $src = Join-Path $staging 'x'
        } elseif ($From.ToLowerInvariant().EndsWith('.zip')) {
            if ($PSCmdlet.ShouldProcess($From, 'extract')) {
                Expand-Archive -LiteralPath $From -DestinationPath (Join-Path $staging 'x') -Force
            }
            $src = Join-Path $staging 'x'
        } else {
            $src = $From
        }

        # A publish can be nested one level down inside the archive.
        if (-not (Test-Path -LiteralPath (Join-Path $src $exeName))) {
            $found = @(Get-ChildItem -LiteralPath $src -Recurse -Filter $exeName -ErrorAction SilentlyContinue)
            if ($found.Count -eq 0) { throw "no $exeName under $src" }
            $src = $found[0].DirectoryName
        }

        if ($PSCmdlet.ShouldProcess($InstallTo, 'install build')) {
            New-Item -ItemType Directory -Path $InstallTo -Force | Out-Null
            Copy-Item -Path (Join-Path $src '*') -Destination $InstallTo -Recurse -Force
            Write-Host ("  installed to {0}" -f $InstallTo)
        }
    } finally {
        Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$exe = Join-Path $InstallTo $exeName
if (-not (Test-Path -LiteralPath $exe)) { throw "no $exe - nothing to start" }

# ---------------------------------------------------------------- 5. verify
Write-Step "check-config"
& $exe --check-config
if ($LASTEXITCODE -ne 0) { throw "--check-config exited $LASTEXITCODE" }

Write-Step "selftest"
& $exe --selftest
if ($LASTEXITCODE -ne 0) { throw "--selftest failed ($LASTEXITCODE). Not starting." }

# ---------------------------------------------------------------- 6. start
if ($NoStart) { Write-Step "done (-NoStart)"; return }

if ($WorldExe) {
    Write-Step "restart World"
    $world = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($WorldExe)) -ErrorAction SilentlyContinue)
    if ($world.Count -gt 0 -and $PSCmdlet.ShouldProcess('WorldServer', 'stop')) {
        $world | Stop-Process -Force
        Start-Sleep -Seconds 3
    }
    if ($PSCmdlet.ShouldProcess($WorldExe, 'start')) {
        Start-Process -FilePath $WorldExe -WorkingDirectory (Split-Path -Parent $WorldExe)
        Write-Host '  started; it connects in about 3 minutes'
    }
}

Write-Step "start"
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$logFile = Join-Path $LogDir ('arbiter-' + (Get-Date -Format 'yyyy-MM-dd-HHmmss') + '.log')
Write-Host ("  console -> {0}" -f $logFile)
if ($PSCmdlet.ShouldProcess($exe, 'start')) {
    & $exe 2>&1 | Tee-Object -FilePath $logFile
}
