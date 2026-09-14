<#
.SYNOPSIS
    Reframe an Arbiter<->World tap log into a condensed control listing plus an
    optional full-frame dump.

.DESCRIPTION
    arbiter-world-tap.js logs raw TCP chunks, not frames. One chunk can hold 23
    frames (the 0x147E promotion burst arrives as one 30234-byte write) and one
    frame can span several chunks, so anything that reads a chunk as a packet is
    wrong in both directions. This script reassembles the byte stream per
    direction - and, since T56, per World socket - and cuts it into frames on the
    leading u32 length.

    Frame layout (Arbiter<->World, plaintext):  [u32 totalLen][u16 opcode][payload]
    totalLen INCLUDES the six header bytes, so a frame with no payload is 6 bytes.

    Two outputs, both optional:

      -Ctl     one line per frame, minus the noise opcodes in -Skip, with the
               first -Bytes payload bytes. This is the file to read first.
      -Frames  the complete hex of every frame whose opcode is in -Opcodes.

    A frame is numbered by the CHUNK its first byte arrived in, not by its
    position in the stream, so a number in the listing can be found directly in
    the raw log. That is what the original ad-hoc reframer did and the numbering
    is kept compatible.

.PARAMETER Log
    The tap log. Both direction spellings are accepted: the pre-T56 "[W->A]" and
    the current "[W->A#3]", where 3 is the World socket the chunk arrived on.

.PARAMETER Opcodes
    Opcodes to dump in full, e.g. 0x138E,0x138D,0x13BE. Accepts 0x-prefixed hex
    or decimal. Without this, -Frames is not written.

.PARAMETER Skip
    Opcodes to leave out of the condensed listing. The default is the ten
    high-volume frames that drown everything else out; it reproduces
    D:\packetlogs\cap_relog9827_ctl.txt from its source log.

.PARAMETER Link
    Only process this World socket id (1-based). Needs a post-T56 log.

.EXAMPLE
    .\reframe-tap.ps1 -Log D:\packetlogs\arb_world_2026-09-13T11-33-30-680Z.log `
                      -Opcodes 0x138E,0x138D,0x1390,0x13BE,0x13C0,0x2711,0x2738

.NOTES
    PowerShell's [IO.File] and [IO.StreamReader] resolve relative paths against
    the PROCESS working directory, which is usually system32 - never the shell's.
    Every path here goes through Resolve-Path / GetFullPath first.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $Log,
    [string]   $Ctl,
    [string]   $Frames,
    [string[]] $Opcodes,
    [string[]] $Skip = @('0x13F7','0x13F6','0x13F2','0x13E5','0x15A8','0x138A','0x138B','0x2801','0x147E','0x1436'),
    [int]      $Bytes = 64,
    [int]      $Link = 0,
    [string]   $Names,
    [int]      $MaxFrame = 1048576
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-Opcode([string] $s) {
    $t = $s.Trim()
    if ($t -match '^0[xX][0-9A-Fa-f]+$') { return [int][Convert]::ToUInt16($t.Substring(2), 16) }
    return [int]$t
}

# ---- paths -------------------------------------------------------------------
$logPath = (Resolve-Path -LiteralPath $Log).ProviderPath
$base    = [IO.Path]::Combine([IO.Path]::GetDirectoryName($logPath),
                              [IO.Path]::GetFileNameWithoutExtension($logPath))
if (-not $Ctl)    { $Ctl    = "$base`_ctl.txt" }
if (-not $Frames) { $Frames = "$base`_frames.txt" }
$Ctl    = [IO.Path]::GetFullPath($Ctl)
$Frames = [IO.Path]::GetFullPath($Frames)

$skipSet = New-Object 'System.Collections.Generic.HashSet[int]'
foreach ($s in $Skip)    { [void]$skipSet.Add((ConvertTo-Opcode $s)) }
$wantSet = New-Object 'System.Collections.Generic.HashSet[int]'
if ($Opcodes) { foreach ($s in $Opcodes) { [void]$wantSet.Add((ConvertTo-Opcode $s)) } }

# ---- optional opcode -> name table ("0xNNNN|NAME" per line) ------------------
$nameOf = @{}
if ($Names) {
    $np = (Resolve-Path -LiteralPath $Names).ProviderPath
    foreach ($line in [IO.File]::ReadLines($np)) {
        $p = $line.Split('|')
        if ($p.Count -ge 2 -and $p[0].StartsWith('0x')) {
            try { $nameOf[[int][Convert]::ToUInt16($p[0].Substring(2), 16)] = $p[1].Trim() } catch { }
        }
    }
    Write-Verbose "loaded $($nameOf.Count) opcode names from $np"
}

