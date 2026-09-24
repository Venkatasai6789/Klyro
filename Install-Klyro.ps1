<#
.SYNOPSIS
    Automated Local Installer & Setup Wizard for Klyro Secure Assessment Browser.
.DESCRIPTION
    Launches the modern Klyro Setup Wizard GUI featuring Terms & Conditions agreement,
    destination selection, shortcut configuration radial buttons, live progress tracking,
    administrator rights detection, and automatic application launch.
.PARAMETER Silent
    Runs the installation in unattended silent mode without displaying the GUI wizard.
.PARAMETER NoGui
    Alias for -Silent.
.PARAMETER InstallDir
    Target directory for installation (defaults to C:\Klyro for all-users or %LOCALAPPDATA%\Programs\Klyro for current-user).
.PARAMETER CurrentUser
    Installs exclusively for the current user into %LOCALAPPDATA%\Programs\Klyro (no administrator rights required).
.PARAMETER Elevate
    Relaunches the installer with elevated Administrator privileges via Windows UAC prompt.
.PARAMETER NoShortcuts
    Skips shortcut creation in silent mode.
.PARAMETER Force
    Forces re-publishing and recompilation even if cached binaries are current.
.PARAMETER Configuration
    Build configuration (Debug or Release). Defaults to Release.
#>

[CmdletBinding()]
param(
    [switch]$Silent,
    [switch]$NoGui,
    [string]$InstallDir = "",
    [switch]$CurrentUser,
    [switch]$Elevate,
    [switch]$NoShortcuts,
    [switch]$Force,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

# 0. Check Administrator Privileges
$currentPrincipal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$isAdmin = $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Elevate -and -not $isAdmin) {
    Write-Host "Elevating setup session via Windows UAC..." -ForegroundColor Yellow
    $scriptPath = $MyInvocation.MyCommand.Path
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`""
    if ($Silent -or $NoGui) { $argList += " -Silent" }
    if ($NoShortcuts) { $argList += " -NoShortcuts" }
    if ($Force) { $argList += " -Force" }
    if ($InstallDir) { $argList += " -InstallDir `"$InstallDir`"" }
    Start-Process powershell -Verb RunAs -ArgumentList $argList
    exit 0
}

# Determine default target directory based on privilege level and switches
if ([string]::IsNullOrWhiteSpace($InstallDir)) {
    if ($CurrentUser -or (-not $isAdmin)) {
        $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\Klyro"
    } else {
        $InstallDir = "C:\Klyro"
    }
}

$installerProj = Join-Path $PSScriptRoot "src\Klyro.Installer\Klyro.Installer.csproj"
$installerExe = Join-Path $PSScriptRoot "src\Klyro.Installer\bin\Debug\net8.0-windows\Klyro.Installer.exe"
$appProj = Join-Path $PSScriptRoot "src\Klyro.App\Klyro.App.csproj"
$sourcePublish = Join-Path $PSScriptRoot "artifacts\publish"
$dotnet = if ($env:USERPROFILE -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { "dotnet" }

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Klyro Secure Assessment Browser Setup Wizard            " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Privilege Level : $(if ($isAdmin) { 'Administrator (Elevated)' } else { 'Standard User (Non-Elevated)' })" -ForegroundColor $(if ($isAdmin) { 'Green' } else { 'Yellow' })
Write-Host "Target Location : $InstallDir" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Stop any running Klyro processes before building or deploying
Write-Host "[1/3] Checking running processes..." -ForegroundColor Yellow
Get-Process -Name "Klyro*", "msedgewebview2*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

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
    Write-Host "[3/3] Executing silent installation directly to $InstallDir..." -ForegroundColor Yellow
    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }
    Copy-Item -Path "$sourcePublish\*" -Destination $InstallDir -Recurse -Force
    
    $configSrc = Join-Path $PSScriptRoot "config"
    if (Test-Path $configSrc) {
        $targetConfig = Join-Path $InstallDir "config"
        if (-not (Test-Path $targetConfig)) {
            New-Item -ItemType Directory -Path $targetConfig -Force | Out-Null
        }
        Copy-Item -Path "$configSrc\*" -Destination $targetConfig -Recurse -Force
    }

    if (-not $NoShortcuts) {
        $wshShell = New-Object -ComObject WScript.Shell
        $desktopPath = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
        $shortcut = $wshShell.CreateShortcut((Join-Path $desktopPath "Klyro Secure Assessment Browser.lnk"))
        $shortcut.TargetPath = (Join-Path $InstallDir "Klyro.App.exe")
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.Description = "Klyro Secure Assessment Browser"
        $iconPath = Join-Path $InstallDir "Assets\AppIcon.ico"
        if (Test-Path $iconPath) {
            $shortcut.IconLocation = "$iconPath,0"
        }
        $shortcut.Save()

        $startMenuPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::StartMenu)) "Programs"
        $startShortcut = $wshShell.CreateShortcut((Join-Path $startMenuPath "Klyro Secure Assessment Browser.lnk"))
        $startShortcut.TargetPath = (Join-Path $InstallDir "Klyro.App.exe")
        $startShortcut.WorkingDirectory = $InstallDir
        $startShortcut.Description = "Klyro Secure Assessment Browser"
        if (Test-Path $iconPath) {
            $startShortcut.IconLocation = "$iconPath,0"
        }
        $startShortcut.Save()
    }
    Write-Host "Installation completed successfully in silent mode." -ForegroundColor Green
} else {
    Write-Host "[3/3] Launching Klyro Setup Wizard GUI..." -ForegroundColor Green
    Write-Host "Displaying installation screens:" -ForegroundColor Gray
    Write-Host "  - Screen 1: Terms & Conditions (EULA acceptance radio buttons)" -ForegroundColor Gray
    Write-Host "  - Screen 2: Privilege detection, Scope, Destination & shortcut radial buttons" -ForegroundColor Gray
    Write-Host "  - Screen 3: Ready to Install & live progress tracking" -ForegroundColor Gray
    Write-Host "  - Screen 4: Installation complete & launch prompt" -ForegroundColor Gray
    
    $proc = Start-Process -FilePath $installerExe -PassThru
    Write-Host "Setup Wizard is running (PID: $($proc.Id)). Complete the steps in the installer window." -ForegroundColor Cyan
}
