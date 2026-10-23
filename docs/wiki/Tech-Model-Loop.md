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

## Bounds and recovery

A task has at most 60 model turns and a linked 20-minute deadline. Two consecutive text-only replies receive continuation prompts; the third fails. Invalid tool arguments get correction feedback; successful entries must not be repeated merely because another entry failed.

Transient network, interrupted-stream, service, and rate-limit failures allow up to four attempts per round. Delays are normally 1, 2, and 4 seconds; Retry-After can extend a wait to 30 seconds. Incomplete responses are not executed. Authentication, invalid parameters, refusals, and explicit output-limit failures are not automatically retried. Earlier completed rounds remain intact; unreported retry usage makes cost unknown.

## File uploads and caching

Files API support is opt-in per provider and snapshotted for new tasks. Images are deduplicated by MIME type and content hash within the client session, including page crops and generated images. Responses uploads use `purpose=user_data`, `expires_after[anchor]=created_at`, and `expires_after[seconds]=3600`; Messages uses its own upload shape with `expires_in_seconds=3600`. Model calls use file IDs instead of resending base64 data. Cleanup attempts every uploaded file after success, failure, or cancellation. Rejected uploads fail explicitly; there is no silent inline or permanent-upload fallback.

OpenRouter uses a sticky endpoint session; Messages explicitly caches fixed system instructions and automatically caches the growing conversation. Gemini on OpenRouter uses implicit caching. Cache behavior still depends on the provider and model.

Usage records contain configured rates, currency, reported token buckets, retry status, and estimates. Unknown counts/prices stay unknown. File storage and meaning-search charges are not recognition-token estimates.

Sources: [runner](../../src/OhMyStudy.Infrastructure/Grading/GradingRunner.cs), [tools](../../src/OhMyStudy.Infrastructure/Grading/GradingTools.cs), [retry activity](../../src/OhMyStudy.Infrastructure/Grading/ModelActivity.cs), [file session](../../src/OhMyStudy.Infrastructure/Providers/ProviderFileSession.cs), [protocol](../../src/OhMyStudy.Infrastructure/Providers/ProviderProtocol.cs).
