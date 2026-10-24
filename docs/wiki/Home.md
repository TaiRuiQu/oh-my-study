# Oh My Study Wiki

**English** · [简体中文](Home-zh-CN.md)

Oh My Study turns exam PDFs and images into a local, editable question bank and wrong-question collection. Compare results with the original, correct recognition and grading, and create review PDFs.

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

The wiki has **20 topic chapters in each language**, plus the section overviews. Illustrations are conceptual diagrams, not screenshots or promises of exact screen/print layout.

## Quick Start

[Quick Start](Quick-Start.md) is a four-part tutorial: [prepare a model](Quick-Start-Setup.md), [recognize an exam](Quick-Start-First-Exam.md), [verify and correct](Quick-Start-Verify.md), and [review, export, and back up](Quick-Start-Review-Export.md). The section overview also provides a quick tour of every function.

The main workflow is **configure a model → import an exam → follow recognition → verify and edit → review mistakes → export**. Saved materials stay in your library; AI processing sends materials to your selected provider and may incur charges.

## Detailed guide

The [Feature Guide](Feature-Guide.md) covers model setup, automatic detection and three explicit import modes, supplementary student answers, original-page navigation, structured editing, reference answers, re-recognition, progress, execution permissions, costs, exports, library management, appearance, and troubleshooting.

Its nine dedicated guides include field explanations, step-by-step workflows, examples, limitations, and recovery advice. Start with [models and costs](Features-Models.md), or go directly to [editing](Features-Workspace-Editing.md), [tasks](Features-Tasks.md), [PDF export](Features-Export.md), or [library recovery](Features-Library.md).

For download choices, upgrades, and uninstallation, see the [installation guide](../installation.md).

## Tech for nerds

[Tech for nerds](Tech-for-nerds.md) explains the complete processing pipeline and each feature's implementation: document reading, model tool calls, validation, grading, task execution, storage, credentials, mathematics, PDF generation, and packaging. Source links let you follow the design into the code.

Seven chapters trace [architecture](Tech-Architecture.md), [document preparation](Tech-Document-Pipeline.md), [model protocols](Tech-Model-Loop.md), [grading invariants](Tech-Validation-Grading.md), [execution](Tech-Jobs-Execution.md), [storage](Tech-Storage-Recovery.md), and [rendering](Tech-Rendering-Export.md).

This wiki describes the current repository implementation. Older releases may differ. AI completion means processing checks passed; it does not guarantee correct transcription or grading. Review unclear text, formulas, scores, and reference matches against the originals.

[Source code](https://github.com/TaiRuiQu/oh-my-study) · [Releases](https://github.com/TaiRuiQu/oh-my-study/releases) · [Report an issue](https://github.com/TaiRuiQu/oh-my-study/issues)

## Repository maintenance and recent changes

The maintained Wiki now lives in `docs/wiki`. Use [contents](_Sidebar.md) for all chapters and [migration notes](README.md) for provenance.

Current behavior includes shared stems and ordered items, dedicated question reading pages, local text and optional [meaning search](Features-Search.md), [task retry recovery](Features-Tasks.md), batch page reads/saves, reusable one-hour file uploads, persistent Python, generated study images, and SQLite v6. Older releases may differ.
