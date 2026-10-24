# Import modes, limits, and original-page management

[Home](Home.md) · [Feature Guide](Feature-Guide.md) · [简体中文](Features-Import-zh-CN.md)

![Study workflow: model setup, local import, recognition, source review, question collections, and PDF export.](images/study-workflow.png)

## Prepare the source before uploading

Open the document yourself. Decrypt encrypted PDFs, check page order, and ensure small print and handwriting are legible. The application accepts PDF, PNG, JPEG, and WebP; an unsupported office document should first be converted using its own application.

Limits apply at several levels: 12 pages across the exam, student answer attachments, and references, 64 MiB per file, 256 MiB per batch, and 40 million pixels per image. Derived media can also exceed processing budgets. A small PDF file can still contain too many pages; a one-page image can still be too large.

## Take photos directly from your phone

In **Import exam**, **Add reference answers**, or **Upload student answers**, choose **Take photos with phone**. Connect your phone to the same Wi-Fi as the computer and scan the displayed QR code with the phone camera. The code opens the capture page directly; no app, account, IP address entry, or pairing code is needed. You can also copy the connection link.

Photograph one page at a time, or choose existing images/PDFs. Multiple selected photos are reviewed in sequence; sending one opens the next automatically. The page shows received and waiting counts. A failed file retains the waiting queue: retry it or skip it to continue. Photos are analyzed locally for page edges, corners, and text direction; sideways and upside-down photos are rotated automatically when the direction is clear. Check the four handles, drag them if needed, or select **Use full photo**. **Rotate right** lets you override the detected direction. Preview page rectification with color restoration, grayscale restoration, or no enhancement. Check for clipped text and send the page, then repeat. Files appear in capture order and can be removed on the phone. Refreshing the phone page lets you continue editing uploaded photos that have not been sent; files not yet uploaded must be selected again. Click **Import** on the computer when ready; the usual recognition settings and confirmation still apply. Original photos and corrected pages are saved separately in the library.

The temporary receiver listens only on the selected computer network. The QR code contains a random session credential and expires after 20 minutes; closing the window stops reception. Photos travel over local HTTP, so use a trusted network. Capture processing uses bundled local CPU models: DocAligner LCNet100 for corners, PaddleOCR PP-LCNet_x1_0_doc_ori for direction, and DocScanner-L for dense rectification, totaling about 45.7 MB of weights. It requires no cloud relay, provider key, GPU, or model API calls. Low-confidence corners remain the full photo for manual adjustment. The existing page/file/batch limits still apply, including originals and corrected images. Multiple PDFs may exceed the page limit even if fewer than 12 files were sent; final import validates their pages.

If scanning does not connect, select another computer network before uploading, keep the computer awake, and allow Oh My Study on private networks if Windows prompts. Guest Wi-Fi can isolate devices; a phone hotspot is another way to put both devices on the same network. Some in-app QR browsers restrict file capture; open the link in Safari or Chrome. Correction can improve mild page curvature and defocus, but cannot recreate missing detail. Retake severely blurred, glared, or folded pages.

## Local document restoration

Phone correction first applies your page corners and rotation, then uses the bundled CPU DocScanner-L dense backward-grid model to flatten curved pages. Color and grayscale restoration then use OpenCV luminance-based shadow removal and conservative Richardson–Lucy deconvolution for mild defocus. **No enhancement** keeps geometric correction and skips those photometric stages. Retained originals and manual corner edits remain available when you cancel or adjust the preview. Severe motion blur, glare, and missing detail still require a new photo; always check faint pencil marks and mathematics in the corrected preview.

Optional **DocRes** is available in the computer's phone-import dialog only when a single discrete NVIDIA GPU has strictly more than 12 GiB dedicated VRAM and an installed CUDA runtime can initialize that same physical device. Exactly 12 GiB is ineligible; memory from multiple GPUs is never added together. A driver banner, CUDA folder, or shared memory alone is insufficient. Select **Download and install DocRes** on the computer: model weights total 896,242,296 bytes (about 855 MiB), with several additional GB for an isolated Python/PyTorch CUDA runtime. The dialog shows a progress bar for each download, the same progress is written to the log, installation can be canceled, and nothing is downloaded on startup. Installation verifies hashes and real CUDA FP32/FP16 inference before becoming ready. Enable it separately, then choose DocRes on the phone. These operations are local; no photos are uploaded to a provider.

Select dewarping, shadow removal, mild blur recovery, and appearance enhancement independently. Tasks run in that order, with blur recovery before appearance enhancement. DocRes dewarping replaces DocScanner dewarping; when only DocRes photometric tasks are selected, DocScanner still handles geometry. **No enhancement** skips neural photometric tasks too. Appearance enhancement can alter faint marks, so inspect before sending. Large deblur inputs use overlapping 768-pixel tiles with feathered joins; shadow/appearance inference uses the upstream 1600-pixel limit and detail-preserving ratio recovery. If CUDA memory or a model fails, the page stays editable: select **Standard (DocScanner + OpenCV)** and retry. Cancellation does not accept or replace a page.

DocRes downloads are stored separately in `%LOCALAPPDATA%/OhMyStudy/DocRes`. Disabling it stops its use without deleting downloaded files. It needs an appropriate NVIDIA driver and installed working CUDA runtime; the app does not install a global CUDA toolkit.

DocScanner is by Hao Feng and has a separate non-commercial license requiring attribution and share-alike; commercial use requires the author's prior written permission. The bundled ONNX model combines the upstream U2NETP mask and 12-iteration DocScanner-L model, with normalized backward-grid output. Its license and modification notice ship in `native/scanner/DocScanner-LICENSE.md` and `DocScanner-NOTICE.txt`. DocRes has an MIT license. See [installation](../installation.md) for preparation details.

## Mode is a grading contract

Automatic detection asks the model to decide from the page images and saved responses whether the exam is blank, answered, or teacher-graded, then apply the corresponding behavior. Choose an explicit mode when you already know the source type.

Blank mode extracts reusable questions and generates standard answers and solutions while leaving attempts absent, even if reference answers exist. Answered mode must retain student work and distinguish a visibly blank response from illegible writing. Teacher mode prioritizes explicit teacher results and uses AI where a teacher result is unknown. Incorrect or partial work receives a separate error analysis when the cause is supported by the evidence; unclear causes stay empty.

After import, use **More → Upload student answers** inside the exam to add answer photos or PDFs. To type work for an individual question, use **Enter student answer** on its page. Then recognize the exam again. Added work matches existing question keys; original questions and standard solutions remain. A blank exam with added responses switches to automatic detection so the model can grade them. Manually entered responses are protected, and editing a response clears its old grade and error analysis.

The model can also report the printed exam title. An absent title keeps the imported name, and a user-edited title is preserved on re-recognition.

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
