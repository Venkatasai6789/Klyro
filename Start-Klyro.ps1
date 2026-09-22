<#
.SYNOPSIS
    Launcher script for Klyro Secure Assessment Browser.
.DESCRIPTION
    Launches Klyro from C:\Klyro (or builds and installs if not present).
#>

$installedExe = "C:\Klyro\Klyro.App.exe"

if (-not (Test-Path $installedExe)) {
    Write-Host "Installing Klyro first..." -ForegroundColor Yellow
    & "$PSScriptRoot\Install-Klyro.ps1"
    exit 0
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Launching Klyro Secure Assessment Browser               " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Target: $installedExe" -ForegroundColor Gray

Start-Process -FilePath $installedExe
Write-Host "[OK] Klyro is running." -ForegroundColor Green
