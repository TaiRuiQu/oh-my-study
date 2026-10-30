# 技术内幕

[首页](Home-zh-CN.md) · [快速开始](Quick-Start-zh-CN.md) · [功能详解](Feature-Guide-zh-CN.md) · [English](Tech-for-nerds.md)

## 沿实现阅读

- [架构、构建与验证](Tech-Architecture-zh-CN.md)
- [文档准备、页面身份与坐标](Tech-Document-Pipeline-zh-CN.md)
- [OCR、切题、并行解题与评分](Tech-Model-Loop-zh-CN.md)
- [评分项、评分约束与编辑保护](Tech-Validation-Grading-zh-CN.md)
- [任务生命周期、重试恢复与代码隔离](Tech-Jobs-Execution-zh-CN.md)
- [SQLite v7、资源、搜索索引与恢复](Tech-Storage-Recovery-zh-CN.md)
- [原生阅读、公式资源与 PDF 编译](Tech-Rendering-Export-zh-CN.md)

## 项目边界

| 项目 | 职责 |
| --- | --- |
| Domain | 类型化题目/评分项、评分、供应商契约、校验、源生成 JSON。 |
| Application | 任务队列、授权、编排、搜索契约与服务。 |
| Infrastructure | SQLite、凭据、供应商 SDK、文件上传、评分工具、进程、搜索索引。 |
| Documents | PDFium 栅格化、坐标、VelociTex 数学、Typst 导出。 |
| App | WinUI 工作区、结构化编辑、列表/详情导航、本地化、Velopack 更新客户端与视觉冒烟。 |
| 打包与 CI | `packaging/` 和 `.github/` 中的 Velopack 打包及发布脚本，生成 Setup、便携 ZIP 和更新包。 |

## 处理与持久化

导入校验限制并保留原件，再排入固定工作流，而不是一个模型的自由会话。形式判断模型先区分页面，MinerU 抽取 Markdown、图片和表格，切题模型组合题目并绑定材料，解题模型仅用受控 `python` 工具独立作答，最后由独立评分调用比对原始作答截图。仅参考任务跳过解题和评分。每次保存均经过类型校验、活动任务和取消边界。

题目结构为公共题干与有序扁平 `QuestionItem`。参考引用评分项键，覆盖引用题目键，抽取插图保留原件证据。数据库当前 v7，新增存放 OCR 输出与各阶段 JSON 的 `workflow_artifacts` 表。显式重试仅在恢复检查允许时回滚失败轮次；重新识别保留现有结果及受保护手动/教师评分。

识别、文件上传、可选后台向量化与重排具有不同数据路径。[语义搜索](Features-Search-zh-CN.md) 使用缓存向量、本地余弦扫描与可选远端重排。供应商/搜索密钥在库外 Windows 凭据管理器中。

## 构建与视觉检查

使用 `global.json` 固定的 SDK。CI 仅在推送新标签时触发，并在 publish job 中直接构建、打包和发布三种版本，不运行 Lint、测试或回归套件。

```powershell
dotnet restore OhMyStudy.slnx -p:Configuration=Release --locked-mode
./packaging/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
dotnet build OhMyStudy.slnx -c Release --no-restore
./packaging/Invoke-Lint.ps1
```

Lint 仅在本地检查改动中的空白错误，并在 30 秒内停止。界面与 PDF 使用真实内容进行视觉检查。本地打包可运行 `./packaging/Invoke-CI.ps1 -Phase Build`。

使用共享的 Windows API 截图脚本进行一次界面检查。脚本启动指定程序、保存 PNG，并关闭自己启动的进程；截图失败时也会关闭：

```powershell
./scripts/Capture-AppWindow.ps1 -ExecutablePath ./src/OhMyStudy.App/bin/Release/net10.0-windows10.0.26100.0/win-x64/OhMyStudy.App.exe -Destination ./output/ui-review/main-window.png
```

`-DelaySeconds` 控制窗口打开后的渲染等待时间。`-ArgumentList` 传递程序参数；Debug 构建请传入 `--ui-process`，直接截取界面进程。可选的 `-PrepareWindow` 脚本块在截图前接收已启动的进程，用于打开需要检查的页面。`-ProcessId` 改为截取已在运行的实例；`-Width`/`-Height` 会先调整窗口尺寸，`-Cursor` 包含鼠标指针。生成的截图放在 `output`；清理输出目录后，`scripts` 中的脚本仍会保留。

脚本调用 `tools/ScreenCapture`，通过 Direct3D 11 使用 Windows Graphics Capture 读取帧。它不要求窗口在前台，也不会置顶或激活窗口，因此被遮挡或处于后台的窗口同样能正确截取，检查过程中桌面焦点不会改变。临时截图可直接运行：

```powershell
dotnet run --project tools/ScreenCapture -c Release -- --process 1234 --destination output/ui-review/tasks.png
```
