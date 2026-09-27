# SPDX-License-Identifier: MIT
# Copyright (c) 2026 the TeraSharp contributors

<#
.SYNOPSIS
    T177 - helpers shared by dupe-battlefield.ps1 and dupe-dungeon.ps1. Dot-source only.

.DESCRIPTION
    Text-level, not DOM: a sheet is read as bytes, its BOM kept, and only the spans an edit names
    are touched, so comments, attribute order and line endings survive a round trip. Every
    inserted row is fenced by a marker pair and every created file carries a marker line, which
    is what -Revert keys on - nothing is restored from a backup over a newer edit.

      <!-- dupe:<kind>:<newId> begin --> ... <!-- dupe:<kind>:<newId> end -->
      <!-- dupe:<kind>:<newId> created from <source file> (T177) -->

    Comments are masked (same length, contents blanked) before any element is searched, so a
    commented-out row such as DungeonMatching's <!--Dungeon id="9981" ...> is never the match.
#>

Set-StrictMode -Version 2.0

$script:DupeUtf8 = New-Object System.Text.UTF8Encoding($false)
$script:DupeSingle = [System.Text.RegularExpressions.RegexOptions]::Singleline

function Read-DupeSheet([string] $Path) {
    $full = (Resolve-Path -LiteralPath $Path).ProviderPath
    $bytes = [IO.File]::ReadAllBytes($full)
    $bom = 'none'; $enc = $script:DupeUtf8; $skip = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) { $bom = 'utf8'; $skip = 3 }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $bom = 'utf16'; $enc = New-Object System.Text.UnicodeEncoding($false, $false); $skip = 2
    }
    $text = $enc.GetString($bytes, $skip, $bytes.Length - $skip)
    $nl = "`n"; if ($text.Contains("`r`n")) { $nl = "`r`n" }
    [pscustomobject]@{ Path = $full; Name = [IO.Path]::GetFileName($full); Text = $text; Bom = $bom
                       Encoding = $enc; NewLine = $nl; Changed = $false }
}

function Get-DupeBytes($Sheet) {
    $pre = [byte[]]@()
    if ($Sheet.Bom -eq 'utf8')  { $pre = [byte[]](0xEF, 0xBB, 0xBF) }
    if ($Sheet.Bom -eq 'utf16') { $pre = [byte[]](0xFF, 0xFE) }
    $body = $Sheet.Encoding.GetBytes($Sheet.Text)
    $all = New-Object byte[] ($pre.Length + $body.Length)
    [Array]::Copy($pre, 0, $all, 0, $pre.Length)
    [Array]::Copy($body, 0, $all, $pre.Length, $body.Length)
    return ,$all
}

function Write-DupeSheet($Sheet, [string] $Path) {
    if (-not $Path) { $Path = $Sheet.Path }
    [IO.File]::WriteAllBytes($Path, (Get-DupeBytes $Sheet))
}

# Same length as the input; comment bodies become spaces, so indexes line up with the original.
function Get-DupeMasked([string] $Text) {
    $eval = [System.Text.RegularExpressions.MatchEvaluator] {
        param($m) '<!--' + (' ' * ($m.Length - 7)) + '-->'
    }
    [regex]::Replace($Text, '<!--.*?-->', $eval, $script:DupeSingle)
}

# First <Tag ... Attr="Value" ...> outside comments -> @{ Start; End } (End exclusive, after the
# matching close tag or the '/>'). $null when absent.
function Find-DupeElement([string] $Text, [string] $Tag, [string] $Attr, [string] $Value, [int] $From = 0) {
    $masked = Get-DupeMasked $Text
    $open = New-Object regex ('<' + $Tag + '\b[^>]*?\s' + $Attr + '\s*=\s*"' + [regex]::Escape($Value) + '"[^>]*?(/?)>'), $script:DupeSingle
    $m = $open.Match($masked, $From)
    if (-not $m.Success) { return $null }
    if ($m.Groups[1].Value -eq '/') { return @{ Start = $m.Index; End = $m.Index + $m.Length } }
    $any = New-Object regex ('<(/?)' + $Tag + '\b[^>]*?(/?)>'), $script:DupeSingle
    $depth = 1; $pos = $m.Index + $m.Length
    while ($depth -gt 0) {
        $t = $any.Match($masked, $pos)
        if (-not $t.Success) { throw ('unclosed <{0} {1}="{2}">' -f $Tag, $Attr, $Value) }
        if ($t.Groups[1].Value -eq '/') { $depth-- } elseif ($t.Groups[2].Value -ne '/') { $depth++ }
        $pos = $t.Index + $t.Length
    }
    @{ Start = $m.Index; End = $pos }
}

