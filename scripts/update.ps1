<#
.SYNOPSIS
    Replaces the Rest Mind binaries with a newer build, keeping the schedule and password.

.DESCRIPTION
    The iteration loop: publish on the dev machine, copy the publish folder next to this
    script, then run this from an elevated prompt. Config and state under ProgramData are left
    untouched, so a mid-break update still resumes the break correctly.
#>
[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'publish'),
    [string]$InstallPath = 'C:\Program Files\RestMind'
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'RestMind'

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run from an elevated (Administrator) PowerShell prompt.'
}

if (-not (Test-Path $SourcePath)) {
    throw "Published binaries not found at '$SourcePath'."
}

if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    throw "The $ServiceName service is not installed. Run install.ps1 instead."
}

Write-Host 'Stopping the service and agent...'
Stop-Service -Name $ServiceName -Force
Get-Process -Name 'RestMind.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host 'Copying the new binaries...'
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force

Write-Host 'Starting the service...'
Start-Service -Name $ServiceName

Write-Host ''
Write-Host 'Updated. The agent starts on the next sign-in, or within a few seconds via the watchdog.' -ForegroundColor Green
