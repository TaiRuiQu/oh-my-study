# Task progress, retries, and execution requests

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Tasks-zh-CN.md)

## Read task activity

One AI task runs at a time. Select a task to inspect state, page reads, saved-question count, activity, and usage details. The summary defaults to its content height; drag its divider to adjust the summary/log split. Percentage progress describes activity rather than remaining time.

Successful tools show their names. Running, waiting, and failed tools show their state; expand failed Python/PowerShell entries to copy actual output. Two consecutive text-only replies trigger continuation prompts; a third fails because prose cannot save questions.

Temporary network/stream/service/rate-limit failures retry the current round up to four attempts, normally after 1, 2, and 4 seconds. Retry-After can extend a wait to 30 seconds. Completed rounds/tools are retained, incomplete responses are not executed, and a round whose retry usage was not reported stays out of the cost total.

## Cancel, retry, or re-recognize

Cancel stops later work while keeping saved results. Minimizing continues work; exiting stops it. Unfinished jobs become interrupted after restart.

**Retry task** is available for failed/interrupted tasks. Choose model/thinking settings and confirm: it rolls back that run's question changes, references, paper information, generated images, and task files, then replaces the failed task with a fresh run, logs, usage, and permissions. Originals, earlier results, and notes on earlier questions are kept. Prior charges and code changes outside task folders cannot be undone.

Retry is refused while the same exam is busy, after a newer run, without older recovery records, or when saved questions were edited. Use **Re-recognize** in Exams to retain current results and edits in those cases. Both operations can incur new charges.

## Code execution policy

| Setting | Behavior |
| --- | --- |
| Ask every time | Review exact Python/PowerShell code before host execution; Allow later calls applies only to this task. Later keeps waiting. |
| Run in sandbox | Task-specific Python AppContainer, separate PowerShell AppContainer per call, copied inputs and private result folders. Network defaults off. |
| Run directly on this machine | Current default; runs with your Windows account's file/network access without per-call approval. |

Change settings before starting a task. A sandbox-unavailable start dialog explains proposed host execution; failures inside a working sandbox do not silently rerun on the host. AI provider uploads are independent of sandbox networking.

Python state persists between calls within one task, including ordinary exceptions. Timeout/cancellation/output overflow/worker exit resets it. PowerShell shares files, not variables. Generated images can be inspected and reused in questions and PDF exports.

## Local diagnostics

Debug and Release write `%LOCALAPPDATA%/OhMyStudy/logs/session-<timestamp>-<pid>.log`; `OHMYSTUDY_LOG_FILE` overrides the file. Failures retain request/tool arguments, provider rejection bodies, stream positions, and full exception chains. Credentials, authorization, cookies, and secret tokens are redacted. Logs may contain study text and code; inspect them before sharing. They are not uploaded automatically.

Sources: [task UI](../../src/OhMyStudy.App/MainWindow.Tasks.cs), [queue](../../src/OhMyStudy.Application/Jobs/JobQueue.cs), [retry](../../src/OhMyStudy.Infrastructure/Storage/LibraryJobRetry.cs).
