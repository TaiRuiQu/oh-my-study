---
name: "Oh My Study"
description: "Mobile capture design tokens; desktop styling remains governed by WinUI resources."
colors:
  mobile-blue: "#2454ff"
  mobile-blue-hover: "#1746ec"
  mobile-blue-tint: "#eef1ff"
  mobile-canvas: "#f1f2f7"
  mobile-surface: "#fff"
  mobile-ink: "#242529"
  mobile-muted: "#626672"
  mobile-border: "#daddE5"
  mobile-camera: "#000"
  mobile-viewfinder: "#171717"
  mobile-camera-toolbar: "#202020"
  mobile-page-stage: "#e6e7ee"
  mobile-sheet: "#f5f6fb"
  mobile-shutter: "#343434"
  mobile-error: "#c42b31"
  mobile-camera-error: "#ffb8bc"
typography:
  mobile-body:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    lineHeight: 1.5
  mobile-headline:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "24px"
    letterSpacing: "-.025em"
  mobile-brand:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "25px"
    fontWeight: 650
    letterSpacing: "-.025em"
  mobile-tool-label:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "13px"
    fontWeight: 450
    lineHeight: "20px"
  mobile-camera-label:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "12px"
    fontWeight: 450
    lineHeight: "20px"
  mobile-navigation:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "15px"
    fontWeight: 500
    lineHeight: "24px"
  mobile-segment:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "16px"
    fontWeight: 500
    lineHeight: "22px"
  mobile-send:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "16px"
    fontWeight: 600
    lineHeight: "24px"
  mobile-meta:
    fontFamily: "\"Study Sans\",\"Microsoft YaHei UI\",sans-serif"
    fontSize: "12px"
  desktop-interface:
    fontFamily: "Microsoft YaHei UI"
rounded:
  mobile-control: "12px"
  mobile-mode: "8px"
  mobile-page: "10px"
  mobile-tools: "15px"
  mobile-sheet: "22px"
  mobile-pill: "24px"
  mobile-circle: "50%"
spacing:
  mobile-tight: "4px"
  mobile-gap: "8px"
  mobile-control-inset: "12px"
  mobile-detail-gap: "14px"
  mobile-grid-gutter: "20px"
  mobile-stage-gutter: "22px"
  mobile-detail-gutter: "24px"
components:
  mobile-send:
    backgroundColor: "{colors.mobile-blue}"
    textColor: "{colors.mobile-surface}"
    typography: "{typography.mobile-send}"
    rounded: "{rounded.mobile-pill}"
    padding: "7px 16px"
    height: "40px"
  mobile-send-hover:
    backgroundColor: "{colors.mobile-blue-hover}"
  mobile-surface-action:
    backgroundColor: "{colors.mobile-surface}"
    textColor: "{colors.mobile-ink}"
    rounded: "{rounded.mobile-pill}"
    padding: "8px 12px"
  mobile-restoration-select:
    backgroundColor: "{colors.mobile-surface}"
    textColor: "{colors.mobile-ink}"
    rounded: "{rounded.mobile-control}"
    padding: "8px 12px"
  mobile-capture-mode-selected:
    backgroundColor: "{colors.mobile-blue}"
    textColor: "{colors.mobile-surface}"
    rounded: "{rounded.mobile-mode}"
    padding: "8px 12px"
  mobile-review-tab:
    textColor: "{colors.mobile-ink}"
    typography: "{typography.mobile-navigation}"
    padding: "8px 12px"
  mobile-count:
    backgroundColor: "{colors.mobile-blue}"
    textColor: "{colors.mobile-surface}"
    rounded: "{rounded.mobile-page}"
    padding: "1px 5px"
  mobile-page-card:
    backgroundColor: "{colors.mobile-page-stage}"
    rounded: "{rounded.mobile-page}"
  mobile-shutter:
    rounded: "{rounded.mobile-circle}"
    padding: "4px"
    width: "72px"
    height: "72px"
  mobile-review-tools:
    backgroundColor: "{colors.mobile-surface}"
    rounded: "{rounded.mobile-tools}"
    padding: "15px 6px 10px"
  mobile-processing-option:
    textColor: "{colors.mobile-ink}"
    typography: "{typography.mobile-tool-label}"
---

# Design System: Oh My Study

## Overview

**Creative North Star: "The Pocket Document Scanner"**

