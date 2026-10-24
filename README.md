# Oh My Study

**English** · [中文](README.zh-CN.md)

A Windows study app for students. It imports exams, recognizes and grades questions, organizes a question bank and wrong-question collection, and exports review PDFs.

## Key features

- Import PDF, PNG, JPEG, and WebP files; automatically detect the exam type or select blank, answered, or teacher-graded mode.
- Generate standard answers and solutions for blank exams; add student answer files or type responses later, then recognize again for grading and error analysis.
- View questions alongside the original, confirm reviewed transcription concerns, and edit stems, answers, explanations, scores, and notes.
- Organize saved questions in the question bank and wrong-question collection, and view recognition task progress and estimated costs.
- Export questions, or answers and explanations, separately as A4 or A3 landscape PDFs.
- Store exam originals and study materials locally, and change the library location when needed.
- Choose a Simplified Chinese, English, Japanese, or Korean interface; English and Chinese remain the focus for document typesetting.

## Getting started

1. Download a Windows 10/11 x64 release. Each source tag `vX` has independent `vX-aot`, `vX-self-contained`, and `vX-framework-dependent` releases; if unsure, choose `vX-aot`.
2. Run `Setup.exe` for a per-user installation, or fully extract the portable ZIP and run `OhMyStudy.App.exe`.
3. Open "Settings", add a model provider, key, and model, and save.
4. Click "Import exam", select a PDF or image, the exam type, and a model, then click "Import and recognize".

AI recognition and grading require an internet connection, send materials to the selected provider, and may incur costs. The packages bundle the components needed for local typesetting and computation; installing the app and viewing saved materials do not call a model. AI results may be wrong, so verify them against the originals and reference answers.

## Documentation

- [Wiki](docs/wiki/Home.md): [Quick Start](docs/wiki/Quick-Start.md), [Feature Guide](docs/wiki/Feature-Guide.md), and [Tech for nerds](docs/wiki/Tech-for-nerds.md).
- [Installation, upgrade, and uninstallation](docs/installation.md): choosing a download, installation steps, and keeping your materials.
- [Usage guide](docs/usage.md): model setup, exam import, question organization, PDF export, and FAQ.
- [v0.2.0 release notes](docs/releases/v0.2.0.md): exam renaming, Velopack installation, and incremental updates.

## License

This project is licensed under [GPL-3.0-or-later](LICENSE). Bundled components retain their own licenses and notices.
