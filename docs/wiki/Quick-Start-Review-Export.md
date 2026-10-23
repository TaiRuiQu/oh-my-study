# 4. Build a review pack and protect your library

[Home](Home.md) · [Quick Start](Quick-Start.md) · [简体中文](Quick-Start-Review-Export-zh-CN.md)

![PDF layouts: A4 portrait and A3 landscape with two columns; question and answer editions are separate exports.](images/pdf-layouts.png)

## Pick the collection that matches the session

Open an exam to review its questions together, Question bank for all saved questions, or Mistakes for partial, incorrect, and unanswered work. A top-level question also qualifies when a item is wrong. This collection is generated from saved grades, not a separate manual favorite list.

Before exporting, look at the displayed list and save pending edits. The export dialog's count matters: it includes displayed saved questions, not only the selected row.

## Make two files

First export Questions on A4 portrait or A3 landscape. Then repeat with Answers and explanations. Use names you can distinguish, such as Algebra-review-questions.pdf and Algebra-review-answers.pdf.

The question edition leaves writing space; the answer edition uses standard answers and explanations rather than student-attempt text. A3 landscape uses two columns. Diagrams above illustrate layout concepts, not an exact print preview.

## Inspect before printing

Open both PDFs in a reader. Confirm formulas, figure crops, numbering, items, and page breaks. A source-formula warning means you must inspect the affected notation. If a figure contains handwriting, use a genuinely clean source region before a question-sheet export; changing its flag does not erase ink.

Export is local. Re-recognition is a separate potentially billable model operation, so you do not need to rerun recognition just to produce another PDF layout.

## Keep an independent backup

Find the actual path in Settings → Library. Save work, exit the app, and copy the complete folder to a separate location. A PDF is a review output, not a replacement for the editable library.

To recover a current-version library, preserve your backup, make a working copy, and select that copy as an existing library. Keys may need re-entry on a new computer. Moving the library through Copy and switch retains the original, but a copy on the same failing disk is not an independent backup.

Implementation and reference: [MainWindow.Documents.cs](../../src/OhMyStudy.App/MainWindow.Documents.cs) · [MainWindow.LibrarySettings.cs](../../src/OhMyStudy.App/MainWindow.LibrarySettings.cs)

---

[Previous chapter](Quick-Start-Verify.md) · [Section contents](Quick-Start.md)
