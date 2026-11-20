# Text Downloader

Windows 10/11 x64 desktop application, derived from the Windows UI and patterns of
bespalovnikita/video-downloader. The original repository is reference-only.

- App: C# / .NET 8 / WPF, `src/TextDownloader`. No Python or PowerShell runtime dependency.
- Download/extraction/export engine: cross-platform `src/TextDownloader.Core`.
- Tests: executable assertions in `tests/TextDownloader.Core.Tests`; real STA/WPF
  scenarios in `tests/TextDownloader.UiTests`, run on Windows.
- All network and expensive I/O must be asynchronous and cancellable, outside UI work.
- Source adapters must verify a complete, ordered story; cached pages do not prove success.
- EPUB/FB2 export must preserve text, validate files and publish atomically without
  overwriting unrelated files. Only successfully published results enter history.
- State, mutex, updates and resources belong to TextDownloader, never VideoDownloader.
- Secrets, actual downloaded stories, user profiles and build outputs must not enter Git.
- Build/Test/Publish entry point: `scripts/build.ps1`; Windows CI must pass before release.
- Keep documentation in Russian, describe implemented behavior and limitations accurately.
