# Builds portable zip, Setup.exe (Inno), and MSIX sideload packages.
# Usage (from repo root):  powershell -File tools\Publish-Release.ps1
# Requires: .NET 8 SDK, Inno Setup 6 (ISCC.exe)

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$Version = "1.0.3.0"
$PortableRoot = Join-Path $RepoRoot "dist\portable\win-x64"
$PortableApp = Join-Path $PortableRoot "App"
$PortableZip = Join-Path $RepoRoot "dist\AudiobookApp-win-x64-portable.zip"
$SetupExe = Join-Path $RepoRoot "dist\AudiobookApp-Setup-x64.exe"
$MsixFlat = Join-Path $RepoRoot "dist\AudiobookApp_${Version}_x64.msix"
$DictDb = Join-Path $RepoRoot "AudiobookApp\Assets\Dictionary\wordnet-en.db"
$IssPath = Join-Path $RepoRoot "installer\AudiobookApp.iss"

function Resolve-Iscc {
    $cmd = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        "${env:LocalAppData}\Programs\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )
    foreach ($path in $candidates) {
        if (Test-Path $path) { return $path }
    }
    return $null
}

Write-Host "== Verify dictionary pack ==" -ForegroundColor Cyan
if (-not (Test-Path $DictDb)) {
    Write-Host "wordnet-en.db missing — building…" -ForegroundColor Yellow
    dotnet run --project (Join-Path $RepoRoot "tools\BuildWordNetDb\BuildWordNetDb.csproj") -c Release -- $DictDb
}

Write-Host "== Tests ==" -ForegroundColor Cyan
dotnet test (Join-Path $RepoRoot "AudiobookApp.Tests\AudiobookApp.Tests.csproj") -c Release -p:Platform=x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "Tests failed." }

Write-Host "== Portable: publish app into App\ ==" -ForegroundColor Cyan
if (Test-Path $PortableRoot) { Remove-Item $PortableRoot -Recurse -Force }
New-Item -ItemType Directory -Path $PortableApp -Force | Out-Null

dotnet publish (Join-Path $RepoRoot "AudiobookApp\AudiobookApp.csproj") `
  -c Release `
  -p:Platform=x64 `
  -p:PublishProfile=win-x64 `
  -p:PublishTrimmed=false `
  -p:PublishReadyToRun=true `
  -o $PortableApp `
  --nologo
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

if (-not (Test-Path (Join-Path $PortableApp "AudiobookApp.exe"))) {
    throw "App publish did not produce AudiobookApp.exe"
}
if (-not (Test-Path (Join-Path $PortableApp "Assets\Dictionary\wordnet-en.db"))) {
    throw "Dictionary missing from portable App folder"
}
if (-not (Test-Path (Join-Path $PortableApp "Assets\App.ico"))) {
    throw "App.ico missing from portable App folder — run tools\Generate-AppIcons.ps1"
}

Write-Host "== Portable: publish root launcher (Native AOT) ==" -ForegroundColor Cyan
$launcherOut = Join-Path $RepoRoot "dist\launcher-build"
if (Test-Path $launcherOut) { Remove-Item $launcherOut -Recurse -Force }

dotnet publish (Join-Path $RepoRoot "AudiobookApp.Launcher\AudiobookApp.Launcher.csproj") `
  -c Release `
  -r win-x64 `
  -p:Platform=x64 `
  --self-contained `
  -p:PublishAot=true `
  -o $launcherOut `
  --nologo
if ($LASTEXITCODE -ne 0) { throw "Launcher publish failed." }

Copy-Item (Join-Path $launcherOut "AudiobookApp.exe") (Join-Path $PortableRoot "AudiobookApp.exe") -Force
@"
AudiobookApp portable

Run AudiobookApp.exe in this folder.
The App folder contains the program files — you can ignore it.

Library data: %LocalAppData%\AudiobookApp\
"@ | Set-Content -Path (Join-Path $PortableRoot "README.txt") -Encoding UTF8

Write-Host "== Zip portable ==" -ForegroundColor Cyan
if (Test-Path $PortableZip) { Remove-Item $PortableZip -Force }
Compress-Archive -Path (Join-Path $PortableRoot "*") -DestinationPath $PortableZip -Force

Write-Host "== Setup.exe (Inno Setup) ==" -ForegroundColor Cyan
$iscc = Resolve-Iscc
if (-not $iscc) {
    throw "ISCC.exe not found. Install Inno Setup 6 (https://jrsoftware.org/isinfo.php) and retry."
}
if (-not (Test-Path $IssPath)) { throw "Missing $IssPath" }
if (Test-Path $SetupExe) { Remove-Item $SetupExe -Force }
& $iscc $IssPath
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed." }
if (-not (Test-Path $SetupExe)) { throw "Setup.exe not produced at $SetupExe" }

Write-Host "== MSIX ==" -ForegroundColor Cyan
dotnet publish (Join-Path $RepoRoot "AudiobookApp\AudiobookApp.csproj") `
  -c Release `
  -p:Platform=x64 `
  -p:RuntimeIdentifierOverride=win-x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=false `
  -p:PublishTrimmed=false `
  -o (Join-Path $RepoRoot "dist\msix-build") `
  --nologo
if ($LASTEXITCODE -ne 0) { throw "MSIX publish failed." }

$msixSrcDir = Join-Path $RepoRoot "AudiobookApp\AppPackages\AudiobookApp_${Version}_x64_Test"
$msixSrc = Join-Path $msixSrcDir "AudiobookApp_${Version}_x64.msix"
if (-not (Test-Path $msixSrc)) { throw "MSIX not found at $msixSrc" }

$msixDestDir = Join-Path $RepoRoot "dist\msix\AudiobookApp_${Version}_x64_Test"
New-Item -ItemType Directory -Path $msixDestDir -Force | Out-Null
Copy-Item -Path (Join-Path $msixSrcDir "*") -Destination $msixDestDir -Recurse -Force
Copy-Item $msixSrc $MsixFlat -Force

Write-Host ""
Write-Host "Done." -ForegroundColor Green
Write-Host "  Setup:    $SetupExe  (primary GitHub download)"
Write-Host "  Portable: $PortableZip"
Write-Host "            (root has AudiobookApp.exe + App\ + README.txt)"
Write-Host "  MSIX:     $MsixFlat  (sideload; optional)"
