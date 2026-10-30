# Usage Guide

[Home](../README.md) · [Installation, Upgrade, and Uninstallation](installation.md) · [中文](usage.zh-CN.md)

The interface supports Simplified Chinese, English, Japanese, and Korean. This guide includes Chinese labels in parentheses for reference.

## Language and typography

In **Settings → Appearance → Language**, choose **Follow Windows**, **简体中文**, **English**, **日本語**, or **한국어**. Save any edits and restart the app to apply the language. This preference is stored on this computer independently of your library. Unsupported Windows languages fall back to English; Chinese Windows locales use Simplified Chinese.

English and Chinese typography remain the primary focus. Interface fonts follow the selected language, and longer labels adapt to narrower windows. Changing the interface language does not translate imported materials, edits, or model responses. PDF headings and generated labels use English, while question content keeps its original language. PDF output retains the existing Chinese/English typesetting and mathematics support; Japanese and Korean interface support does not add Japanese or Korean print fonts.

The window uses native frosted Acrylic on supported graphics hardware. Windows automatically uses a solid background when hardware support is insufficient, Battery Saver or high contrast is active, or transparency effects are disabled. Reading surfaces stay opaque for legibility.

## Setting up models

When individual question or subquestion points are not printed, recognition may estimate them from common scoring conventions, constrained by any known totals. Estimated allocations and their basis appear in the question's uncertainties; AI grading reasons identify scores based on estimated points. Printed scores and protected manual grades take priority.

1. Open "Settings (设置)" and click "Add provider (添加供应商)".
2. Enter a name, API type, address, and key, then click "Save (保存)". The API type should match the service offered by the provider; you can choose OpenAI Responses or Anthropic Messages.
3. Click "Import models (导入模型)" to select models offered by the provider, or click "Add model (添加模型)" to enter a model ID manually.
4. Fill in the image, tool, and thinking options and the pricing according to the provider's documentation, then click "Save model (保存模型)". Capabilities and prices you are unsure about can be left as unconfirmed or unset.
5. After selecting a model, you can click "Test connection (测试连接)". The test sends a short request and may incur a small charge.

Set **Max output tokens (max_tokens)** for each model to control the output limit of new recognition tasks. Leave it empty to keep the automatic limit. The saved value is sent as `max_output_tokens` for Responses or `max_tokens` for Messages, including on later rounds; it takes priority over the automatic limit and is preserved when filling catalog details. Enter a positive integer supported by your provider, and keep any fixed thinking budget below it.

"Fill from models.dev (从 models.dev 填充)" retrieves reference information; after checking it, you still need to click "Save model (保存模型)". Reference prices may not match what the provider actually charges; use the provider's bill as the source of truth.

Every recognition task uses the same three fixed stage instructions for splitting, solving, and grading, regardless of model family. The prompt selected for each stage does not change during a task. See [workflow prompts](model-prompts.md) for the contracts and outputs. Provider-specific reasoning and capability options are still configured per model.

The Tasks page shows estimated costs. Click "Details (明细)" to view cost and usage. A request with a missing output count still contributes its known input and cache charges. A request that still cannot be priced is omitted, and the task total is the sum of the priced requests. When no request can be priced, the page says the estimate is unavailable. An omitted request or a missing total does not mean that work was free.

Images sent to the model target 200 KiB each before base64 encoding, including full pages, question crops, extracted figures and answer-region crops. Small images remain lossless PNGs; larger images use color JPEG, lowering quality down to 0.6 and scaling down proportionally to a long-side floor of 1280 pixels without upscaling smaller sources. Images may exceed the target at those readability limits. Original files, question previews, and PDF export assets retain their existing quality. The target reduces transfer size but does not guarantee recognition accuracy or lower image token charges.

For an OpenAI Responses provider, "Codex mode (Codex 模式)" sends a stable `prompt_cache_key` with each solving request. The key is the solving session's identifier; keeping it constant within a question can raise cache hit rates. This adds input tokens. The option defaults to off and applies only to Responses.

