# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
  T237 - what differs between the box's Datasheet and the PC's, in one table.

.DESCRIPTION
  Copy the box's Datasheet to a local folder first (never hash-sync over RDP - see
  docs\OPERATIONS.md section 7), then point this at both. Size is compared first and SHA-256 only
  when the sizes match, so a 15 MB UserSkillData pair costs one length check when it has not moved.

  Backups are excluded by default: .bak, .orig, .stock and the job-tagged forms (.m1.bak, .m5.bak,
  .t220.bak, .t236b.bak ...). They are supposed to differ, and including them hides the real drift.

  Exit code is the number of files that differ or are missing on one side, so it can gate a deploy.

.PARAMETER Box
  A LOCAL folder holding the copy of the box's Datasheet.

.PARAMETER Pc
  The PC's Datasheet. Defaults to D:\v100\TERA_SERVER.100\Executable\Datasheet.

.PARAMETER Filter
  Only compare files matching this wildcard, e.g. 'UserSkillData_*.xml'.

.PARAMETER IncludeBackups
  Compare the backup files too.

.PARAMETER All
  List identical files as well, not only the drift.

.EXAMPLE
  .\check-box.ps1 -Box D:\packetlogs\box-datasheet
  .\check-box.ps1 -Box D:\packetlogs\box-datasheet -Filter UserSkillData_*.xml -All
  .\check-box.ps1 -Box D:\packetlogs\box-datasheet -PushList drift.txt
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)] [string] $Box,
    [string] $Pc = 'D:\v100\TERA_SERVER.100\Executable\Datasheet',
    [string] $Filter = '*',
    [switch] $IncludeBackups,
    [switch] $All,
    # Write just the names that need pushing, one per line, for push-sheets.ps1 -ListFile.
    [string] $PushList
)

$ErrorActionPreference = 'Stop'
foreach ($d in @($Box, $Pc)) { if (-not (Test-Path -LiteralPath $d)) { throw "not a directory: $d" } }

# A backup is anything whose name ends .bak / .orig / .stock, with or without a job tag in front
# (AnimationData.xml.t220.bak, DefaultSkillSet.xml.m5.bak, UserData.xml.orig).
function Is-Backup([string] $name) { return $name -match '\.(bak|orig|stock)$' }

function Index([string] $root) {
    $h = @{}
    foreach ($f in (Get-ChildItem -LiteralPath $root -File -Filter $Filter -ErrorAction SilentlyContinue)) {
        if ((-not $IncludeBackups) -and (Is-Backup $f.Name)) { continue }
        $h[$f.Name] = $f
    }
    return $h
}

$pcFiles = Index $Pc
$boxFiles = Index $Box
Write-Host ("pc  : {0}   ({1} file(s))" -f $Pc, $pcFiles.Count)
Write-Host ("box : {0}   ({1} file(s))" -f $Box, $boxFiles.Count)
if (-not $IncludeBackups) { Write-Host 'backups (.bak / .orig / .stock) excluded - pass -IncludeBackups to compare them too' }

$rows = @()
foreach ($name in (($pcFiles.Keys + $boxFiles.Keys) | Sort-Object -Unique)) {
    $p = $pcFiles[$name]; $b = $boxFiles[$name]
    if ($p -and -not $b) {
        $rows += [pscustomobject]@{ File = $name; Verdict = 'PC only'; Pc = $p.Length; Box = 0; Delta = $p.Length }
        continue
    }
    if ($b -and -not $p) {
        $rows += [pscustomobject]@{ File = $name; Verdict = 'BOX only'; Pc = 0; Box = $b.Length; Delta = -$b.Length }
        continue
    }
    if ($p.Length -ne $b.Length) {
        $rows += [pscustomobject]@{ File = $name; Verdict = 'DIFFERS'; Pc = $p.Length; Box = $b.Length; Delta = ($p.Length - $b.Length) }
        continue
    }
    $ph = (Get-FileHash -LiteralPath $p.FullName -Algorithm SHA256).Hash
    $bh = (Get-FileHash -LiteralPath $b.FullName -Algorithm SHA256).Hash
    $v = 'same'; if ($ph -ne $bh) { $v = 'DIFFERS' }
    $rows += [pscustomobject]@{ File = $name; Verdict = $v; Pc = $p.Length; Box = $b.Length; Delta = 0 }
}

$drift = @($rows | Where-Object { $_.Verdict -ne 'same' })
$show = $rows
if (-not $All) { $show = $drift }

Write-Host ''
if (@($show).Count -eq 0) {
    Write-Host ("in sync - {0} file(s) compared, none differ." -f @($rows).Count)
    exit 0
}
Write-Host ((@($show) | Sort-Object Verdict, File |
    Format-Table @{ L = 'file'; E = { $_.File } },
                 @{ L = 'verdict'; E = { $_.Verdict } },
                 @{ L = 'pc bytes'; E = { $_.Pc }; A = 'right' },
                 @{ L = 'box bytes'; E = { $_.Box }; A = 'right' },
                 @{ L = 'delta'; E = { if ($_.Delta) { '{0:+#;-#;0}' -f $_.Delta } else { '' } }; A = 'right' } |
    Out-String -Width 160).TrimEnd())

Write-Host ''
Write-Host ("{0} of {1} file(s) differ or are missing on one side." -f @($drift).Count, @($rows).Count)
$toPush = @($drift | Where-Object { $_.Verdict -ne 'BOX only' } | Select-Object -Expand File)
if ($toPush.Count -gt 0) {
    Write-Host ''
    Write-Host 'to push (feed this list to tools\push-sheets.ps1):'
    foreach ($f in $toPush) { Write-Host "  $f" }
    if ($PushList) {
        Set-Content -LiteralPath $PushList -Value $toPush -Encoding UTF8
        Write-Host ''
        Write-Host ("list written to {0}" -f $PushList)
    }
}
exit @($drift).Count
