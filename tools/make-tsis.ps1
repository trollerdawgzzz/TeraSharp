#Requires -Version 5.1
<#
.SYNOPSIS
    Build a TSIS fixture container from a reframer's *_frames.txt.

.DESCRIPTION
    The byte-exact tests in src/TeraSharp.Arbiter.Tests read their ground truth from small
    TSIS containers in data/. This repository ships none of them - each operator regenerates
    the ones they want from a capture of their own server. This script is the last step of
    that: reframe-tap.ps1 / reframe-client.ps1 turn a raw log into <log>_frames.txt, and this
    turns the frames you name into data\<something>.bin.

    TSIS is deliberately tiny, all little-endian:

        "TSIS"                       4 bytes, magic
        u32 recordCount
        per record:
            u32 seq                  the frame number the reframer printed
            u16 opcode
            u32 payloadLength
            payload                  the frame MINUS its header (see -HeaderBytes)

    Payloads only. A test that cites a frame-relative offset is 6 (tap) or 4 (client) more
    than the payload index it lands on; that convention is what the existing data\*.md notes
    describe, and it is kept here.

.PARAMETER Frames
    A *_frames.txt written by reframe-tap.ps1 or reframe-client.ps1. Its shape is
    "=== <seq> ... 0x<OPCODE> ... len=<n>" followed by one line of space-separated hex.

.PARAMETER Out
    The container to write, e.g. data\cap_t26.bin.

.PARAMETER Seq
    Frame numbers to include. Omit to take every frame in the file. A number that is not in
    the file is reported and is an error, so a typo does not silently produce a short fixture.

.PARAMETER HeaderBytes
    How many bytes to strip off the front of each frame. 6 for an Arbiter<->World tap frame
    ([u32 length][u16 opcode]), 4 for a client packet ([u16 length][u16 opcode]). Default 6.

.PARAMETER Force
    Overwrite -Out if it already exists.

.EXAMPLE
    .\reframe-tap.ps1 -Log <your tap log> -Opcodes 0x2890,0x2891,0x2909,0x2910
    .\make-tsis.ps1 -Frames <your tap log>_frames.txt -Out ..\data\cap_t26.bin -Seq 336,413,376,511,2068

.EXAMPLE
    .\make-tsis.ps1 -Frames capture_..._frames.txt -Out ..\data\cap_client_settings.bin -HeaderBytes 4

.NOTES
    Nothing this writes belongs in a public commit. data\*.bin is gitignored for that reason;
    tools\audit-release.ps1 fails the build if one is staged anyway.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Frames,
    [Parameter(Mandatory = $true)] [string] $Out,
    [int[]] $Seq,
    [ValidateRange(0, 64)] [int] $HeaderBytes = 6,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$Frames = [IO.Path]::GetFullPath($Frames)
$Out    = [IO.Path]::GetFullPath($Out)
if (-not (Test-Path -LiteralPath $Frames)) { throw "no such frames file: $Frames" }
if ((Test-Path -LiteralPath $Out) -and -not $Force) { throw "$Out exists; pass -Force to overwrite" }

# --- read the frames file -------------------------------------------------------------
# "=== 413 A->W 2026-..-..T..:..:.. 0x2891 NAME len=52" then one line of "13 00 00 00 ...".
$header = [regex] '^===\s+(?<seq>\d+)\b.*?\b0x(?<op>[0-9A-Fa-f]{1,4})\b.*?\blen=(?<len>\d+)\s*$'

$records = New-Object 'System.Collections.Generic.List[object]'
$pending = $null
foreach ($line in [IO.File]::ReadLines($Frames)) {
    $m = $header.Match($line)
    if ($m.Success) {
        $pending = [pscustomobject]@{
            Seq    = [uint32] $m.Groups['seq'].Value
            Opcode = [uint16] ('0x' + $m.Groups['op'].Value)
            Len    = [int]    $m.Groups['len'].Value
        }
        continue
    }
    if ($null -eq $pending) { continue }
    $hex = $line.Trim()
    if ($hex.Length -eq 0) { continue }

    $parts = $hex -split '\s+'
    $bytes = New-Object byte[] $parts.Count
    for ($i = 0; $i -lt $parts.Count; $i++) { $bytes[$i] = [Convert]::ToByte($parts[$i], 16) }

    if ($bytes.Count -ne $pending.Len) {
        throw ("frame {0}: header says len={1} but the hex line has {2} byte(s)" -f $pending.Seq, $pending.Len, $bytes.Count)
    }
    if ($bytes.Count -lt $HeaderBytes) {
        throw ("frame {0}: {1} byte(s) is shorter than -HeaderBytes {2}" -f $pending.Seq, $bytes.Count, $HeaderBytes)
    }

    $records.Add([pscustomobject]@{
        Seq     = $pending.Seq
        Opcode  = $pending.Opcode
        Payload = $bytes[$HeaderBytes..($bytes.Count - 1)]
    })
    $pending = $null
}

if ($records.Count -eq 0) { throw "no frames parsed out of $Frames - is it a *_frames.txt?" }

# --- select ---------------------------------------------------------------------------
if ($PSBoundParameters.ContainsKey('Seq') -and $null -ne $Seq -and $Seq.Count -gt 0) {
    $want  = @{}
    foreach ($s in $Seq) { $want[[uint32]$s] = $true }
    $chosen = @($records | Where-Object { $want.ContainsKey($_.Seq) })
    $got = @{}
    foreach ($r in $chosen) { $got[$r.Seq] = $true }
    $missing = @($Seq | Where-Object { -not $got.ContainsKey([uint32]$_) })
    if ($missing.Count -gt 0) { throw ("not in {0}: frame(s) {1}" -f (Split-Path -Leaf $Frames), ($missing -join ', ')) }
} else {
    # $records.ToArray(), not @($records): wrapping a List[object] in @() throws
    # "Argument types do not match" - the array subexpression does not accept a
    # generic List the way it accepts a pipeline. @(<pipeline>) above is fine.
    $chosen = $records.ToArray()
}

$seen = @{}
foreach ($r in $chosen) {
    if ($seen.ContainsKey($r.Seq)) {
        Write-Warning ("frame {0} appears more than once; the reader keys on seq, so the last one wins" -f $r.Seq)
    }
    $seen[$r.Seq] = $true
}

# --- write ----------------------------------------------------------------------------
# $blob, not $out: PowerShell variable names are case-insensitive, so a local $out would
# silently overwrite the [string] $Out parameter and every AddRange below would fail.
$blob = New-Object 'System.Collections.Generic.List[byte]'
$blob.AddRange([byte[]][char[]]'TSIS')
$blob.AddRange([BitConverter]::GetBytes([uint32] $chosen.Count))
foreach ($r in $chosen) {
    $blob.AddRange([BitConverter]::GetBytes([uint32] $r.Seq))
    $blob.AddRange([BitConverter]::GetBytes([uint16] $r.Opcode))
    $blob.AddRange([BitConverter]::GetBytes([uint32] $r.Payload.Count))
    $blob.AddRange([byte[]] $r.Payload)
}

$dir = Split-Path -Parent $Out
if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
[IO.File]::WriteAllBytes($Out, $blob.ToArray())

Write-Host ("  {0}  {1} record(s), {2} byte(s)" -f $Out, $chosen.Count, $blob.Count)