If your provider supports the Files API and image file IDs, enable "Support Files API (支持 Files API)" in its settings and save. This option defaults to off and applies to new tasks. It supports the configured Responses or Messages protocol and uses the same provider address and key. Splitting, solving, and grading upload only the page images, crops and extracted figures they receive. Identical images upload once per provider client; subsequent calls reference their file IDs. Different crops remain distinct images and use the same compression policy.

Uploads request expiration **one hour after creation**, and the app also attempts to delete its uploaded files when the task completes, fails, or is canceled. If the app exits unexpectedly, the provider's requested expiration remains in effect. The provider must support the protocol's expiration parameter; rejected uploads fail with an actionable task message instead of silently sending a large inline request or uploading without expiration. Deletion failures and request sizes are recorded in the local diagnostic log, without recording uploaded image bytes. Providers may charge for storage or model input; file IDs reduce transfer size but do not remove model input token or image limits. See the [Responses file upload contract](https://developers.openai.com/api/reference/resources/files/methods/create) and [Messages file upload contract](https://platform.claude.com/docs/en/api/http/files/upload).

## Setting up meaning search

In **Settings → Meaning search models**, choose **API type** independently for embedding and reranking, then enter the **Base URL**, **Model name**, and optional **API key**. **OpenAI-compatible** uses `/embeddings` for embedding and the existing `/rerank` contract for services that support reranking. **DashScope** uses the native [embedding](https://help.aliyun.com/zh/model-studio/text-embedding-synchronous-api) and [reranking](https://help.aliyun.com/zh/model-studio/text-rerank-api) APIs; use the Base URL ending in `/api/v1` for your region and workspace. The operation path is appended automatically. Existing configurations keep the OpenAI-compatible API type. These connections remain independent of recognition providers. Keys are stored separately in Windows Credential Manager; leave a key blank to retain it or select **Remove saved key**. Leave both Base URL and model name empty to disable a model, then click **Save search models**.

In **Question bank** or **Mistakes**, select **Meaning**, describe a question or concept, and click **Search** or press Enter. Subject and result filters apply before retrieval. Review the confirmation showing the embedding destination, model, and number of new or changed question texts. The query is sent on each search; a configured reranker receives up to 50 candidate texts. Calls can incur provider charges and are not included in the recognition task cost estimate.

The index uses question stems, formulas, subquestions, options, and knowledge points, up to 6,000 characters per question. Original files, personal notes, and student answers are excluded. Vectors are cached in the library's SQLite database, and a local exact cosine-similarity scan retrieves up to 50 questions without a vector-search extension. When an embedding model is configured, questions saved by recognition are automatically embedded in the background without delaying recognition. The recognition confirmation shows the additional destination and possible charges. Interrupted or failed batches and manual edits are indexed on the next meaning search. Changing the embedding API type, Base URL, or model creates a separate vector space. Cancellation keeps completed batches. **Clear local index** removes cached vectors; the next search rebuilds them after confirmation. Leave the reranker unconfigured to rank entirely by local vector similarity after embedding. Keyword search remains local and works without search model configuration.

## Importing exams

Choose **Take photos with phone** in exam, reference answer, or student answer imports. Keep both devices on the same trusted network and scan the code. **Continuous camera (experimental HTTPS)** uses the publicly disclosed eMudhra EM OV TLS CA - G3C intermediate with the known scalar 4, cross-signed by AAA Certificate Services. The computer generates a fresh private leaf key and a one-hour certificate for its selected private LAN address; it does not install a root or change browser validation. The bundled public issuer is pinned by SHA-256 `7485B51C0ED192878FA1D2F948FB529AE1842CC5086C4B67F7BE5B2039DE9489` from the [incident dataset](https://emudhra.we-have-a-problem.com/impacted-intermediates.json). This compromised chain is experimental: current Chromium rejected it in testing, and revocation or Certificate Transparency enforcement can prevent it from working on a phone. The compromised issuer cannot establish reliable server identity on a hostile network. If rejected, choose **System camera and files (HTTP)** on the computer and scan the replacement code. HTTP supports the system camera and file selection; an embedded live camera requires a browser-trusted secure context.

In the camera screen, leave **Multiple pages** selected, enable the rear camera, and keep tapping the shutter. **Single page** opens the preview after one capture. Photos accumulate in capture order without uploading or waiting for cropping. Open **Recent scans** or tap a thumbnail when finished: originals transfer directly to the computer for local preview. Swipe left or right in the large-page viewer to change pages, pinch to zoom the photo itself, and drag to pan while zoomed. The viewer also supports previous/next buttons, original comparison, white-background enhancement and rotation, and **Continue shooting**. Open **Pages** to review all results in a selectable grid. Hold a page for a moment, then drag it onto another page to reorder the batch. The computer imports confirmed pages in this order; a failed order save restores the previous arrangement. Inspect individual pages to adjust rotation, geometry correction and white-background enhancement. Failed pages retain their originals and can be retried or deselected; canceling processing keeps the queue. Tap **Send (N)** once to confirm the selection together, then click **Import** on the computer. Photos and PDFs remain unavailable for import until confirmed. Recognition still requires its existing provider and cost confirmation.

**HD** starts off. Enabling it keeps the live preview at its normal resolution and requests the camera's highest supported still-photo width when you press the shutter. If the browser does not support native still capture, use **System camera** for full-resolution photos. If native capture fails, HD switches off and a standard photo is saved, with a message explaining the fallback.

The session lasts 20 minutes and supports up to 12 files, 64 MiB per file and 256 MiB including originals, previews and corrected copies. The existing combined 12-page import limit still applies to PDFs. Keep the phone page open: reloading loses photos that have not transferred to the computer; transferred drafts can be recovered during the same session. Closing the desktop capture dialog stops both receivers.

Click "Import exam (导入试卷)" and choose a PDF, PNG, JPEG, or WebP file. An exam plus its supplementary student answers and references may total at most 12 pages; split longer materials first.

In **Settings → Exam workflow**, save a MinerU Bearer token and choose the question splitting model from your saved providers; it reuses that provider's address and key. **Identify exam format** optionally uses a separate model and otherwise reuses the splitting model. **Grading model** optionally differs from the solving model. Parallel questions (1–8, default 3) and maximum calls per solution (1–8, default 4) are set here too.

Choose the exam type in the import dialog:

| Exam type | Purpose |
| --- | --- |
| Detect automatically (自动识别) | A small model classifies the pages before OCR and decides whether the exam is blank, answered, or teacher-graded, then organizes and grades it accordingly |
| Blank exam (空白试卷) | Recognize questions and generate standard answers and solutions; questions without student responses are not counted as wrong |
| Answered, needs grading (已作答，需批改) | Recognize questions and student answers, and have AI grade them |
| Already graded by a teacher (已有教师批改) | Extract the teacher's grading results and supplement explanations |

Choose a provider and one of its models, plus any thinking options the model supports, then confirm and click "Start workflow (开始工作流)". Your last chosen provider, model, and reasoning level are remembered and preselected the next time. Each stage sends materials to its own model and may incur costs; the confirmation names the destinations. Automatic detection recognizes, but does not override, an exam type you choose explicitly.

Recognition runs as fixed stages rather than a free-form agent loop. Page previews first go to the format model, which identifies one of five layouts: blank paper, answers on the exam paper, teacher-marked answers on the exam paper, an exam with separate answer sheets, or an exam with teacher-marked answer sheets. MinerU then extracts each page's Markdown, images and tables, and an optional local question-region model proposes printed question boxes in parallel. A small splitting model groups the OCR text, images, tables and answer regions into questions with their materials. The solving model answers each question independently, and a separate grading call compares the original answer crops with that solution. Blank exams skip grading. Question formats are detected per page, so mixed question-and-answer layouts and separate answer sheets are bound by printed question number and subquestion rather than page order.

Imported originals are copied into the library, so you can still view them after deleting the external original files. Recognized questions are saved to the question bank as the task progresses; saving the original does not mean recognition has finished. A failed task keeps its OCR materials, question drafts and completed questions. After OCR completes, **Export recognition materials** in Tasks saves a ZIP containing `full.md`, the page images, the raw OCR output, the split plan and each available model result.

Recognition can use the title printed on the exam as its name. If no title is visible, the imported filename remains the fallback. A title you edit in Exam information is preserved when recognizing again.

After import, open an exam and choose **More → Upload student answers** to add answer photos or PDFs, or open a question and choose **Enter student answer** to type the student's work. Then select **Recognize again** in the exam to grade the added answers. Originals and standard solutions are retained, and manually entered responses are preserved. Changing an entered response clears its previous grade and error analysis until it is graded again. Supplementary pages count toward the same 12-page limit alongside exam and reference pages.

For incorrect or partially correct responses, the model can show a separate **Error analysis** explaining the supported mistake and a correction. It stays empty when the cause cannot be determined from the work. Standard answers and solutions are generated even for a blank exam, with uncertainty recorded when a question cannot be solved reliably.

## Optional photo processing

Computer image imports and drag-and-drop offer **Use image correction**, initially unchecked, before recognition settings. Enable it for automatic orientation followed by the project's fine-tuned full-frame geometry and white-background models. The option covers exams, reference answers and student answers; PDFs remain unchanged. Originals are retained separately from corrected PNGs.

Phone **Scan settings** offers **Automatic orientation**, **Correct page geometry** and **Enhance white background**, initially enabled. Global changes apply to all unsent and future photos; page editing supports individual switches and rotation. Manual rotation takes precedence over automatic orientation. **Turn off all processing** also resets rotation. There is no four-corner crop or perspective preprocessing. Correction runs on the computer with bundled ONNX Runtime CPU models and needs no model download, Python, CUDA or OpenCV. Hard shadows, mixed lighting, faint strokes, bleed-through and blur remain limitations; inspect the preview before sending.

If no explicit paper score is provided, the paper list totals the question scores when every saved question has a determined score. Explicit paper scores, including manually entered scores, take precedence. Missing scores remain undetermined rather than counting as zero, and the derived total does not overwrite model output.

## Viewing and organizing questions

The app restores your previous window size and maximized state on the next normal launch. Long formulas wrap at suitable operators within the reading pane; indivisible groups fit the available width. Copying a formula retains its complete original source.

After checking the model's transcription concerns against the original, click **Confirm result** in the **Needs review** notice. This clears the review text directly while preserving question content, notes, and grades. An undetermined grade remains undetermined and may still appear under Needs review.

- In "Exams (试卷)", search by title or subject and open an exam to view its questions. Edit the title, subject, and score through "Exam information (试卷信息)" inside the exam, or through "Exam details (试卷信息)" in the exam's list context menu.
- Drag dividers between functional panes to resize them: navigation and content, source and questions, question list and detail, question and notes, task list and detail, task summary and log, and provider list and settings. Horizontal dividers resize heights and vertical dividers resize widths. Focus a divider with Tab and use the matching arrow keys; Home/End select the minimum/maximum size. Narrow layouts retain draggable dividers wherever both panes are shown.
- "Question bank (题库)" lists every saved question across exams. Search question text, answers, notes, or exam names, filter by subject or result ("All results (全部结果)", "Mistakes (错题)", "Needs review (待确认)", "Correct (正确)"), and sort the list. More questions load as you scroll, so large banks stay responsive.
- "Mistakes (错题)" lists questions with at least one partially correct, incorrect, or unanswered subquestion.
- Click a question to open its page. Use the arrows to move to the previous or next question in the current list, and the back button to return to the same position in the list.
- A question page uses the available width and shows the shared stem followed by every subquestion in order, like a printed paper. Subquestion labels such as `(1)` appear inline with their prompts. All responses are collected in one "Your answer (我的作答)" card marked by an accent bar, with each subquestion's score shown as a small badge beside the answer. One "Show answer and explanation (查看答案与解析)" control at the end reveals the standard answers, explanations, and solution steps for every subquestion.
- Write your own review notes in "My notes (我的笔记)" and click "Save notes (保存笔记)". Notes are kept when an exam is recognized again.
- Click "View in exam (在试卷中查看)" to open the question's exam with the original page and the question highlighted. Inside an exam, click "View original (查看原件)" to expand the original, or "Locate original (定位原件)" on a question to see its position; you can page, zoom, and rotate.
- Click "Edit question (编辑题目)" to modify the stem, subquestions, options, answers, explanations, or grading, then click "Save (保存)". A grade you change is marked as edited by you and is kept when the exam is recognized again. Save your changes before exiting or switching libraries.
- In an exam, click "Add reference answers (添加参考答案)" to supplement materials; to recognize reference answers, choose "Organize reference answers (整理参考答案)" from "More (更多)". To reprocess an exam, click "Re-recognize (重新识别)"; this calls the model again and may incur costs.
- Use "More → Manage pages (更多 → 管理页面)" to organize the original; pages still referenced by questions or reference answers cannot be removed directly.

AI recognition and grading results may be wrong. Verify the original question, answer, score, and reference answer, especially for blurry images, handwriting, and complex formulas.

Recognition saves questions, responses, teacher annotations, and reference answers as structured text linked to their source pages. Diagrams and figures retain their crop regions. Newly recognized questions locate the whole source page; existing precise regions are preserved when recognized again. Mathematics uses `$...$` delimiters, including in options, answers, and tables; older formula delimiters remain readable.

"Delete exam (删除试卷)" in the exam list context menu or in the "More (更多)" menu inside an exam also deletes that exam's questions, reference answers, task records, and the original copy in the library. This cannot be undone after confirmation; back up any materials you want to keep first.

## Viewing tasks and handling execution requests

The "Tasks (任务)" page shows the five workflow stages, per-question solution and grading states, and estimated costs. One exam task runs at a time and the rest queue; questions inside a task run in parallel. You can cancel a task, and questions that have already been saved successfully are kept. Tasks continue after the window is minimized; quitting the app stops them.

The stage track distinguishes pending, running, completed, failed, and not required. Blank exams and reference imports mark their skipped stages explicitly instead of appearing stalled. The per-question list shows solution and grading independently, so one question can be grading while another is still being solved.

For a failed or interrupted task, click "Retry task (重试任务)" in its details. Choose the workflow models and confirm to roll back that run's question changes, reference answers, exam details, generated images, recognition materials, and temporary files. The workflow then starts again from the beginning with fresh progress, logs, usage records, and execution permissions. Original documents, results from earlier runs, and notes on earlier questions are kept. Previous provider charges cannot be undone; retrying can incur new charges.

Retry is unavailable while another task processes the same exam. If a newer run has processed the exam, retry its latest failed task. Tasks created before recovery records were available, or whose saved questions have since been manually edited, must use "Re-recognize (重新识别)" in Exams to preserve those results and edits.

Each stage call retries when the network disconnects, a stream ends without its completion event, or the provider reports a temporary service or rate-limit error. A call allows four attempts, normally waiting 1, 2, and 4 seconds between attempts; a provider's Retry-After can extend a wait up to 30 seconds. Completed questions keep their results. Authentication errors, invalid parameters, refusals, and explicit output-limit failures are not retried, and invalid stage JSON fails that stage instead of starting an open-ended repair conversation. Cancellation interrupts both requests and retry waits. Retried requests can incur additional charges. A call whose usage was not reported stays out of the estimate, and the task total keeps the calls that were priced.

The interface keeps messages brief. Local diagnostics are written in both Debug and Release builds to `%LOCALAPPDATA%/OhMyStudy/logs/session-<timestamp>-<pid>.log`; `OHMYSTUDY_LOG_FILE` can select a different file. Logs retain task results, model output, code output, concise HTTP summaries, and retry decisions with timestamps and correlation IDs. Clicks, keystrokes, navigation, search edits, and routine progress updates are not logged. Successful calls omit full HTTP payloads, conversation history, and tool arguments. Failures retain request or tool arguments, provider rejection bodies, stream interruption positions, and full exception chains. Credentials, authorization values, cookies, and secret tokens are redacted. Logs can contain study text and submitted code, stay on this computer, and are not uploaded automatically; inspect them before sharing.

The task details show stage activity, model replies, controlled Python calculations, and their errors. Expand a failed calculation to select and copy the actual error output. Open **Export recognition materials** to inspect what OCR read. Cache misses equal total input minus cache hits, including tokens written to the cache. Missing input or cache-hit statistics are shown as not provided, separately from an explicit zero.

Splitting, solving, and grading calls use their own fresh context. Solving is the only stage that offers a tool, and it is limited to controlled Python; there are no page-reading, saving, or shell tools. OpenRouter requests pin every turn of one question to one provider endpoint with a sticky session, so the cache written by one turn is the one the next turn reads. Anthropic Messages requests cache the fixed stage instructions explicitly, with the default five-minute lifetime. OpenRouter also receives this app's attribution headers, so usage is listed under "Oh My Study". Cache hits still depend on the model's minimum prompt size, expiry, and provider support; the first request normally writes the cache before later requests can read it.

Choose an execution mode under "Settings → Execution → Code and commands (设置 → 执行 → 代码与命令)":

| Option | Behavior |
| --- | --- |
| Ask every time (每次询问) | You review each controlled Python calculation before it runs on this machine |
| Run in sandbox (沙箱运行) | Runs the controlled calculator in a task-specific Windows AppContainer; sandbox network access is denied by default |
| Run directly on this machine (直接在本机运行) | Runs the controlled calculator with the current Windows account's permissions without asking each time |

The default mode is "Run directly on this machine (直接在本机运行)". If you want to review each calculation first, change it to "Ask every time (每次询问)" before starting a task. Changes to execution settings affect only new tasks.

When a task is waiting for permission, click "View execution request (查看执行请求)". After reviewing the calculation, choose "Allow (允许)" or "Deny (拒绝)"; selecting "Also allow subsequent calls for this task (本任务的后续调用也允许)" applies only to the current task. Choosing "Later (稍后)" keeps waiting, and you can also cancel the task.

Whether the sandbox is available depends on the check performed when the task starts. If it is unavailable, the dialog explains that this run will use the local machine instead and what that means; cancel the start if you do not accept it. If a calculation fails in the sandbox, it is not automatically retried on the local machine.

The sandbox applies only to Python calculations. Model requests for AI recognition still send materials to the selected provider over the network.

AppContainer does not require Windows Sandbox, virtualization, or an optional Windows feature. It uses Windows process isolation rather than a virtual machine. Enabling sandbox networking grants outbound Internet access; it does not grant access to other files or the local network.

Only the solving stage can calculate, through a restricted Python evaluator. It accepts mathematical expressions, simple variable assignments, `print`, and the listed numeric and SymPy functions; imports, attributes, strings passed to mathematical functions, files, network and shell are rejected. Variables are local to each calculation, so a question cannot read another question's state. Each calculation is limited to 30 seconds. An optional Python tool is not available during splitting or grading.

## Exporting PDFs

1. Open the exam you want to export, and save your changes first.
2. Click "Export questions (导出题目)" and confirm the number of questions being exported.
3. Choose A4 portrait or A3 landscape, independently choose 1–4 columns, and select "Questions" or "Answers and explanations". The default remains one column on A4 and two on A3.
4. Click "Continue (继续)" and choose where to save the PDF.

Question sheets and answer/explanation sheets are exported separately. After exporting, check the PDF to confirm that formulas, figures, and pagination meet your needs, then print it with a PDF reader.

Model-created text, tables, captions, options, answers, and explanations can use `{font:serif}…{/font}` for NSimSun, `{font:sans}…{/font}` for Microsoft YaHei, and `{font:kai}…{/font}` for KaiTi. Unmarked text uses the interface sans-serif font in the reading view and NSimSun in exported PDFs. Directives can surround inline formulas without changing the formula renderer; malformed or unclosed directives remain visible as literal text. In tables, a cell containing only `^`, `<`, or `>` merges upward, leftward, or rightward. Rectangular merges are preserved, and wide-table panels avoid splitting a merge across panels.

Model-created Mermaid `flowchart`/`graph` diagrams support TD, LR, BT, and RL flowcharts with common rectangle, rounded, and diamond nodes, chained links, comments, and labeled or unlabeled arrows. Unsupported or invalid Mermaid is rejected with an error rather than exported as an incorrect diagram. Image and diagram width percentages refer to the full page text width, not the current column. A visual wider than its column is centered as a spanning float and may move to the top of a page; narrower visuals remain inline. The existing image height cap and handwritten-image restriction still apply.

If a question image contains handwriting, you must replace it with a clean image before exporting a question sheet. If export fails, follow the prompt to check the content or export in batches; if the prompt says some formulas retained their source text, verify them before printing.

## Saving, backing up, and recovering materials

The default library location is `%LOCALAPPDATA%/OhMyStudy/Library`. Check the actual "Save location (保存位置)" under "Settings → Library (设置 → 资料库)", or click "Open folder (打开文件夹)".

- Change location: save your changes and finish tasks first, then click "Change location (更改位置)", choose a target folder, and confirm "Copy and switch (复制并切换)". The app creates `OhMyStudy资料库` under it, copies the existing materials, and keeps the original library; if the target already exists, choose another folder.
- Manual backup: save your changes and quit the app, then copy the entire library folder to another location. Do not copy only some of the question or original files.
- Open existing materials: click "Select existing library (选择已有资料库)" and choose a complete library folder created by the current version. When you need to use a backup, keep the original backup and select a copy of it to avoid overwriting existing materials.
- Location unavailable: confirm the disk is connected and the folder is accessible, then select an existing library in Settings. If changing the location fails, follow the prompt to check the target location and free space, then retry.
- Upgrading a library: the first time this version opens a library, it converts saved questions to the current question format and keeps a copy of the previous database file (`study.sqlite3.pre-v6-…`) in the library folder. Earlier app versions cannot open a converted library, so keep a backup if you may need to go back.

Provider keys are stored in the Credential Manager of the current Windows account. After copying the library to another account or computer, you may need to re-enter the keys. Back up older-version materials completely first; do not overwrite old materials directly with a new version.

## FAQ

| Situation | What to do |
| --- | --- |
| Prompt asks you to set up a recognition model first | Save a provider, key, and at least one model, then import again |
| Connection test or recognition fails | Check the API type, address, key, model ID, account quota, and network, then fix the issue and retry |
| The original was saved, but recognition did not start or failed midway | Fix the settings and use "Retry task (重试任务)" in Tasks to start over; use "Re-recognize (重新识别)" in the exam to keep saved results |
| PDF cannot be read | Confirm the file opens normally; decrypt encrypted files first, and obtain a new copy of corrupted files |
| Page limit exceeded | Split the materials so that each exam plus its supplementary student answers and references totals no more than 12 pages |
| Prompt says import components are missing or damaged | Extract the portable version completely again, or repair or reinstall the app |
| Cannot export | Save the questions first, check the question images and save location; export in batches if there is too much content |

You can choose "Follow Windows (跟随 Windows)", "Light (浅色)", or "Dark (深色)" under "Settings → Appearance (设置 → 外观)".

[Wiki contents](wiki/Home.md)
