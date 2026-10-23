# Usage Guide

[Home](../README.md) · [Installation, Upgrade, and Uninstallation](installation.md) · [中文](usage.zh-CN.md)

The interface supports Simplified Chinese, English, Japanese, and Korean. This guide includes Chinese labels in parentheses for reference.

## Language and typography

In **Settings → Appearance → Language**, choose **Follow Windows**, **简体中文**, **English**, **日本語**, or **한국어**. Save any edits and restart the app to apply the language. This preference is stored on this computer independently of your library. Unsupported Windows languages fall back to English; Chinese Windows locales use Simplified Chinese.

English and Chinese typography remain the primary focus. Interface fonts follow the selected language, and longer labels adapt to narrower windows. Changing the interface language does not translate imported materials, edits, or model responses. PDF headings and generated labels use English, while question content keeps its original language. PDF output retains the existing Chinese/English typesetting and mathematics support; Japanese and Korean interface support does not add Japanese or Korean print fonts.

## Setting up models

1. Open "Settings (设置)" and click "Add provider (添加供应商)".
2. Enter a name, API type, address, and key, then click "Save (保存)". The API type should match the service offered by the provider; you can choose OpenAI Responses or Anthropic Messages.
3. Click "Import models (导入模型)" to select models offered by the provider, or click "Add model (添加模型)" to enter a model ID manually.
4. Fill in the image, tool, and thinking options and the pricing according to the provider's documentation, then click "Save model (保存模型)". Capabilities and prices you are unsure about can be left as unconfirmed or unset.
5. After selecting a model, you can click "Test connection (测试连接)". The test sends a short request and may incur a small charge.

"Fill from models.dev (从 models.dev 填充)" retrieves reference information; after checking it, you still need to click "Save model (保存模型)". Reference prices may not match what the provider actually charges; use the provider's bill as the source of truth.

The Tasks page shows estimated costs. Click "Details (明细)" to view cost and usage. Costs may not be calculable when prices or the necessary usage data are missing; a missing cost does not mean the service is free.

## Importing exams

Click "Import exam (导入试卷)" and choose a PDF, PNG, JPEG, or WebP file. An exam plus its reference answers may total at most 12 pages; split longer materials first.

Choose the exam type in the import dialog:

| Exam type | Purpose |
| --- | --- |
| Blank exam (空白试卷) | Recognize questions and add them to the question bank; unanswered questions are not counted as wrong |
| Answered, needs grading (已作答，需批改) | Recognize questions and student answers, and have AI grade them |
| Already graded by a teacher (已有教师批改) | Extract the teacher's grading results and supplement explanations |

Select a model and any thinking options it supports, confirm, then click "Import and recognize (导入并识别)". Materials are sent to the selected provider and may incur costs.

Imported originals are copied into the library, so you can still view them after deleting the external original files. Recognized questions are saved to the question bank as the task progresses; saving the original does not mean recognition has finished.

## Viewing and organizing questions

- In "Exams (试卷)", search by title or subject and open an exam to view its questions; "Exam information (试卷信息)" lets you edit the title, subject, and score.
- "Question bank (题库)" lists every saved question across exams. Search question text, answers, notes, or exam names, filter by subject or result ("All results (全部结果)", "Mistakes (错题)", "Needs review (待确认)", "Correct (正确)"), and sort the list. More questions load as you scroll, so large banks stay responsive.
- "Mistakes (错题)" lists questions with at least one partially correct, incorrect, or unanswered subquestion.
- Click a question to open its page. Use the arrows to move to the previous or next question in the current list, and the back button to return to the same position in the list.
- A question page uses the available width and shows the shared stem followed by every subquestion in order, like a printed paper. Subquestion labels such as `(1)` appear inline with their prompts. Each subquestion keeps its own response, score, and grading notes. Click its "Show answer and explanation (查看答案与解析)" control to reveal the standard answer, explanation, and solution steps.
- Write your own review notes in "My notes (我的笔记)" and click "Save notes (保存笔记)". Notes are kept when an exam is recognized again.
- Click "View in exam (在试卷中查看)" to open the question's exam with the original page and the question highlighted. Inside an exam, click "View original (查看原件)" to expand the original, or "Locate original (定位原件)" on a question to see its position; you can page, zoom, and rotate.
- Click "Edit question (编辑题目)" to modify the stem, subquestions, options, answers, explanations, or grading, then click "Save (保存)". A grade you change is marked as edited by you and is kept when the exam is recognized again. Save your changes before exiting or switching libraries.
- In an exam, click "Add reference answers (添加参考答案)" to supplement materials; to recognize reference answers, choose "Organize reference answers (整理参考答案)" from "More (更多)". To reprocess an exam, click "Re-recognize (重新识别)"; this calls the model again and may incur costs.
- Use "More → Manage pages (更多 → 管理页面)" to organize the original; pages still referenced by questions or reference answers cannot be removed directly.

