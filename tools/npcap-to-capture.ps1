<#
.SYNOPSIS
    Convert a Noctenium .npcap client capture into the text format
    reframe-client.ps1 (and every other tool here) already reads.

.DESCRIPTION
    Noctenium writes D:\...\noctenium\logs\packet-captures\capture-*.npcap.
    The container is tiny and was reversed in T130 from classic_live.npcap:

        file header, 16 bytes
            [0]  char[4] "NPCP"
            [4]  u32     version (2)
            [8]  u64     capture start, UNIX nanoseconds

        record, 14-byte header then payload
            [0]  u16     type   0 = S->C wire, 1 = C->S wire,
                                2 = S->C packet, 3 = C->S packet
            [2]  u64     nanoseconds since the client started
            [10] u32     payload length
            [14] payload

    Types 0/1 are socket-level: one record is one read, so its payload holds
    zero or more whole TERA frames back to back. Types 2/3 are packet-level:
    one record is exactly one frame. Both views are in the same file and they
    are NOT identical - see -Stream.

    A TERA frame is [u16 totalLen][u16 opcode][body], totalLen including the
    four header bytes, which is what makes the split self-checking: every
    record and every walk of a wire record has to land exactly on its end.

.PARAMETER Npcap
    The .npcap file.

.PARAMETER Out
    Output .log. Default: the input with .log instead of .npcap.

.PARAMETER Stream
    Which view to write.
      Split (default) - types 2/3. One record per frame, already framed by
                        Noctenium, and the complete list.
      Raw             - types 0/1, re-split here. The socket view.
    Either way the other view is walked as well and any disagreement is
    reported, because a silent difference between them is the one thing that
    would make this capture lie to you.

.PARAMETER DataJson
    tera-server-proxy\data\data.json, for opcode names. Same default as
    reframe-client.ps1.

.PARAMETER Protocol
    Protocol map key. Default 376012 (this build).

.EXAMPLE
    .\npcap-to-capture.ps1 -Npcap D:\packetlogs\classic_live.npcap
    .\reframe-client.ps1   -Log   D:\packetlogs\classic_live.log

.NOTES
    Runs on Windows PowerShell 5.1 as well as pwsh 7: no ternary, no ?? and no calculated
    -Property on Measure-Object, which is the one that bit first.

    Opcode names come from the map, never from the file - .npcap stores none.
    An opcode the map does not know is written UNKNOWN_0xNNNN so the record
    still parses; reframe-client.ps1 then renames it if its map is newer.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Npcap,
    [string] $Out,
    [ValidateSet('Split', 'Raw')][string] $Stream = 'Split',
    [string] $DataJson,
    [string] $Protocol = '376012'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- paths -------------------------------------------------------------------
# Resolve before any [IO.*] call: those resolve against the PROCESS working
# directory, not the shell's. Same note as reframe-client.ps1.
$inPath = (Resolve-Path -LiteralPath $Npcap).ProviderPath
if (-not $Out) {
    $Out = [IO.Path]::Combine([IO.Path]::GetDirectoryName($inPath),
                              [IO.Path]::GetFileNameWithoutExtension($inPath) + '.log')
}
$Out = [IO.Path]::GetFullPath($Out)

if (-not $DataJson) {
    $root = if ($env:TERASHARP_DATA) { $env:TERASHARP_DATA } else { 'D:\v100\TERA_SERVER.100' }
    $DataJson = Join-Path $root 'tera-server-proxy\data\data.json'
}

# ---- opcode map --------------------------------------------------------------
$nameOf = @{}
if (Test-Path -LiteralPath $DataJson) {
    $json = Get-Content -LiteralPath (Resolve-Path -LiteralPath $DataJson).ProviderPath -Raw | ConvertFrom-Json
    if ($json.maps.PSObject.Properties.Name -contains $Protocol) {
        foreach ($p in $json.maps.$Protocol.PSObject.Properties) { $nameOf[[int]$p.Value] = $p.Name }
        Write-Verbose "loaded $($nameOf.Count) opcodes for protocol $Protocol"
    } else {
        Write-Warning "data.json has no map '$Protocol' - every packet will be UNKNOWN_0xNNNN"
    }
} else {
    Write-Warning "data.json not found at $DataJson - every packet will be UNKNOWN_0xNNNN"
}

