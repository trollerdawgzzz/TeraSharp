<#
.SYNOPSIS
  Which client packets the real Arbiter handles, and which of them TeraSharp registers.

.DESCRIPTION
  Regenerates status\MISSING-HANDLERS.txt. T114 wrote this down because the file existed
  with no script beside it and its numbers could not be reproduced or explained.

  THE DENOMINATOR. Ghidra kept the Arbiter's trace guards, so every client handler in the
  binary leaves one of two strings in the decompile:

      "bool __cdecl Handler_C_XXX(void *,const unsigned char *,int)"
      "bool __cdecl making_Handler_C_XXX(void *,const unsigned char *,int)"

  The union of the two names is the packet set (273 on 100.02). It is a union and not just
  the first form because exactly one packet - C_REQUEST_CHANGE_PARTY_MATCH_RULE - has only
  a making_ guard, which is what T97 found when it went looking for a handler that is not
  there. There is no list of client opcodes anywhere else that is this complete:
  tera-server-proxy's data.json is a name->opcode map for a different protocol build and
  includes packets this binary does not handle.

  THE NUMERATOR. A packet is REGISTERED when its name appears as a C# string literal in
  HandlerRegistry.cs or in one of the six wiring tables the registry loops over. That is
  the whole of the registration surface - PacketDispatcher is only ever reached through it.
  Being named in a comment is not being registered, which is why this counts literals
  rather than mentions: the two differ by four packets today, and every one of those four
  is a constant or a comment explaining why the packet is NOT handled.

  Unregistered matters because of what the fallback does: PacketDispatcher forwards an
  unregistered client packet to World, and World answers
  "handler has not been implemented yet!!!" and drops it (status\CLIENT-REJECTS.md).

.PARAMETER Decompile
  Folder holding Arb_part_*.c. Default D:\v100\TERA_SERVER.100.

.PARAMETER Source
  The TeraSharp.Arbiter source folder. Default ..\src\TeraSharp.Arbiter beside this script.

.PARAMETER Out
  Where to write the list. Default ..\status\MISSING-HANDLERS.txt. Pass "-" for stdout only.

.EXAMPLE
  .\tools\count-handlers.ps1
  .\tools\count-handlers.ps1 -Out -
#>
[CmdletBinding()]
param(
    [string]$Decompile = 'D:\v100\TERA_SERVER.100',
    [string]$Source,
    [string]$Out
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSCommandPath        # tools\
$repo = Split-Path -Parent $root
if (-not $Source) { $Source = Join-Path $repo 'src\TeraSharp.Arbiter' }
if (-not $Out)    { $Out    = Join-Path $repo 'status\MISSING-HANDLERS.txt' }

if (-not (Test-Path -LiteralPath $Decompile)) { throw "no decompile folder at $Decompile" }
if (-not (Test-Path -LiteralPath $Source))    { throw "no source folder at $Source" }

# ---- the denominator ---------------------------------------------------------------
$parts = Get-ChildItem -LiteralPath $Decompile -Filter 'Arb_part_*.c' -File
if ($parts.Count -eq 0) { throw "no Arb_part_*.c under $Decompile" }

$names = [System.Collections.Generic.HashSet[string]]::new()
$guard = [regex]'"bool __cdecl (?:making_)?Handler_(C_[A-Z0-9_]+)\('
foreach ($p in $parts) {
    foreach ($m in $guard.Matches([IO.File]::ReadAllText($p.FullName))) {
        [void]$names.Add($m.Groups[1].Value)
    }
}
$all = $names | Sort-Object

# ---- the numerator -----------------------------------------------------------------
# HandlerRegistry plus the six tables it loops over. Adding a seventh wiring table means
# adding it here, or its packets start reporting as unregistered.
$registrars = @(
    'Handlers\HandlerRegistry.cs'
    'World\PartyWiring.cs'
    'World\GuildWiring.cs'
    'Handlers\BrokerHandlers.cs'
    'World\PartyMatchManager.cs'
    'World\GuildWarManager.cs'
    'World\ChatManager.cs'
)
$sb = [Text.StringBuilder]::new()
foreach ($r in $registrars) {
    $f = Join-Path $Source $r
    if (-not (Test-Path -LiteralPath $f)) { Write-Warning "registrar missing: $r" ; continue }
    [void]$sb.AppendLine([IO.File]::ReadAllText($f))
}
$reg = $sb.ToString()

$missing = @($all | Where-Object { -not $reg.Contains('"' + $_ + '"') })

# ---- report ------------------------------------------------------------------------
$header = "{0} client handlers in the Arbiter decompile, {1} not registered" -f $all.Count, $missing.Count
Write-Host $header
Write-Host ("registered: {0}" -f ($all.Count - $missing.Count))

if ($Out -ne '-') {
    $lines = @(
        $header
        "# regenerate: .\tools\count-handlers.ps1   (the script explains both numbers)"
        "# denominator: Handler_C_* / making_Handler_C_* trace guards in Arb_part_*.c"
        "# registered: the name appears as a string literal in HandlerRegistry.cs or a wiring table"
        "# unregistered: PacketDispatcher forwards it to World -> 'handler has not been implemented yet!!!'"
        "# generated {0:yyyy-MM-dd}" -f (Get-Date)
    ) + $missing
    # LF-free: the repo stores status\*.txt as CRLF like everything else.
    [IO.File]::WriteAllText($Out, ($lines -join "`r`n") + "`r`n")
    Write-Host "wrote $Out"
} else {
    $missing | ForEach-Object { Write-Output $_ }
}
