#Requires -Version 5.1
<#
.SYNOPSIS
    Default-deny inbound firewall for the TeraSharp box. Public: 81 and 7801 only.

.DESCRIPTION
    Replaces the DENYLIST in !Firewall_Lockdown.bat with an ALLOWLIST.

    The .bat blocks a hand-written list of 26 ports. That is fine until something
    starts listening on a port nobody thought of - a new tera-api service, a debug
    listener, a future TeraSharp port - and then it is public and nothing complains.
    This script sets the profile default to Block instead, so an unlisted port is
    closed because it was never opened, not because someone remembered it.

    Loopback is never filtered by Windows Firewall, so "everything else on
    localhost" costs nothing extra: the proxy still reaches the Arbiter on
    127.0.0.1:7701, World still reaches 127.0.0.1:7802, and the admin web on
    127.0.0.1:8050 stays private.

    READ THIS BEFORE RUNNING IT ON A REMOTE BOX
    Default-deny closes RDP too. You will lock yourself out unless you pass
    -AdminIp (recommended) or -AllowRdpFromAnywhere (worse, but not a lockout).
    The script refuses to run without one of them.

.PARAMETER AdminIp
    Your own address or range, e.g. 203.0.113.9 or 203.0.113.0/24. RDP (3389) is
    opened to these only. Several are allowed.

.PARAMETER AllowRdpFromAnywhere
    Open 3389 to the internet. Only if you have no static address. Pair it with a
    long password and NLA, and move to a VPN when you can.

.PARAMETER RdpPort
    If you moved RDP off 3389, say so.

.PARAMETER ExtraTcp
    More public TCP ports, e.g. 80,443 when you put nginx in front of the portal.

.PARAMETER Rollback
    Put the inbound default back to Allow and remove the rules this script added.

.EXAMPLE
    .\harden-netcup.ps1 -AdminIp 203.0.113.9 -WhatIf
    Show every change without making one. Do this first.

.EXAMPLE
    .\harden-netcup.ps1 -AdminIp 203.0.113.9 -ExtraTcp 80,443

.NOTES
    Run elevated, on the server. Verify from OUTSIDE afterwards:
      nmap -Pn -p 81,3389,7701,7801,7802,8050,1433,3306 YOUR_PUBLIC_IP
    81 and 7801 open, 3389 open only from your address, everything else filtered.
    If 7701 answers, STOP: C_ADMIN is unauthenticated at the Arbiter and the proxy
    is the only GM gate (see !SECURITY_TODO_before_going_public.txt).
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'High')]
param(
    [string[]] $AdminIp = @(),
    [switch]   $AllowRdpFromAnywhere,
    [int]      $RdpPort = 3389,
    [int[]]    $ExtraTcp = @(),
    [switch]   $Rollback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$RuleTag     = 'TeraSharp-GoLive'
$StateRoot   = if ($env:ProgramData) { Join-Path $env:ProgramData 'TeraSharp' } else { $PSScriptRoot }
$StateFile   = Join-Path $StateRoot 'firewall-before-harden.json'
$PublicTcp   = @(81, 7801)

function Assert-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object Security.Principal.WindowsPrincipal($id)
    if (-not $pr.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Not elevated. Start PowerShell as Administrator and run this again.'
    }
}

function Get-FirewallCmdlets {
    if (-not (Get-Command Set-NetFirewallProfile -ErrorAction SilentlyContinue)) {
        throw 'The NetSecurity module is missing. Use netsh, or run this on a supported Windows Server build.'
    }
}

function Remove-TaggedRules {
    Get-NetFirewallRule -ErrorAction SilentlyContinue |
        Where-Object { $_.DisplayName -like "$RuleTag*" } |
        ForEach-Object {
            if ($PSCmdlet.ShouldProcess($_.DisplayName, 'Remove firewall rule')) {
                Remove-NetFirewallRule -Name $_.Name
            }
        }
}

function Save-PreviousState {
    $profiles = Get-NetFirewallProfile -All |
        Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction
    $dir = Split-Path $StateFile -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    if (Test-Path $StateFile) {
        Write-Host "  [=] keeping the existing snapshot at $StateFile"
        return
    }
    if ($PSCmdlet.ShouldProcess($StateFile, 'Save the current profile defaults')) {
        $profiles | ConvertTo-Json -Depth 4 | Set-Content -Path $StateFile -Encoding UTF8
        Write-Host "  [+] saved the current defaults to $StateFile"
    }
}

function Invoke-Rollback {
    Write-Host 'Rolling back.'
    Remove-TaggedRules
    if (Test-Path $StateFile) {
        $saved = Get-Content $StateFile -Raw | ConvertFrom-Json
        foreach ($p in $saved) {
            if ($PSCmdlet.ShouldProcess($p.Name, "Restore DefaultInboundAction = $($p.DefaultInboundAction)")) {
                Set-NetFirewallProfile -Name $p.Name -DefaultInboundAction $p.DefaultInboundAction
            }
        }
        Write-Host '  [+] profile defaults restored from the snapshot.'
    } else {
        foreach ($name in @('Domain', 'Private', 'Public')) {
            if ($PSCmdlet.ShouldProcess($name, 'Restore DefaultInboundAction = Allow')) {
                Set-NetFirewallProfile -Name $name -DefaultInboundAction Allow
            }
        }
        Write-Warning 'No snapshot found, so the defaults went back to Allow - which is Windows stock, not necessarily what you had.'
    }
    Write-Host 'Done. The old !Firewall_Lockdown.bat block rules, if any, are untouched.'
}

