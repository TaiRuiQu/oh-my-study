# Oh My Study

## Product

Oh My Study is a Windows study app for students, built with C# 14, .NET 10, and WinUI. It imports exams and reference or student answers, recognizes and grades questions, supports review against original documents, manages a question bank and wrong-question collection, and exports review PDFs.

Study libraries and originals stay local by default. AI transcription, answers, and grades are reviewable suggestions; users can correct them. Preserve original documents, user edits, library compatibility, and source relationships when extending workflows. Recognition sends materials to the selected provider and may incur costs, so its upload and cost confirmation remains separate from local capture.

## Phone capture contract

- Offer phone capture alongside computer file selection in all three import flows: exams, reference answers, and student answers.
- Open a temporary local HTTP receiver on the selected computer network. The phone scans a QR code on the same LAN; no separate app, account, manual IP entry, or pairing code is required. Use a trusted network. Each random-token session lasts 20 minutes; closing the desktop capture dialog stops the receiver.
- Let the phone take photos or choose existing photos and PDFs. Keep queued files in capture order, support removal and retry, and require desktop confirmation before import.
- Correct images locally with the retained PP-LCNet orientation classifier followed by project fine-tuned full-frame geometry and white-background illumination models. Bundle the ONNX weights and license notices in packaging/models/restoration/. No corner crop, perspective preprocessing, Python, CUDA, OpenCV or model download is used.
- Provide manual rotation, independent orientation/geometry/white-background switches and original-versus-corrected preview. Preserve originals separately. Computer image imports, including drag-and-drop, offer opt-in correction before recognition; PDFs are unchanged.
- Keep existing library and import bounds, including aggregate page, file, batch, and image limits. Originals and corrected copies count toward processing budgets; PDFs still require final page validation. Continue through the existing recognition confirmation after capture.

## Interface continuity

Recognition uses a fixed workflow: upload and MinerU OCR, question splitting with material binding, independent parallel solving, and student-work grading. Present this as connected icon Steps rather than numbered explanatory rows; the start dialog uses a compact horizontal flow. Select splitting and grading models from saved providers and choose the solving model when starting a task. Reuse provider credentials and the saved MinerU token. Tasks opens on submission and shows stage outputs plus per-question solution and grading status. Technical records are secondary, expandable content. Blank exams and reference-only imports skip irrelevant steps. The task surface does not preserve the former agent presentation. Provider list and settings cards are separated by whitespace, without a divider.

This is an extension of the existing interface. Inherit the teal accents, neutral surfaces, Microsoft YaHei UI desktop typography, WinUI controls, and light, dark, and high-contrast behavior established in `src/OhMyStudy.App/Themes/WorkspaceResources.xaml`. The phone page uses system sans-serif typography and the corresponding teal/neutral palette.

Keep controls readable, focus visible, mathematics legible, and long operations responsive. Show progress, actionable errors, retry, and cancellation. The phone editor permits vertical scrolling and manual rotation. Keep interface text in the Simplified Chinese, English, Japanese, and Korean localization catalogs. Use concise interface messages and detailed English diagnostics without credentials.

## Verification limits

The current integration was compiled in Release and Native AOT and visually inspected using a supplied photo processed by the C# orientation, geometry and white-background pipeline. No performance benchmark was run. Interactive import confirmation, Android and iPhone camera flows and diverse LAN hardware were not verified in this integration.

Geometry retains the source aspect ratio. Hard shadows, mixed lighting, faint strokes, bleed-through and existing blur remain limitations. Inspect corrected previews before import. Earlier checks of the replaced pipeline do not establish the current models' detection or orientation accuracy.
