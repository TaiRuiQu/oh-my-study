# Architecture, build constraints, and verification

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Architecture-zh-CN.md)

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

## Responsibility follows the boundary

Domain defines what a valid document/question/grade means. Application controls when jobs run, pause, commit, and cancel. Infrastructure supplies SQLite, provider adapters, credentials, files, and processes. Documents handles rasterization, coordinates, formulas, and print generation. App translates interactions into service calls. Setup owns installation behavior.

This separation means an invalid score is rejected by a shared rule rather than only by a particular textbox. It also lets tests exercise provider and storage behavior without opening the WinUI workspace.

## Trace a user action

Import starts in MainWindow.Library: it gathers model/mode choices and invokes media/storage work. A job is submitted with input and execution policy. JobQueue drives IJobRunner; GradingRunner requests model turns and tools. Saves cross JobContext into LibraryRepository and domain validation. View models and task events update the UI. Export takes persisted questions into the document service.

These are successive trust and persistence boundaries. The model does not receive the repository connection or a general-purpose save callback; it requests a defined tool operation whose arguments the application checks.

## Async does not mean every component is concurrent

The database has a single owner thread. AI jobs have a single worker. Media preparation and reading use their own services/cancellation. These choices serialize sensitive mutations while keeping expensive work off the UI thread; they do not promise arbitrary parallel recognition.

The MainWindow partial files group feature behavior. They coexist with view models rather than claiming the entire application is a pure declarative MVVM implementation.

## Native AOT and dependencies

The pinned SDK is in global.json. Package versions are centralized and locked for supported publish profiles. Source-generated JSON contexts enumerate serializable shapes, including polymorphic content, so trimming/AOT do not depend on discovering arbitrary types at runtime.

Native PDF, formula, print, imaging, and computation components must be packaged together. A portable app is a directory payload, not an isolated EXE. AOT, AVX512 AOT, self-contained, and framework-dependent builds differ in CPU/runtime requirements while using the same domain concepts.

## Verification maps to risk

| Boundary | Relevant verification |
| --- | --- |
| Grade/content rules | Domain and grading executable tests. |
| Durable data and upgrades | Storage/integration and migration checks. |
| Remote protocols | Provider tests, including parsing/usage behavior. |
| Code execution | Execution tests and native runtime checks. |
| Rendering/export | Media/document tests, probes, and visual acceptance. |
| Installation | Setup and MSI/package regression checks. |

CI runs lint/check/test/build phases through packaging/Invoke-CI.ps1. Ordinary branch/PR runs use managed provider/grading SDK suites; tag/manual runs enable full Native AOT verification; desktop visuals and physical printing need their own acceptance. Documentation should state which checks actually ran, not equate the existence of a suite with a successful run.

Source: [OhMyStudy.slnx](../../OhMyStudy.slnx) · [Directory.Packages.props](../../Directory.Packages.props) · [global.json](../../global.json) · [Invoke-CI.ps1](../../packaging/Invoke-CI.ps1)

---

[Section contents](Tech-for-nerds.md) · [Next chapter](Tech-Document-Pipeline.md)