Mobile capture follows the document-scanner reference chosen by the user: a black camera workspace with white outline controls and saturated blue selection, followed by a pale review workspace where the photographed page dominates. Rounded white controls frame the document without competing with it. The app name remains in the browser title; the camera navigation bar was removed at the user's request.

This record describes the implemented phone surface in src/OhMyStudy.App/MobileCapture/index.html. All mobile-prefixed tokens and every camera, review, tool-grid, and responsive measurement below apply only to mobile capture. Desktop keeps its native WinUI theme resources, teal accent, typography, control states, and layout authority in src/OhMyStudy.App/Themes/WorkspaceResources.xaml; this document establishes no new desktop layout scale.

**Key Characteristics:**

- Black camera chrome and a cool, light review canvas.
- Blue marks the current mode, selected controls, and send action.
- Consistent outline icons, fixed icon rows, and aligned captions.
- Large-page review, paging, optional processing, and one batch send.

The current [surface brief](.impeccable/surfaces/src-ohmystudy-app-mobilecapture-index-html.md) records the user-supplied camera and editor references. This is a code-extracted record of that selected mobile world. No shipping raster assets were added; page previews come from documents, and interface icons use inline SVG.

## Colors

Mobile capture uses a saturated scanner blue, black camera chrome, and cool pale neutrals. The frontmatter is normative for the extracted mobile values.

### Primary

- **Scanner Blue** marks the selected capture mode, active review controls, selection checks, progress, and batch send.
- **Pressed Blue** is the primary hover treatment.
- **Blue Mist** distinguishes the selected image/pages segment on its white capsule.

### Neutral

- **Review Canvas** is the cool, pale background behind the large page and fixed dock.
- **Control White** fills capsules, tool trays, sheets' controls, and selection marks.
- **Document Ink** is the main review text; **Readable Slate** is the final muted-text value after the source's override.
- **Quiet Divider** separates settings and secondary navigation.
- **Camera Black** and **Viewfinder Charcoal** distinguish camera chrome and live preview.
- **Page Stage** supports portrait thumbnails in the Pages grid.
- **Settings Mist** fills the modal settings sheet; **Shutter Charcoal** fills the white-ring shutter.

### Status

- **Error Red** colors actionable review errors.
- **Camera Error Rose** keeps errors readable against the black camera surface. Errors also have explanatory text.

### Desktop authority

The desktop exam workflow extends the native system without changing tokens. Settings use a connected vertical Steps track before provider management: MinerU recognition, provider-backed splitting, parallel solving, and grading. Each 36px icon node connects to the next with a 1px rule; captions use 16px semibold text and 13px output hints. Configuration fields align to the right and move below their own caption below 680px of step width, while the track remains continuous. No numerical prefixes are used. The start dialog uses a compact horizontal icon flow with arrows and a simple model form. Tasks reuse the connected step component with live states, then show per-question progress; diagnostic records stay in an expander. Provider cards have a 20px gap without a divider or resize handle. Existing task brushes and WinUI controls retain theme behavior. No mobile tokens or raster assets are used.

Desktop colors remain theme resources in [WorkspaceResources.xaml](src/OhMyStudy.App/Themes/WorkspaceResources.xaml), including `TaskAccentBrush`, `TaskAccentBackgroundBrush`, `WorkspaceCanvasBrush`, `WorkspaceSurfaceBrush`, `WorkspaceCardBrush`, `WorkspaceBorderBrush`, and paper/text resources. Preserve their Light, Default, and HighContrast mappings. Mobile's fixed light review palette does not replace those theme dictionaries.

**The The Surface Boundary Rule.** Mobile blue, Study Sans, shell dimensions, and tool grids belong to mobile capture. Desktop continues to use WinUI resources and native control behavior.

The sidecar's eight-step tonal ramps are synthesized swatch previews, not additional application tokens.

## Typography

**Mobile Interface Font:** Study Sans, then Microsoft YaHei UI and sans-serif. The embedded face retains the variable weight range (100–900), uses `font-display: swap`, and disables synthetic font styles.

