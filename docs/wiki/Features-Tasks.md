# Task progress, retries, and execution requests

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Tasks-zh-CN.md)

## Read task activity

One exam task runs at a time, with questions processed in parallel inside it. Tasks opens automatically when processing starts. Five ordered steps show exam-format classification, upload and OCR, question splitting and material binding, independent parallel solving, and student-work grading. Each step shows its current state and output count; skipped steps such as grading a blank exam are marked as not required. The summary shows recognized pages, split questions and estimated model cost, excluding OCR and unpriced calls. Solving and grading can overlap across questions.

The per-question list shows solution and grading states independently, including waiting and not required; questions whose answers could not be matched or graded with confidence are marked for review. Blank exams skip grading; reference imports skip solving and grading. Expand Technical details and model records for model replies, controlled Python calculations, output and errors. The former agent conversation is no longer the main task interface. After processing stops, Export recognition materials saves a ZIP with full.md, images, OCR records, the split plan and available model results.

Choose splitting and optional grading models from saved providers in Settings → Exam workflow. The start dialog can change these models and select the solving provider/model. Provider addresses and keys are reused, selections are remembered, and each task retains its own snapshot. Model calls are not automatically replayed after failure.

## Cancel, retry, or re-recognize

Cancel stops later work while keeping saved results. Minimizing continues work; exiting stops it. Unfinished jobs become interrupted after restart.

**Retry task** is available for failed/interrupted tasks. Choose the workflow models and confirm: it rolls back that run's question changes, references, paper information, generated images, and task files, then starts a fresh workflow. Originals, earlier results and protected manual edits are kept. Previous charges remain.

Retry is refused while the same exam is busy, after a newer run, without older recovery records, or when saved questions were edited. Use **Re-recognize** in Exams to retain current results and edits in those cases. Both operations can incur new charges.

## Code execution policy

| Setting | Behavior |
| --- | --- |
| Ask every time | Review Python calculation execution before running; allowing later calls applies only to this task. |
| Run in sandbox | Executes the controlled calculator in a task-specific Python AppContainer. |
| Run directly on this machine | Runs the controlled calculator locally without per-call approval. |

Change settings before starting a task. A sandbox-unavailable start dialog explains proposed host execution; failures inside a working sandbox do not silently rerun on the host. AI provider uploads are independent of sandbox networking.

Only solving exposes the Python calculator. It accepts approved mathematical expressions and simple assignments, with no shell, imports, files or network operations. Calculation variables are local to each call. Reasoning and capability options are under Model and calculation options in the start dialog.

## Local diagnostics

Debug and Release write `%LOCALAPPDATA%/OhMyStudy/logs/session-<timestamp>-<pid>.log`; `OHMYSTUDY_LOG_FILE` overrides the file. Failures retain request/tool arguments, provider rejection bodies, stream positions, and full exception chains. Credentials, authorization, cookies, and secret tokens are redacted. Logs may contain study text and code; inspect them before sharing. They are not uploaded automatically.

Sources: [task UI](../../src/OhMyStudy.App/MainWindow.Tasks.cs), [queue](../../src/OhMyStudy.Application/Jobs/JobQueue.cs), [retry](../../src/OhMyStudy.Infrastructure/Storage/LibraryJobRetry.cs).
