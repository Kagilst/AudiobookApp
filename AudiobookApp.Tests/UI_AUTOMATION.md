# UI automation (follow-up)

This project currently covers **unit tests** for EPUB TTS helpers, sleep timer logic, reading settings persistence, and annotation JSON round-trips.

Full WinUI UI automation (Click / navigate pages) is deferred: the app is a single-project WinUI `WinExe`, which is awkward to host under UI test frameworks without extra packaging. A later pass can add WinAppDriver / Appium or a dedicated UI test harness once distribution artifacts are stable.

**Manual smoke checklist** (run before shipping):

1. Import audiobook + EPUB; library save/reload
2. Series create/reorder; ratings/tags
3. Full / collapsed / floating mini player (volume, speed, sleep, autoplay)
4. EPUB: TOC, named bookmark, search, highlight + note (margin notes), highlight list right-click edit/delete, remove via text context menu
5. EPUB Listen play/pause, Settings voice/volume/speed/sleep/stop, resume after reopen
