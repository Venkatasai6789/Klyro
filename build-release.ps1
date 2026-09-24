<#
.SYNOPSIS
    Builds and packages a production release of Klyro for win-x64.

.DESCRIPTION
    Compiles Klyro.sln in Release mode, runs all test suites, publishes the self-contained
    WinUI 3 application, and packages the executable and runtime files into a distribution zip.

.PARAMETER Version
    The semantic version string (e.g. 1.0.0). Defaults to 1.0.0.

.EXAMPLE
    .\build-release.ps1 -Version "1.0.0"
#>
param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ScriptDir

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Building Klyro Secure Assessment Browser v$Version" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Restore & Test
Write-Host "`n[1/4] Running test suites..." -ForegroundColor Yellow
dotnet test "$ScriptDir\Klyro.sln" -c Release --verbosity normal
if ($LASTEXITCODE -ne 0) {
    Write-Error "Test suite failed. Aborting release build."
}

# 2. Publish
$OutputDir = "$ScriptDir\dist\Klyro-v$Version-win-x64"
Write-Host "`n[2/4] Publishing self-contained win-x64 application to $OutputDir..." -ForegroundColor Yellow
if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}

$dotnetExe = if ($env:USERPROFILE -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) {
    "$env:USERPROFILE\.dotnet\dotnet.exe"
} else {
    "dotnet"
}

& $dotnetExe publish "$ScriptDir\src\Klyro.App\Klyro.App.csproj" `
    -c Release `
    -r win-x64 `
    -p:Platform=x64 `
    --self-contained true `
    -p:PublishSingleFile=false `
    -p:Version=$Version `
    -o $OutputDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "Publish of Klyro.App failed."
}

# 2b. Publish Klyro Setup Wizard
Write-Host "`nPublishing Klyro Setup Wizard..." -ForegroundColor Yellow
& $dotnetExe publish "$ScriptDir\src\Klyro.Installer\Klyro.Installer.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -o "$OutputDir\Setup"

if (Test-Path "$OutputDir\Setup\Klyro.Installer.exe") {
    Move-Item "$OutputDir\Setup\Klyro.Installer.exe" "$OutputDir\Klyro.Setup.exe" -Force
    Remove-Item "$OutputDir\Setup" -Recurse -Force -ErrorAction SilentlyContinue
}

# 3. Compress Archive
$ZipPath = "$ScriptDir\dist\Klyro-v$Version-win-x64.zip"
Write-Host "`n[3/4] Creating distribution archive: $ZipPath..." -ForegroundColor Yellow
if (Test-Path $ZipPath) {
    Remove-Item $ZipPath -Force
}
Compress-Archive -Path "$OutputDir\*" -DestinationPath $ZipPath -Force

# 4. Checksums
Write-Host "`n[4/4] Generating SHA256 checksum..." -ForegroundColor Yellow
$Hash = Get-FileHash -Path $ZipPath -Algorithm SHA256
$HashFile = "$ZipPath.sha256"
"$($Hash.Hash)  $([System.IO.Path]::GetFileName($ZipPath))" | Out-File $HashFile -Encoding utf8

Write-Host "`nRelease build complete!" -ForegroundColor Green
Write-Host "Executable:       $OutputDir\Klyro.App.exe" -ForegroundColor White
Write-Host "Release Archive:  $ZipPath" -ForegroundColor White
Write-Host "SHA256 Checksum:  $($Hash.Hash)" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Cyan
