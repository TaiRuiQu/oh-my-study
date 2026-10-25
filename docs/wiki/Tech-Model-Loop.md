# Provider adapters, batch tools, streaming, and usage

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Model-Loop-zh-CN.md)

## One paper, one conversation

Responses and Messages use distinct SDK adapters behind a shared turn contract. The runner sends document metadata, saved questions without personal notes, and references; page images enter on demand. Model-specific prompt profiles are selected once per task. Unknown versions use family guidance, and unknown IDs use the generic profile. See [prompt profiles](../model-prompts.md).

Tools, instructions, and previous messages stay stable across rounds. Responses and Messages preserve their own opaque reasoning and tool-call context. Partial streamed text is activity feedback and cannot save a question.

| Tool | Contract |
| --- | --- |
| `read_pages` | Ordered batch of full pages/crops, with metadata, images, and local files. Only full reads count toward coverage. |
| `save_references` | Replace the complete reference list; match stable item keys and preserve keys used by attempts. |
| `save_paper_info` | Infer subject/totals while protecting manually edited paper information. Whole-paper tasks only. |
| `save_questions` | Save batches of shared stems and items, usually 4–8 questions. Each entry has its own result; successful entries stay committed when another fails. Whole-paper tasks only. |
| `python`, `shell` | Calculation/image processing under the task execution policy. Optional `outputs` returns up to eight generated images with reusable asset IDs. |
| `finish` | Reconcile full-page coverage and saved results; a successful call ends the task. |

Reference-only tasks include page reading, reference saving, computation, and finish, but cannot save questions or paper information. Code calls default to 60 seconds; `timeout_ms` accepts 1–300,000 milliseconds. Python shares notebook state within the task; PowerShell starts a fresh process per call.

Question content supports text, formulas, structured tables, images, and Mermaid flowcharts. Image and Mermaid `width_percent` is optional and ranges from 1 to 100. Omitted widths use a PDF default maximum of 9 cm, limited to the current column; explicit values override that default. Mermaid requires code and an original material region; generated images also retain evidence regions. Omitted widths remain unspecified in storage; previously saved numeric widths are preserved. Clear a numeric width in the question editor to restore the default. Prompts prefer Markdown/structured tables, Mermaid flowcharts, and Matplotlib PNGs over unclear crops, and document the exact font directives and `^`, `<`, `>` whole-cell merge markers. Mermaid output is limited to raw `flowchart`/`graph` code with simple nodes and labeled arrows so the bounded native renderer can display it.

## Bounds and recovery

A task has at most 60 model turns and a linked 20-minute deadline. Two consecutive text-only replies receive continuation prompts; the third fails. Invalid tool arguments get correction feedback; successful entries must not be repeated merely because another entry failed. Arguments that are not one JSON object, including truncated or malformed streams, are never executed and do not end the task: each such call is answered with a correction message so the model can retry.

Transient network, interrupted-stream, service, and rate-limit failures allow up to four attempts per round. Delays are normally 1, 2, and 4 seconds; Retry-After can extend a wait to 30 seconds. Incomplete responses are not executed. Authentication, invalid parameters, refusals, and explicit output-limit failures are not automatically retried. Earlier completed rounds remain intact. Unreported retry usage leaves that call unpriced, and the task total keeps the priced calls.

## File uploads and caching

Files API support is opt-in per provider and snapshotted for new tasks. Images are deduplicated by MIME type and content hash within the client session, including page crops and generated images. Responses uploads use `purpose=user_data`, `expires_after[anchor]=created_at`, and `expires_after[seconds]=3600`; Messages uses its own upload shape with `expires_in_seconds=3600`. Model calls use file IDs instead of resending base64 data. Cleanup attempts every uploaded file after success, failure, or cancellation. Rejected uploads fail explicitly; there is no silent inline or permanent-upload fallback.

OpenRouter uses a sticky endpoint session; Messages explicitly caches fixed system instructions and automatically caches the growing conversation. Gemini on OpenRouter uses implicit caching. Cache behavior still depends on the provider and model.

Codex mode is an opt-in provider flag and is snapshotted for new tasks. When the protocol is Responses and the flag is on, every turn sends `prompt_cache_key` set to the exam document id. The value stays constant for the task. It is omitted when the flag is off, the protocol is Messages, or the id is empty. The name is reserved, so an extra parameter with the same name is rejected.

Usage records contain configured rates, currency, reported token buckets, retry status, and estimates. A missing output count omits that charge. A missing input bucket, an inconsistent input split, a missing rate for a known nonzero bucket, or an unreported retry leaves that call's estimate unknown. The task total sums the known call estimates. File storage and meaning-search charges are not recognition-token estimates.

Sources: [runner](../../src/OhMyStudy.Infrastructure/Grading/GradingRunner.cs), [tools](../../src/OhMyStudy.Infrastructure/Grading/GradingTools.cs), [retry activity](../../src/OhMyStudy.Infrastructure/Grading/ModelActivity.cs), [file session](../../src/OhMyStudy.Infrastructure/Providers/ProviderFileSession.cs), [protocol](../../src/OhMyStudy.Infrastructure/Providers/ProviderProtocol.cs).
