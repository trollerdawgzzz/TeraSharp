#Requires -Version 5.1
<#
.SYNOPSIS
    Fail the build if anything that must not be published is in the tree.

.DESCRIPTION
    Run this before every push to a public remote, and from CI. It exits non-zero when it
    finds any of:

      RETAIL      a file whose SHA-256 is one of the known retail-derived blobs, under any
                  name, plus anything that looks like a datasheet, a decompile or a capture
      PII         a ranking/leaderboard capture, which is a list of other people's character
                  names by construction
      SECRET      a credential assigned to a literal in source, or a private key / .env file
      ADDRESS     a routable public IPv4. Loopback, RFC1918, 0.0.0.0 and the RFC 5737
                  documentation ranges (192.0.2, 198.51.100, 203.0.113) are fine - those are
                  what examples are supposed to use
      JUNK        build output, databases, logs, archives, .bak/.orig strays

    It also reports, without failing:

      REVIEW      a line of verbatim decompiler output - pasted Ghidra variables rather than
                  a description of what the code does. Citing an address in prose is fine and
                  is not reported; two or more decompiler identifiers on one line is a paste.
                  -Strict makes these fail too.

    Findings print as "<severity>  <file>:<line>  <what>". Nothing is modified.

.PARAMETER Path
    Repository root. Defaults to the parent of tools\.

.PARAMETER Tracked
    Audit only what git tracks (git ls-files) instead of walking the working tree. Use this in
    CI; use the default locally, where an untracked file is still one commit away from being a
    tracked one.

.PARAMETER AllowAddress
    Public IPv4 addresses that are allowed to appear, e.g. one quoted from a decompile in a
    comment. Each must be justified in the commit that adds it.

.PARAMETER Quiet
    Print the summary only.

.PARAMETER Strict
    Make REVIEW findings fail as well.

.EXAMPLE
    .\tools\audit-release.ps1

.EXAMPLE
    .\tools\audit-release.ps1 -Tracked -AllowAddress 203.0.113.9
#>
[CmdletBinding()]
param(
    [string]   $Path = (Split-Path -Parent $PSScriptRoot),
    [switch]   $Tracked,
    [string[]] $AllowAddress = @(),
    [switch]   $Strict,
    [switch]   $Quiet
)

$ErrorActionPreference = 'Stop'
$Path = [IO.Path]::GetFullPath($Path)
if (-not (Test-Path -LiteralPath $Path)) { throw "no such folder: $Path" }

