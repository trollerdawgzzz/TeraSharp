<#
.SYNOPSIS
    Re-list a client<->Arbiter packet-logger capture into a condensed listing plus
    an optional full-packet dump, resolving opcode names from data.json.

.DESCRIPTION
    The client side is easier than the tap: the packet logger already splits the
    stream into packets and writes one record each -

        [12] [S->C] S_LOGIN (19989) len=421
        HEX: A5 01 15 4E ...
        FIELDS: {...}            (only capture.log has this line)

    So this script does not reassemble anything. What it does do is check every
    record against itself and against the protocol map, then emit the same two
    shapes reframe-tap.ps1 produces so both sides of a session can be read the
    same way and lined up by sequence.

    Client frame layout:  [u16 totalLen][u16 opcode][body]
    totalLen INCLUDES the four header bytes, and the header is INSIDE the HEX
    line - so a record's len, its HEX byte count and the u16 at HEX[0..1] must
    all agree. When they do not, the capture was truncated: chat_capture.log cuts
    every HEX line at 200 bytes, which makes 131 of its 557 packets unusable for
    layout work and is exactly the kind of thing this check exists to surface.

.PARAMETER Log
    A packet-logger capture (capture_*.log, lobby_proxy.log, cap_newchar_client.log...).

.PARAMETER DataJson
    tera-server-proxy\data\data.json. Default:
    $env:TERASHARP_DATA\tera-server-proxy\data\data.json, else D:\v100\TERA_SERVER.100\...

.PARAMETER Protocol
    Protocol map key. Default 376012 (this build). NOT 367081 - the def folder's
    README names the wrong one.

.PARAMETER Packets
    Packets to dump in full. Accepts names (C_LOGIN_ARBITER), 0x hex or decimal.

.PARAMETER Skip
    Packets to leave out of the condensed listing. Default is empty: unlike the
    tap there is no tunnel noise here, and S_CHAT is usually what you came for.

.EXAMPLE
    .\reframe-client.ps1 -Log D:\packetlogs\capture_2026-09-13T11-42-27-513Z.log `
                         -Packets S_SPAWN_ME,S_LOGIN,C_LOAD_TOPO_FIN

.NOTES
    Relative paths are resolved before any [IO.*] call - those resolve against the
    PROCESS working directory (usually system32), not the shell's.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Log,
    [string]   $Ctl,
    [string]   $Frames,
    [string[]] $Packets,
    [string[]] $Skip = @(),
    [int]      $Bytes = 64,
    [string]   $DataJson,
    [string]   $Protocol = '376012'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---- paths -------------------------------------------------------------------
$logPath = (Resolve-Path -LiteralPath $Log).ProviderPath
$base    = [IO.Path]::Combine([IO.Path]::GetDirectoryName($logPath),
                              [IO.Path]::GetFileNameWithoutExtension($logPath))
if (-not $Ctl)    { $Ctl    = "$base`_ctl.txt" }
if (-not $Frames) { $Frames = "$base`_frames.txt" }
$Ctl    = [IO.Path]::GetFullPath($Ctl)
$Frames = [IO.Path]::GetFullPath($Frames)

if (-not $DataJson) {
    $root = if ($env:TERASHARP_DATA) { $env:TERASHARP_DATA } else { 'D:\v100\TERA_SERVER.100' }
    $DataJson = Join-Path $root 'tera-server-proxy\data\data.json'
}

# ---- opcode map --------------------------------------------------------------
$nameOf = @{}   # opcode -> name
$codeOf = @{}   # NAME    -> opcode
if (Test-Path -LiteralPath $DataJson) {
    $json = Get-Content -LiteralPath (Resolve-Path -LiteralPath $DataJson).ProviderPath -Raw | ConvertFrom-Json
    if ($json.maps.PSObject.Properties.Name -contains $Protocol) {
        foreach ($p in $json.maps.$Protocol.PSObject.Properties) {
            $codeOf[$p.Name] = [int]$p.Value
            $nameOf[[int]$p.Value] = $p.Name
        }
        Write-Verbose "loaded $($nameOf.Count) opcodes for protocol $Protocol"
    } else {
        Write-Warning "data.json has no map '$Protocol' - names will come from the capture only"
    }
} else {
    Write-Warning "data.json not found at $DataJson - names will come from the capture only"
}

function Resolve-Packet([string] $s) {
    $t = $s.Trim()
    if ($t -match '^0[xX][0-9A-Fa-f]+$') { return [int][Convert]::ToUInt16($t.Substring(2), 16) }
    if ($t -match '^\d+$')               { return [int]$t }
    if ($codeOf.ContainsKey($t))         { return $codeOf[$t] }
    throw "unknown packet '$s' - not hex, not decimal, and not in protocol map $Protocol"
}

$skipSet = New-Object 'System.Collections.Generic.HashSet[int]'
foreach ($s in $Skip)    { [void]$skipSet.Add((Resolve-Packet $s)) }
$wantSet = New-Object 'System.Collections.Generic.HashSet[int]'
if ($Packets) { foreach ($s in $Packets) { [void]$wantSet.Add((Resolve-Packet $s)) } }

# ---- walk --------------------------------------------------------------------
$rejects = New-Object 'System.Collections.Generic.List[string]'
$records = 0; $ctlLines = 0; $frmLines = 0; $renamed = 0
$curSeq = 0; $curDir = ''; $curName = ''; $curOp = 0; $curLen = 0
$hexBuf = New-Object Text.StringBuilder
$have = $false

