<#
.SYNOPSIS
    Launcher script for Klyro Secure Assessment Browser (Manual Testing).
.DESCRIPTION
    Launches the self-contained WinUI 3 Klyro application for manual verification.
#>

$publishDir = Join-Path $PSScriptRoot "src\Klyro.App\bin\Debug\net8.0-windows10.0.26100.0\win-x64\publish"
$exePath = Join-Path $publishDir "Klyro.App.exe"

if (-not (Test-Path $exePath)) {
    Write-Host "Publishing Klyro.App..." -ForegroundColor Cyan
    $dotnet = if (Test-Path "$HOME\.dotnet\dotnet.exe") { "$HOME\.dotnet\dotnet.exe" } else { "dotnet" }
    & $dotnet publish "$PSScriptRoot\src\Klyro.App\Klyro.App.csproj" -c Debug -r win-x64 --self-contained true
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Launching Klyro Secure Assessment Browser               " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Target: $exePath" -ForegroundColor Gray

Start-Process -FilePath $exePath
Write-Host "[OK] Klyro process initiated." -ForegroundColor Green