# ---- read --------------------------------------------------------------------
$bytes = [IO.File]::ReadAllBytes($inPath)
if ($bytes.Length -lt 16) { throw "$inPath is $($bytes.Length) B - too short to be an .npcap" }
$magic = [Text.Encoding]::ASCII.GetString($bytes, 0, 4)
if ($magic -ne 'NPCP') { throw "$inPath does not start with NPCP (got '$magic')" }
$version = [BitConverter]::ToUInt32($bytes, 4)
if ($version -ne 2) { Write-Warning "npcap version $version - this script was written against 2" }
$startNs = [BitConverter]::ToUInt64($bytes, 8)
$startUtc = [DateTimeOffset]::FromUnixTimeMilliseconds([int64]($startNs / 1000000)).UtcDateTime

# ---- walk the records --------------------------------------------------------
# One pass, keeping both views. Frames stay as (dir, bytes) in order.
$wire  = New-Object 'System.Collections.Generic.List[object]'
$split = New-Object 'System.Collections.Generic.List[object]'
$typeCount = @{ 0 = 0; 1 = 0; 2 = 0; 3 = 0 }
$problems = New-Object 'System.Collections.Generic.List[string]'
$records = 0
$off = 16

while ($off + 14 -le $bytes.Length) {
    $type = [BitConverter]::ToUInt16($bytes, $off)
    $len  = [BitConverter]::ToUInt32($bytes, $off + 10)
    $at   = $off + 14
    if ($at + $len -gt $bytes.Length) {
        $problems.Add("record $records at offset $off says $len B but only $($bytes.Length - $at) B remain - file truncated")
        break
    }
    $records++
    if ($typeCount.ContainsKey([int]$type)) { $typeCount[[int]$type]++ }
    else { $problems.Add("record $($records): unknown record type $type") ; $off = $at + $len; continue }

    $dir = if ($type -eq 0 -or $type -eq 2) { 'S->C' } else { 'C->S' }

    if ($type -eq 2 -or $type -eq 3) {
        # One record, one frame - and it has to say so itself.
        if ($len -lt 4) {
            $problems.Add("packet record $($records): $len B is shorter than the 4-byte header")
        } else {
            $inner = [BitConverter]::ToUInt16($bytes, $at)
            if ($inner -ne $len) {
                $problems.Add("packet record $($records): frame header says $inner B, record says $len B")
            }
            $f = [byte[]]::new($len)
            [Array]::Copy($bytes, $at, $f, 0, $len)
            $split.Add([pscustomobject]@{ Dir = $dir; Data = $f })
        }
    } else {
        # A socket read: walk it frame by frame and insist on landing on the end.
        $p = 0
        while ($p + 4 -le $len) {
            $fl = [BitConverter]::ToUInt16($bytes, $at + $p)
            if ($fl -lt 4 -or $p + $fl -gt $len) {
                $problems.Add("wire record $($records): frame at +$p says $fl B, which does not fit the $len B read")
                break
            }
            $f = [byte[]]::new($fl)
            [Array]::Copy($bytes, $at + $p, $f, 0, $fl)
            $wire.Add([pscustomobject]@{ Dir = $dir; Data = $f })
            $p += $fl
        }
        if ($p -ne $len) { $problems.Add("wire record $($records): walked $p of $len B") }
    }
    $off = $at + $len
}
if ($off -ne $bytes.Length) {
    $problems.Add("stopped at offset $off of $($bytes.Length) - the record chain does not cover the file")
}

