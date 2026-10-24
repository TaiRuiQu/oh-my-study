# 2. Import and recognize your first exam

[Home](Home.md) · [Quick Start](Quick-Start.md) · [简体中文](Quick-Start-First-Exam-zh-CN.md)

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

## Prepare a small, readable sample

Use a PDF, PNG, JPEG, or WebP. Check that it opens and the question numbers, diagrams, and writing are readable. An exam including supplementary student answer and reference pages is limited to 12 pages; a single source file is limited to 64 MiB and an import batch to 256 MiB. Images may not exceed 40 million pixels.

For your first run, use material you can check yourself. A small blank paper is useful for learning recognition; a small answered paper demonstrates grading.

## Choose the right mode

| Your material | Import mode | What to expect |
| --- | --- | --- |
| Let the model decide | Detect automatically | The model reads the pages and applies the matching mode below. |
| Questions with no student work | Blank exam | Structured questions, standard answers, and solutions without graded attempts. |
| Student work needing evaluation | Answered, needs grading | Student answers plus AI grading. |
| A teacher has already marked it | Already graded by a teacher | Teacher evidence and protected explicit teacher results. |

Do not select answered mode simply to make an empty practice paper appear in Mistakes. Blank mode intentionally does not classify unfilled questions as mistakes.

## Start processing

Click Import exam, select the files, choose the exam type, provider, model, and thinking options. Your last provider, model, and thinking choice are remembered for the next task. Keep the default thinking choice unless you have confirmed another option is supported. If capabilities are unconfirmed, understand the uncertainty before allowing a trial.

The confirmation explains provider uploads and may show a sandbox-unavailable warning. Import and recognize starts local preservation and queues recognition. Your external source file is not the app's only copy after import.

## Follow the first result

Open Tasks. Reading, saving, computation, and completion are distinct activities. A question can appear before the job finishes. A saved original is evidence of import, not of complete recognition.

If the task fails, keep the imported exam. Read the error and fix the provider/model or material problem. Use Retry task to roll back the failed run and start over, or Re-recognize to retain saved results; see [task recovery](Features-Tasks.md). This may incur fresh usage, but avoids unnecessary duplicate imports.

Implementation and reference: [MainWindow.Library.cs](../../src/OhMyStudy.App/MainWindow.Library.cs) · [LibraryContracts.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryContracts.cs)

---

[Previous chapter](Quick-Start-Setup.md) · [Section contents](Quick-Start.md) · [Next chapter](Quick-Start-Verify.md)
