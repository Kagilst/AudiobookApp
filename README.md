# AudiobookApp

A Windows desktop app for audiobooks and EPUB novels — local library, player, series, tags, and a full offline EPUB reader.

## Download (Windows x64)

**[Download portable zip](https://github.com/Kagilst/AudiobookApp/releases/latest/download/AudiobookApp-win-x64-portable.zip)**

1. Unzip the download  
2. Run `AudiobookApp.exe` in the extracted folder  
3. No installer or .NET install required  

Your library data is stored in `%LocalAppData%\AudiobookApp\`.

> The download link points at the latest GitHub Release asset named `AudiobookApp-win-x64-portable.zip`.

## Features

- **Library** — import audiobooks and EPUBs, covers, tags, filters, series  
- **Audiobook player** — transport, sleep timer, speed, mini player  
- **EPUB reader** — chapters/TOC, bookmarks, search, text-to-speech  
- **Highlights & notes** — colored highlights, margin notes, expand, nearby-note tabs  
- **Offline dictionary** — select a word → right-click → **Look up…** (Princeton WordNet, English)

## Requirements

- Windows 10 version 1809 (build 17763) or newer (x64)

## Build from source

See [docs/DISTRIBUTION.md](docs/DISTRIBUTION.md) for portable + MSIX packaging (`tools/Publish-Release.ps1`).
