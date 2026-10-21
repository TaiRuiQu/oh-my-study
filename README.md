# Oh My Study

面向学生的原生 Windows 学习应用，支持试卷导入与批改、原件对照、题库和错题整理。当前工程采用 C# / .NET 10 / WinUI 3，提供 Native AOT（普通 / AVX-512）、非 AOT 自带运行时和依赖 .NET 的版本，均面向 Windows 10/11 x64。每种版本同时提供 portable ZIP 与MSI 安装包；下载选择见 [安装说明](docs/installation.zh-CN.md)。

已实现图片与 PDF 导入、三种导入模式、Responses 与 Messages 整卷任务、题目编辑、来源定位、备份恢复、A3/A4 题目与答案 PDF 导出，以及离线安装、升级和卸载。真实模型与样卷、干净系统、Sandbox、物理打印和辅助功能仍待用户验收；当前验证状态与待办见 [维护进度](migration-progress.md)。

## 安装与使用

双击 MSI，选择安装位置和是否创建桌面快捷方式，安装后从开始菜单打开；portable ZIP 解压后直接运行 `OhMyStudy.App.exe`。旧 EXE 安装需先卸载旧版，再安装 MSI，资料会保留。升级保留资料，卸载保留资料和备份。首次导入旧资料前退出旧版并保留完整备份。

默认资料目录为 `%LOCALAPPDATA%/OhMyStudy/Library`，可在设置中更改。导入会保存原件副本，删除原文件后仍可查看；更改位置会复制现有资料并保留原库。AI 功能需要自行配置供应商、密钥和模型；离线包包含本地排版和计算依赖，无需开发 SDK 或系统 Python/Node/TeX。具体步骤和恢复方法见 [安装与卸载](docs/installation.zh-CN.md)。

## 开发与验证

环境：Windows x64、PowerShell 7、.NET SDK 10.0.402、MSVC x64 Build Tools 和 Windows SDK。版本以仓库锁文件为准。

Responses 与 Messages 分别由官方 `OpenAI` 和启用 Native AOT 的 `TairuiQu.Anthropic.Aot` SDK 承担请求与流式解析，Messages 默认启用自动前缀缓存。全部发布变体恢复 Native AOT 与裁剪分析，不再需要非 AOT 构建参数。切换与验收证据见 [Anthropic AOT 切换记录](docs/verification/anthropic-aot-switch.2026-10-20.md)。

```powershell
./packaging/Test-RepositoryLayout.ps1
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke
./packaging/Publish-OfflinePackage.ps1 -DesktopSmoke -WorkflowSmoke
```

首次工具准备及 NuGet 还原需要网络。缓存和报告保存在忽略的 `output/`。还原须指定 `-p:Configuration=Release`，同一工程的还原和发布串行执行。旧实现只保留在忽略的 `.reference/`，正式构建不依赖它。

本地调试可运行：

```powershell
dotnet run --project src/OhMyStudy.App -c Debug -p:Platform=x64
```

Debug 和 Release 都是 WinExe，双击或从资源管理器启动不会创建 cmd/控制台窗口。Debug 从终端启动时会附加父终端显示启动、导入、识别任务和完整异常日志，同时输出到调试器，并保存到实际运行目录下的 `logs/debug-日期-进程号.log`（启动日志会打印完整路径）；从资源管理器启动时只写日志文件。宿主与界面进程分开，界面发生原生异常退出时会记录退出码；F5 已附加调试器时直接运行界面，保留断点调试。构建会自动复制已准备的 PDF 与图片处理依赖；缺少 PDF 依赖时会提示先运行 `./probes/prepare-documents.ps1`。Release 保持普通桌面应用的启动方式。

`OhMyStudy.slnx` 包含 `src/` 下的 App、Domain、Application、Infrastructure、Documents、Setup 工程；`tests/` 保存回归与夹具，`runtime/` 保存计算入口，`probes/` 和 `packaging/` 保存验证及发布工具。

## 文档

- [需求与产品待办](docs/requirements.zh-CN.md)、[设计原则](docs/design-principles.zh-CN.md)
- [架构](docs/architecture.zh-CN.md)、[执行与权限](docs/execution-permissions.zh-CN.md)
- [应用流程与诊断](src/OhMyStudy.App/README.md)、[兼容夹具](tests/Fixtures/Compatibility/README.md)
- [安装与卸载](docs/installation.zh-CN.md)、[CI 与版本发布](docs/ci.zh-CN.md)
- [验收清单](docs/acceptance.zh-CN.md)、[维护进度与验证证据](migration-progress.md)

项目采用 [GPL-3.0-or-later](LICENSE)，分发组件保留各自许可证。
