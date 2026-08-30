<#
.SYNOPSIS
    Installs Rest Mind on this Windows machine. Run once, from an elevated PowerShell prompt.

.DESCRIPTION
    Copies the published binaries to Program Files, creates the ACL'd data directory under
    ProgramData, registers the enforcement service, sets the parent password, and arranges for
    the overlay agent to start at every sign-in.

.PARAMETER SourcePath
    Folder containing the published binaries. Defaults to a 'publish' folder next to this script.

.EXAMPLE
    .\install.ps1
#>
[CmdletBinding()]
param(
    [string]$SourcePath = (Join-Path $PSScriptRoot 'publish'),
    [string]$InstallPath = 'C:\Program Files\RestMind',
    [string]$DataPath = 'C:\ProgramData\RestMind'
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'RestMind'
$RunKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'This script must be run from an elevated (Administrator) PowerShell prompt.'
    }
}

Assert-Administrator

if (-not (Test-Path $SourcePath)) {
    throw "Published binaries not found at '$SourcePath'. Run scripts/publish.sh on the dev machine first, then copy the publish folder next to this script."
}

$serviceExe = Join-Path $InstallPath 'RestMind.Service.exe'
$agentExe = Join-Path $InstallPath 'RestMind.Agent.exe'
$ctlExe = Join-Path $InstallPath 'RestMind.Ctl.exe'

# --- Stop anything already running so the files are not locked -------------------------------
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    Write-Host 'Stopping the existing RestMind service...'
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 2
}
Get-Process -Name 'RestMind.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force

# --- Copy the binaries -----------------------------------------------------------------------
Write-Host "Installing to $InstallPath..."
New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null
Copy-Item -Path (Join-Path $SourcePath '*') -Destination $InstallPath -Recurse -Force

foreach ($exe in @($serviceExe, $agentExe, $ctlExe)) {
    if (-not (Test-Path $exe)) {
        throw "Expected '$exe' after copying, but it is missing. Check the publish output."
    }
}

# --- Data directory, readable by everyone but writable only by administrators -----------------
Write-Host "Creating $DataPath..."
New-Item -ItemType Directory -Path (Join-Path $DataPath 'logs') -Force | Out-Null

# /inheritance:r drops inherited user-writable rights; without it a standard user could edit
# config.json and simply turn enforcement off.
icacls $DataPath /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'Users:(OI)(CI)RX' /T /Q | Out-Null

# --- Parent password -------------------------------------------------------------------------
$configFile = Join-Path $DataPath 'config.json'
if (-not (Test-Path $configFile)) {
    Write-Host ''
    Write-Host 'Set the parent password. This is what unlocks a break early.' -ForegroundColor Cyan
    & $ctlExe set-password
    if ($LASTEXITCODE -ne 0) {
        throw 'Setting the parent password failed; nothing else was configured.'
    }
} else {
    Write-Host 'Existing config.json kept. Use "RestMind.Ctl set-password" to change the password.'
}

# --- Register the service ---------------------------------------------------------------------
Write-Host 'Registering the RestMind service...'
# LocalSystem, and the service ACL stays at its default, so a standard user cannot stop it.
sc.exe create $ServiceName binPath= "`"$serviceExe`"" start= auto obj= LocalSystem DisplayName= 'Rest Mind screen breaks' | Out-Null
sc.exe description $ServiceName 'Enforces scheduled screen breaks.' | Out-Null
# Restart the service if it ever crashes.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null
Start-Service -Name $ServiceName

# --- Start the agent at every sign-in ----------------------------------------------------------
# HKLM (not HKCU) so it applies to every account and a standard user cannot remove it.
Write-Host 'Registering the overlay agent to start at sign-in...'
New-ItemProperty -Path $RunKey -Name 'RestMindAgent' -Value "`"$agentExe`"" -PropertyType String -Force | Out-Null

Write-Host ''
Write-Host 'Rest Mind is installed and running.' -ForegroundColor Green
Write-Host ''
Write-Host 'Next steps:'
Write-Host "  1. Sign in as your daughter's account; the overlay agent starts automatically."
Write-Host "  2. Check the schedule with: `"$ctlExe`" show-config"
Write-Host "  3. Edit $configFile if you want different hours, then: `"$ctlExe`" reload"
Write-Host ''
Write-Host 'Day to day:'
Write-Host "  `"$ctlExe`" status              What it is doing right now"
Write-Host "  `"$ctlExe`" pause --minutes 30  Suspend enforcement for a while"
Write-Host "  `"$ctlExe`" unlock              End the current break"
