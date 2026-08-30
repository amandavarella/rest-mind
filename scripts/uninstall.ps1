<#
.SYNOPSIS
    Removes Rest Mind. Run from an elevated PowerShell prompt.

.PARAMETER KeepData
    Leave C:\ProgramData\RestMind in place, so the schedule and password survive a reinstall.
#>
[CmdletBinding()]
param(
    [string]$InstallPath = 'C:\Program Files\RestMind',
    [string]$DataPath = 'C:\ProgramData\RestMind',
    [switch]$KeepData
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'RestMind'
$RunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated (Administrator) PowerShell prompt.'
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host 'Stopping and removing the service...'
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}

Get-Process -Name 'RestMind.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host 'Removing the sign-in entry...'
Remove-ItemProperty -Path $RunKey -Name 'RestMindAgent' -ErrorAction SilentlyContinue

# The service normally clears this when it stops, but make sure an interrupted uninstall can
# never leave Task Manager disabled.
$policyKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
if (Test-Path $policyKey) {
    Remove-ItemProperty -Path $policyKey -Name 'DisableTaskMgr' -ErrorAction SilentlyContinue
    Write-Host 'Task Manager re-enabled.'
}

if (Test-Path $InstallPath) {
    Write-Host "Removing $InstallPath..."
    Remove-Item -Path $InstallPath -Recurse -Force
}

if ($KeepData) {
    Write-Host "Keeping $DataPath (schedule and password preserved)."
} elseif (Test-Path $DataPath) {
    Write-Host "Removing $DataPath..."
    Remove-Item -Path $DataPath -Recurse -Force
}

Write-Host ''
Write-Host 'Rest Mind has been removed.' -ForegroundColor Green