AI recognition and grading results may be wrong. Verify the original question, answer, score, and reference answer, especially for blurry images, handwriting, and complex formulas.

Recognition saves questions, responses, teacher annotations, and reference answers as structured text linked to their source pages. Diagrams and figures retain their crop regions. Newly recognized questions locate the whole source page; existing precise regions are preserved when recognized again. Mathematics uses `$...$` delimiters, including in options, answers, and tables; older formula delimiters remain readable.

"Delete exam (删除试卷)" in the exam list context menu or in the "More (更多)" menu inside an exam also deletes that exam's questions, reference answers, task records, and the original copy in the library. This cannot be undone after confirmation; back up any materials you want to keep first.

## Viewing tasks and handling execution requests

The "Tasks (任务)" page shows progress, saved questions, execution logs, and estimated costs. One AI task runs at a time and the rest queue; you can cancel a task, and questions that have already been saved successfully are kept. Tasks continue after the window is minimized; quitting the app stops them.

The task log shows when the model is thinking or receiving a response. Successful tools show only their names; running, pending, and failed tools show their state, with guidance on failures. If the model replies with text without using tools, the task can prompt it to continue twice in a row; those calls still count toward usage and cost. Missing cache statistics are shown as not provided, separately from an explicit zero.

The model can read multiple pages with `read_pages` and save multiple questions with `save_questions` in one call. Each entry reports its own result; successful entries are kept if another fails. OpenRouter Responses requests for Claude and Gemini include cache breakpoints for shared context and the latest page results. Cache hits still depend on the model's minimum prompt size, expiry, and provider support.

Choose an execution mode under "Settings → Execution → Code and commands (设置 → 执行 → 代码与命令)":

| Option | Behavior |
| --- | --- |
| Ask every time (每次询问) | You review and decide whether to allow code or commands before they run on this machine |
| Run in sandbox (沙箱运行) | Runs code and commands in a separate Windows AppContainer for each call; sandbox network access is denied by default |
| Run directly on this machine (直接在本机运行) | Runs with the current Windows account's permissions without asking each time; can access that account's files and network |

The default mode is "Run directly on this machine (直接在本机运行)". If you want to review what will run first, change it to "Ask every time (每次询问)" before starting a task. Changes to execution settings affect only new tasks.

When a task is waiting for permission, click "View execution request (查看执行请求)". After reviewing the content, choose "Allow (允许)" or "Deny (拒绝)"; selecting "Also allow subsequent calls for this task (本任务的后续调用也允许)" applies only to the current task. Choosing "Later (稍后)" keeps waiting, and you can also cancel the task.

Whether the sandbox is available depends on the check performed when the task starts. If it is unavailable, the dialog explains that this run will use the local machine instead and what that means; cancel the start if you do not accept it. If code execution fails in the sandbox, it is not automatically retried on the local machine.

The sandbox applies only to code and commands. Model requests for AI recognition still send materials to the selected provider over the network.

