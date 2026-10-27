# Import modes, limits, and original-page management

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Import-zh-CN.md)

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

## Prepare the source before uploading

Open the document yourself. Decrypt encrypted PDFs, check page order, and ensure small print and handwriting are legible. The application accepts PDF, PNG, JPEG, and WebP; an unsupported office document should first be converted using its own application.

Limits apply at several levels: 12 pages across the exam, student answer attachments, and references, 64 MiB per file, 256 MiB per batch, and 40 million pixels per image. Derived media can also exceed processing budgets. A small PDF file can still contain too many pages; a one-page image can still be too large.

## Take photos directly from your phone

In **Import exam**, **Add reference answers**, or **Upload student answers**, choose **Take photos with phone**. Connect your phone to the same Wi-Fi as the computer and scan the displayed QR code with the phone camera. The code opens the capture page directly; no app, account, IP address entry, or pairing code is needed. You can also copy the connection link. The computer offers two connections: **Continuous camera (experimental HTTPS)** for an embedded viewfinder, and **System camera and files (HTTP)** for the phone's own camera and file picker.

Leave **Multiple pages** selected and keep tapping the shutter; photos queue on the phone in capture order without uploading or waiting for a correction. **Single page** opens the preview after one capture. Open **Recent scans** or a capture thumbnail to send the originals to the computer for local previews, then work through the large page: previous and next navigation, original comparison, white-background enhancement, rotation, and image-correction settings, and **Continue shooting**. Open **Pages** to review every result in a selectable grid, keep the pages you want, and remove the rest. A failed page keeps its original and can be retried or deselected; canceling processing keeps the queue. Tap **Send (N)** once to confirm the selection together, then click **Import** on the computer; the usual recognition settings and confirmation still apply. Photos and PDFs stay out of the import list until they are confirmed. Original photos and corrected pages are saved separately in the library. Keep the phone page open: reloading loses photos that have not been transferred to the computer, while transferred drafts can be recovered during the same session.

The temporary receiver listens only on the selected computer network, and the QR code carries a random session credential that expires after 20 minutes; closing the window stops both receivers. **Continuous camera (experimental HTTPS)** needs a browser-trusted secure context, so the computer serves a one-hour certificate for its selected private LAN address, signed by the publicly disclosed eMudhra EM OV TLS CA - G3C intermediate and pinned by SHA-256 `7485B51C0ED192878FA1D2F948FB529AE1842CC5086C4B67F7BE5B2039DE9489`. It installs no root and changes no browser validation. That compromised chain is experimental: current Chromium rejected it in testing, and revocation or Certificate Transparency enforcement can block a phone, so it cannot establish reliable server identity on a hostile network. Choose **System camera and files (HTTP)** and scan the replacement code when it does not open; HTTP keeps the system camera and file selection and is meant for trusted networks. Processing runs locally on the computer with the retained orientation classifier and the project fine-tuned geometry and illumination models. Automatic orientation precedes geometry. Geometry sees the complete photo without corner cropping or perspective preprocessing. The existing page/file/batch limits still apply, including originals and corrected images. Multiple PDFs may exceed the page limit even if fewer than 12 files were sent; final import validates their pages.

If scanning does not connect, select another computer network before uploading, keep the computer awake, and allow Oh My Study on private networks if Windows prompts. Guest Wi-Fi can isolate devices; a phone hotspot is another way to put both devices on the same network. Some in-app QR browsers restrict file capture; open the link in Safari or Chrome. Correction addresses page geometry, shadows, and paper appearance. It does not recover blur or recreate missing detail. Retake severely blurred, glared, or folded pages.

## Local image correction

When importing images from this computer, including drag-and-drop, **Use image correction** is offered before the usual recognition settings. It starts unchecked. Enable it to run automatic orientation → project fine-tuned full-frame geometry → project fine-tuned illumination and white-background enhancement. This applies to exams, reference answers and student answers. PDFs are not corrected. Original image bytes are retained separately from processed PNGs, and both count toward existing import limits. Recognition and provider-upload confirmation remain separate.

Phone **Scan settings** provides **Automatic orientation**, **Correct page geometry**, and **Enhance white background**, enabled initially. Changes apply to all unsent photos and future photos; sent pages and PDFs stay unchanged. The page editor supports individual settings and manual rotation; manual rotation takes precedence over automatic orientation. **Turn off all processing** also resets rotation. Inspect the full-page preview and colored or faint marks before sending.

The two project fine-tuned models contain about 65 MB of ONNX weights. The existing 6.8 MB direction classifier remains. All run with ONNX Runtime CPU. Image correction requires no separate download, Python, CUDA, or OpenCV. Upstream UVDoc and SyntheticDoc MIT notices and the orientation model's Apache-2.0 notice ship under native/restoration/. Source setup uses packaging/prepare-restoration.ps1 after git lfs pull; see [installation](../installation.md).

Hard shadow boundaries, mixed-light color casts, faint marks, bleed-through and existing blur remain limitations. This pipeline does not perform character synthesis or learned deblurring. No local latency guarantee is implied. Canceling or failing correction does not replace the original.

## Mode is a grading contract

Automatic detection asks the model to decide from the page images and saved responses whether the exam is blank, answered, or teacher-graded, then apply the corresponding behavior. Choose an explicit mode when you already know the source type.

Blank mode extracts reusable questions and generates standard answers and solutions while leaving attempts absent, even if reference answers exist. Answered mode must retain student work and distinguish a visibly blank response from illegible writing. Teacher mode prioritizes explicit teacher results and uses AI where a teacher result is unknown. Incorrect or partial work receives a separate error analysis when the cause is supported by the evidence; unclear causes stay empty.

After import, use **More → Upload student answers** inside the exam to add answer photos or PDFs. To type work for an individual question, use **Enter student answer** on its page. Then recognize the exam again. Added work matches existing question keys; original questions and standard solutions remain. A blank exam with added responses switches to automatic detection so the model can grade them. Manually entered responses are protected, and editing a response clears its old grade and error analysis.

The model can also report the printed exam title. An absent title keeps the imported name, and a user-edited title is preserved on re-recognition.

Recognition stores question numbers and subquestion labels separately from the question wording so they appear once in the workspace and exported PDF. Numbers within the wording, question references, and answer-option labels remain part of the content. This prompt change applies to future recognition; it does not rewrite already saved questions.

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

When no explicit paper score is supplied, the paper list sums the question scores only when every saved question has a determined score. An explicit paper score and manual paper metadata retain priority; missing question scores are not treated as zero. The derived list total does not overwrite the model result.