# ============================================================== known retail material
# SHA-256 of every capture-derived blob that has ever been in this repository. A rename does
# not get one of these past the check. Add a line when a new one is identified; never remove
# one.
$RetailHashes = @{
    '7f0eb43fb285afcc35f5e94d9af6618146e218d770cb93f2eb67c395d4839e9a' = 'cap_client_settings.bin (captured client settings frames)'
    '3ff269a7022ee4b17e191377cde12e804a498e2c3378d6a7cdea2d01a5b59611' = 'cap_guild.bin (captured guild frames)'
    'f93ef6f511d7ef749ab6dbb16c38c55c1709480e05c1738ac319cf32da127914' = 'cap_item_single.bin (captured DB-proxy frames)'
    'ad9c45fa31ad73e1aa6c8ddc405be7b321b0eb07754b53fc2a2fc3be3bdd5698' = 'cap_t15.bin (captured DB-proxy frames)'
    '61211a2e71eab8ebe0659d766cfdc3c3b4cbe88125f9ca30a605d95fe48b6740' = 'cap_t22_newchar.bin (captured character-creation frames)'
    'fb7def5fd410418e4561bd60da28f83e1bff07e30031d80d67daddcd2d1dd3bc' = 'cap_t22_relog.bin (captured relog frames)'
    'df641d62c9ee66833391a5ded0cd2d7cd48bc696189adde892fbce01a65fdfb9' = 'cap_t26.bin (captured reputation frames)'
    'b6cd4d45dadefef453f155572f81b1c3fc08eec9846c397ff08a5488316054b8' = 'cap_t38.bin (captured tunnel frames)'
    '536071928c6b5e904b281b2e689a279d7bece3c89b1c1ba55fc259ad98e56a82' = 'cap_timeline.bin (captured timeline frames)'
    'f8b8d15213d52b542a9b667aadf7a0a95bc70b5ad5976c97e0dd321850756b61' = 'handshake_burst.bin (captured handshake burst)'
    'bfd37e6f370f800a65501b44c79cd761df38da63cb8254e829936e21d4f0725b' = 'promotions_147E.bin (captured promotion records)'
    '5110b07e0497125ee69a12c37022f338c70ae609af874fc957ec337d71583ebb' = 'starter_blob.bin (captured world blob)'
    'd83ba62cd6714f10387b7707ba41c19a443039349a1a2390aa9a3d8fe8007606' = 'starter_inventory.bin (captured starter kit payload)'
    '1947240df3a41fb590c045482ef4e74321ebb327f82102e50fd28fd252686265' = 't93_admin_inven_frame724.bin (captured inventory frame)'
    'f3b54513af8eaf640fd8e1d99f031c200e4737162ea6cfc44ccfb1d440a40cfe' = 'S_PVE_LEADER_BOARD_INFO (live capture)'
    'd5a78003da9947de0d0f169fc6cec1c48245f36895e371e4d8bf0157d7c2699a' = 'S_PVE_RANKING_LIST (live capture, third-party character names)'
    '1a46e57b10e937af35d1c6347e71d13133c9b0c7c872639a7da72cbe298d866f' = 'S_PVE_RANKING_LIST class 16 (live capture, third-party character names)'
    'f88c9575b7c8becf44fef03c699f29a7c18553aa1f3d54af16bb20eb388b06aa' = 'S_PVP_LEADER_BOARD_INFO (live capture)'
    '8daa025118f1745de984b2b3ee975e0bed665bc3407075220d36f1d0ebdf78ad' = 'S_PVP_RANKING_LIST (live capture, third-party character names)'
}

# Name patterns. A file matching one of these is refused whatever its contents.
$BannedNames = @(
    @{ Sev = 'RETAIL'; Rx = '\.(bin|hex)$';                      Why = 'captured packet bytes - data/ is generated locally, see data/README.md' },
    @{ Sev = 'RETAIL'; Rx = '\.(npcap|pcap|pcapng|cap)$';        Why = 'raw capture' },
    @{ Sev = 'RETAIL'; Rx = '(?i)^Arb_part_.*\.c$';              Why = 'decompiled source' },
    @{ Sev = 'RETAIL'; Rx = '(?i)(ArbiterServer|WorldServer)\.exe'; Why = 'retail binary or its decompile' },
    @{ Sev = 'RETAIL'; Rx = '(?i)^StrSheet_.*\.xml$';            Why = 'client datasheet' },
    @{ Sev = 'RETAIL'; Rx = '(?i)[\\/](Datasheet|DatasheetAside|tera_v100_MASTER_FINAL|tera-server-proxy)[\\/]'; Why = 'retail data tree' },
    @{ Sev = 'RETAIL'; Rx = '(?i)\.def$';                        Why = 'client packet definition' },
    @{ Sev = 'SECRET'; Rx = '(?i)(^|[\\/])\.env$';               Why = 'real environment file - only .env.example is tracked' },
    @{ Sev = 'SECRET'; Rx = '(?i)\.(pfx|p12|pem|key)$|(^|[\\/])id_rsa'; Why = 'private key material' },
    @{ Sev = 'SECRET'; Rx = '(?i)(^|[\\/])deploy\.ps1$';         Why = 'real deploy script - only deploy.example.ps1 is tracked' },
    @{ Sev = 'JUNK';   Rx = '(?i)[\\/](bin|obj)[\\/]';           Why = 'build output' },
    @{ Sev = 'JUNK';   Rx = '(?i)\.(db|db-wal|db-shm|sqlite3?)$';Why = 'database' },
    @{ Sev = 'JUNK';   Rx = '(?i)\.log$';                        Why = 'log' },
    @{ Sev = 'JUNK';   Rx = '(?i)\.(7z|zip|rar)$';               Why = 'archive' },
    @{ Sev = 'JUNK';   Rx = '(?i)\.(bak|orig|rej|tmp)$';         Why = 'stray' }
)

