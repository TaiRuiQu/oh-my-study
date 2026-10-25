# Installation, Upgrade, and Uninstallation

[Home](../README.md) · [Usage Guide](usage.md) · [中文](installation.zh-CN.md)

The application interface supports Simplified Chinese, English, Japanese, and Korean. UI labels in this guide include Chinese labels in parentheses. See [Language and typography](usage.md#language-and-typography) to change languages.

## Choosing a download

The app targets Windows 10/11 x64. Each source tag `vX` has three independent releases: `vX-aot`, `vX-self-contained`, and `vX-framework-dependent`. Each release offers two download formats:

| Format | How to use |
| --- | --- |
| `Setup.exe` | Velopack one-click, per-user installation with a Start menu entry |
| Portable ZIP | Extract and run directly; adds no system installation entries |

Choose a variant based on the release tag:

| Tag | When to use it |
| --- | --- |
| `aot` | No separate .NET installation is required; choose this if unsure |
| `self-contained` | Bundles the .NET runtime; no separate .NET installation is required |
| `framework-dependent` | Requires Microsoft ASP.NET Core 10 x64 Runtime (including .NET Runtime) to be installed first; Setup does not download it automatically |

The packages include the components needed for local typesetting, image processing, and computation, so you do not need to install Python, Node.js, or TeX yourself. AI features still require a network connection, a provider key, and an available model.

Release packages exclude debug symbols, Python bytecode caches, and dependency test suites, while retaining the runtime helpers needed for computation. Bundled Windows UI components, PDF tools, and numerical libraries still contribute to the download size. Releases use Velopack rather than MSI, MSP, or WiX.

## Document restoration components

Standard phone-photo restoration includes CPU DocScanner-L ONNX inference and OpenCV. It needs no Python installation or CUDA. DocScanner's separate non-commercial, attribution/share-alike license is included with the model; commercial use needs prior written permission from Hao Feng. Optional DocRes is downloaded only by selecting **Download and install DocRes** in the desktop phone-import dialog, then enabled separately. Its weights are about 855 MiB, plus several GB of isolated Python/PyTorch CUDA dependencies under `%LOCALAPPDATA%/OhMyStudy/DocRes`. A single discrete NVIDIA GPU must have strictly more than 12 GiB dedicated VRAM and a working installed CUDA runtime. Exactly 12 GiB does not qualify. The app does not install a global CUDA toolkit. See [local document restoration](wiki/Features-Import.md#local-document-restoration) for tasks, cancellation, and fallback.

For source builds, install Git LFS and run `git lfs pull`, then `./packaging/prepare-scanner.ps1`. The three bundled ONNX models are versioned with Git LFS in `packaging/models/scanner/`; preparation verifies their pinned sizes and SHA-256 hashes and copies them into the build toolchain without downloading upstream weights or installing Python. CI checks out the LFS objects before preparing dependencies. Models, licenses, and modification notices ship together. Maintainers can explicitly regenerate the tracked DocScanner ONNX with `./packaging/prepare-docscanner.ps1 -CheckpointPath <DocScanner-L.pth>`; this separate exporter downloads pinned upstream sources and uses an isolated CPU Python/PyTorch toolchain when needed. `-PythonExecutable <python.exe>` optionally supplies an exporter environment with torch 2.9.1 CPU, torchvision 0.24.1 CPU, onnx 1.19.1, onnxruntime 1.23.2, numpy 2.2.6 and pillow 11.3.0. The export verifies PyTorch/ONNX numerical parity and the pinned output SHA-256.

## Installing with Setup

1. Close Oh My Study if it is running, then double-click the downloaded `Setup.exe`.
2. When the one-click installation finishes, open Oh My Study from the Start menu.

Setup installs the app for the current user under `%LOCALAPPDATA%/TaiRuiQu.OhMyStudy`. The app library and settings remain separate under `%LOCALAPPDATA%/OhMyStudy`; installing or updating the app does not move them.

Release files are not code-signed yet. If the system warns that the publisher cannot be verified, first confirm the download source and compare it against the SHA-256 checksum provided on the release page. The checksum confirms file integrity; it does not replace a publisher signature.

## Using the portable version

Extract the ZIP completely into an accessible folder, then run `OhMyStudy.App.exe` from it. Keep all extracted files; you cannot copy only the EXE.

By default, the portable version still stores study materials in your user profile directory. Do not extract it into the installation directory managed by Setup.

## Updating an installed app

1. Open **Settings → About → Check for updates**. Update status appears after you start the check.
2. If an update is available, confirm the download. A progress bar and percentage show the download progress.
3. After the download finishes, confirm the restart. The app closes gracefully and prompts you to save or resolve unfinished work before restarting into the new version.

Each variant has full and delta `.nupkg` update packages for every subsequent release. Its first Velopack bootstrap release has a full package only. If a delta update cannot be used, the app automatically falls back to the full package. Updates preserve the separate app library and settings.

If restarting after an update still opens the old version, check `%LOCALAPPDATA%/Velopack/velopack_TaiRuiQu.OhMyStudy.log`. The v0.3.3 delta package can fail with `Missing required manifest property: mainExe` because bundled dependency metadata is mistaken for the application manifest. Close the app and run the complete `Setup.exe` for the same variant to recover.

If Setup reports that it cannot remove the existing application directory, close editors and terminals using files under `%LOCALAPPDATA%/TaiRuiQu.OhMyStudy`, then retry. For example, opening `current/package-manifest.json` in Sublime Text can leave its crash-reporting helper holding the directory open; fully exit Sublime Text to release it. The installer log is `%LOCALAPPDATA%/Velopack/velopack.log`. If the directory remains locked, restart Windows before retrying. Keep the separate library directory intact.

The portable version has no in-app updater. To update it, close the app and manually replace its program files with a complete extraction of the new portable ZIP. Do not copy only the EXE or selected changed files.

Version v0.1.9 upgrades writable libraries to SQLite schema v6 to support supplementary student answer pages. Before that first launch, save, close the app, and back up the entire library folder. The automatic `study.sqlite3.pre-v6-…` snapshot contains the database only. Older apps cannot open an upgraded library; use a complete earlier backup if returning to an older app. See the [release notes](releases/v0.1.9.md) and [library guide](wiki/Features-Library.md).

## Migrating an existing MSI installation

Back up the entire library first. Uninstall the old MSI-based app through Windows **Installed apps**, then run the new `Setup.exe`; do not install Setup over the old MSI installation. Uninstalling preserves study materials and settings, but a complete backup is still recommended before migration.

## Uninstalling and keeping your materials

Find Oh My Study in Windows **Installed apps** and uninstall it. After closing the portable version, you can delete its program folder.

Updating or uninstalling preserves the separate app library, settings, and backups under `%LOCALAPPDATA%/OhMyStudy`, as well as credentials in Windows Credential Manager. The default library is at `%LOCALAPPDATA%/OhMyStudy/Library`; if you changed the location, use **Save location (保存位置)** under **Settings → Library (设置 → 资料库)** in the app.

To delete study materials, confirm your backups and close the app first, then delete the actual library folder separately. This deletes the exam originals and saved questions in it and cannot be undone by reinstalling.

## First launch

Add a provider, key, and model in **Settings (设置)** and save, then import an exam. See the [Usage Guide](usage.md) for details.

CI runs only when a new tag is pushed. Each publish job builds, packages, and releases its distribution variant directly. It runs no automated tests, regressions, or lint. Local whitespace lint is limited to 30 seconds; review UI and PDF output visually before release. Each variant is released immediately after its own build completes, without waiting for the other variants.

[Wiki contents](wiki/Home.md)
