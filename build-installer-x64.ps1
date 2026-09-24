<#
.SYNOPSIS
    Builds the standalone self-extracting Windows x64 installer executable (Setup.exe) for Klyro.
.DESCRIPTION
    Compiles Klyro.App (Release, win-x64 self-contained), packages application binaries into the
    embedded installer payload, and compiles Klyro.Installer into a single-file x64 executable:
    dist\Klyro-Setup-v<Version>-win-x64.exe
.PARAMETER Version
    The semantic version string (e.g. 1.0.0). Defaults to 1.0.0.
#>

param(
    [string]$Version = "1.0.0",
    [switch]$SkipAppPublish
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
Set-Location $ScriptDir

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "  Building Standalone Windows x64 Installer: Klyro v$Version " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$dotnetExe = if ($env:USERPROFILE -and (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe")) {
    "$env:USERPROFILE\.dotnet\dotnet.exe"
} else {
    "dotnet"
}

# 1. Publish Klyro.App Self-Contained Binaries
$appPublishDir = Join-Path $ScriptDir "artifacts\publish"
if (-not $SkipAppPublish -or (-not (Test-Path (Join-Path $appPublishDir "Klyro.App.exe")))) {
    Write-Host "`n[1/4] Publishing Klyro.App self-contained win-x64 binaries..." -ForegroundColor Yellow
    if (Test-Path $appPublishDir) {
        Remove-Item $appPublishDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    & $dotnetExe publish "$ScriptDir\src\Klyro.App\Klyro.App.csproj" `
        -c Release `
        -r win-x64 `
        -p:Platform=x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:Version=$Version `
        -o $appPublishDir

    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish Klyro.App binaries."
    }
} else {
    Write-Host "`n[1/4] Using existing Klyro.App binaries in $appPublishDir..." -ForegroundColor Yellow
}

# 2. Package Binaries into Embedded Payload Archive
Write-Host "`n[2/4] Packaging embedded payload archive (app.payload.zip)..." -ForegroundColor Yellow
$resourcesDir = Join-Path $ScriptDir "src\Klyro.Installer\Resources"
if (-not (Test-Path $resourcesDir)) {
    New-Item -Path $resourcesDir -ItemType Directory -Force | Out-Null
}

$payloadZip = Join-Path $resourcesDir "app.payload.zip"
if (Test-Path $payloadZip) {
    Remove-Item $payloadZip -Force -ErrorAction SilentlyContinue
}

# Clean any ephemeral logs from publish dir before packaging
Get-ChildItem -Path $appPublishDir -Filter "*.log" -ErrorAction SilentlyContinue | Remove-Item -Force

Compress-Archive -Path "$appPublishDir\*" -DestinationPath $payloadZip -CompressionLevel Fastest
$zipSizeMb = [math]::Round((Get-Item $payloadZip).Length / 1MB, 2)
Write-Host "Payload archive created ($zipSizeMb MB)." -ForegroundColor Gray

# 3. Compile Standalone Single-File Windows x64 Setup Executable
Write-Host "`n[3/4] Compiling standalone single-file x64 installer executable..." -ForegroundColor Yellow
$installerBuildDir = Join-Path $ScriptDir "dist\installer-build"
if (Test-Path $installerBuildDir) {
    Remove-Item $installerBuildDir -Recurse -Force -ErrorAction SilentlyContinue
}

$distDir = Join-Path $ScriptDir "dist"
if (-not (Test-Path $distDir)) {
    New-Item -Path $distDir -ItemType Directory -Force | Out-Null
}

& $dotnetExe publish "$ScriptDir\src\Klyro.Installer\Klyro.Installer.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:Version=$Version `
    -o $installerBuildDir

if ($LASTEXITCODE -ne 0) {
    throw "Failed to compile Klyro.Installer executable."
}

$finalExeName = "Klyro-Setup-v$Version-win-x64.exe"
$finalExePath = Join-Path $distDir $finalExeName
$standardExePath = Join-Path $distDir "Klyro-Setup-x64.exe"

Move-Item -Path "$installerBuildDir\Klyro.Installer.exe" -Destination $finalExePath -Force
Copy-Item -Path $finalExePath -Destination $standardExePath -Force
Remove-Item -Path $installerBuildDir -Recurse -Force -ErrorAction SilentlyContinue

# 4. Generate SHA256 Checksum
Write-Host "`n[4/4] Generating SHA256 checksum..." -ForegroundColor Yellow
$hash = Get-FileHash -Path $finalExePath -Algorithm SHA256
$hashFile = "$finalExePath.sha256"
"$($hash.Hash)  $finalExeName" | Out-File $hashFile -Encoding utf8

$fileSizeMb = [math]::Round((Get-Item $finalExePath).Length / 1MB, 2)

Write-Host ""
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  STANDALONE WINDOWS x64 INSTALLER CREATED SUCCESSFULLY!  " -ForegroundColor Green
Write-Host "==========================================================" -ForegroundColor Green
Write-Host "  Installer Executable : $finalExePath" -ForegroundColor White
Write-Host "  Convenience Alias    : $standardExePath" -ForegroundColor White
Write-Host "  File Size            : $fileSizeMb MB" -ForegroundColor White
Write-Host "  SHA256 Checksum      : $($hash.Hash)" -ForegroundColor White
Write-Host "  Checksum File        : $hashFile" -ForegroundColor White
Write-Host "==========================================================" -ForegroundColor Green
