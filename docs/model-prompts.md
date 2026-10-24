# Model prompt profiles

[Usage](usage.md) · [中文](model-prompts.zh-CN.md)

Each recognition or reference-answer task selects a prompt profile from the chosen model ID when execution starts. It tailors the instructions, context placement, and descriptions of the existing study tools. It keeps the selected model, API type, thinking settings, prices, tool operations, and argument validation.

Selection ignores letter case and recognizes gateway prefixes, common version separators, and dated model IDs. For example, `anthropic/claude-opus-5.5` and `claude-opus-5-5` select the same profile. A newer version without a specific profile uses its family's general guidance. Unrecognized names use the generic study prompt. Names are a routing hint, not proof of a model's developer or capabilities.

The profile is selected once per task. Tools, instructions, and earlier messages stay fixed across its rounds, preserving reusable cache prefixes. Gemini places its task after the document context. Selection is local and does not fetch documentation or make extra model requests at task startup.

Question saves normally contain 4–8 questions per call. Short, simple questions may use larger batches; long questions, many subquestions, diagrams, and detailed solutions use smaller batches. Save ready questions promptly and retry only failed entries.

## Supported profiles

| Model IDs | Prompt and tool guidance |
| --- | --- |
| GPT-4.1 and its variants | Explicit tool workflow, evidence before transcription, and a concrete example of the sequence of reads and saves. |
| GPT-5.4 | Compact execution contract, batched independent operations, and sequential dependencies. |
| GPT-5.4 mini/nano | Numbered workflow, explicit ambiguity handling, and recovery before dependent operations. |
| GPT-6 family, including GPT-6.1 | Follow through on the whole task, make routine decisions from evidence, respect document-data boundaries, and stop at validated completion. |
| Other IDs containing a separate `gpt` token | General OpenAI guidance with explicit operations and completion criteria. |
| OpenAI o1/o3/o4 families | Direct goals and constraints, internal reasoning, and useful solution explanations in saved fields. |
| Claude Haiku 5.5 | Explicit completion checklist, actual-result verification, and continued tool use through `finish`. |
| Claude Opus 5 | Concise explanations, bounded scope, and targeted inspection without extra review loops. |
| Claude Opus 5.5 | Avoid progress-only early stops; use crops or image processing for dense diagrams and small labels. |
| Claude Sonnet 5.5 | Carry through all requested parts, use exact tool arguments, inspect executed results, and avoid scope expansion. |
| Other Claude versions | General Claude guidance with explicit tool purposes, effects, and dependencies. |
| Gemini 2.5 | Multimodal grounding and examples of unknown scores and questions without subquestions. |
| Gemini 3 family | Direct structured instructions, explicit image-to-page relationships, and function calls without mandatory text preambles. |
| Other Gemini versions | General Gemini guidance with exact function arguments and the task after its context. |
| Unrecognized IDs | Generic study instructions and tool descriptions. |

Every profile retains source preservation, language and mathematics rules, notebook-style Python state, protected user edits, and server-side validation. Model-specific wording changes how the model uses these tools; library writes still pass through the same validated save operations.

## Paper information, solutions, and student responses

`save_paper_info.name` is an optional nullable title transcribed from the exam. Missing, null, or whitespace names retain the import name; user-edited paper information is protected. Renaming does not invalidate page snapshots or coverage, and failed-run retry restores the previous title.

Every readable item requests `answer` and `explanation`, including blank papers with `attempt=null`. The explanation contains the concise standard solution; the legacy `attempt.ai_solution` field remains readable but need not duplicate it. Unknown answers require a specific uncertainty rather than invented content. `attempt.error_analysis` is nullable and describes evidence-supported causes for incorrect or partial work; uncertain causes, correct answers, and absent work use null.

Supplementary pages use `role=student`. The model matches them to existing stable item keys and supplies `attempt.student_page_ids` for evidence without repeating question stems or creating duplicate questions. Manually entered answers are preserved while their AI grade and error analysis can be updated. Adding responses to blank papers enables automatic grading; changing an entered response clears its old result.

## Guidance and validation

Reviewed on 2026-10-23. The profiles adapt these official sources to exam organization and grading:

- OpenAI: [prompt engineering](https://developers.openai.com/api/docs/guides/prompt-engineering), [GPT-4.1 prompting](https://developers.openai.com/cookbook/examples/gpt4-1_prompting_guide), [GPT-5.4 prompting](https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5.4), [GPT-6 prompting](https://developers.openai.com/api/docs/guides/latest-model#prompting-best-practices), and [reasoning best practices](https://developers.openai.com/api/docs/guides/reasoning-best-practices).
- Anthropic: [general prompting](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices), [Haiku 5.5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-haiku-5-5), [Opus 5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5), [Opus 5.5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5-5), and [Sonnet 5.5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-sonnet-5-5).
- Google: [prompt design strategies](https://ai.google.dev/gemini-api/docs/prompting-strategies) and [function calling best practices](https://ai.google.dev/gemini-api/docs/function-calling#best-practices).

These are maintained templates rather than a claim of optimal recognition accuracy. Regression tests verify selection, unchanged business constraints, wire-format tool descriptions, task completion, and stable conversation prefixes with synthetic responses. Accuracy and cost improvements on real papers require evaluation with the selected provider and model. Add version-specific guidance only when supported by the model's documentation or measured behavior; keep the family fallback for other versions.
