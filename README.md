# Oh My Study

**English** · [中文](README.zh-CN.md)

A Windows study app for students. It imports exams, recognizes and grades questions, organizes a question bank and wrong-question collection, and exports review PDFs.

## Key features

- Import PDF, PNG, JPEG, and WebP files; work with blank exams, exams awaiting grading, or exams already graded by a teacher.
- View questions alongside the original and edit stems, answers, explanations, scores, and notes.
- Organize saved questions in the question bank and wrong-question collection, and view recognition task progress and estimated costs.
- Export questions, or answers and explanations, separately as A4 or A3 landscape PDFs.
- Store exam originals and study materials locally, and change the library location when needed.
- Choose a Simplified Chinese, English, Japanese, or Korean interface; English and Chinese remain the focus for document typesetting.

## Getting started

1. Download the Windows 10/11 x64 build that suits you from the project's releases page. If unsure, choose a build whose filename contains `aot` and does not contain `avx512`.
2. Double-click the `-installer.msi` to install, or extract the `-portable.zip` and run `OhMyStudy.App.exe`.
3. Open "Settings", add a model provider, key, and model, and save.
4. Click "Import exam", select a PDF or image, the exam type, and a model, then click "Import and recognize".

AI recognition and grading require an internet connection, send materials to the selected provider, and may incur costs. The installer bundles the components needed for local typesetting and computation; installing the app and viewing saved materials do not call a model. AI results may be wrong, so verify them against the originals and reference answers.

## Documentation

- [Wiki](https://github.com/TaiRuiQu/oh-my-study/wiki): [Quick Start](https://github.com/TaiRuiQu/oh-my-study/wiki/Quick-Start), [Feature Guide](https://github.com/TaiRuiQu/oh-my-study/wiki/Feature-Guide), and [Tech for nerds](https://github.com/TaiRuiQu/oh-my-study/wiki/Tech-for-nerds).
- [Installation, upgrade, and uninstallation](docs/installation.md): choosing a download, installation steps, and keeping your materials.
- [Usage guide](docs/usage.md): model setup, exam import, question organization, PDF export, and FAQ.

## License

This project is licensed under [GPL-3.0-or-later](LICENSE). Bundled components retain their own licenses and notices.
