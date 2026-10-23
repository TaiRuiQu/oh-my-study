# Job lifecycle, retry recovery, and code isolation

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Jobs-Execution-zh-CN.md)

## Queue and durable commits

JobQueue has a bounded channel and a single AI worker. JobContext mediates progress, activity, commits, cancellation, and authorized execution. The repository checks active-job state and the document page/order/rotation snapshot before grading writes.

Cancellation stops later work and preserves earlier successful commits. On startup unfinished persisted jobs become interrupted; model conversations and old permissions are not replayed.

## Explicit task retry

Only failed or interrupted jobs can be retried. Retry uses recorded before/after changes to roll back that run's question writes, references, paper information, generated assets, and task files, then atomically replaces the old job with a queued job. It removes that job's usage/log records and obtains current model options and a new execution policy.

The repository refuses retry while the exam is busy, after a newer grading run, without recovery records for a run that began, or when rollback would overwrite edited question data. Notes on earlier questions are retained. Use Re-recognize to preserve current results when retry is refused. Remote charges and host code changes outside task folders cannot be rolled back.

## Authorization and execution state

The permission broker binds approval to an immutable serialized call and digest. Calls are consumed once; cancellation revokes authorization. Task-wide grants do not survive restart. The runner supplies the working directory.

Direct host execution is the default. Ask mode requires a decision before running on the host. Sandbox mode runs Python in a task-specific AppContainer and PowerShell in a separate AppContainer per call, with copied inputs, a readable private runtime, and dedicated writable output folders. Networking defaults off. Request/response identity, nonce, and digest checks bind results to the authorized call.

Python imports, variables, functions, and images persist within a task, including after ordinary exceptions. Timeout, cancellation, output overflow, or worker exit resets the namespace. PowerShell shares files but not process variables. Generated PNG/JPEG/WebP outputs can become managed question assets while source evidence remains intact.

Sandbox availability is checked before submission and a proposed host fallback is disclosed. Failure inside an available sandbox never silently retries on the host. Process cleanup is bounded; a failed cleanup may require restart. Provider requests remain independent of sandbox network permission.

Sources: [queue](../../src/OhMyStudy.Application/Jobs/JobQueue.cs), [retry rollback](../../src/OhMyStudy.Infrastructure/Storage/LibraryJobRetry.cs), [permissions](../../src/OhMyStudy.Application/Execution/PermissionBroker.cs), [Python sessions](../../src/OhMyStudy.Infrastructure/Execution/PythonSessionExecutor.cs), [sandbox](../../src/OhMyStudy.Infrastructure/Execution/SandboxToolExecutor.cs).
