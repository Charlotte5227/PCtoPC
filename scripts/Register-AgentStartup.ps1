[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$AgentExecutable = (Join-Path (Split-Path -Parent $PSScriptRoot) 'agent/PCtoPC.Agent.exe'),
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$taskName = 'PCtoPC Agent - ' + [Security.Principal.WindowsIdentity]::GetCurrent().Name.Replace('\','-')
if ($Remove) {
    if ($PSCmdlet.ShouldProcess($taskName, 'Remove logon task')) { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false }
    return
}
$executable = (Resolve-Path -LiteralPath $AgentExecutable).Path
$userName = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $executable -Argument 'serve' -WorkingDirectory (Split-Path -Parent $executable)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $userName
$principal = New-ScheduledTaskPrincipal -UserId $userName -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
if ($PSCmdlet.ShouldProcess($taskName, 'Register current-user logon task')) {
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force
}
Write-Output 'Agent starts after this user signs in, not before logon. Credentials remain bound to this user.'
