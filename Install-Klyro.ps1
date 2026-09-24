<#
.SYNOPSIS
    Automated Local Installer & Setup Wizard for Klyro Secure Assessment Browser.
.DESCRIPTION
    Launches the modern Klyro Setup Wizard GUI featuring Terms & Conditions agreement,
    destination selection, shortcut configuration radial buttons, live progress tracking,
    and automatic application launch.
.PARAMETER Silent
    Runs the installation in unattended silent mode without displaying the GUI wizard.
.PARAMETER NoGui
    Alias for -Silent.
.PARAMETER InstallDir
    Target directory for installation (default: C:\Klyro).
.PARAMETER NoShortcuts
    Skips shortcut creation in silent mode.
#>

[CmdletBinding()]
param(
    [switch]$Silent,
    [switch]$NoGui,
    [string]$InstallDir = "C:\Klyro",
    [switch]$NoShortcuts,
    [switch]$Force,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$installerProj = Join-Path $PSScriptRoot "src\Klyro.Installer\Klyro.Installer.csproj"
$installerExe = Join-Path $PSScriptRoot "src\Klyro.Installer\bin\Debug\net8.0-windows\Klyro.Installer.exe"
$appProj = Join-Path $PSScriptRoot "src\Klyro.App\Klyro.App.csproj"
$sourcePublish = Join-Path $PSScriptRoot "artifacts\publish"
$dotnet = if ($env:USERPROFILE -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { "dotnet" }

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Klyro Secure Assessment Browser Setup Wizard            " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Stop any running Klyro processes before building or deploying
Write-Host "[1/3] Checking running processes..." -ForegroundColor Yellow
Get-Process -Name "Klyro*", "msedgewebview2*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 800

# 2. Ensure application binaries and installer are compiled
Write-Host "[2/3] Verifying and building binaries..." -ForegroundColor Yellow

$publishedExe = Join-Path $sourcePublish "Klyro.App.exe"
$shouldPublish = $Force -or (-not (Test-Path $publishedExe))
if (-not $shouldPublish) {
    $latestSource = Get-ChildItem -Recurse (Join-Path $PSScriptRoot "src") | Measure-Object -Property LastWriteTime -Maximum
    $pubExeTime = (Get-Item $publishedExe).LastWriteTime
    if ($latestSource.Maximum -gt $pubExeTime) {
        $shouldPublish = $true
    }
}

if ($shouldPublish) {
    Write-Host "Publishing Klyro self-contained binaries to artifacts\publish ($Configuration)..." -ForegroundColor Gray
    & $dotnet publish $appProj -c $Configuration -r win-x64 --self-contained true -o $sourcePublish
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Host "Published binaries are up to date ($publishedExe)." -ForegroundColor Gray
}

$shouldBuildInstaller = $Force -or (-not (Test-Path $installerExe))
if (-not $shouldBuildInstaller) {
    $latestInstallerSource = Get-ChildItem -Recurse (Join-Path $PSScriptRoot "src\Klyro.Installer") | Measure-Object -Property LastWriteTime -Maximum
    $instExeTime = (Get-Item $installerExe).LastWriteTime
    if ($latestInstallerSource.Maximum -gt $instExeTime) {
        $shouldBuildInstaller = $true
    }
}

if ($shouldBuildInstaller) {
    Write-Host "Compiling Klyro Setup Wizard..." -ForegroundColor Gray
    & $dotnet build $installerProj -c Debug
    if ($LASTEXITCODE -ne 0) {
        throw "Installer build failed with exit code $LASTEXITCODE"
    }
} else {
    Write-Host "Installer executable is up to date ($installerExe)." -ForegroundColor Gray
}

# 3. Launch Setup Experience
if ($Silent -or $NoGui) {
    Write-Host "[3/3] Executing silent installation..." -ForegroundColor Yellow
    $argsList = @("--silent", "--dir=$InstallDir")
    if ($NoShortcuts) {
        $argsList += "--no-shortcuts"
    }
    $proc = Start-Process -FilePath $installerExe -ArgumentList $argsList -Wait -PassThru
    if ($proc.ExitCode -eq 0) {
        Write-Host "Installation completed successfully in silent mode." -ForegroundColor Green
    } else {
        throw "Installer failed with exit code $($proc.ExitCode)"
    }
} else {
    Write-Host "[3/3] Launching Klyro Setup Wizard GUI..." -ForegroundColor Green
    Write-Host "Displaying installation screens:" -ForegroundColor Gray
    Write-Host "  - Screen 1: Terms & Conditions (EULA acceptance radio buttons)" -ForegroundColor Gray
    Write-Host "  - Screen 2: Destination directory & shortcut creation radial buttons" -ForegroundColor Gray
    Write-Host "  - Screen 3: Ready to Install & live progress tracking" -ForegroundColor Gray
    Write-Host "  - Screen 4: Installation complete & launch prompt" -ForegroundColor Gray
    
    $proc = Start-Process -FilePath $installerExe -PassThru
    Write-Host "Setup Wizard is running (PID: $($proc.Id)). Complete the steps in the installer window." -ForegroundColor Cyan
}
