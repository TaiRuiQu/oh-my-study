# OCR, question grouping, parallel solving, and assessment

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Model-Loop-zh-CN.md)

## Fixed stages

The recognition runner uses a workflow with separate model contexts. MinerU recognizes the source page images, a small model groups questions and binds their materials, and the selected solving model answers each question independently. Student work enters a separate assessment call, optionally using a different configured model. Reference-only jobs recognize and match reference answers without solving questions.

MinerU uses `https://mineru.net/api/v4/file-urls/batch`, signed PUT upload URLs, and `extract-results/batch/{batch_id}` polling. Its official API requires a Bearer token; Access Key / Secret Key pairs are not accepted as a substitute. Original page assets remain local and are preserved. The workflow uploads rendered pages, rather than original PDFs, to keep page identities and coordinate mapping explicit.

OCR and local question-region detection can run concurrently. Each page retains its original MinerU ZIP, Markdown, content-list JSON and referenced image files. `full.md` combines the pages in source order and namespaces their image links. `ocr.json` records normalized source regions and managed image assets. `regions.json`, `split-plan.json`, and each solving/assessment JSON response remain local in the library's `workflow_artifacts` table. Schema 7 adds this table and creates a pre-upgrade database backup. Library backups include these artifacts; retry recovery removes artifacts belonging to the rolled-back run.

## Grouping and region fallback

The splitting model is selected from saved providers, independently from the solving and grading models. Each task snapshots the selected provider, model, endpoint, and credential reference. Direct DeepSeek uses `/responses` with thinking disabled and strict JSON Schema output; other providers use their configured Responses or Messages protocol without tools. The splitter receives OCR, original page images, existing stable keys, verified references, and detections. It must account for every block and detection, join cross-page continuations, retain shared passages/figures/tables, and identify student-work regions. The full-paper Markdown is retained; normalized OCR blocks are the splitter's structured input.

YOLO26N Question Segmentation v2 is a one-class question detector. Run `./packaging/prepare-question-regions.ps1` with `uv` installed to convert the pinned public checkpoint to ONNX. Conversion tools stay under ignored `output/`; the app uses ONNX Runtime without Python/PyTorch for detection. A prepared `native/question-regions/base.onnx` is used automatically. Settings can override it and add a second Chinese checkpoint. Both models' regions are supplied independently; no handwriting model is claimed. Without an installed detector, the task explicitly reports that fallback is unavailable and uses OCR plus source-image review.

The ONNX contract is fixed float32 RGB `[1,3,1280,1280]`, letterboxed with value 114, with end-to-end `[1,N,6]` output (`x1,y1,x2,y2,confidence,class`). Export uses `nms=False`, opset 17, and static shapes. Confidence starts at 0.20. Stored regions use fractional coordinates on the unrotated full page; MinerU's 0–1000 corners are converted once. User display rotation does not rewrite evidence coordinates.

The [Chinese dataset assessment](../question-regions-dataset.md) excludes the proposed unverified dataset from production fine-tuning. A Chinese checkpoint is not produced by this change.

## Independent solving and grading

Grouping produces durable question drafts before paid solving starts. Each solution sees only its bound question and original evidence crops. It returns answers, explanations, knowledge points, and uncertainties. Question/item keys cannot change. Blank mode never creates attempts. Answered and teacher-graded modes assess every question; automatic mode assesses questions whose grouping stage found work, uncertain work, response regions, or an existing attempt.

Assessment receives the independent solution, matched references, and original student-work crops with their full-page coordinates. If no answer crop was identified, it receives the relevant full source page. It must mark unreadable handwriting uncertain, preserve explicit teacher grades in teacher/automatic modes, and never substitute an AI solution for the student's response. Existing manually edited responses, manual grades, teacher evidence, and notes remain protected by repository validation.

The only solving tool is `python`. It evaluates mathematical expressions and simple assignments through an AST interpreter with approved numeric/SymPy functions. Imports, attributes, filesystem/network access, shell, and string evaluation are unavailable. Symbolic functions receive expression objects rather than strings. Calculation variables are local to the call. Execution still crosses the existing permission/cancellation boundary; a shared Python execution gate prevents questions from corrupting the task's worker session. There are no model-driven read/save/finish/shell tools.

## Bounds, costs, and failures

Default concurrency is 3 questions (configurable 1–8). Each solution has at most 4 calls (configurable 1–8), and each controlled calculation has a 30-second limit. The task has a 30-minute deadline. The default splitting output budget is 32,768 tokens, capped by the model limit; incomplete direct responses retain their native response and token usage. Classification, splitting, and assessment each allow at most three validation attempts; automatic transport retries are disabled. Short task prompts are separate from the API output schema. Model responses pass through a format-normalization boundary, then schema and domain validation; raw and normalized artifacts remain available. Genuine missing evidence or contradictory grading is returned as bounded correction feedback. The storage JSON contract and page-coverage rules remain strict.

Token usage is recorded for splitting, solving, and assessment. Estimates use configured provider rates; priced CNY and USD amounts are converted before summing. MinerU and unpriced calls are explicitly excluded from displayed estimates. The import confirmation names all upload destinations and explains that each stage may incur charges. Keys are stored only in Windows Credential Manager and never in workflow snapshots or artifacts.

A failed task can retain OCR artifacts, question drafts and completed results. Retry rolls back that run using existing recovery records; re-recognition preserves saved edits. Both can incur new provider charges. Files API cleanup remains scoped to each question's client session, which prevents concurrent questions from sharing uploaded-file state.

Sources: [runner](../../src/OhMyStudy.Infrastructure/Grading/GradingRunner.cs), [MinerU](../../src/OhMyStudy.Infrastructure/Grading/MinerUClient.cs), [splitter](../../src/OhMyStudy.Infrastructure/Grading/QuestionSplitter.cs), [prompts](../../src/OhMyStudy.Infrastructure/Grading/WorkflowPrompts.cs), [detector](../../src/OhMyStudy.Documents/QuestionRegionModel.cs).
