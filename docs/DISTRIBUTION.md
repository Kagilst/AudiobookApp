# Distributing AudiobookApp

Build **both** a portable folder (zip) and an MSIX installer for Windows x64.

## Requirements

- Windows 10 version 1809 (build 17763) or newer
- .NET 8 SDK with Windows App SDK workloads for building
- Recipients of the **portable** zip need no extra runtime (self-contained)
- Recipients of the **MSIX** must allow sideloading (or trust your signing certificate)

## One-shot release build (recommended)

From the repo root:

```powershell
powershell -File tools\Publish-Release.ps1
```

This runs tests, builds the portable zip with a clean folder layout, and builds the MSIX.

Outputs:

- `dist\AudiobookApp-win-x64-portable.zip`
- `dist\AudiobookApp_1.0.2.0_x64.msix`

## Portable layout

The zip extracts to a tidy root:

```text
AudiobookApp.exe     ← double-click this (small launcher)
README.txt
App\                 ← program files, DLLs, dictionary, etc.
  AudiobookApp.exe
  …
```

The root `AudiobookApp.exe` is a tiny Native AOT launcher that starts `App\AudiobookApp.exe`. WinUI / Windows App SDK require those dependencies beside the real app binary, so they stay inside `App\`.

Library data lives in `%LocalAppData%\AudiobookApp\`.

### Manual portable commands

```powershell
dotnet publish AudiobookApp\AudiobookApp.csproj `
  -c Release -p:Platform=x64 -p:PublishProfile=win-x64 `
  -p:PublishTrimmed=false -p:PublishReadyToRun=true `
  -o dist\portable\win-x64\App

dotnet publish AudiobookApp.Launcher\AudiobookApp.Launcher.csproj `
  -c Release -r win-x64 -p:Platform=x64 --self-contained `
  -p:PublishAot=true -o dist\launcher-build

Copy-Item dist\launcher-build\AudiobookApp.exe dist\portable\win-x64\AudiobookApp.exe -Force

Compress-Archive -Path dist\portable\win-x64\* `
  -DestinationPath dist\AudiobookApp-win-x64-portable.zip -Force
```

## MSIX (sideload)

MSIX installs through Windows and is launched from **Start**. Users do not browse a folder of DLLs day-to-day; the package payload must keep the app executable next to its dependencies for the OS installer.

```powershell
dotnet publish AudiobookApp\AudiobookApp.csproj `
  -c Release `
  -p:Platform=x64 `
  -p:RuntimeIdentifierOverride=win-x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=false `
  -p:PublishTrimmed=false `
  -o dist\msix-build
```

The publish step writes something like:

`dist\msix\AudiobookApp_1.0.2.0_x64_Test\AudiobookApp_1.0.2.0_x64.msix`

Copy that file to `dist\` for a flat download layout.

Install notes for users:

1. Settings → Privacy & security → For developers → enable **Sideload apps** (or Installer signing).
2. Double-click the `.msix`, or: `Add-AppxPackage -Path .\AudiobookApp_1.0.2.0_x64.msix`
3. For public distribution, sign with a trusted code-signing certificate and set `AppxPackageSigningEnabled=true` with your PFX.

Bump `<Identity Version="..."/>` in `AudiobookApp\Package.appxmanifest` for each release (and the `$Version` in `tools\Publish-Release.ps1`).

## Verify before shipping

```powershell
dotnet test AudiobookApp.Tests\AudiobookApp.Tests.csproj -c Release -p:Platform=x64
dotnet build AudiobookApp\AudiobookApp.csproj -c Release -p:Platform=x64
```

Then run the manual checklist in `AudiobookApp.Tests\UI_AUTOMATION.md`.

## Trim warning

Release profiles enable `PublishTrimmed` by default. For distribution builds, pass **`-p:PublishTrimmed=false`** on the main app so WinUI / speech / media reflection keep working. The root launcher uses Native AOT trim separately and is safe to trim.
