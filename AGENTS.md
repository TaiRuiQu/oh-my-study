# Agent Guide

Oh My Study is a Windows study app built with C# 14, .NET 10, and WinUI. It imports exams, recognizes and grades questions, manages study collections, and exports review PDFs.

## Design principles

- Keep study materials local by default. Make provider uploads, recognition progress, and estimated costs clear to users; never log credentials.
- Preserve original documents and user edits. Treat AI output as reviewable suggestions and validate it before persistence.
- Favor a focused, accessible interface with consistent controls, readable mathematics, and responsive long-running operations.
- Preserve library compatibility and user data when changing storage, migrations, import, export, or installation behavior.
- Keep the interface concise and the logs detailed. When an operation failed, print all information needed to debug it. For example, when model returned a tool call that failed, print its params and detailed error log.

## Code philosophy

- Prefer simple, explicit code and small, cohesive changes. Reuse existing patterns before adding abstractions or dependencies.
- Use English for source text and diagnostics. Keep translations in localization catalogs and preserve multilingual fixtures and legacy identifiers.
- Keep domain rules in `Domain`, orchestration and contracts in `Application`, external integrations and storage in `Infrastructure`, document processing in `Documents`, and UI in `App`. Velopack integration belongs in `App`; packaging and release automation belong in `packaging` and `.github`.
- Maintain Native AOT and trimming compatibility. Use source-generated JSON serialization and avoid reflection-dependent designs; fix compatibility warnings rather than suppressing them.
- Follow `.editorconfig`: UTF-8, LF, four-space C# indentation, and two-space project/config indentation. Respect nullable annotations and existing naming conventions.
- Use asynchronous I/O and cancellation where supported. Surface actionable errors and dispose native, file, and database resources reliably.
- You should write no comments.

## Working and verification

- Use the SDK pinned in `global.json`; keep package versions centralized in `Directory.Packages.props` and preserve lock files.
- Restore with `dotnet restore OhMyStudy.slnx -p:Configuration=Release --locked-mode`. Native prerequisites and setup are documented in `.github/workflows/ci.yml` and its setup action.
- Use `./packaging/Invoke-CI.ps1 -Phase Lint`, `-Phase Checks`, and `-Phase Test` as appropriate. Tests are executable projects; the CI test phase verifies published Native AOT executables.
- Add focused regression coverage for changed behavior. Check affected UI and document output visually when relevant.
- Prioritize rendered visual inspection for PDF and UI layout changes. Use realistic content and inspect spacing, baselines, wrapping, and pagination; passing code tests does not establish visual quality. Avoid repeated local code-test runs when visual review is requested.
- Update English and Chinese user documentation together when workflows change. Keep generated artifacts out of commits and preserve unrelated work.
- Report changes, verification performed, and any checks that could not run.
- Before adding complexity for a speculative or vanishingly unlikely race or security edge case, explain the concrete failure mode, likelihood, and complexity cost to the user and get their buy-in. Do not silently expand scope for theoretical robustness.
