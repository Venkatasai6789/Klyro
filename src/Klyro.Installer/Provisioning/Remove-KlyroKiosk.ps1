<#
.SYNOPSIS
    Automated Rollback & Teardown Script for Klyro Secure Assessment Browser Kiosk.
.DESCRIPTION
    Restores the workstation to standard university desktop configuration:
    1. Removes Shell Launcher v2 custom shell mapping for the examination user.
    2. Reverts local registry lockdown policies (Fast User Switching, etc.).
    3. Optionally removes the local examination user account (KLYRO-EXAM).
    4. Optionally deletes deployed Klyro binaries from C:\Program Files\Klyro.
.PARAMETER ExamUser
    Local account name for examinations (default: KLYRO-EXAM).
.PARAMETER DeleteUser
    If $true, deletes the local exam user account. (default: $false).
.PARAMETER RemoveBinaries
    If $true, deletes C:\Program Files\Klyro. (default: $false).
#>

param(
    [string]$ExamUser = "KLYRO-EXAM",
    [bool]$DeleteUser = $false,
    [bool]$RemoveBinaries = $false
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Teardown script must be executed in an elevated PowerShell session (Administrator)."
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  KLYRO — University Workstation Kiosk Teardown / Rollback " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Revert Shell Launcher Configuration
Write-Host "[1/4] Removing Shell Launcher v2 custom shell configuration..." -ForegroundColor Yellow
$WmiScope = "root\standardcimv2\embedded"

try {
    $objUser = New-Object System.Security.Principal.NTAccount($ExamUser)
    $strSID = $objUser.Translate([System.Security.Principal.SecurityIdentifier]).Value

    $existing = Get-CimInstance -Namespace $WmiScope -ClassName WESL_UserSetting -ErrorAction SilentlyContinue | Where-Object { $_.Sid -eq $strSID }
    if ($existing) {
        Remove-CimInstance -InputObject $existing
        Write-Host "✓ Removed custom shell mapping for SID: $strSID" -ForegroundColor Green
    } else {
        Write-Host "✓ No custom shell mapping found for $ExamUser." -ForegroundColor Green
    }
}
catch {
    Write-Host "! Could not resolve SID or connect to WMI (User may not exist or feature not present): $($_.Exception.Message)" -ForegroundColor Yellow
}

# 2. Revert Registry Restrictions
Write-Host "[2/4] Reverting registry restrictions..." -ForegroundColor Yellow
try {
    $systemPolicies = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    if (Test-Path $systemPolicies) {
        Remove-ItemProperty -Path $systemPolicies -Name "HideFastUserSwitching" -ErrorAction SilentlyContinue
        Write-Host "✓ Reverted HideFastUserSwitching registry policy." -ForegroundColor Green
    }
}
catch {
    Write-Warning "Could not revert registry key: $($_.Exception.Message)"
}

# 3. Handle Local Exam Account
Write-Host "[3/4] Checking local exam account ($ExamUser)..." -ForegroundColor Yellow
$userExists = Get-LocalUser -Name $ExamUser -ErrorAction SilentlyContinue
if ($userExists) {
    if ($DeleteUser) {
        Remove-LocalUser -Name $ExamUser
        Write-Host "✓ Local user account $ExamUser deleted." -ForegroundColor Green
    } else {
        Write-Host "ℹ Account $ExamUser preserved (use -DeleteUser `$true to remove)." -ForegroundColor Gray
    }
} else {
    Write-Host "✓ Account $ExamUser does not exist." -ForegroundColor Green
}

# 4. Handle Deployed Binaries
Write-Host "[4/4] Checking deployed application files..." -ForegroundColor Yellow
$targetDir = "C:\Program Files\Klyro"
if (Test-Path $targetDir) {
    if ($RemoveBinaries) {
        Remove-Item -Path $targetDir -Recurse -Force
        Write-Host "✓ Removed $targetDir." -ForegroundColor Green
    } else {
        Write-Host "ℹ Binaries in $targetDir preserved (use -RemoveBinaries `$true to remove)." -ForegroundColor Gray
    }
} else {
    Write-Host "✓ $targetDir not present." -ForegroundColor Green
}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  KLYRO TEARDOWN COMPLETE                                 " -ForegroundColor Green
Write-Host "  Workstation returned to standard configuration.         " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
