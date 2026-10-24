# Quick Start

[Home](Home.md) · **Quick Start** · [Feature Guide](Feature-Guide.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Quick-Start-zh-CN.md)

## Chapter reading path

Follow these four short tutorials in order. Each ends with a concrete result you can inspect before moving on.

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

| Chapter | What you will learn |
| --- | --- |
| [1. Install and prepare your first model](Quick-Start-Setup.md) | Install, save a connection, confirm capabilities and execution policy. |
| [2. Import and recognize your first exam](Quick-Start-First-Exam.md) | Prepare files, choose a mode, follow the first task, recover from failure. |
| [3. Verify evidence and make your first correction](Quick-Start-Verify.md) | Compare evidence, correct formulas/grades, understand preservation. |
| [4. Build a review pack and protect your library](Quick-Start-Review-Export.md) | Choose collections, export two editions, inspect and back up. |

The overview below is a reference; open a chapter for the detailed walkthrough.

## Your first exam

1. **Install.** Get a Windows 10/11 x64 build from [Releases](https://github.com/TaiRuiQu/oh-my-study/releases). If unsure, choose the `vX-aot` release. Run `Setup.exe` for a per-user installation, or fully extract the portable ZIP and open `OhMyStudy.App.exe`. See [installation](../installation.md) for other variants and prerequisites.
2. **Configure a model.** Open **Settings → Add provider**, enter the API type, address, and key, then save. Use **Import models** or **Add model**, confirm image/tool capabilities and thinking options, and **Save model**. A connection test makes a small, potentially billable request.
3. **Import.** Choose **Import exam**, select PDF, PNG, JPEG, or WebP files, choose automatic detection or an explicit mode and a model, then **Import and recognize**. Each exam, its supplementary student answers, and its references together can contain at most 12 pages.
4. **Follow progress.** Open **Tasks** to see page reads, saved questions, execution requests, and estimated costs. Questions appear as they are saved; a copied original alone does not mean recognition finished.
5. **Verify.** Open the exam, use **View original** and **Locate in original**, and check questions, answers, scores, and formulas. Use **Edit question** for corrections and save.
6. **Review and export.** Use **Mistakes** for mistakes or **Question bank** for all saved questions. **Export questions** exports the currently displayed saved questions as a question sheet or an answer/explanation sheet. Choose A4 or A3 landscape and save the PDF.

AI processing sends page images and task context, including relevant saved questions and references, to the selected provider. Calls can cost money. Before your first task, check **Settings → Execution → Code and commands**: the current default runs model-requested code directly with your Windows account's permissions. Choose **Ask every time** to review requests, or **Run in sandbox** for AppContainer isolation.

## Every function at a glance

| Function | What you do |
| --- | --- |
| Providers and models | Add, edit, or delete connections and model configurations; import IDs or add them manually. |
| Model reference data | Fill from models.dev, verify capabilities and prices, then save. |
| Connection test | Check a saved model with a short request before importing. |
| Blank exam | Extract questions without grading; blank answers do not become mistakes. |
| Answered, needs grading | Extract student work and request grading and explanations. |
| Already graded by a teacher | Extract teacher results and supplement missing analysis. |
| Exam list | Search, filter, sort by date/title/subject/score rate, and open an exam. |
| Exam information | Correct the title, subject, and total score information. |
| Original viewer | Compare source pages; page, zoom, fit, rotate, or locate a question's source. |
| Manage pages | Reorder or rotate pages and remove unreferenced pages. |
| Edit question | Change text, LaTeX formulas, tables, source-image regions, options, answers, explanations, points, grading, knowledge points, and subquestions. |
| Question bank | Browse questions saved from your exams. |
| Mistakes | Review partial, incorrect, or unanswered questions, including mistakes within subquestions. |
| Reference answers | Add answer pages, then use **More → Organize reference answers**. This alone does not regrade. |
| Recognition results | Inspect reference matches, uncertainties, and page coverage; jump to an answer's original page. |
| Re-recognize | Run whole-exam processing again after correcting setup or adding references; new calls can incur charges. |
| Tasks | Inspect progress/logs, open saved questions, cancel, or return to the latest log entry. |
| Execution requests | Allow or deny code in ask mode; optionally allow later calls for that task. |
| Usage and cost details | Inspect token usage and estimates; missing data is not a zero charge. |
| PDF export | Export questions or answers/explanations separately as A4 portrait or A3 landscape. |
| Library location | Open the folder, copy and switch location, or select an existing current-version library. |
| Backup and recovery | Quit and copy the complete library; preserve the backup and open a copy when recovering. |
| Delete exam | Remove the exam, questions, references, tasks, and stored originals after confirmation. |
| Appearance | Choose system/light/dark appearance and system/Simplified Chinese/English/Japanese/Korean language. Restart to apply a language change. |
| Keyboard access | Ctrl+O imports, Ctrl+S saves, Ctrl+E edits a question, Ctrl+0 fits the page, Ctrl+1–4 switches sections. |

## Before calling it done

Open **Recognition results** and review unreadable pages and unmatched references. Check uncertain grades separately: they do not automatically enter Mistakes. Save corrections before exporting or switching libraries. Inspect the exported PDF before printing, especially formulas, cropped figures, and pagination.

Continue with the [Feature Guide](Feature-Guide.md) for exact steps, limits, and recovery advice.

## Search and recover

Question bank and Mistakes support local text search, filters, a dedicated reading page with previous/next navigation, per-item answers, and saved personal notes. Configure [meaning search](Features-Search.md) if you want embedding/reranking and accept the disclosed uploads.

For failed/interrupted tasks, **Retry task** starts over after confirming rollback of that run. To retain current results or edited questions, use **Re-recognize** in Exams. See [recovery rules](Features-Tasks.md).