# Content patterns, checked line by line in text files.
$BannedContent = @(
    @{ Sev = 'SECRET'; Rx = '(?i)\b(password|passwd|pwd|api[_-]?key|client[_-]?secret|connectionstring)\s*[:=]\s*["''][^"'']{4,}["'']'; Why = 'credential assigned to a literal' },
    @{ Sev = 'SECRET'; Rx = '(?i)\bsecret\s*=\s*["''][^"'']{8,}["'']';  Why = 'secret assigned to a literal' },
    @{ Sev = 'SECRET'; Rx = '-----BEGIN [A-Z ]*PRIVATE KEY-----';       Why = 'inline private key' },
    @{ Sev = 'SECRET'; Rx = '\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.'; Why = 'JWT' },
    # Two or more decompiler-only identifiers on one line. A citation in prose - "the writer
    # FUN_140350eb0 (Arb_part_017.c:9283)" - has none of these and is fine; pasted Ghidra
    # output is full of them. One alone is not enough: it is how a field gets named.
    @{ Sev = 'REVIEW'; Rx = '(?:\b(?:param_\d|[a-z]{1,3}Var\d+|local_[0-9a-f]{2,}|DAT_[0-9a-f]{6,}|auStack_[0-9a-f]+)\b.*){2,}'; Why = 'verbatim decompiler output - describe the behaviour, do not paste the code' },
    @{ Sev = 'REVIEW'; Rx = '^\s*(undefined[248]?|ulonglong|longlong)\s+\**[a-zA-Z_]\w*\s*[;=)]'; Why = 'verbatim decompiler output' }
)

# Exceptions: files whose whole job is to name the things above.
$SelfReferential = @('tools/audit-release.ps1', '.gitignore', 'data/README.md', 'data/classic-live/README.md')

# ============================================================== collect the file list
$files = @()
if ($Tracked) {
    Push-Location $Path
    try {
        $listed = @(& git ls-files 2>$null)
        if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed - is this a git checkout?' }
        foreach ($rel in $listed) {
            $full = Join-Path $Path $rel
            if (Test-Path -LiteralPath $full -PathType Leaf) { $files += (Get-Item -LiteralPath $full) }
        }
    } finally { Pop-Location }
} else {
    $files = @(Get-ChildItem -LiteralPath $Path -Recurse -File -Force -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -notmatch '(?i)[\\/]\.git[\\/]' })
}

$textExt = @('.cs','.md','.txt','.ps1','.psm1','.js','.ts','.json','.xml','.yml','.yaml','.csproj','.sln','.props','.targets','.diff','.patch','.example','.gitignore','.gitattributes','.editorconfig','.env')

$ipRx    = [regex] '(?<![\w.])((?:\d{1,3}\.){3}\d{1,3})(?![\w.])'
$findings = New-Object 'System.Collections.Generic.List[object]'
function Add-Finding {
    param([string] $Sev, [string] $File, [int] $Line, [string] $What)
    $findings.Add([pscustomobject]@{ Sev = $Sev; File = $File; Line = $Line; What = $What })
}

function Test-DocAddress {
    param([string] $Ip)
    $p = $Ip.Split('.')
    if ($p.Count -ne 4) { return $true }
    $n = @(0,0,0,0)
    for ($i = 0; $i -lt 4; $i++) {
        $v = 0
        if (-not [int]::TryParse($p[$i], [ref] $v)) { return $true }
        if ($v -lt 0 -or $v -gt 255) { return $true }
        $n[$i] = $v
    }
    if ($n[0] -eq 0 -or $n[0] -eq 10 -or $n[0] -eq 127 -or $n[0] -ge 224) { return $true }
    if ($n[0] -eq 172 -and $n[1] -ge 16 -and $n[1] -le 31)                { return $true }
    if ($n[0] -eq 192 -and $n[1] -eq 168)                                  { return $true }
    if ($n[0] -eq 169 -and $n[1] -eq 254)                                  { return $true }
    if ($n[0] -eq 192 -and $n[1] -eq 0   -and $n[2] -eq 2)                 { return $true }   # RFC 5737
    if ($n[0] -eq 198 -and $n[1] -eq 51  -and $n[2] -eq 100)               { return $true }
    if ($n[0] -eq 203 -and $n[1] -eq 0   -and $n[2] -eq 113)               { return $true }
    if ($n[0] -eq 100 -and $n[1] -ge 64  -and $n[1] -le 127)               { return $true }   # CGNAT
    if ($n[0] -eq 255)                                                     { return $true }
    return $false
}

