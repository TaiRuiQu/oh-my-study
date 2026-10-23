# Question schema, grading invariants, and preservation rules

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Validation-Grading-zh-CN.md)

## Shared stem and ordered items

A `Question` has a stable `draft_key`, printed number, shared `stem`, source regions, subject/type, knowledge points, uncertainties, notes, and an ordered `items` list. Each `QuestionItem` owns its stable `key`, printed label, prompt, options, points, standard answer, explanation, and optional attempt. A question without subquestions still has one item, usually with an empty label and prompt; its text belongs in the stem.

There are 1–100 items per question. Nested printed labels are flattened, for example `(2)(a)`; the current format has no recursive `Parts` tree. Multi-item questions need nonempty labels. Item keys are unique across the paper, while coverage uses top-level draft keys and reference matches use item keys.

Text, formula, table, and image blocks remain typed. Images can reference a processed `asset_id` while retaining the original source region. Tool regions use integer `bbox=[left,top,right,bottom]` on the full unrotated page, from 0 to 999. Storage uses fractional `x/y/width/height`; the runner converts between them.

## Validation before persistence

Closed tool schemas reject extra fields, typed deserialization checks shape, and domain/repository validation checks ownership and meaning. Questions need a number, nonempty stem, material-page sources, valid stable keys of at most 128 UTF-8 bytes, and at most 1 MiB of serialized content.

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

An attempt separates student work, teacher annotations, matched reference, AI solution/conflict, and grade. Provenance is `Teacher`, `AiReference`, `AiSolution`, or `Manual`. Existing manual grades are protected; teacher mode also preserves explicit teacher results and evidence. AI cannot create a manual provenance label or remove a protected item.

Re-recognition reuses stable keys. Omitted auxiliary values keep saved data; explicit empty values can replace unprotected data. Personal notes survive recognition. This is not a complete revision history for question text.

## Completion and collections

`finish` checks exactly one coverage entry for every required page, prior full-page reads, saved source keys, and valid reference-to-item matches. Whole-paper runs must save every current top-level question during the run. Reference-only runs cover reference pages and cannot save questions or paper information.

A question enters Mistakes when any item is partial, incorrect, or unanswered. Uncertain grades and transcription uncertainties appear under Needs review; uncertainty alone does not imply a known mistake. Completion validates consistency, not the correctness of every symbol or grade.

Sources: [models](../../src/OhMyStudy.Domain/Models.cs), [validation](../../src/OhMyStudy.Domain/GradingValidation.cs), [tool schemas](../../src/OhMyStudy.Infrastructure/Grading/GradingTools.cs), [persistence](../../src/OhMyStudy.Infrastructure/Storage/LibraryGrading.cs).