function New-AllowRule {
    param([string] $Name, [int[]] $Ports, [string[]] $RemoteAddress = @('Any'))
    $display = "$RuleTag - $Name"
    if ($PSCmdlet.ShouldProcess($display, "Allow TCP $($Ports -join ',') from $($RemoteAddress -join ',')")) {
        New-NetFirewallRule -DisplayName $display -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $Ports -RemoteAddress $RemoteAddress `
            -Profile Any -Enabled True | Out-Null
    }
    Write-Host ("  [+] {0,-26} TCP {1,-12} from {2}" -f $Name, ($Ports -join ','), ($RemoteAddress -join ','))
}

# ---------------------------------------------------------------- main

Assert-Elevated
Get-FirewallCmdlets

if ($Rollback) { Invoke-Rollback; return }

if ($AdminIp.Count -eq 0 -and -not $AllowRdpFromAnywhere) {
    throw @'
Refusing to run: default-deny will close RDP and you would be locked out.

Pass -AdminIp with the address you administer from:
    .\harden-netcup.ps1 -AdminIp 203.0.113.9

or, if you have no static address and accept the risk:
    .\harden-netcup.ps1 -AllowRdpFromAnywhere

Add -WhatIf to either one to see the changes without making them.
'@
}

Write-Host ''
Write-Host '=== TeraSharp go-live firewall ==============================='
Write-Host '  public:   81 (portal), 7801 (game proxy)'
if ($ExtraTcp.Count) { Write-Host "  also:     $($ExtraTcp -join ', ')" }
if ($AdminIp.Count)  { Write-Host "  rdp $RdpPort from: $($AdminIp -join ', ')" }
elseif ($AllowRdpFromAnywhere) { Write-Warning "  rdp $RdpPort is being opened to the whole internet." }
Write-Host '  everything else: inbound blocked by default (loopback is unaffected)'
Write-Host '=============================================================='
Write-Host ''

Save-PreviousState
Write-Host 'Clearing any rules this script made before...'
Remove-TaggedRules

Write-Host 'Adding the allow rules...'
New-AllowRule -Name 'portal and game' -Ports $PublicTcp
if ($ExtraTcp.Count) { New-AllowRule -Name 'extra public' -Ports $ExtraTcp }
if ($AdminIp.Count) {
    New-AllowRule -Name 'rdp (restricted)' -Ports @($RdpPort) -RemoteAddress $AdminIp
} elseif ($AllowRdpFromAnywhere) {
    New-AllowRule -Name 'rdp (OPEN)' -Ports @($RdpPort)
}

Write-Host 'Setting the inbound default to Block on every profile...'
foreach ($name in @('Domain', 'Private', 'Public')) {
    if ($PSCmdlet.ShouldProcess($name, 'DefaultInboundAction = Block, firewall enabled')) {
        Set-NetFirewallProfile -Name $name -Enabled True -DefaultInboundAction Block -DefaultOutboundAction Allow
    }
    Write-Host "  [+] $name"
}

Write-Host ''
Write-Host 'The old !Firewall_Lockdown.bat rules are LEFT ALONE on purpose:'
Write-Host '  its TERA-BLOCK-* rules are redundant under default-deny but not harmful,'
Write-Host '  and its TERA-ALLOW-81/7801 say the same thing the rules above do.'
Write-Host '  Delete them by hand if you want a tidy list; nothing here depends on it.'
Write-Host ''
Write-Host 'Listening sockets that are now NOT reachable from outside:'
try {
    $allowed = @($PublicTcp) + @($ExtraTcp)
    if ($AdminIp.Count -or $AllowRdpFromAnywhere) { $allowed += $RdpPort }
    Get-NetTCPConnection -State Listen -ErrorAction Stop |
        Where-Object { $_.LocalAddress -in @('0.0.0.0', '::') -and $_.LocalPort -notin $allowed } |
        Select-Object -ExpandProperty LocalPort -Unique |
        Sort-Object |
        ForEach-Object { Write-Host "  [-] $_" }
} catch {
    Write-Host '  (could not enumerate listeners on this host)'
}

Write-Host ''
Write-Host 'Now verify from a DIFFERENT machine:'
Write-Host "  nmap -Pn -p 81,$RdpPort,7701,7801,7802,8050,1433,3306 YOUR_PUBLIC_IP"
Write-Host ''
Write-Host '  81 and 7801 must be open. 7701 must NOT be: the Arbiter does not check'
Write-Host '  GM privilege on C_ADMIN, so the proxy on 7801 is the only gate.'
Write-Host ''
Write-Host 'Undo with:  .\harden-netcup.ps1 -Rollback'