# ---- state -------------------------------------------------------------------
# One reassembly buffer per (direction, link). A pre-T56 log has one link, "-".
$buf        = @{}   # key -> [Collections.Generic.List[byte]]
$startChunk = @{}   # key -> chunk seq that began the frame in progress
$startIso   = @{}   # key -> iso of that chunk
$rejects    = New-Object 'System.Collections.Generic.List[string]'

$ctlOut = $null; $frmOut = $null
try {
    # UTF8Encoding($false) = no BOM. [Text.Encoding]::UTF8 emits one, and a BOM on
    # line 1 breaks every downstream parser that expects the listing to start with
    # a space-padded frame number.
    $utf8 = New-Object Text.UTF8Encoding($false)
    $ctlOut = New-Object IO.StreamWriter($Ctl, $false, $utf8)
    if ($wantSet.Count -gt 0) { $frmOut = New-Object IO.StreamWriter($Frames, $false, $utf8) }

    $reader   = New-Object IO.StreamReader($logPath)
    $hdr      = '^\[(\d+)\]\s+\[([^\]]+)\]\s+(\S+)\s+len=(\d+)\s*$'
    $chunks   = 0; $framesSeen = 0; $ctlLines = 0; $frmLines = 0
    $curSeq   = 0; $curDir = ''; $curIso = ''; $curLen = 0
    $hexBuf   = New-Object Text.StringBuilder
    $haveChunk = $false

    # Emit every complete frame sitting in one direction's buffer.
    $flush = {
        param($key, $dir, $linkId, $chunkSeq, $iso)
        $b = $buf[$key]
        while ($b.Count -ge 4) {
            $n = [BitConverter]::ToUInt32(($b.GetRange(0,4).ToArray()), 0)
            if ($n -lt 6 -or $n -gt $MaxFrame) {
                $rejects.Add(("chunk {0} [{1}] {2}: frame length {3} is outside 6..{4} - the stream is desynced, {5} buffered byte(s) dropped" -f `
                              $chunkSeq, $dir, $iso, $n, $MaxFrame, $b.Count))
                $b.Clear(); break
            }
            if ($b.Count -lt $n) { break }
            $frame = $b.GetRange(0, [int]$n).ToArray()
            $b.RemoveRange(0, [int]$n)
            $op = [BitConverter]::ToUInt16($frame, 4)
            $script:framesSeen++

            $label = if ($linkId -eq '-') { $dir } else { "$dir#$linkId" }
            $nm    = if ($nameOf.ContainsKey([int]$op)) { ' ' + $nameOf[[int]$op] } else { '' }

            if (-not $skipSet.Contains([int]$op)) {
                # The payload starts at 6. Never write $frame[6..$end] - PowerShell
                # REVERSES a descending range, so a 6-byte frame yields $frame[6],$frame[5]
                # and prints the opcode's high byte as if it were payload. That is the
                # stray "27" after "len=     6" in the original cap_relog9827_ctl.txt.
                $take = [Math]::Min($Bytes, $frame.Length - 6)
                $hex  = ''
                if ($take -gt 0) {
                    $sb = New-Object Text.StringBuilder
                    for ($i = 6; $i -lt 6 + $take; $i++) { [void]$sb.Append('{0:X2} ' -f $frame[$i]) }
                    $hex = $sb.ToString().TrimEnd()
                }
                $ctlOut.WriteLine(('{0,5} {1} {2} 0x{3:X4}{4} len={5,6} {6}' -f `
                    $chunkSeq, $label, $iso.Substring(11, 12), $op, $nm, $frame.Length, $hex).TrimEnd())
                $script:ctlLines++
            }

            if ($wantSet.Contains([int]$op)) {
                $sb = New-Object Text.StringBuilder
                foreach ($x in $frame) { [void]$sb.Append('{0:X2} ' -f $x) }
                # The outer parentheses are load-bearing: inside a method call the comma
                # is the ARGUMENT separator, so WriteLine('fmt' -f $a, $b) passes $b to
                # WriteLine and leaves -f with one argument.
                $frmOut.WriteLine(('=== {0} {1} {2} 0x{3:X4}{4} len={5}' -f $chunkSeq, $label, $iso, $op, $nm, $frame.Length))
                $frmOut.WriteLine($sb.ToString().TrimEnd())
                $script:frmLines++
            }
        }
    }

    # A chunk is a header line followed by hex lines, ended by the next header or EOF.
    $emit = {
        if (-not $haveChunk) { return }
        $hexText = $hexBuf.ToString()
        $data = [byte[]]::new(0)
        if ($hexText.Trim().Length -gt 0) {
            # [char[]] is load-bearing: with a string[] separator PowerShell binds the
            # Split(String, StringSplitOptions) overload instead and returns the whole
            # line as one token, which then fails in [Convert]::ToByte.
            $tok = $hexText.Split([char[]]@(' ', "`t", "`r", "`n"), [StringSplitOptions]::RemoveEmptyEntries)
            $data = [byte[]]::new($tok.Count)
            for ($i = 0; $i -lt $tok.Count; $i++) { $data[$i] = [Convert]::ToByte($tok[$i], 16) }
        }
        if ($data.Length -ne $curLen) {
            $rejects.Add(("chunk {0} [{1}] {2}: header says len={3} but {4} hex byte(s) follow - the log is truncated here" -f `
                          $curSeq, $curDir, $curIso, $curLen, $data.Length))
        }
        # $linkId, not $link: PowerShell variable names are case-insensitive, so a
        # local $link would BE the -Link parameter and the filter below would compare
        # the value against itself.
        $dir = $curDir; $linkId = '-'
        $h = $curDir.IndexOf('#')
        if ($h -ge 0) { $dir = $curDir.Substring(0, $h); $linkId = $curDir.Substring($h + 1) }
        if ($Link -gt 0 -and $linkId -ne "$Link") { return }

        $key = "$dir/$linkId"
        if (-not $buf.ContainsKey($key)) {
            $buf[$key] = New-Object 'System.Collections.Generic.List[byte]'
            $startChunk[$key] = $curSeq; $startIso[$key] = $curIso
        }
        if ($buf[$key].Count -eq 0) { $startChunk[$key] = $curSeq; $startIso[$key] = $curIso }
        $buf[$key].AddRange($data)

        # Frames completed by this chunk are attributed to the chunk that STARTED them.
        while ($true) {
            $before = $buf[$key].Count
            & $flush $key $dir $linkId $startChunk[$key] $startIso[$key]
            if ($buf[$key].Count -eq $before) { break }
            $startChunk[$key] = $curSeq; $startIso[$key] = $curIso
        }
        $script:chunks++
    }

    while ($null -ne ($line = $reader.ReadLine())) {
        if ($line -match $hdr) {
            & $emit
            $curSeq = [int]$Matches[1]; $curDir = $Matches[2]
            $curIso = $Matches[3];      $curLen = [int]$Matches[4]
            [void]$hexBuf.Clear(); $haveChunk = $true
        }
        elseif ($haveChunk -and $line.Trim().Length -gt 0) {
            [void]$hexBuf.Append($line); [void]$hexBuf.Append(' ')
        }
    }
    & $emit
    $reader.Close()

    foreach ($key in $buf.Keys) {
        if ($buf[$key].Count -gt 0) {
            $rejects.Add(("{0}: {1} trailing byte(s) never formed a complete frame - the capture stops mid-frame" -f `
                          $key, $buf[$key].Count))
        }
    }

    if ($chunks -eq 0) {
        Write-Warning "no chunk headers matched in $logPath - this does not look like an arbiter-world-tap.js log (a TeraSharp console log is UTF-16 text, not a tap)."
    }
}
finally {
    if ($ctlOut) { $ctlOut.Close() }
    if ($frmOut) { $frmOut.Close() }
}

Write-Host ("{0}: {1} chunk(s) -> {2} frame(s)" -f [IO.Path]::GetFileName($logPath), $chunks, $framesSeen)
Write-Host ("  {0}  {1} line(s), {2} frame(s) skipped as noise" -f $Ctl, $ctlLines, ($framesSeen - $ctlLines))
if ($wantSet.Count -gt 0) { Write-Host ("  {0}  {1} frame(s)" -f $Frames, $frmLines) }
if ($rejects.Count -gt 0) {
    Write-Warning "$($rejects.Count) reject(s):"
    foreach ($r in $rejects) { Write-Warning "  $r" }
} else {
    Write-Host '  no rejects - every byte in the log landed in a frame'
}
