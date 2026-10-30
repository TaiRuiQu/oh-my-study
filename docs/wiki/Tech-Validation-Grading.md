# Question schema, grading invariants, and preservation rules

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Validation-Grading-zh-CN.md)

## Shared stem and ordered items

A `Question` has a stable `draft_key`, printed number, shared `stem`, source regions, subject/type, knowledge points, uncertainties, notes, and an ordered `items` list. Each `QuestionItem` owns its stable `key`, printed label, prompt, options, points, standard answer, explanation, and optional attempt. A question without subquestions still has one item, usually with an empty label and prompt; its text belongs in the stem.

There are 1–100 items per question. Nested printed labels are flattened, for example `(2)(a)`; the current format has no recursive `Parts` tree. Multi-item questions need nonempty labels. Item keys are unique across the paper, while coverage uses top-level draft keys and reference matches use item keys.

Text, formula, table, and image blocks remain typed. Images can reference a processed `asset_id` while retaining the original source region. Workflow regions use fractional `x/y/width/height` on the full unrotated source page. MinerU 0–1000 corners are converted at the OCR boundary, including inverse display rotation.

## Validation before persistence

Model-output normalization accepts common formatting variants and records ignored extra fields before schema validation. Persisted JSON contracts reject extra fields; typed deserialization checks shape, and domain/repository validation checks ownership and meaning. Questions need a number, nonempty stem, material-page sources, valid stable keys of at most 128 UTF-8 bytes, and at most 1 MiB of serialized content.

| Input | Result |
| --- | --- |
| Partial, 2 of 4 | Valid score/verdict combination. |
| Partial, 4 of 4 | Rejected; partial requires a score strictly between zero and maximum. |
| Uncertain, earned 0 | Rejected; uncertain earned points must be unknown. |
| Earned score with no maximum | Rejected. |
| Blank-mode item with an attempt | Rejected; blank papers do not create unanswered grades. |
| Reference evidence on a material page | Rejected; reference sources require reference pages. |

Question maximum points are the sum of item points only when every item maximum is known. Earned points are summed only when every item earned score is known. There is no separate editable parent grade or parent/child total to reconcile.

## Evidence and protected edits

An attempt separates student work, teacher annotations, matched reference, AI solution/conflict, and grade. Provenance is `Teacher`, `AiReference`, `AiSolution`, or `Manual`. Existing manual grades are protected; teacher and automatic modes also preserve explicit teacher results and evidence. AI cannot create a manual provenance label or remove a protected item.

Standard answers and solutions belong in item `answer` and `explanation`, including blank papers with no attempt. Legacy `attempt.ai_solution` remains readable. Nullable `attempt.error_analysis` describes evidence-supported causes of incorrect/partial work, separately from the grade reason.

Supplementary response pages use `role=student`. The splitting plan and assessment carry normalized `student_regions`; the question's sources still require material pages. Matching reuses existing item keys. `SaveStudentAnswersAsync` marks changed responses edited, clears stale assessment evidence, and sets an uncertain grade; it changes blank mode to automatic detection and clears the paper earned score. AI preserves the entered response while updating unprotected assessment fields.

Split evidence accounting includes student-page OCR blocks and detected regions fully contained in `matched` or `blank` item `answer_bindings`. They do not need a duplicate entry in `ignored_*_ids`. Material-page evidence still requires explicit question or ignored IDs. Missing or unknown evidence IDs reject the plan, with the missing evidence and full response recorded in the local diagnostic log.

The splitter receives the effective import mode after layout classification and its `allowed_answer_binding_statuses`. Answered exams require `matched`, `blank`, or `unmatched` for every item; clean printed question pages do not disable assessment of separate answer sheets. `none` is reserved for blank mode or items without student work in unresolved automatic mode. Split validation failures record the effective mode, reason, and full response in the local diagnostic log.

Item `answer_bindings.block_ids` can reference response OCR blocks directly. The runner resolves those IDs to their original regions before validation and assessment, avoiding copied or incorrectly merged coordinates. Additional response areas without OCR evidence still use `regions`. Unknown IDs and invalid page roles are rejected; older plans without `block_ids` remain readable.

An invalid split plan is returned to the splitter with its validation error and evidence IDs for correction, up to three total attempts. Each attempt retains its response artifact and records model usage and estimated cost. Questions are saved only after the plan passes validation; a third invalid plan fails the task.

Independent solution inputs omit attempt, answer, and explanation fields. Responses calls use strict JSON Schema output generated from the source-generated serialization metadata. Direct DeepSeek calls use the Responses schema field too. Local schema references are expanded and nullable objects use explicit alternatives for provider compatibility. Common envelopes, field aliases, collection forms, numeric strings, score fractions, and normalized corner coordinates are normalized before validation; malformed layout results can be corrected within three attempts. Invalid solution JSON or changed item keys can be corrected within the configured question-round limit. Assessment shape, item keys, evidence regions, and domain grading rules are validated before accepting a response, with at most three attempts. Missing subquestion maximum points may be estimated using exam-setting conventions, relative difficulty, solution steps and printed question totals. The estimate becomes the item maximum when no existing maximum is supplied; numeric grades must remain consistent with it. Existing maximum points are preserved. Every response and validation error is retained, and correction calls count toward model usage and estimated cost.

For Responses reasoning at `high` or `max` effort, the default output budget is 65,536 tokens, capped by the model's output limit. An explicitly configured output-token limit still takes precedence. Incomplete Responses events log the provider's reason, output limit, and usage so token exhaustion can be distinguished from an interrupted connection.

`ConfirmQuestionAsync` clears the question's `uncertainties` while retaining notes and grades. It requires an idle document and does not turn an uncertain verdict into a resolved one.

Re-recognition reuses stable keys. Workflow results replace unprotected fields after typed and domain validation. Personal notes survive recognition. This is not a complete revision history for question text.

## Completion and collections

The runner checks exactly one coverage entry for every OCR page, saved source keys, and valid reference-to-item matches before marking the task complete. Whole-paper runs must save every current top-level question during the run. Reference-only runs cover reference pages and cannot save questions or paper information.

A question enters Mistakes when any item is partial, incorrect, or unanswered. Uncertain grades and transcription uncertainties appear under Needs review; uncertainty alone does not imply a known mistake. Completion validates consistency, not the correctness of every symbol or grade.

Sources: [models](../../src/OhMyStudy.Domain/Models.cs), [validation](../../src/OhMyStudy.Domain/GradingValidation.cs), [workflow contracts](../../src/OhMyStudy.Domain/ExamWorkflow.cs), [persistence](../../src/OhMyStudy.Infrastructure/Storage/LibraryGrading.cs).
