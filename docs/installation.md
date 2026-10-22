# Installation, Upgrade, and Uninstallation

[Home](../README.md) · [Usage Guide](usage.md) · [中文](installation.zh-CN.md)

> The application interface is currently in Chinese. UI labels in this guide show the English name with the original Chinese label in parentheses, for example "Settings (设置)".

## Choosing a download

The app targets Windows 10/11 x64. Each variant offers two download formats:

| File suffix | How to use |
| --- | --- |
| `-installer.msi` | Uses the installation wizard, adds a Start menu entry, and optionally creates a desktop shortcut |
| `-portable.zip` | Extract and run directly; adds no system installation entries |

Choose a variant based on the tag in the filename:

| Tag | When to use it |
| --- | --- |
| `aot` | No separate .NET installation is required; choose this if unsure |
| `aot-avx512` | Requires a processor and operating system that support x86-64-v4 (AVX-512 F/BW/CD/DQ/VL); choose `aot` if unsure |
| `self-contained` | Bundles the .NET runtime; no separate .NET installation is required |
| `framework-dependent` | Requires Microsoft .NET 10 x64 Runtime to be installed first; the installer does not download it automatically |

The package includes the components needed for local typesetting, image processing, and computation, so you do not need to install Python, Node.js, or TeX yourself. AI features still require a network connection, a provider key, and an available model.

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

Save your changes and close the app first, then run the new MSI. Upgrading preserves study materials and keeps the original installation location and desktop shortcut choices by default. All four variants install as the same application, so you can switch between them during an upgrade; side-by-side installation is not supported, and the installer refuses downgrades.

To repair an installation, run the original MSI again. To update the portable version, extract the new version completely into a separate folder and run the new app.

If you previously installed an older EXE version, exit the app and uninstall the old version through Windows Settings or the old uninstall entry before installing the MSI. Uninstalling the old version preserves study materials; installing the new version does not mean the old materials have been migrated, so keep a complete backup first.

## Uninstalling and keeping your materials

Find Oh My Study in Windows "Installed apps" and uninstall it, or uninstall through the original MSI. After closing the portable version, you can delete its program folder.

Uninstalling preserves study materials, credentials, and backups. The default library is at `%LOCALAPPDATA%/OhMyStudy/Library`; if you changed the location, use "Save location (保存位置)" under "Settings → Library (设置 → 资料库)" in the app.

To delete study materials, confirm your backups and close the app first, then delete the actual library folder separately. This deletes the exam originals and saved questions in it and cannot be undone by reinstalling.

## First launch

Add a provider, key, and model in "Settings (设置)" and save, then import an exam. See the [Usage Guide](usage.md) for details.
