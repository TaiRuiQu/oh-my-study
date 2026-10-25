# Architecture, build constraints, and verification

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Architecture-zh-CN.md)

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

## Responsibility follows the boundary

Domain defines what a valid document/question/grade means. Application controls when jobs run, pause, commit, and cancel. Infrastructure supplies SQLite, provider adapters, credentials, files, and processes. Documents handles rasterization, coordinates, formulas, and print generation. App translates interactions into service calls and hosts the Velopack update client. Packaging and release scripts live in `packaging/` and `.github/`; there is no separate Setup source project.

This separation means an invalid score is rejected by a shared rule rather than only by a particular textbox. It also lets tests exercise provider and storage behavior without opening the WinUI workspace.

## Trace a user action

Import starts in MainWindow.Library: it gathers model/mode choices and invokes media/storage work. A job is submitted with input and execution policy. JobQueue drives IJobRunner; GradingRunner requests model turns and tools. Saves cross JobContext into LibraryRepository and domain validation. View models and task events update the UI. Export takes persisted questions into the document service.

These are successive trust and persistence boundaries. The model does not receive the repository connection or a general-purpose save callback; it requests a defined tool operation whose arguments the application checks.

## Async does not mean every component is concurrent

The database has a single owner thread. AI jobs have a single worker. Media preparation and reading use their own services/cancellation. These choices serialize sensitive mutations while keeping expensive work off the UI thread; they do not promise arbitrary parallel recognition.

The MainWindow partial files group feature behavior. They coexist with view models rather than claiming the entire application is a pure declarative MVVM implementation.

## Native AOT and dependencies

The pinned SDK is in global.json. Package versions are centralized and locked for supported publish profiles. Source-generated JSON contexts enumerate serializable shapes, including polymorphic content, so trimming/AOT do not depend on discovering arbitrary types at runtime.

Native PDF, formula, print, imaging, and computation components must be packaged together. A portable app is a directory payload, not an isolated EXE. AOT, self-contained, and framework-dependent builds differ in runtime requirements while using the same domain concepts.

## Build and review

CI runs only for newly pushed tags. Each publish job directly builds, packages, and releases its Native AOT, self-contained, or framework-dependent variant. Automated tests, regression suites, smoke launch modes, and CI lint have been removed. Local whitespace lint has a 30-second limit. Review UI and PDF changes visually with realistic content.

Source: [OhMyStudy.slnx](../../OhMyStudy.slnx) · [Directory.Packages.props](../../Directory.Packages.props) · [global.json](../../global.json) · [Invoke-CI.ps1](../../packaging/Invoke-CI.ps1)

---

[Section contents](Tech-for-nerds.md) · [Next chapter](Tech-Document-Pipeline.md)