Study Sans is a local WOFF2 subset of Noto Sans SC from the [Google Fonts source](https://github.com/google/fonts/tree/main/ofl/notosanssc). It contains printable ASCII and characters used by the English and Simplified Chinese localization catalogs, while retaining OpenType layout features and the variable weight axis. Japanese uses Yu Gothic UI with system fallbacks; Korean uses Malgun Gothic with system fallbacks.

The receiver embeds [StudySans.woff2](src/OhMyStudy.App/MobileCapture/StudySans.woff2) as data, so this interface makes no font-service request. [Font-NOTICE.md](src/OhMyStudy.App/MobileCapture/Font-NOTICE.md) records the upstream font URL, source SHA-256, and subset tooling. [OFL.txt](src/OhMyStudy.App/MobileCapture/OFL.txt) carries the SIL Open Font License 1.1 and the upstream copyright/Reserved Font Name notice. Preserve both notices when redistributing the font.

**Desktop Interface Font:** Microsoft YaHei UI through `ContentControlThemeFontFamily`. Desktop text sizes and native role styles remain defined by WinUI and the existing XAML resources.

### Hierarchy

- **Mobile headline:** the short surface heading; camera empty-state and editor headings use their observed smaller overrides.
- **Mobile tool label:** review captions with a fixed line box. Camera captions use their own smaller role, reduced further on compact screens.
- **Mobile navigation:** medium-weight preview/filter/pages tabs.
- **Mobile segment:** compact image/pages controls; selected state increases weight to 600.
- **Mobile send:** the compact, semibold top-right batch action.
- **Mobile metadata:** progress, explanatory text, and compact feedback.

Body line height is inherited from the mobile root. There is no invented display face, modular type ratio, or global desktop size scale.

**The The Aligned Tool Rule.** Camera and review tools reserve separate icon and caption rows; label length must not shift adjacent icons or button baselines.

## Layout

Every measurement in this section belongs to mobile capture.

The phone page is centered in a shell with a maximum width (600px). It fills the viewport on phones; the same shell remains centered in a wide browser. Camera uses dynamic viewport height with a small-viewport fallback, safe-area bottom padding, and hidden outer overflow.

Camera fills the dynamic viewport, respecting safe-area insets. Its grid contains a flexible viewfinder with a minimum (180px) and controls (126px). The title/navigation bar, scan-category row, and Local processing badge were removed at the user's request. The viewfinder is edge to edge; floating capture modes sit near its top, HD and settings tools align along the right, and captured thumbnails sit at the lower right. The bottom row has five columns (`1fr 1fr 82px 1fr 1fr`) around the shutter. HD defaults off and captures native still photos without changing preview resolution.

Review starts with a compact header (76px): a circular back control, an image/pages capsule, and blue batch send. The default review stage uses side margins (22px), fits the complete page with `object-fit: contain`, and places the document above a fixed dock. The stage height follows the dynamic viewport minus controls (310px), with a minimum (250px).

The dock stays within the same shell and includes paging/original/continue controls, a white four-column tool tray, and preview/filter/pages navigation. Review reserves bottom space (230px); the rotation editor reserves its own action-bar space (100px). Safe-area padding protects bottom actions.

Pages is an alternate two-column portrait grid, not the default review composition. It uses row/column gaps (18px/12px), side gutters (20px), and a thumbnail aspect ratio (3:4). Settings options use two columns with independent checkboxes.

At the compact breakpoint (maximum width 360px), header gaps and insets tighten, camera captions reduce to 11px, segment labels reduce to 14px, and paging/continue labels reduce. Tool icon and caption rows retain their shared geometry. Do not infer desktop application breakpoints from this web shell.

## Elevation & Depth

The scanner uses flat tonal layers. A black camera, pale review canvas, white controls, and the document itself supply most separation. The page image remains the visual focus without decorative card shadows.

### Shadow Vocabulary

- **Status notice** (`0 4px 20px #00000012`): transient feedback above the workspace.
- **Preview progress** (`0 4px 20px #0001`): the compact processing notice over the page.

**The The Flat Workspace Rule.** Use tonal separation for the camera, page, and tool tray. Reserve shadows for transient notices.

## Shapes

Mobile controls use soft corners and capsules. General controls and mode containers use the control radius; selected capture modes use the smaller mode radius. Page thumbnails use the page radius, the review tool tray has its own softer radius, and the settings sheet uses a larger sheet radius.

Back controls, pager controls, selection marks, and the shutter use circular silhouettes. The shutter has a white outer ring (4px) around its dark inner disc (56px).

Outline icons use rounded line caps and joins, normally on a square canvas (26px) with stroke width (1.8). Camera controls use their observed icon override (27px, stroke 1.9); review tools use square icons (30px) centered in the shared icon row (32px).

## Components

### Buttons

Actions are concise and touch-oriented. General controls have a minimum height (44px); camera/review tool cells are taller (76px). Compact header segments and pager controls retain their source-specific sizes rather than inheriting a blanket touch-height rule.

Primary actions use Scanner Blue and white text. The top send action is a capsule with a fixed height (40px). Surface actions use white or transparent backgrounds according to their surrounding layer. Pressed controls reduce opacity (0.65); disabled controls reduce opacity (0.35). Keyboard focus uses a blue outline (3px) offset from the control (3px).

### Capture modes and shutter

The single/multiple-page selector floats over the viewfinder in a translucent dark capsule. Blue fill and `aria-pressed` identify the selected mode. Multiple pages is the initial mode.

The shutter remains centered between two actions on each side. A short white capture flash fades from opacity (0.65) over 220ms with ease-out. Reduced-motion preferences disable the animation. This flash is feedback, not a navigation transition.

The viewfinder shows the complete camera preview without a detected-corner outline. Original frames reach the computer before model correction; capture does not crop the queued photo.

### Review header and navigation

Image/pages segments sit inside a white capsule. Selected segments combine a pale blue fill, blue text, and increased weight. The blue send label includes the selected count when pages are selected and stays disabled until selected previews are ready.

Bottom navigation uses blue text and a short blue underline (16px wide, 4px high) for the active preview/filter/pages tab. The page counter uses tabular numerals; original comparison and continue shooting flank the pager.

### Tool tray

Four equal-width tools sit on a white rounded tray. Each reserves an icon row (32px), a caption row (20px), and a consistent row gap (8px). Keep the actual white-background filter, image-correction settings, rotation, and remove actions; this record does not authorize adding unsupported reference functions.

### Page previews and counts

The default review shows one large, fully contained document. Horizontal swipes change pages with finger-follow feedback, edge resistance, and reduced-motion support. Keep vertical scrolling and pinch zoom available; selection controls do not initiate swipes. In the Pages grid, a 450ms hold starts dragging, with haptic feedback when supported, a lifted thumbnail, a highlighted drop target, and edge scrolling. Save the order to the computer and restore it if saving fails. Keep previous/next buttons as an accessible alternative. The alternate Pages grid uses selectable portrait previews with a page index, short filename, state text, and removal control. Circular checkboxes use blue fill and a white check when selected. File names truncate; detailed status and errors remain available as text.

The camera's count badge is a compact blue capsule anchored to Recent scans. Captured document thumbnails supply imagery; no decorative raster set is part of this visual system.

### Inputs and processing settings

Settings and editor options use independent labeled checkboxes, with shared label rows and a visible focus outline. Automatic orientation, full-frame geometry and white-background enhancement start enabled. Camera settings and the main Restore tool act on every unsent photo and future photos. Closing the settings sheet regenerates existing previews once. Single-page overrides remain in the rotation editor; manual rotation takes precedence over automatic orientation. Computer imports use a native WinUI confirmation with an initially unchecked image-correction option.

The modal settings sheet rests near the bottom with a dark backdrop. Processing errors remain explicit text. Keep the reset action that turns off all processing and clears rotation.

## Do's and Don'ts

### Do:

- Do scope every mobile-prefixed token and scanner layout measurement to mobile capture.
- Do preserve the aligned 32px icon rows and 20px caption rows in camera and review tools.
- Do keep the page fully visible in review and provide paging, continue shooting, and batch send controls.
- Do present orientation, geometry and white-background processing independently, initially enabled on the phone.
- Do retain visible focus outlines, text status, disabled states, and reduced-motion behavior.
- Do preserve the local font embedding and distribute its font notice and OFL license.

### Don't:

- Don't propagate the mobile blue palette, font, or 600px shell into the desktop WinUI workspace.
- Don't restore the rejected teal camera and two-column-first review composition.
- Don't substitute unsupported reference features or another product's branding for the implemented actions.
- Don't let variable caption lengths move icons out of their shared rows.
- Don't enable restoration tasks implicitly when another processing option is selected.
- Don't replace user document previews with decorative shipping imagery.


Mobile capture keeps structure and styles in `MobileCapture/index.html` and interactions in `capture.js`. Model inference runs locally on the computer. Embedded script assets are served from an explicit allowlist with self-only script CSP. HTML contains only non-executable JSON configuration.

Interface copy names actions and reports actual state. Do not add instructional captions for swiping, dragging, tapping, paging, resizing, searching, or other standard interactions. Empty states use concise titles and relevant actions without tutorial paragraphs. Retain error recovery, progress, constraints, upload/cost notices, processing scope and accessible control names. Interaction instructions belong in documentation.