AppContainer does not require Windows Sandbox, virtualization, or an optional Windows feature. Each call receives a copy of its input files, read access to the private computation runtime, and write access to its own result folder. It uses Windows process isolation rather than a virtual machine. Enabling sandbox networking grants outbound Internet access; it does not grant access to other files or the local network.

The model can execute Python and Windows PowerShell scripts for calculations, file processing, and image processing. The bundled Python includes Pillow, NumPy, SymPy, and Matplotlib. Page reads provide local copies of the displayed image and the unrotated original. Working files and generated outputs carry over to later calls, including sandbox calls. The task details show execution results and errors so failed code can be corrected.

The model can request previews of generated PNG, JPEG, or WebP images and use their returned asset IDs in saved question images. These processed images are stored in the library and used in question previews and PDF exports; the original documents and source regions remain available. Image regions use `[left, top, right, bottom]` with integer coordinates from 0 to 999 on the full unrotated original page; `[0, 0, 999, 999]` means the full page. Existing saved regions remain compatible.

## Exporting PDFs

1. Open the exam you want to export, and save your changes first.
2. Click "Export questions (导出题目)" and confirm the number of questions being exported.
3. Choose A4 or A3 landscape, and "Questions" or "Answers and explanations".
4. Click "Continue (继续)" and choose where to save the PDF.

Question sheets and answer/explanation sheets are exported separately. After exporting, check the PDF to confirm that formulas, figures, and pagination meet your needs, then print it with a PDF reader.

If a question image contains handwriting, you must replace it with a clean image before exporting a question sheet. If export fails, follow the prompt to check the content or export in batches; if the prompt says some formulas retained their source text, verify them before printing.

## Saving, backing up, and recovering materials

The default library location is `%LOCALAPPDATA%/OhMyStudy/Library`. Check the actual "Save location (保存位置)" under "Settings → Library (设置 → 资料库)", or click "Open folder (打开文件夹)".

- Change location: save your changes and finish tasks first, then click "Change location (更改位置)", choose a target folder, and confirm "Copy and switch (复制并切换)". The app creates `OhMyStudy资料库` under it, copies the existing materials, and keeps the original library; if the target already exists, choose another folder.
- Manual backup: save your changes and quit the app, then copy the entire library folder to another location. Do not copy only some of the question or original files.
- Open existing materials: click "Select existing library (选择已有资料库)" and choose a complete library folder created by the current version. When you need to use a backup, keep the original backup and select a copy of it to avoid overwriting existing materials.
- Location unavailable: confirm the disk is connected and the folder is accessible, then select an existing library in Settings. If changing the location fails, follow the prompt to check the target location and free space, then retry.
- Upgrading a library: the first time this version opens a library, it converts saved questions to the current question format and keeps a copy of the previous database file (`study.sqlite3.pre-v5-…`) in the library folder. Earlier app versions cannot open a converted library, so keep a backup if you may need to go back.

Provider keys are stored in the Credential Manager of the current Windows account. After copying the library to another account or computer, you may need to re-enter the keys. Back up older-version materials completely first; do not overwrite old materials directly with a new version.

## FAQ

| Situation | What to do |
| --- | --- |
| Prompt asks you to set up a recognition model first | Save a provider, key, and at least one model, then import again |
| Connection test or recognition fails | Check the API type, address, key, model ID, account quota, and network, then fix the issue and retry |
| The original was saved, but recognition did not start or failed midway | Check the task message, fix the settings, then click "Re-recognize (重新识别)" in the exam; saved questions are kept |
| PDF cannot be read | Confirm the file opens normally; decrypt encrypted files first, and obtain a new copy of corrupted files |
| Page limit exceeded | Split the materials so that each exam plus its reference answers totals no more than 12 pages |
| Prompt says import components are missing or damaged | Extract the portable version completely again, or repair or reinstall the app |
| Cannot export | Save the questions first, check the question images and save location; export in batches if there is too much content |

You can choose "Follow Windows (跟随 Windows)", "Light (浅色)", or "Dark (深色)" under "Settings → Appearance (设置 → 外观)".