# ---- the two views, compared -------------------------------------------------
# They are two logs of the same session, not two copies of one log: Noctenium is
# a proxy, so a packet a mod rewrites or injects reaches the packet view without
# ever being on the socket. Saying how far they agree is the whole point.
# Windows PowerShell 5.1 has no CALCULATED -Property on Measure-Object (that arrived in
# PS 6), so project with ForEach-Object first and sum the plain numbers. The [int64] cast
# also covers the empty case: Measure-Object -Sum over nothing gives $null, not 0.
$wireBytes  = [int64](($wire  | ForEach-Object { $_.Data.Length } | Measure-Object -Sum).Sum)
$splitBytes = [int64](($split | ForEach-Object { $_.Data.Length } | Measure-Object -Sum).Sum)

function Compare-View([string] $dir) {
    $a = @($wire  | Where-Object { $_.Dir -eq $dir })
    $b = @($split | Where-Object { $_.Dir -eq $dir })
    $n = [Math]::Min($a.Count, $b.Count)
    $same = 0
    while ($same -lt $n) {
        $x = $a[$same].Data; $y = $b[$same].Data
        if ($x.Length -ne $y.Length) { break }
        $eq = $true
        for ($k = 0; $k -lt $x.Length; $k++) { if ($x[$k] -ne $y[$k]) { $eq = $false; break } }
        if (-not $eq) { break }
        $same++
    }
    [pscustomobject]@{ Dir = $dir; Wire = $a.Count; Split = $b.Count; Agree = $same }
}
$cmp = @((Compare-View 'S->C'), (Compare-View 'C->S'))

# ---- write -------------------------------------------------------------------
# @() is load-bearing: a List[object] coming out of an if-expression is UNROLLED by the
# pipeline, so an empty one lands as $null and $chosen.Count then throws under StrictMode.
$chosen = @(if ($Stream -eq 'Raw') { $wire } else { $split })
$utf8 = New-Object Text.UTF8Encoding($false)
$w = New-Object IO.StreamWriter($Out, $false, $utf8)
try {
    $seq = 0
    foreach ($f in $chosen) {
        $seq++
        $op = [BitConverter]::ToUInt16($f.Data, 2)
        $name = if ($nameOf.ContainsKey([int]$op)) { $nameOf[[int]$op] } else { 'UNKNOWN_0x{0:X4}' -f $op }
        # Outer parentheses are load-bearing: inside a method call the comma is the
        # argument separator, so WriteLine('fmt' -f $a, $b) hands $b to WriteLine.
        $w.WriteLine(('[{0}] [{1}] {2} ({3}) len={4}' -f $seq, $f.Dir, $name, $op, $f.Data.Length))
        $w.WriteLine('HEX: ' + [BitConverter]::ToString($f.Data).Replace('-', ' '))
        $w.WriteLine()
    }
} finally { $w.Close() }

# ---- report ------------------------------------------------------------------
Write-Host ("{0}: {1} B, npcap v{2}, started {3:yyyy-MM-dd HH:mm:ss} UTC" -f `
    [IO.Path]::GetFileName($inPath), $bytes.Length, $version, $startUtc)
Write-Host ("  {0} record(s): {1} S->C wire, {2} C->S wire, {3} S->C packet, {4} C->S packet" -f `
    $records, $typeCount[0], $typeCount[1], $typeCount[2], $typeCount[3])
Write-Host ("  wire view  {0} frame(s), {1} B" -f $wire.Count, $wireBytes)
Write-Host ("  packet view {0} frame(s), {1} B" -f $split.Count, $splitBytes)
foreach ($c in $cmp) {
    $note = if ($c.Wire -eq $c.Split -and $c.Agree -eq $c.Wire) { 'identical' }
            else { "diverge after {0}" -f $c.Agree }
    Write-Host ("  {0}: wire {1} / packet {2}, {3}" -f $c.Dir, $c.Wire, $c.Split, $note)
}
Write-Host ("  {0}  {1} frame(s) from the {2} view" -f $Out, $chosen.Count, $Stream.ToLower())
if ($problems.Count -gt 0) {
    Write-Warning "$($problems.Count) problem(s):"
    foreach ($p in $problems | Select-Object -First 20) { Write-Warning "  $p" }
} else {
    Write-Host '  no problems - every record and every frame ends where it said it would'
}
