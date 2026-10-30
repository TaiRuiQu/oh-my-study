# Tech for nerds

[Home](Home.md) · [Quick Start](Quick-Start.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Tech-for-nerds-zh-CN.md)

## Follow the implementation

- [Architecture, builds, and verification](Tech-Architecture.md)
- [Document preparation, page identity, and coordinates](Tech-Document-Pipeline.md)
- [OCR, question grouping, parallel solving, and assessment](Tech-Model-Loop.md)
- [Question items, grading invariants, and edit protection](Tech-Validation-Grading.md)
- [Job lifecycle, retry recovery, and code isolation](Tech-Jobs-Execution.md)
- [SQLite v7, assets, search indexes, and recovery](Tech-Storage-Recovery.md)
- [Native reading, formula assets, and PDF compilation](Tech-Rendering-Export.md)

## Project boundaries

| Project | Responsibility |
| --- | --- |
| Domain | Typed questions/items, grades, provider contracts, validation, source-generated JSON. |
| Application | Job queue, authorization, orchestration, search contracts/service. |
| Infrastructure | SQLite, credentials, provider SDK adapters, file uploads, grading tools, processes, search indexing. |
| Documents | PDFium rasterization, coordinates, VelociTex mathematics, Typst export. |
| App | WinUI workspace, structured editors, list/detail navigation, localization, Velopack update client, visual smoke fixtures. |
| Packaging and CI | Velopack packaging and release scripts in `packaging/` and `.github/`; builds Setup, portable ZIP, and update packages. |

## Processing and persistence

Import validates limits and preserves originals, then queues a fixed workflow rather than one agent conversation. A format model classifies the pages, MinerU extracts Markdown/images/tables, a splitting model groups questions and binds materials, the solving model answers each question independently with only the controlled `python` tool, and a separate assessment call grades original answer crops. Reference-only tasks skip solving and grading. Every save crosses typed validation and the active-job/cancellation boundary.

The question model is a shared stem plus ordered flat `QuestionItem` records. References target item keys, coverage targets question keys, and extracted figures keep original evidence. Storage is SQLite schema v7, which adds `workflow_artifacts` for OCR output and stage JSON. Explicit task retry rolls back only the failed run when recovery checks permit it; re-recognition preserves current results and protected manual/teacher grades.

Recognition, file upload, optional background embedding, and reranking have distinct data paths. [Meaning search](Features-Search.md) uses cached embeddings, a local cosine scan, and optional remote reranking. Provider/search keys stay outside the library in Windows Credential Manager.

## Build and visual review

Use the SDK pinned in `global.json`. CI runs only for newly pushed tags and builds all three distribution variants inside their publish jobs; it runs no lint, tests, or regression suites.

```powershell
dotnet restore OhMyStudy.slnx -p:Configuration=Release --locked-mode
./packaging/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
dotnet build OhMyStudy.slnx -c Release --no-restore
./packaging/Invoke-Lint.ps1
```

Lint is local only, checks whitespace in changed files, and stops within 30 seconds. Review UI and rendered PDF output visually with realistic content. Use `./packaging/Invoke-CI.ps1 -Phase Build` to package the app locally.

Use the shared Windows API capture script for a one-time UI review. It starts the specified executable, saves a PNG, and closes the process it started, including when capture fails:

```powershell
./scripts/Capture-AppWindow.ps1 -ExecutablePath ./src/OhMyStudy.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/OhMyStudy.App.exe -Destination ./output/ui-review/main-window.png
```

`-DelaySeconds` controls the rendering wait after the window opens. `-ArgumentList` passes application arguments; use `--ui-process` for a Debug build to capture the UI process directly. An optional `-PrepareWindow` script block receives the started process before capture, for opening the view being reviewed. `-ProcessId` captures an already running instance instead of starting one. `-Width`/`-Height` resize the window first and `-Cursor` includes the pointer. Keep generated screenshots under `output`; the script remains in `scripts` when outputs are cleaned.

The script drives `tools/ScreenCapture`, which reads frames with Windows Graphics Capture through Direct3D 11. It does not need the window in front and does not raise or activate it, so an obscured or background window is captured correctly and the desktop focus does not change during a review. Run it directly for ad-hoc captures:

```powershell
dotnet run --project tools/ScreenCapture -c Release -- --process 1234 --destination output/ui-review/tasks.png
```
