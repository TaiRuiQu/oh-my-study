# PDF editions, page layouts, figures, and formula checks

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Export-zh-CN.md)

![PDF layouts: A4 portrait and A3 landscape with two columns; question and answer editions are separate exports.](images/pdf-layouts.png)

## Define the export scope

Open the exam, bank, or mistake list that contains the intended material. Save edits, start Export questions, and read the question count. The export takes currently displayed saved questions, including their items, not just the row with focus.

The service supports 1–500 top-level questions and additional text/image budgets. A large collection can need multiple exports even if every individual question is valid.

## Choose an edition

| Edition | Includes | Intended use |
| --- | --- | --- |
| Questions | Question blocks, options, figures, items, writing space | Practice without displaying the answer key. |
| Answers and explanations | Questions with standard answers and explanations | Checking and guided review. |

Student-attempt text is not the answer key. A recognized answer still needs human review before the answer edition becomes trustworthy. Export numbering is sequential while original printed question numbers are kept in the body.

## Choose a paper layout

A4 is portrait. A3 landscape uses two columns. The image illustrates their organization, not the exact pagination of your question set. Longer questions, large diagrams, and mathematical expressions affect breaks.

Produce separate question and answer files rather than expecting one export to create both. Printing itself happens in a PDF reader, where printer paper/scaling must also match your intention.

## Handle figures responsibly

Image blocks use managed generated images when provided, otherwise crops from managed source pages. Check that the crop includes axis labels, units, and legends without bringing in neighboring answers. The export deduplicates identical decoded image payloads and enforces an aggregate budget.

Question-edition export blocks figures marked as handwriting-containing, including items. This prevents knowingly carrying student work into a clean practice sheet. A false flag does not clean pixels: use a source region that actually lacks the unwanted writing. If no clean source exists, revise the content deliberately rather than claiming the app removes handwriting.

## Check formula fallbacks and failures

Supported LaTeX renders into print assets; unsupported or oversized formulas can retain their source with a warning. A successful PDF write does not prove formula fidelity. Inspect fractions, minus signs, roots, superscripts, matrices, and line/column breaks.

If export fails, save edits first, verify regions and handwriting, reduce the batch, and check the destination. Missing/damaged local tools require package repair or full re-extraction. Local export does not call a model or spend model tokens.

Implementation and reference: [MainWindow.Documents.cs](../../src/OhMyStudy.App/MainWindow.Documents.cs) · [TypstDocumentService.cs](../../src/OhMyStudy.Documents/TypstDocumentService.cs) · [ExamDocument.cs](../../src/OhMyStudy.Documents/ExamDocument.cs)

---

[Previous chapter](Features-Tasks.md) · [Section contents](Feature-Guide.md) · [Next chapter](Features-Library.md)
