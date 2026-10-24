# Installation, Upgrade, and Uninstallation

[Home](../README.md) · [Usage Guide](usage.md) · [中文](installation.zh-CN.md)

The application interface supports Simplified Chinese, English, Japanese, and Korean. UI labels in this guide include Chinese labels in parentheses. See [Language and typography](usage.md#language-and-typography) to change languages.

## Choosing a download

The app targets Windows 10/11 x64. Each variant offers two download formats:

| File suffix | How to use |
| --- | --- |
| `-installer.msi` | Uses the installation wizard, adds a Start menu entry, and optionally creates a desktop shortcut |
| `-portable.zip` | Extract and run directly; adds no system installation entries |
| `-from-VERSION-patch.msp` | Optional patch for the named baseline MSI version and the same build variant |

Choose a variant based on the tag in the filename:

| Tag | When to use it |
| --- | --- |
| `aot` | No separate .NET installation is required; choose this if unsure |
| `aot-avx512` | Requires a processor and operating system that support x86-64-v4 (AVX-512 F/BW/CD/DQ/VL); choose `aot` if unsure |
| `self-contained` | Bundles the .NET runtime; no separate .NET installation is required |
| `framework-dependent` | Requires Microsoft .NET 10 x64 Runtime to be installed first; the installer does not download it automatically |

The package includes the components needed for local typesetting, image processing, and computation, so you do not need to install Python, Node.js, or TeX yourself. AI features still require a network connection, a provider key, and an available model.

Release packages exclude debug symbols, Python bytecode caches, and dependency test suites, while retaining the runtime helpers needed for computation. Bundled Windows UI components, PDF tools, and numerical libraries still contribute to the download size.

## Using the installation wizard

1. Double-click the downloaded MSI.
2. Read the license, choose the installation location, and choose whether to create a desktop shortcut.
3. Continue after Windows asks for administrator confirmation.
4. When installation finishes, open Oh My Study from the Start menu or the desktop shortcut.

The default installation location is `%ProgramFiles%/OhMyStudy`. Changing the program installation location does not move your study materials; the library location is selected separately in the app settings.

Release files are not code-signed yet. If the system warns that the publisher cannot be verified, first confirm the download source and compare it against the SHA-256 checksum provided on the release page. The checksum confirms file integrity; it does not replace a publisher signature.

## Using the portable version

Extract the ZIP completely into an accessible folder, then run `OhMyStudy.App.exe` from it. Keep all extracted files; you cannot copy only the EXE.

By default, the portable version still stores study materials in your user profile directory. Do not extract the portable version into an installation directory managed by the MSI.

## Upgrading and repairing

Version v0.1.9 upgrades writable libraries to SQLite schema v6 to support supplementary student answer pages. Before the first launch, save, close the app, and back up the entire library folder. The automatic `study.sqlite3.pre-v6-…` snapshot contains the database only. Older apps cannot open an upgraded library; use a complete earlier backup if returning to an older app. See the [release notes](releases/v0.1.9.md) and [library guide](wiki/Features-Library.md).

Save your changes and close the app first, then run the new MSI. Upgrading preserves study materials and keeps the original installation location and desktop shortcut choices by default. All four variants install as the same application, so you can switch between them during an upgrade; side-by-side installation is not supported, and the installer refuses downgrades.

To repair an installation, run the original MSI again. To update the portable version, extract the new version completely into a separate folder and run the new app.

If an MSP is available, check that its `from-VERSION` and build variant match your originally installed MSI. Close the app, double-click the MSP, and approve the Windows administrator prompt. Keep the original MSI for repair or patch removal. Remove a patch through Control Panel → Programs and Features → View installed updates. MSPs do not support portable installations, first-time installations, or switching variants; use the full new MSI when they do not match. File-layout changes, including the initial removal of PDB files from older installations, also require a full MSI first.

For silent installation or patching, first open PowerShell as administrator. `/qn` does not show elevation prompts; running without elevation can cause 1925, 2502, or 2503. Do not change Windows Installer or temporary-folder permissions as a workaround. Keep a `/L*v` log and inspect the errors preceding the final failure:

```powershell
msiexec /i "C:\Downloads\OhMyStudy-installer.msi" /qn /norestart /L*v "$env:TEMP\OhMyStudy-install.log"
msiexec /p "C:\Downloads\OhMyStudy-patch.msp" REINSTALL=ALL REINSTALLMODE=omus /qn /norestart /L*v "$env:TEMP\OhMyStudy-patch.log"
```

Developers can run `./packaging/Build-MspPackage.ps1 -BaselineMsi <original.msi> -UpdateMsi <new.msi> -OutputFile <update.msp>`, or pass `-BaselineMsi` to `Publish-OfflinePackage.ps1` or `Invoke-CI.ps1 -Phase Build`. Versions must increase, variants must match, and component, file, and installation layouts must remain identical; incompatible changes are rejected with instructions to use the full MSI. Input MSIs are never modified. Start with a full MSI built by the current packaging scripts as the baseline. MSPs omit unchanged dependencies; their size depends on which files change and is not guaranteed to be small.

For a manual CI run, set `baseline_tag` to download each variant's baseline MSI and verify it against the release SHA-256 checksums before creating four patches. Release manifests and checksums include patch and baseline identities. Leaving it empty continues to build only full MSIs and ZIPs. Local installation regression tests request UAC; `./packaging/tests/Test-MsiInstaller.ps1 -BuildOnly` checks patch creation without installing anything.

For successive cumulative patches, keep using the same original MSI as `baseline_tag`, so installations with earlier patches can continue updating. A newer full MSI has a different product identity; its patch series applies only after users actually install that full MSI.

If you previously installed an older EXE version, exit the app and uninstall the old version through Windows Settings or the old uninstall entry before installing the MSI. Uninstalling the old version preserves study materials; installing the new version does not mean the old materials have been migrated, so keep a complete backup first.

## Uninstalling and keeping your materials

Find Oh My Study in Windows "Installed apps" and uninstall it, or uninstall through the original MSI. After closing the portable version, you can delete its program folder.

Uninstalling preserves study materials, credentials, and backups. The default library is at `%LOCALAPPDATA%/OhMyStudy/Library`; if you changed the location, use "Save location (保存位置)" under "Settings → Library (设置 → 资料库)" in the app.

To delete study materials, confirm your backups and close the app first, then delete the actual library folder separately. This deletes the exam originals and saved questions in it and cannot be undone by reinstalling.

## First launch

Add a provider, key, and model in "Settings (设置)" and save, then import an exam. See the [Usage Guide](usage.md) for details.

Normal branch and pull-request CI runs regression tests without producing all four download packages. Provider and grading suites run in managed mode to avoid repeating the expensive SDK Native AOT compilation; the other native suites remain enabled. Tag and manual runs perform full Native AOT validation and build all four packages. Obsolete runs for the same branch are canceled.

[Wiki contents](wiki/Home.md)
