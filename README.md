# Oh My Study

面向学生的本地桌面学习程序，围绕试卷识别批改、题库与错题、复习卷生成三个功能设计。

需求与技术架构已定稿，当前为可开始开发的方案，尚未实现应用。

- [需求与决策记录](docs/requirements.zh-CN.md)
- [技术架构选型](docs/architecture.zh-CN.md)
- [实现计划](docs/implementation-plan.zh-CN.md)
- [执行环境与三种权限模式](docs/execution-permissions.zh-CN.md)
- [用户样卷分析](docs/sample-analysis.zh-CN.md)

主体技术：Tauri 2、React、TypeScript、Vite、Rust、SQLite；原生支持 Responses / Messages / Gemini，使用 Python 与 Matplotlib 计算绘图，Typst 排版 PDF。支持请求权限、Windows Sandbox、绕过权限三种执行模式，仅提供整卷入口。

目标平台为 Windows 10 / 11 x64，未来可扩展 macOS / Linux。项目采用 [GNU GPL v3.0 或更新版本](LICENSE)（SPDX: GPL-3.0-or-later），具体分发组件保留各自许可证。