$allow = @{}
foreach ($a in $AllowAddress) { $allow[$a] = $true }

# ============================================================== scan
foreach ($f in $files) {
    $rel = $f.FullName.Substring($Path.Length).TrimStart('\', '/').Replace('\', '/')

    foreach ($b in $BannedNames) {
        if ($rel -match $b.Rx) { Add-Finding $b.Sev $rel 0 $b.Why }
    }

    $hash = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($RetailHashes.ContainsKey($hash)) {
        $sev = 'RETAIL'
        if ($RetailHashes[$hash] -match 'character names') { $sev = 'PII' }
        Add-Finding $sev $rel 0 ("known retail blob: " + $RetailHashes[$hash])
    }

    if ($SelfReferential -contains $rel) { continue }
    if ($textExt -notcontains $f.Extension.ToLowerInvariant() -and $f.Name -notmatch '^\.') { continue }
    if ($f.Length -gt 8MB) { continue }

    $lineNo = 0
    foreach ($line in [IO.File]::ReadLines($f.FullName)) {
        $lineNo++
        foreach ($c in $BannedContent) {
            if ($line -match $c.Rx) { Add-Finding $c.Sev $rel $lineNo $c.Why }
        }
        foreach ($m in $ipRx.Matches($line)) {
            $ip = $m.Groups[1].Value
            if ($allow.ContainsKey($ip)) { continue }
            if (Test-DocAddress $ip) { continue }
            Add-Finding 'ADDRESS' $rel $lineNo ("public IPv4 " + $ip + " - use an RFC 5737 documentation address, or pass -AllowAddress")
        }
    }
}

# ============================================================== report
$order = @{ 'RETAIL' = 0; 'PII' = 1; 'SECRET' = 2; 'ADDRESS' = 3; 'JUNK' = 4; 'REVIEW' = 5 }
$fatalSeverities = @('RETAIL', 'PII', 'SECRET', 'ADDRESS', 'JUNK')
if ($Strict) { $fatalSeverities += 'REVIEW' }
$sorted = @($findings | Sort-Object @{ Expression = { $order[$_.Sev] } }, File, Line)

if (-not $Quiet) {
    foreach ($x in $sorted) {
        $where = $x.File
        if ($x.Line -gt 0) { $where = $where + ':' + $x.Line }
        Write-Host ("  {0,-7} {1}" -f $x.Sev, $where) -NoNewline
        Write-Host ("  " + $x.What) -ForegroundColor DarkGray
    }
    if ($sorted.Count -gt 0) { Write-Host '' }
}

$counts = @{}
foreach ($x in $sorted) {
    if (-not $counts.ContainsKey($x.Sev)) { $counts[$x.Sev] = 0 }
    $counts[$x.Sev] = $counts[$x.Sev] + 1
}

$fatal = @($sorted | Where-Object { $fatalSeverities -contains $_.Sev })

Write-Host ("audit-release: {0} file(s) scanned under {1}" -f $files.Count, $Path)
foreach ($k in 'RETAIL', 'PII', 'SECRET', 'ADDRESS', 'JUNK', 'REVIEW') {
    if (-not $counts.ContainsKey($k)) { continue }
    $colour = 'Red'
    if ($fatalSeverities -notcontains $k) { $colour = 'Yellow' }
    Write-Host ("  {0,-7} {1}" -f $k, $counts[$k]) -ForegroundColor $colour
}
if ($fatal.Count -eq 0) {
    if ($sorted.Count -eq 0) { Write-Host '  clean' -ForegroundColor Green }
    else { Write-Host ("  no blocking finding; {0} to review (-Strict makes them fail)" -f $sorted.Count) -ForegroundColor Green }
    exit 0
}
Write-Host ("  {0} blocking finding(s) - not releasable" -f $fatal.Count) -ForegroundColor Red
exit 1
