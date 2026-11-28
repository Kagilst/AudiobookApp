# Generates App.ico and Package logo PNGs from Assets/Brand/book-icon-source.jpg
# Usage (from repo root):  powershell -File tools\Generate-AppIcons.ps1

$ErrorActionPreference = "Stop"
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $RepoRoot

$project = Join-Path $RepoRoot "tools\GenerateAppIcons\GenerateAppIcons.csproj"
dotnet run --project $project -c Release -- $RepoRoot
if ($LASTEXITCODE -ne 0) { throw "GenerateAppIcons failed." }
