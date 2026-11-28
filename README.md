# AudiobookApp

A Windows desktop app for audiobooks, EPUB novels, and comics — local library, player, series, tags, and offline readers.

## Download (Windows x64)

**[Download installer](https://github.com/Kagilst/AudiobookApp/releases/latest/download/AudiobookApp-Setup-x64.exe)**

1. Run `AudiobookApp-Setup-x64.exe`  
2. Open **AudiobookApp** from the Start menu or Windows Search  
3. No .NET install required  

Your library data is stored in `%LocalAppData%\AudiobookApp\`.

> The download link points at the latest GitHub Release asset named `AudiobookApp-Setup-x64.exe`.

**[Portable zip](https://github.com/Kagilst/AudiobookApp/releases/latest/download/AudiobookApp-win-x64-portable.zip)** — unzip and run `AudiobookApp.exe` (no installer).

## Features

- **Library** — import audiobooks, EPUBs, and CBZ comics; covers, tags, filters, series  
- **Audiobook player** — transport, sleep timer, speed, mini player  
- **EPUB reader** — chapters/TOC, bookmarks, search, highlights, text-to-speech  
- **Comics reader** — CBZ pages with LTR / RTL / top-to-bottom modes  
- **Offline dictionary** — select a word → right-click → **Look up…** (Princeton WordNet, English)

## Requirements

- Windows 10 version 1809 (build 17763) or newer (x64)

## Build from source

See [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md) for Setup.exe + portable + MSIX packaging (`tools/Publish-Release.ps1`).
