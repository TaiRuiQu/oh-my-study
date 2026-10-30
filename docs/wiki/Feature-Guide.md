# Feature Guide

[Home](Home.md) · [Quick Start](Quick-Start.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Feature-Guide-zh-CN.md)

## Choose a workflow

- [Providers, models, thinking, uploads, and costs](Features-Models.md)
- [Import modes, limits, and page management](Features-Import.md)
- [Reading, navigation, and structured editing](Features-Workspace-Editing.md)
- [Text and meaning search](Features-Search.md)
- [Reference answers, grading evidence, and re-recognition](Features-References.md)
- [Task progress, retries, and execution requests](Features-Tasks.md)
- [PDF editions, layouts, images, and formula checks](Features-Export.md)
- [Library location, backups, recovery, and deletion](Features-Library.md)
- [Troubleshooting, appearance, and language](Features-Troubleshooting.md)

## Current behavior at a glance

Configure a Responses or Messages provider and model before importing PDF, PNG, JPEG, or WebP. Limits are 12 combined exam/student-answer/reference pages, 64 MiB per source file, 256 MiB per import batch, and 40 million pixels per input image. Automatic detection lets the model classify the paper itself; blank exams have no attempts; answered exams retain student work; teacher mode protects explicit teacher grades.

Question pages show a shared stem followed by flat, independently graded items. Search saved questions locally or configure meaning search with separate embedding/reranking services. Review Needs review separately from Mistakes; uncertainty alone does not establish a wrong answer.

Recognition uses batched page reads and question saves. Failed or interrupted tasks can be retried with rollback checks; Re-recognize preserves current results for a new run. Save edits and notes before exporting question and answer editions separately on A4 portrait or A3 landscape. Export is local; AI recognition and meaning search send data to configured services and may incur charges.

The default code execution mode runs on the host without per-call approval. Change it to Ask every time or Run in sandbox before submitting new tasks. Files API support defaults off; compatible uploads use one-hour expiration and task-end deletion attempts. Codex mode also defaults off and, for Responses providers, sends a stable `prompt_cache_key` on new tasks. See the models and tasks chapters for details.

The library is local and currently uses schema v7. Back up the complete folder after exit before upgrading or broad reprocessing. Keys stay in Windows Credential Manager. See [installation](../installation.md), [usage](../usage.md), and [model prompt profiles](../model-prompts.md).
