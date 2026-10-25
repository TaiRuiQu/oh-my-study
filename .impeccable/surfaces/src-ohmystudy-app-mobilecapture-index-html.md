---
version: 1
slug: "src-ohmystudy-app-mobilecapture-index-html"
primary_target: "src/OhMyStudy.App/MobileCapture/index.html"
related_targets: []
---

# Mobile capture

Mode: Operate. Students photograph several pages beside their computer, then review and send a batch. Preserve local processing, originals, correction tools, and import limits. The user rejected the previous teal scanner design and explicitly requested a one-to-one adaptation of two supplied Quark scanner screenshots, retaining only supported functions.

## Direction contract

THESIS: Keep the camera available for repeated captures; collect pages before correction or confirmation.

OWN-WORLD: Follow the supplied reference: black camera chrome, a full-width viewfinder, white outline icons, a blue selected capture mode, a white-ring shutter, and five bottom actions. Review uses a cool pale gray canvas, white capsule controls and tools, saturated blue selection, and a local variable Noto Sans SC subset for Chinese/English. Tool icons occupy aligned 32px rows; captions occupy aligned 20px rows. These mobile-specific choices override the previous teal direction by explicit user request; desktop WinUI remains unchanged.

STORY: Capture repeatedly, see the thumbnail count grow, open Recent scans to generate previews, adjust individual pages when necessary, and confirm selected pages in one action. Standard processing defaults to automatic corners/crop, orientation, shadows, deblur, and appearance. Global switches affect all unsent photos and future photos; closing settings rebuilds previews together. DocRes defaults to all four official tasks directly on the original photo. Every task remains optional, with single-page overrides available in the crop editor.

FIRST VIEWPORT: Camera fills the viewport with a viewfinder, floating single/multiple-page selector, HD and settings side tools, and five bottom actions around a 72px shutter. The user explicitly removed the app title/navigation bar, the bottom Scan documents category, and the Local processing badge. Review has a white back circle, image/pages capsule and blue send action, a large document, a paging/continue row, a white rounded tool tray, and bottom preview/filter/pages navigation. The Pages control reveals the selectable grid. HD defaults off and uses native still capture without changing the preview stream resolution.

FORM: Reference-led reconstruction from C:/Users/XiaoQ/AppData/Local/Temp/codex-clipboard-92a0293b-68d5-42df-b614-53eb76635ead.jpg and C:/Users/XiaoQ/AppData/Local/Temp/codex-clipboard-73b019d7-6ced-4943-81f6-c47eab495149.jpg. No random form seed or generated comp. Keep the real app name and omit reference features this product does not implement. The signature interaction is continuous capture with a shutter flash, then large-page review with paging and one batch send. Reduced motion disables the flash.

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance

No shipping raster assets are added. Browser screenshots use the supplied photos through a local canvas stream fixture; no camera hardware or remote upload is involved. Continuous camera requires the browser to accept HTTPS; the requested eMudhra chain is experimental and was rejected by current Chromium. HTTP preserves system camera and file picking.

The capture guide uses neutral-color segmentation, morphological cleanup, quadrilateral fitting and boundary refinement before grayscale/Otsu/Sobel fallbacks. Analyze 320-pixel frames in a module Worker at 200ms intervals, skipping frames while busy. Map through video cover cropping and hide unreliable detections. Four user-supplied photos cover wood, keyboards, shadows, clipping and curved spreads; originals and diagnostic copies stay out of source control. Curved boundaries remain quadrilateral approximations.

Mobile review supports horizontal finger-follow swipes, vertical scrolling and pinch zoom. Hold a grid page for 450ms to drag and reorder, with haptics when available, a lifted preview, target highlight and edge scrolling. Sync the order to the computer before confirmation; failed saves restore the old order. Buttons remain available for paging. Structure, interactions, pure detection and worker execution live in separate files.

Finish review: verified 390×844 and 320×640 mobile layouts, module loading under self-only CSP, a real browser drag changing 1/4 to 2/4, and four annotated real-photo detections. Automated checks cover touch sorting, scroll/pinch/cancel handling, order rollback, and native import order. Physical phone touch and native camera hardware remain untested.

User copy constraint: remove visible gesture instructions and other captions explaining standard controls. Preserve the gestures themselves, accessible names, processing scope, errors and connection/file limits. Review has no gesture-hint row.
