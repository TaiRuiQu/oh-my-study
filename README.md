# Oh My Study

面向学生的本地桌面学习程序，围绕试卷识别批改、题库与错题、复习卷生成三个功能设计。

需求与技术架构已定稿，当前正在实现 **M0 风险验证**，尚未交付桌面应用。阶段状态、验证证据和接手步骤见 [progress.md](progress.md)。

- [需求与决策记录](docs/requirements.zh-CN.md)
- [技术架构选型](docs/architecture.zh-CN.md)
- [实现计划](docs/implementation-plan.zh-CN.md)
- [执行环境与三种权限模式](docs/execution-permissions.zh-CN.md)
- [用户样卷分析](docs/sample-analysis.zh-CN.md)
- [M0 资源测量](docs/m0-resources.zh-CN.md)
- [M0 验收与开发验证命令](docs/m0-acceptance.zh-CN.md)

M0 工程使用 Rust 工作区验证执行权限与三种原生协议，`probes/typesetting` 验证公式与 PDF 排版，`runtime` 保存私有计算运行时的构建来源。真实供应商调用、Windows Sandbox 兼容性与纸张打印需分别取得实测证据，不能以合成样本测试替代。

主体技术：Tauri 2、React、TypeScript、Vite、Rust、SQLite；原生支持 Responses / Messages / Gemini，使用 Python 与 Matplotlib 计算绘图，Typst 排版 PDF。支持请求权限、Windows Sandbox、绕过权限三种执行模式，仅提供整卷入口。

目标平台为 Windows 10 / 11 x64，未来可扩展 macOS / Linux。项目采用 [GNU GPL v3.0 或更新版本](LICENSE)（SPDX: GPL-3.0-or-later），具体分发组件保留各自许可证。
