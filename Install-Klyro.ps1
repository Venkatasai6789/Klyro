<#
.SYNOPSIS
    Automated Local Installer & Shortcut Creator for Klyro Secure Assessment Browser.
.DESCRIPTION
    Installs Klyro into C:\Klyro, removes Mark-of-the-Web zone flags, and creates Desktop and Start Menu shortcuts.
#>

$ErrorActionPreference = "Stop"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Installing Klyro Secure Assessment Browser              " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$sourcePublish = Join-Path $PSScriptRoot "src\Klyro.App\bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\publish"
$installDir = "C:\Klyro"
$exePath = Join-Path $installDir "Klyro.App.exe"

# 1. Stop any running Klyro processes before building
Write-Host "[1/5] Stopping any existing Klyro processes..." -ForegroundColor Yellow
Get-Process -Name "Klyro*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

# 2. Build and Publish Self-Contained Binaries
Write-Host "[2/5] Publishing application binaries..." -ForegroundColor Yellow
$dotnet = if (Test-Path "$HOME\.dotnet\dotnet.exe") { "$HOME\.dotnet\dotnet.exe" } else { "dotnet" }
$projPath = Join-Path $PSScriptRoot "src\Klyro.App\Klyro.App.csproj"
& $dotnet publish $projPath -c Debug -r win-x64 -p:Platform=x64 --self-contained true
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path $sourcePublish)) {
    $sourcePublish = Join-Path $PSScriptRoot "src\Klyro.App\bin\Debug\net8.0-windows10.0.26100.0\win-x64\publish"
}

# 3. Deploy to Installation Directory
Write-Host "[3/5] Deploying to $installDir..." -ForegroundColor Yellow
Get-Process -Name "Klyro*" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

Remove-Item -Path "$sourcePublish\*.log" -Force -ErrorAction SilentlyContinue

if (-not (Test-Path $installDir)) {
    New-Item -Path $installDir -ItemType Directory -Force | Out-Null
} else {
    Remove-Item -Path "$installDir\*.log" -Force -ErrorAction SilentlyContinue
}

Copy-Item -Path "$sourcePublish\*" -Destination $installDir -Recurse -Force
$configSource = Join-Path $PSScriptRoot "config"
if (Test-Path $configSource) {
    Copy-Item -Path "$configSource\*" -Destination (Join-Path $installDir "config") -Recurse -Force
}
Get-ChildItem -Path $installDir -Recurse | Unblock-File -ErrorAction SilentlyContinue

# 4. Create Windows Shortcuts (Desktop & Start Menu)
Write-Host "[4/5] Creating Desktop and Start Menu shortcuts..." -ForegroundColor Yellow
$wshShell = New-Object -ComObject WScript.Shell

# Desktop Shortcut
$desktopPath = [Environment]::GetFolderPath("Desktop")
$desktopLnk = Join-Path $desktopPath "Klyro Secure Assessment Browser.lnk"
$desktopShortcut = $wshShell.CreateShortcut($desktopLnk)
$desktopShortcut.TargetPath = $exePath
$desktopShortcut.WorkingDirectory = $installDir
$iconPath = Join-Path $installDir "Assets\AppIcon.ico"
if (Test-Path $iconPath) {
    $desktopShortcut.IconLocation = "$iconPath,0"
}
$desktopShortcut.Description = "Klyro - Secure Assessment Browser"
$desktopShortcut.Save()

# Start Menu Shortcut
$startMenuDir = [Environment]::GetFolderPath("StartMenu")
$startPrograms = Join-Path $startMenuDir "Programs"
$startLnk = Join-Path $startPrograms "Klyro Secure Assessment Browser.lnk"
$startShortcut = $wshShell.CreateShortcut($startLnk)
$startShortcut.TargetPath = $exePath
$startShortcut.WorkingDirectory = $installDir
if (Test-Path $iconPath) {
    $startShortcut.IconLocation = "$iconPath,0"
}
$startShortcut.Description = "Klyro - Secure Assessment Browser"
$startShortcut.Save()

# 5. Launch Application
Write-Host "[5/5] Launching Klyro..." -ForegroundColor Yellow
Start-Process "explorer.exe" -ArgumentList "`"$exePath`""

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  INSTALLATION AND LAUNCH SUCCESSFUL!                     " -ForegroundColor Green
Write-Host "  - Installed Path : $exePath" -ForegroundColor Gray
Write-Host "  - Desktop Icon   : Created" -ForegroundColor Gray
Write-Host "  - Start Menu     : Searchable as 'Klyro'" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Green