# Every value of Attr on <Tag> elements outside comments.
function Get-DupeAttrValues([string] $Text, [string] $Tag, [string] $Attr) {
    $masked = Get-DupeMasked $Text
    $rx = New-Object regex ('<' + $Tag + '\b[^>]*?\s' + $Attr + '\s*=\s*"([^"]*)"'), $script:DupeSingle
    foreach ($m in $rx.Matches($masked)) { $m.Groups[1].Value }
}

function Get-DupeLineNo([string] $Text, [int] $Index) {
    ([regex]::Matches($Text.Substring(0, $Index), "`n")).Count + 1
}

# Set Attr on the first <Tag> start tag of a fragment (outside comments). Adds it when missing.
# Returns @{ Text; Old } - Old is $null when the attribute was added.
function Set-DupeAttr([string] $Frag, [string] $Tag, [string] $Attr, [string] $Value) {
    $masked = Get-DupeMasked $Frag
    $st = (New-Object regex ('<' + $Tag + '\b[^>]*?>'), $script:DupeSingle).Match($masked)
    if (-not $st.Success) { throw ('no <{0}> in fragment' -f $Tag) }
    $head = $Frag.Substring($st.Index, $st.Length)
    $arx = New-Object regex ('(\s' + $Attr + '\s*=\s*")([^"]*)(")')
    $am = $arx.Match($head)
    if ($am.Success) {
        $old = $am.Groups[2].Value
        $newHead = $head.Substring(0, $am.Groups[2].Index) + $Value + $head.Substring($am.Groups[2].Index + $am.Groups[2].Length)
    } else {
        $old = $null
        $cut = $head.Length - 1; if ($head.EndsWith('/>')) { $cut = $head.Length - 2 }
        $newHead = $head.Substring(0, $cut).TrimEnd() + (' {0}="{1}"' -f $Attr, $Value) + $head.Substring($cut)
    }
    @{ Text = $Frag.Substring(0, $st.Index) + $newHead + $Frag.Substring($st.Index + $st.Length); Old = $old }
}

function Get-DupeAttr([string] $Frag, [string] $Tag, [string] $Attr) {
    $masked = Get-DupeMasked $Frag
    $st = (New-Object regex ('<' + $Tag + '\b[^>]*?>'), $script:DupeSingle).Match($masked)
    if (-not $st.Success) { return $null }
    $am = (New-Object regex ('\s' + $Attr + '\s*=\s*"([^"]*)"')).Match($Frag.Substring($st.Index, $st.Length))
    if ($am.Success) { return $am.Groups[1].Value }
    return $null
}

function Get-DupeMarker([string] $Kind, [int] $NewId, [string] $Part) { '<!-- dupe:{0}:{1} {2} -->' -f $Kind, $NewId, $Part }

# Insert Clone as a fenced block after the element at Span. Placed at the end of that element's
# line so a trailing same-line comment stays with its row. Returns the new text.
function Add-DupeBlock($Sheet, $Span, [string] $Clone, [string] $Kind, [int] $NewId) {
    $t = $Sheet.Text; $nl = $Sheet.NewLine
    $lineStart = $t.LastIndexOf("`n", [Math]::Max(0, $Span.Start - 1)) + 1
    if ($Span.Start -eq 0) { $lineStart = 0 }
    $indent = [regex]::Match($t.Substring($lineStart, $Span.Start - $lineStart), '^[ \t]*').Value
    $at = $Span.End
    $eol = $t.IndexOf("`n", $at); if ($eol -lt 0) { $eol = $t.Length }
    $restEnd = $eol; if ($restEnd -gt $at -and $t[$restEnd - 1] -eq "`r") { $restEnd-- }
    $rest = (Get-DupeMasked $t.Substring($at, $restEnd - $at))
    if ($rest -match '^\s*(<!--\s*-->\s*)*$') { $at = $restEnd }
    $block = $nl + $indent + (Get-DupeMarker $Kind $NewId 'begin') + $nl + $indent + $Clone + $nl + $indent + (Get-DupeMarker $Kind $NewId 'end')
    $Sheet.Text = $t.Substring(0, $at) + $block + $t.Substring($at)
    $Sheet.Changed = $true
}

# Remove every fenced block of Kind/NewId. Returns how many were removed.
function Remove-DupeBlocks($Sheet, [string] $Kind, [int] $NewId) {
    $rx = New-Object regex ('\r?\n[ \t]*' + [regex]::Escape((Get-DupeMarker $Kind $NewId 'begin')) + '.*?' + [regex]::Escape((Get-DupeMarker $Kind $NewId 'end'))), $script:DupeSingle
    $n = $rx.Matches($Sheet.Text).Count
    if ($n -gt 0) { $Sheet.Text = $rx.Replace($Sheet.Text, ''); $Sheet.Changed = $true }
    return $n
}

