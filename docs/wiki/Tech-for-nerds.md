# Tech for nerds

[Home](Home.md) · [Quick Start](Quick-Start.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Tech-for-nerds-zh-CN.md)

## Follow the implementation

- [Architecture, builds, and verification](Tech-Architecture.md)
- [Document preparation, page identity, and coordinates](Tech-Document-Pipeline.md)
- [Provider adapters, batch tools, streaming, and usage](Tech-Model-Loop.md)
- [Question items, grading invariants, and edit protection](Tech-Validation-Grading.md)
- [Job lifecycle, retry recovery, and code isolation](Tech-Jobs-Execution.md)
- [SQLite v6, assets, search indexes, and recovery](Tech-Storage-Recovery.md)
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

Import validates limits and preserves originals, then queues one paper in one model conversation. The model calls `read_pages`, `save_references`, `save_paper_info`, `save_questions`, optional `python`/`shell`, and `finish`. Reference-only tasks omit question/paper-information writes. Every save crosses typed validation and the active-job/cancellation boundary.

The question model is a shared stem plus ordered flat `QuestionItem` records. References target item keys, coverage targets question keys, and generated images keep original evidence. Storage is SQLite schema v6. Explicit task retry rolls back only the failed run when recovery checks permit it; re-recognition preserves current results and protected manual/teacher grades.

Recognition, file upload, optional background embedding, and reranking have distinct data paths. [Meaning search](Features-Search.md) uses cached embeddings, a local cosine scan, and optional remote reranking. Provider/search keys stay outside the library in Windows Credential Manager.

## Build and verification

The stack is C# 14, .NET 10, and WinUI. Use SDK 10.0.402 pinned by [global.json](../../global.json); package versions and locks are centralized. Native AOT/trimming require source-generated JSON and explicit supported contracts.

Follow [CI setup](../../.github/actions/setup-native/action.yml) and [workflow](../../.github/workflows/ci.yml):

```powershell
dotnet restore OhMyStudy.slnx -p:Configuration=Release --locked-mode
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
./packaging/Invoke-CI.ps1 -Phase Lint
./packaging/Invoke-CI.ps1 -Phase Checks
./packaging/Invoke-CI.ps1 -Phase Test
```

The executable tests cover domain, storage, providers, execution, grading, media/documents, integration, and packaging. Ordinary branch/PR runs use managed provider/grading SDK tests; tag/manual runs use full Native AOT validation, the complete Velopack installation/update/uninstall regression, and builds of all three distribution variants. Use `./packaging/Invoke-CI.ps1 -Phase Test -FullValidation` to run the complete test phase locally. App smoke fixtures provide separate visual evidence; printing still needs output inspection. See [architecture](Tech-Architecture.md) and [installation](../installation.md).

Keep regressions and behavior likely to break: persisted data compatibility, protocol serialization, recovery after failures, and Markdown/PDF output. Avoid assertions that repeat constants, prompt wording, control visibility, or simple UI state changes; inspect those visually during development. Check shared serialized schemas once per protocol and task mode instead of repeating the same assertions on every model turn.

Complete CI is capped at 300 named tests across regression, packaging, and installer checks. The same curated core runs on branches and tags. Use `./packaging/Invoke-MigrationChecks.ps1 -Extended` for the larger manual suite; it is never selected by release CI. Suite counts and the enforced total are written to `output/ci/test-budget.json`.
