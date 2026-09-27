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
      2. stops a running Arbiter
      3. backs up the database
      4. installs the new build from -From (a folder, a .zip, or a URL)
      5. runs --check-config and --selftest, and refuses to start on a failure
      6. starts the Arbiter with output tee'd to a dated log

    It does NOT touch WorldServer.exe. World keeps running across an Arbiter restart; the
    Arbiter reconnects. Restart World only when you actually need a fresh one.

.PARAMETER From
    Where the new build comes from. A folder holding TeraSharp.Arbiter.exe, a .zip of one, or
    an http(s) URL to such a .zip. Omit to redeploy whatever is already in -InstallTo.

.PARAMETER InstallTo
    Where the build lives on this box.

.PARAMETER EnvFile
    The .env to load. Defaults to .env beside this script.

.PARAMETER NoStart
    Do everything except start the Arbiter. Useful for a dry run on a live box.

.EXAMPLE
    .\deploy.ps1 -From \\build\share\TeraSharp -WhatIf

.EXAMPLE
    .\deploy.ps1 -From https://your-build-host.example/TeraSharp-bin.zip
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string] $From,
    [string] $InstallTo = 'C:\TeraSharp',
    [string] $EnvFile   = (Join-Path $PSScriptRoot '.env'),
    [string] $LogDir    = 'C:\TeraSharp\logs',
    [switch] $NoStart
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

# ---------------------------------------------------------------- 2. stop
Write-Step "stop"
$running = @(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue)
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

Write-Step "start"
New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
$logFile = Join-Path $LogDir ('arbiter-' + (Get-Date -Format 'yyyy-MM-dd-HHmmss') + '.log')
Write-Host ("  console -> {0}" -f $logFile)
if ($PSCmdlet.ShouldProcess($exe, 'start')) {
    & $exe 2>&1 | Tee-Object -FilePath $logFile
}
