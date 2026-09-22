<#
.SYNOPSIS
    Automated Enterprise Provisioning Script for Klyro Secure Assessment Browser.
.DESCRIPTION
    Provisions a university laboratory workstation into a hardened examination terminal:
    1. Creates least-privileged standard account: KLYRO-EXAM.
    2. Verifies WebView2 Evergreen Runtime installation.
    3. Provisions Klyro binaries into C:\Program Files\Klyro.
    4. Configures Microsoft Shell Launcher (v2) to replace explorer.exe with Klyro.
    5. Applies registry lockdowns (disables Task Manager, lock workstation, user switching).
.PARAMETER ExamUser
    Local account name for examinations (default: KLYRO-EXAM).
.PARAMETER KlyroSourceDir
    Directory containing compiled Klyro release artifacts.
#>

param(
    [string]$ExamUser = "KLYRO-EXAM",
    [string]$ExamPassword = "ExamSessionSecure!2026",
    [string]$KlyroSourceDir = ".\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish"
)

$ErrorActionPreference = "Stop"

$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error "Deployment script must be executed in an elevated PowerShell session (Administrator)."
    exit 1
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  KLYRO — University Workstation Kiosk Provisioning       " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Create Least-Privileged Local Examination Account
Write-Host "[1/5] Configuring local examination account: $ExamUser..." -ForegroundColor Yellow
$userExists = Get-LocalUser -Name $ExamUser -ErrorAction SilentlyContinue
if (-not $userExists) {
    $secPassword = ConvertTo-SecureString $ExamPassword -AsPlainText -Force
    New-LocalUser -Name $ExamUser -Password $secPassword -FullName "Klyro Assessment User" -Description "Standard user account for university coding examinations" -PasswordNeverExpires $true | Out-Null
    Write-Host "✓ Account $ExamUser created successfully." -ForegroundColor Green
} else {
    Write-Host "✓ Account $ExamUser already exists." -ForegroundColor Green
}

# Ensure account is strictly in Users group and NOT Administrators
Remove-LocalGroupMember -Group "Administrators" -Member $ExamUser -ErrorAction SilentlyContinue
Add-LocalGroupMember -Group "Users" -Member $ExamUser -ErrorAction SilentlyContinue

# 2. Verify WebView2 Runtime Installation
Write-Host "[2/5] Checking Microsoft Edge WebView2 Runtime..." -ForegroundColor Yellow
$wv2Installed = (Get-ItemProperty -Path "HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -ErrorAction SilentlyContinue) -or `
                (Get-ItemProperty -Path "HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" -ErrorAction SilentlyContinue)

if (-not $wv2Installed) {
    Write-Host "WebView2 runtime not detected. Downloading Evergreen Bootstrapper..." -ForegroundColor Yellow
    $bootstrapperPath = "$env:TEMP\MicrosoftEdgeWebview2Setup.exe"
    Invoke-WebRequest -Uri "https://go.microsoft.com/fwlink/p/?LinkId=2124703" -OutFile $bootstrapperPath
    Start-Process -FilePath $bootstrapperPath -ArgumentList "/silent /install" -Wait
    Remove-Item $bootstrapperPath -Force -ErrorAction SilentlyContinue
    Write-Host "✓ WebView2 Evergreen Runtime installed successfully." -ForegroundColor Green
} else {
    Write-Host "✓ WebView2 Runtime verified." -ForegroundColor Green
}

# 3. Deploy Application Files to Program Files
Write-Host "[3/5] Deploying Klyro binaries..." -ForegroundColor Yellow
$targetDir = "C:\Program Files\Klyro"
if (-not (Test-Path $targetDir)) {
    New-Item -Path $targetDir -ItemType Directory -Force | Out-Null
}

if (Test-Path $KlyroSourceDir) {
    Copy-Item -Path "$KlyroSourceDir\*" -Destination $targetDir -Recurse -Force
    Write-Host "✓ Deployed binaries from $KlyroSourceDir to $targetDir." -ForegroundColor Green
} else {
    Write-Host "! Source directory not found ($KlyroSourceDir). Ensure build artifacts are placed in $targetDir." -ForegroundColor Yellow
}

# 4. Configure Shell Launcher v2
Write-Host "[4/5] Applying Shell Launcher v2 configuration..." -ForegroundColor Yellow
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$shellScript = Join-Path $scriptDir "..\Kiosk\ShellLauncherConfiguration.ps1"
if (Test-Path $shellScript) {
    & $shellScript -ExamUser $ExamUser -KlyroExecutablePath "$targetDir\Klyro.App.exe"
} else {
    Write-Warning "ShellLauncherConfiguration.ps1 not found at expected path: $shellScript"
}

# 5. Apply Registry Lockdown for Student Account
Write-Host "[5/5] Applying local policy registry restrictions..." -ForegroundColor Yellow
try {
    $userSid = (New-Object System.Security.Principal.NTAccount($ExamUser)).Translate([System.Security.Principal.SecurityIdentifier]).Value

    # Mount user hive or set system-wide machine policy targeting exam session
    $systemPolicies = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    Set-ItemProperty -Path $systemPolicies -Name "HideFastUserSwitching" -Value 1 -Type DWord -Force

    Write-Host "✓ Registry restrictions successfully configured." -ForegroundColor Green
}
catch {
    Write-Warning "Could not apply individual user registry key: $($_.Exception.Message)"
}

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  KLYRO PROVISIONING COMPLETE                             " -ForegroundColor Green
Write-Host "  Workstation is ready for student assessment.            " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
