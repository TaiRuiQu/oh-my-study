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

Open Settings → Exam workflow first. Keep your saved MinerU token. Choose the splitting model from your saved providers (for example, DeepSeek `deepseek-flash`); its address and key are reused. Choose an optional grading model. The start dialog also lets you choose splitting, solving, and grading models for this task. Parallel questions default to 3 and calls per solution to 4. If the bundled YOLO detector is prepared, it runs locally; an optional Chinese ONNX checkpoint can be configured after independent validation.

Pages are sent to MinerU and the splitter, bound questions to the solving model, and original student-work crops to the grading model. OCR and unpriced model charges are excluded from the displayed estimate.

Click Import exam, select the files, choose the exam type, provider, model, and thinking options. Your last provider, model, and thinking choice are remembered for the next task. Keep the default thinking choice unless you have confirmed another option is supported. If capabilities are unconfirmed, understand the uncertainty before allowing a trial.

Settings shows a connected icon Steps track with each configuration beside its stage. The start dialog uses a horizontal flow: classify the exam format, upload and OCR, split and bind materials, solve in parallel, and grade student work. Reasoning and calculation permissions are under Model and calculation options. Start workflow saves the originals locally and queues processing; Tasks opens automatically. Your external source file is not the app's only copy after import.

## Follow the first result

After OCR has run and the task stops, use **Export recognition materials** in Tasks to save a ZIP containing `full.md`, page images, raw OCR output and workflow results.

Open Tasks to see the five-stage workflow, including the detected exam format, recognized pages, split questions, and per-question solution and grading status. Blank exams show grading as not required; skipped stages are labelled explicitly. Expand Technical details and model records for model output, calculations, and errors. A question can appear before the job finishes. A saved original is evidence of import, not of complete recognition.

If the task fails, keep the imported exam. Read the error and fix the provider/model or material problem. Use Retry task to roll back the failed run and start over, or Re-recognize to retain saved results; see [task recovery](Features-Tasks.md). This may incur fresh usage, but avoids unnecessary duplicate imports.

Implementation and reference: [MainWindow.Library.cs](../../src/OhMyStudy.App/MainWindow.Library.cs) · [LibraryContracts.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryContracts.cs)

---

[Previous chapter](Quick-Start-Setup.md) · [Section contents](Quick-Start.md) · [Next chapter](Quick-Start-Verify.md)
