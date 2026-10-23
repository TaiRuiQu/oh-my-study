# Import modes, limits, and original-page management

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Import-zh-CN.md)

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

## Prepare the source before uploading

Open the document yourself. Decrypt encrypted PDFs, check page order, and ensure small print and handwriting are legible. The application accepts PDF, PNG, JPEG, and WebP; an unsupported office document should first be converted using its own application.

Limits apply at several levels: 12 pages across the exam and references, 64 MiB per file, 256 MiB per batch, and 40 million pixels per image. Derived media can also exceed processing budgets. A small PDF file can still contain too many pages; a one-page image can still be too large.

## Mode is a grading contract

Blank mode extracts reusable questions and leaves attempts absent, even if reference answers exist. Answered mode must retain student work and distinguish a visibly blank response from illegible writing. Teacher mode prioritizes explicit teacher results and uses AI where a teacher result is unknown.

For example, a red subtraction mark without enough context should not become an invented numerical score. Unknown maximum points cannot support a fabricated earned score. A reference key is evidence for matching, not permission to disregard explicit teacher results.

## What is stored

Import copies originals into managed library assets and prepares local page images/thumbnails. Recognition then runs separately. Your external source can be disconnected without removing the library copy, but deleting the exam deletes its managed original too.

A PDF page record preserves its relationship to the original asset and page index. Rotating or reordering pages is stored as library metadata rather than rewriting the external PDF.

## Manage pages after import

Open the exam and More → Manage pages. Move entries up/down or rotate left/right, inspect the proposed order, then save. Removal is available only for unreferenced pages and must leave at least one page.

References include question sources, embedded image regions, student regions, teacher evidence, items, and answer sources. If removal is blocked, inspect why the page is still useful; bypassing the block would break source navigation. Finish active processing before changing page structure.

## If recognition never finishes

Check Tasks rather than importing the same exam again. A saved original can coexist with a failed or cancelled job. Fix the reported issue and re-recognize, preserving useful saved work. For a genuinely incomplete source, obtain a better copy and plan how to preserve edits before replacing or reprocessing material.

Implementation and reference: [MainWindow.Library.cs](../../src/OhMyStudy.App/MainWindow.Library.cs) · [LibraryAssets.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryAssets.cs) · [MainWindow.Documents.cs](../../src/OhMyStudy.App/MainWindow.Documents.cs)

---

[Previous chapter](Features-Models.md) · [Section contents](Feature-Guide.md) · [Next chapter](Features-Workspace-Editing.md)
