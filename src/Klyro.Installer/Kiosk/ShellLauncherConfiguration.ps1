<#
.SYNOPSIS
    Configures Windows Shell Launcher (v2) for Klyro Secure Assessment Browser.
.DESCRIPTION
    Replaces explorer.exe with Klyro.App.exe for the dedicated examination account (e.g. KLYRO-EXAM).
    Configures Shell Launcher to automatically restart Klyro if the process terminates or crashes.
.PARAMETER ExamUser
    The local standard account name (default: "KLYRO-EXAM").
.PARAMETER KlyroExecutablePath
    The absolute path to Klyro.App.exe.
#>

param(
    [string]$ExamUser = "KLYRO-EXAM",
    [string]$KlyroExecutablePath = "C:\Program Files\Klyro\Klyro.App.exe"
)

# Ensure script is running elevated
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "This script must be executed in an elevated PowerShell session (Run as Administrator)."
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Configuring Shell Launcher for $ExamUser                 " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Enable Shell Launcher Windows Feature if not already enabled
$feature = Get-WindowsOptionalFeature -Online -FeatureName "Client-EmbeddedShellLauncher" -ErrorAction SilentlyContinue
if ($feature -and $feature.State -ne "Enabled") {
    Write-Host "Enabling Shell Launcher Windows feature..." -ForegroundColor Yellow
    Enable-WindowsOptionalFeature -Online -FeatureName "Client-EmbeddedShellLauncher" -NoRestart
}

# 2. Query the SID for the target exam account
try {
    $objUser = New-Object System.Security.Principal.NTAccount($ExamUser)
    $strSID = $objUser.Translate([System.Security.Principal.SecurityIdentifier]).Value
    Write-Host "Located SID for $ExamUser: $strSID" -ForegroundColor Green
}
catch {
    Write-Error "Could not resolve SID for user '$ExamUser'. Ensure the local account has been created."
    exit 1
}

# 3. Connect to WMI Shell Launcher namespace
$WmiScope = "root\standardcimv2\embedded"

# Restart action codes:
# 0 = Restart shell when it exits
# 1 = Restart device when shell exits
# 2 = Shutdown device when shell exits
# 3 = Do nothing when shell exits
$DefaultAction = 0 # Automatically restart Klyro on exit / crash

Write-Host "Configuring custom shell for SID $strSID..." -ForegroundColor Yellow

$ShellLauncherClass = [wmiclass]"\\localhost\$WmiScope:WESL_UserSetting"

# Remove existing custom configuration for this SID if present
$existing = Get-CimInstance -Namespace $WmiScope -ClassName WESL_UserSetting -ErrorAction SilentlyContinue | Where-Object { $_.Sid -eq $strSID }
if ($existing) {
    Remove-CimInstance -InputObject $existing
}

# Apply new Shell Launcher mapping
$ShellLauncherClass.SetCustomShell($strSID, "$KlyroExecutablePath", $DefaultAction)

# Enable Shell Launcher globally
$ShellLauncherGlobal = [wmiclass]"\\localhost\$WmiScope:WESL_ShellLauncher"
$ShellLauncherGlobal.SetEnabled($true)

Write-Host "✓ Shell Launcher successfully configured for $ExamUser!" -ForegroundColor Green
Write-Host "  Custom Shell: $KlyroExecutablePath" -ForegroundColor Gray
Write-Host "  Restart Action: Restart Shell (Code 0)" -ForegroundColor Gray
