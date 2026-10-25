# 安装、升级与卸载

[返回首页](../README.zh-CN.md) · [使用指南](usage.zh-CN.md) · [English](installation.md)

界面支持简体中文、英语、日语和韩语，切换方法见[语言与排版](usage.zh-CN.md#语言与排版)。

## 选择下载文件

应用面向 Windows 10/11 x64。每个源码标签 `vX` 都有三个独立发布：`vX-aot`、`vX-self-contained` 和 `vX-framework-dependent`。每个发布提供两种下载格式：

| 格式 | 使用方式 |
| --- | --- |
| `Setup.exe` | Velopack 一键安装到当前用户，并添加开始菜单入口 |
| 便携 ZIP | 解压后直接运行，不添加系统安装项 |

根据发布标签选择版本：

| 标签 | 适用情况 |
| --- | --- |
| `aot` | 无需另装 .NET；不确定时选择此项 |
| `self-contained` | 自带 .NET 运行时，无需另装 .NET |
| `framework-dependent` | 需要先安装 Microsoft ASP.NET Core 10 x64 Runtime（包含 .NET Runtime），Setup 不会自动下载 |

软件包包含本地排版、图片处理和计算所需组件，无需自行安装 Python、Node.js 或 TeX。AI 功能仍需要网络、供应商密钥和可用模型。

正式软件包不包含调试符号、Python 字节码缓存和依赖库测试套件，但保留计算所需的运行辅助模块。内置的 Windows 界面组件、PDF 工具和数值计算库仍会占用一定下载体积。发布使用 Velopack，不再使用 MSI、MSP 或 WiX。

## 文档修复组件

标准手机照片修复内置 CPU DocScanner-L ONNX 推理与 OpenCV，无需自行安装 Python 或 CUDA。模型附带 DocScanner 独立的非商业许可证，要求署名及相同方式共享；商业用途需提前取得 Hao Feng 的书面许可。可选 DocRes 仅在电脑端手机导入窗口主动点击**下载并安装 DocRes**后下载，且需单独启用。模型权重约 855 MiB，另需数 GB 独立 Python/PyTorch CUDA 依赖，保存在 `%LOCALAPPDATA%/OhMyStudy/DocRes`。单张 NVIDIA 独立显卡必须具有严格大于 12 GiB 的专用显存，并有已安装且可用的 CUDA 运行时；恰好 12 GiB 不符合要求。应用不安装全局 CUDA 工具包。任务选择、取消与回退见[本地文档修复](wiki/Features-Import-zh-CN.md#本地文档修复)。

源码构建时先安装 Git LFS 并执行 `git lfs pull`，再执行 `./packaging/prepare-scanner.ps1`。三个随软件分发的 ONNX 模型通过 Git LFS 保存在 `packaging/models/scanner/`；准备脚本校验固定的文件大小和 SHA-256 后复制到构建工具目录，不再下载上游权重或安装 Python。CI 在准备依赖前检出 LFS 对象。模型、许可证与修改说明一起分发。维护者可主动执行 `./packaging/prepare-docscanner.ps1 -CheckpointPath <DocScanner-L.pth>`，重新生成仓库中的 DocScanner ONNX；这个独立导出器在需要时下载固定版本的上游源码，并使用独立的 CPU Python/PyTorch 工具链。也可用 `-PythonExecutable <python.exe>` 指定含 torch 2.9.1 CPU、torchvision 0.24.1 CPU、onnx 1.19.1、onnxruntime 1.23.2、numpy 2.2.6 与 pillow 11.3.0 的导出环境。导出器验证 PyTorch/ONNX 数值一致性，并核对固定的输出 SHA-256。

## 使用 Setup 安装

1. 如果 Oh My Study 正在运行，请先关闭，再双击下载的 `Setup.exe`。
2. 一键安装完成后，从开始菜单打开 Oh My Study。

Setup 将应用安装到当前用户的 `%LOCALAPPDATA%/TaiRuiQu.OhMyStudy`。应用资料库和设置单独保存在 `%LOCALAPPDATA%/OhMyStudy`，安装或更新应用不会移动它们。

发行文件尚未代码签名。若系统提示无法验证发布者，请先确认下载来源，并与发布页面提供的 SHA-256 校验值核对。校验值用于确认文件完整性，不能代替发布者签名。

## 使用便携版

将 ZIP 完整解压到可访问的文件夹，再运行其中的 `OhMyStudy.App.exe`。请保留解压后的全部文件，不能只复制 EXE。

便携版的学习资料默认仍保存在用户资料目录。不要将便携版解压到 Setup 管理的安装目录。

## 更新已安装的应用

1. 打开 **设置 → 关于 → 检查更新**。开始检查后才显示更新状态。
2. 如有更新，确认下载。下载时显示进度条和百分比。
3. 下载完成后确认重启。应用会正常关闭，提示保存或处理未完成的工作，然后重启进入新版本。

后续每次发布时，每种版本均提供全量和增量 `.nupkg` 更新包；首个 Velopack 引导发布只提供全量包。增量更新不可用时，应用会自动回退到全量包。更新会保留单独存放的应用资料库和设置。

如果更新重启后仍是旧版，请查看 `%LOCALAPPDATA%/Velopack/velopack_TaiRuiQu.OhMyStudy.log`。v0.3.3 的增量包可能将附带的依赖元数据误认作应用清单，报 `Missing required manifest property: mainExe`。可关闭应用，运行同一版本类型的完整 `Setup.exe` 恢复更新。

如果 Setup 提示无法删除已有应用目录，请关闭正在使用 `%LOCALAPPDATA%/TaiRuiQu.OhMyStudy` 下文件的编辑器和终端，再重试。例如，用 Sublime Text 打开 `current/package-manifest.json` 后，其崩溃报告辅助进程可能持续占用目录；完全退出 Sublime Text 可释放占用。安装日志位于 `%LOCALAPPDATA%/Velopack/velopack.log`。若仍被占用，请重启 Windows 后重试。请保留单独存放的资料库目录。

便携版没有应用内更新功能。更新时请关闭应用，完整解压新版便携 ZIP，并手动替换程序文件。不要只复制 EXE 或部分变更文件。

v0.1.9 会将可写资料库升级到 SQLite v6，以支持补充学生作答页面。首次启动该版本前请保存、退出，并备份整个资料库目录。自动生成的 `study.sqlite3.pre-v6-…` 快照只包含数据库；旧版应用无法打开升级后的资料库，回退时须使用完整的旧版备份。详见[发行说明](releases/v0.1.9.zh-CN.md)和[资料库指南](wiki/Features-Library-zh-CN.md)。

## 迁移现有 MSI 安装

先完整备份资料库。通过 Windows **已安装的应用** 卸载旧的 MSI 版应用，再运行新的 `Setup.exe`；不要直接在旧 MSI 安装上安装 Setup。卸载会保留学习资料和设置，但迁移前仍建议保留完整备份。

## 卸载与资料保留

在 Windows **已安装的应用** 中找到 Oh My Study 并卸载。便携版关闭后可删除程序文件夹。

更新或卸载会保留单独存放在 `%LOCALAPPDATA%/OhMyStudy` 的应用资料库、设置和备份，以及 Windows 凭据管理器中的凭据。默认资料库位于 `%LOCALAPPDATA%/OhMyStudy/Library`；如果改过位置，以应用 **设置 → 资料库** 中的 **保存位置** 为准。

如需删除学习资料，请先确认备份并关闭应用，再单独删除实际资料库文件夹。此操作会删除其中的试卷原件和已保存题目，无法通过重新安装找回。

## 首次打开

在 **设置** 中添加供应商、密钥和模型并保存，然后导入试卷。详细操作见[使用指南](usage.zh-CN.md)。

CI 仅在推送新标签时触发，由各 publish job 直接完成构建、打包和发布，不运行自动测试、回归或 Lint。本地空白检查不超过 30 秒；发布前通过实际界面和 PDF 输出进行视觉检查。 每个版本构建完成后立即发布，不等待其他版本。

[Wiki 目录](wiki/Home-zh-CN.md)
