# Oh My Study

## Product

Oh My Study is a Windows study app for students, built with C# 14, .NET 10, and WinUI. It imports exams and reference or student answers, recognizes and grades questions, supports review against original documents, manages a question bank and wrong-question collection, and exports review PDFs.

Study libraries and originals stay local by default. AI transcription, answers, and grades are reviewable suggestions; users can correct them. Preserve original documents, user edits, library compatibility, and source relationships when extending workflows. Recognition sends materials to the selected provider and may incur costs, so its upload and cost confirmation remains separate from local capture.

## Phone capture contract

- Offer phone capture alongside computer file selection in all three import flows: exams, reference answers, and student answers.
- Open a temporary local HTTP receiver on the selected computer network. The phone scans a QR code on the same LAN; no separate app, account, manual IP entry, or pairing code is required. Use a trusted network. Each random-token session lasts 20 minutes; closing the desktop capture dialog stops the receiver.
- Let the phone take photos or choose existing photos and PDFs. Keep queued files in capture order, support removal and retry, and require desktop confirmation before import.
- Analyze photos on the computer with bundled CPU models: DocAligner LCNet100 for page corners, PaddleOCR PP-LCNet_x1_0_doc_ori for 0/90/180/270-degree orientation, and DocScanner-L for dense rectification. Their weights total about 45.7 MB and are versioned with Git LFS in `packaging/models/scanner/`. Capture and correction use no external upload or provider API.
- Provide manual corner and rotation overrides, a correction preview, and color enhancement, grayscale enhancement, or no enhancement. Low-confidence corners fall back to the full photo for manual review. Preserve original photos separately from corrected pages.
- Keep existing library and import bounds, including aggregate page, file, batch, and image limits. Originals and corrected copies count toward processing budgets; PDFs still require final page validation. Continue through the existing recognition confirmation after capture.

## Interface continuity

This is an extension of the existing interface. Inherit the teal accents, neutral surfaces, Microsoft YaHei UI desktop typography, WinUI controls, and light, dark, and high-contrast behavior established in `src/OhMyStudy.App/Themes/WorkspaceResources.xaml`. The phone page uses system sans-serif typography and the corresponding teal/neutral palette.

Keep controls readable, focus visible, mathematics legible, and long operations responsive. Show progress, actionable errors, retry, and cancellation. The phone editor permits vertical scrolling outside the corner handles; the handles retain drag control. Keep interface text in the Simplified Chinese, English, Japanese, and Korean localization catalogs. Use concise interface messages and detailed English diagnostics without credentials.

## Verification limits

Reported capture verification on 2026-10-24 covered four supplied real photos and all 16 rotated variants, 33 published Native AOT media checks, 215 storage checks, and an isolated native QR smoke check. These passed. Android and iPhone native camera flows and diverse LAN hardware were not physically tested.

The first partial-page photo in the supplied set used full-frame fallback for manual corner review. Perspective correction does not fully flatten curved book pages or repair blur and glare. These checks do not establish universal detection or orientation accuracy.