# Marker line for a created file, placed after the XML declaration.
function Add-DupeFileMarker([string] $Text, [string] $Kind, [int] $NewId, [string] $From, [string] $Nl) {
    $line = Get-DupeMarker $Kind $NewId ('created from ' + $From + ' (T177)')
    $decl = [regex]::Match($Text, '^\s*<\?xml[^>]*\?>')
    if ($decl.Success) { return $Text.Substring(0, $decl.Length) + $Nl + $line + $Text.Substring($decl.Length) }
    return $line + $Nl + $Text
}

function Test-DupeFileMarker([string] $Path, [string] $Kind, [int] $NewId) {
    # Only the head is read: -Revert runs this over every file in the Datasheet tree.
    $fs = [IO.File]::OpenRead($Path)
    try { $buf = New-Object byte[] 1024; $n = $fs.Read($buf, 0, $buf.Length) } finally { $fs.Dispose() }
    $enc = $script:DupeUtf8; $skip = 0
    if ($n -ge 3 -and $buf[0] -eq 0xEF -and $buf[1] -eq 0xBB -and $buf[2] -eq 0xBF) { $skip = 3 }
    elseif ($n -ge 2 -and $buf[0] -eq 0xFF -and $buf[1] -eq 0xFE) { $enc = New-Object System.Text.UnicodeEncoding($false, $false); $skip = 2 }
    $head = $enc.GetString($buf, $skip, [Math]::Max(0, $n - $skip))
    return $head.Contains(('<!-- dupe:{0}:{1} created from ' -f $Kind, $NewId))
}

# Copy the originals of every sheet about to change to <Datasheet>\..\dupe-backup\<stamp>\ -
# outside Datasheet\, because the servers walk that tree recursively and would load a backup.
function Backup-DupeSheets([string] $Datasheet, [string[]] $Paths, [string] $Tag) {
    $root = Join-Path (Split-Path -Parent $Datasheet) 'dupe-backup'
    $dir = Join-Path $root ('{0}-{1}' -f $Tag, (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    foreach ($p in $Paths) { Copy-Item -LiteralPath $p -Destination $dir }
    return $dir
}

# Fit a cloned <Role>'s class limits to a new totalUser (already set). MatchServer checks each
# class minimum against totalUser; a group can only form when the minimums sum to at most N and
# the maximums to at least N, so dealer (then healer, then tanker) minimums are lowered and the
# dealer maximum raised as needed. Empty attributes mean "no limit" and stay empty.
function Resize-DupeRole([string] $Frag, [int] $N) {
    $notes = @()
    $val = @{}
    foreach ($a in @('tankerMin', 'tankerMax', 'healerMin', 'healerMax', 'dealerMin', 'dealerMax')) {
        $v = Get-DupeAttr $Frag 'RoleData' $a
        if ($v -ne $null -and $v -ne '') { $val[$a] = [int]$v }
    }
    foreach ($a in @($val.Keys)) {
        if ($val[$a] -gt $N) { $notes += ('{0} {1} -> {2}' -f $a, $val[$a], $N); $val[$a] = $N }
    }
    $sumMin = 0; foreach ($a in @('tankerMin', 'healerMin', 'dealerMin')) { if ($val.ContainsKey($a)) { $sumMin += $val[$a] } }
    foreach ($a in @('dealerMin', 'healerMin', 'tankerMin')) {
        while ($sumMin -gt $N -and $val.ContainsKey($a) -and $val[$a] -gt 0) {
            $val[$a]--; $sumMin--; $notes += ('{0} lowered to {1}' -f $a, $val[$a])
        }
    }
    if ($val.ContainsKey('tankerMax') -and $val.ContainsKey('healerMax') -and $val.ContainsKey('dealerMax')) {
        $sumMax = $val['tankerMax'] + $val['healerMax'] + $val['dealerMax']
        if ($sumMax -lt $N) { $val['dealerMax'] += ($N - $sumMax); $notes += ('dealerMax raised to {0}' -f $val['dealerMax']) }
    }
    foreach ($a in @($val.Keys)) {
        $cur = Get-DupeAttr $Frag 'RoleData' $a
        if ([string]$val[$a] -ne $cur) { $Frag = (Set-DupeAttr $Frag 'RoleData' $a ([string]$val[$a])).Text }
    }
    @{ Text = $Frag; Notes = $notes }
}

function Write-DupePlan([string] $Verb, [string] $File, [string] $What) {
    Write-Host ('  {0,-7} {1,-34} {2}' -f $Verb, $File, $What)
}
