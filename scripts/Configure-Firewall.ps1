# Run on the Agent PC in an elevated PowerShell. No rule is installed by the app itself.
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string[]]$RemoteAddress,
    [ValidateRange(1024,65535)][int]$Port = 47831,
    [string]$AgentExecutable = (Join-Path (Split-Path -Parent $PSScriptRoot) 'agent/PCtoPC.Agent.exe'),
    [switch]$TailscaleInterface
)
$ErrorActionPreference = 'Stop'
if ($RemoteAddress | Where-Object { $_ -in @('Any','*','0.0.0.0/0','::/0','Internet') }) { throw 'Specify trusted client IPs or a narrow LAN subnet.' }
$executable = (Resolve-Path -LiteralPath $AgentExecutable).Path
$rule = @{
    DisplayName = 'PCtoPC Agent (scoped)'; Direction = 'Inbound'; Action = 'Allow'
    Protocol = 'TCP'; LocalPort = $Port; Program = $executable; RemoteAddress = $RemoteAddress
}
if ($TailscaleInterface) {
    $adapters = @(Get-NetAdapter | Where-Object { $_.InterfaceDescription -match 'Tailscale' -or $_.Name -match 'Tailscale' })
    if ($adapters.Count -eq 0) { throw 'Tailscale interface not found.' }
    $rule.Profile = 'Any'
    $rule.InterfaceAlias = @($adapters.Name)
} else { $rule.Profile = @('Private','Domain') }
if ($PSCmdlet.ShouldProcess($executable, 'Add scoped PCtoPC firewall rule')) { New-NetFirewallRule @rule }