$ctlOut = $null; $frmOut = $null
try {
    $utf8   = New-Object Text.UTF8Encoding($false)
    $ctlOut = New-Object IO.StreamWriter($Ctl, $false, $utf8)
    if ($wantSet.Count -gt 0) { $frmOut = New-Object IO.StreamWriter($Frames, $false, $utf8) }

    $emit = {
        if (-not $have) { return }
        $script:records++
        $hexText = $hexBuf.ToString()
        $data = [byte[]]::new(0)
        if ($hexText.Trim().Length -gt 0) {
            # [char[]] is load-bearing - see the note in reframe-tap.ps1.
            $tok = $hexText.Split([char[]]@(' ', "`t", "`r", "`n"), [StringSplitOptions]::RemoveEmptyEntries)
            $data = [byte[]]::new($tok.Count)
            for ($i = 0; $i -lt $tok.Count; $i++) { $data[$i] = [Convert]::ToByte($tok[$i], 16) }
        }

        if ($data.Length -ne $curLen) {
            $rejects.Add(("packet {0} {1} {2}: record says len={3} but {4} hex byte(s) follow - TRUNCATED, unusable for layout work" -f `
                          $curSeq, $curDir, $curName, $curLen, $data.Length))
        }
        if ($data.Length -lt 4) {
            $rejects.Add(("packet {0} {1} {2}: {3} byte(s) is shorter than the 4-byte header" -f `
                          $curSeq, $curDir, $curName, $data.Length))
            return
        }
        $innerLen = [BitConverter]::ToUInt16($data, 0)
        $innerOp  = [BitConverter]::ToUInt16($data, 2)
        if ($innerLen -ne $curLen) {
            $rejects.Add(("packet {0} {1} {2}: header says {3} B, record says {4} B" -f `
                          $curSeq, $curDir, $curName, $innerLen, $curLen))
        }
        if ($innerOp -ne $curOp) {
            $rejects.Add(("packet {0} {1} {2}: header opcode {3} != record opcode {4}" -f `
                          $curSeq, $curDir, $curName, $innerOp, $curOp))
        }

        # The capture's own name wins only when the map does not know the opcode;
        # a disagreement means the capture came from a different protocol build and
        # is worth saying out loud rather than silently trusting.
        $name = $curName
        if ($nameOf.ContainsKey([int]$curOp)) {
            $mapped = $nameOf[[int]$curOp]
            if ($mapped -ne $curName) {
                $script:renamed++
                $rejects.Add(("packet {0}: capture calls {1} '{2}', protocol {3} calls it '{4}'" -f `
                              $curSeq, $curOp, $curName, $Protocol, $mapped))
                $name = $mapped
            }
        }

        if (-not $skipSet.Contains([int]$curOp)) {
            $take = [Math]::Min($Bytes, $data.Length - 4)
            $hex  = ''
            if ($take -gt 0) {
                $sb = New-Object Text.StringBuilder
                for ($i = 4; $i -lt 4 + $take; $i++) { [void]$sb.Append('{0:X2} ' -f $data[$i]) }
                $hex = $sb.ToString().TrimEnd()
            }
            $ctlOut.WriteLine((('{0,5} {1} 0x{2:X4} {3,-40} len={4,6} {5}' -f `
                $curSeq, $curDir, $curOp, $name, $data.Length, $hex)).TrimEnd())
            $script:ctlLines++
        }

        if ($wantSet.Contains([int]$curOp)) {
            $sb = New-Object Text.StringBuilder
            foreach ($x in $data) { [void]$sb.Append('{0:X2} ' -f $x) }
            # Outer parentheses: inside a method call the comma is the argument
            # separator, so WriteLine('fmt' -f $a, $b) hands $b to WriteLine.
            $frmOut.WriteLine(('=== {0} {1} 0x{2:X4} {3} len={4}' -f $curSeq, $curDir, $curOp, $name, $data.Length))
            $frmOut.WriteLine($sb.ToString().TrimEnd())
            $script:frmLines++
        }
    }

    $reader = New-Object IO.StreamReader($logPath)
    $hdr    = '^\[(\d+)\]\s+\[([^\]]+)\]\s+(\S+)\s+\((\d+)\)\s+len=(\d+)\s*$'
    while ($null -ne ($line = $reader.ReadLine())) {
        if ($line -match $hdr) {
            & $emit
            $curSeq  = [int]$Matches[1]; $curDir = $Matches[2]; $curName = $Matches[3]
            $curOp   = [int]$Matches[4]; $curLen = [int]$Matches[5]
            [void]$hexBuf.Clear(); $have = $true
        }
        elseif ($have -and $line.StartsWith('HEX:')) {
            [void]$hexBuf.Append($line.Substring(4)); [void]$hexBuf.Append(' ')
        }
        # FIELDS: and anything else is decoration - ignored on purpose.
    }
    & $emit
    $reader.Close()

    if ($records -eq 0) {
        Write-Warning "no packet headers matched in $logPath - this does not look like a packet-logger capture (an Arbiter<->World tap has no '(opcode)' on the header line; use reframe-tap.ps1)."
    }
}
finally {
    if ($ctlOut) { $ctlOut.Close() }
    if ($frmOut) { $frmOut.Close() }
}

Write-Host ("{0}: {1} packet(s)" -f [IO.Path]::GetFileName($logPath), $records)
Write-Host ("  {0}  {1} line(s)" -f $Ctl, $ctlLines)
if ($wantSet.Count -gt 0) { Write-Host ("  {0}  {1} packet(s)" -f $Frames, $frmLines) }
if ($renamed -gt 0) { Write-Warning "$renamed packet(s) are named differently by protocol $Protocol - is this capture from another build?" }
if ($rejects.Count -gt 0) {
    Write-Warning "$($rejects.Count) reject(s):"
    foreach ($r in $rejects) { Write-Warning "  $r" }
} else {
    Write-Host '  no rejects - every record is self-consistent'
}
