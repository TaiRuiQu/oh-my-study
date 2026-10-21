# 后台窗口截图与不置顶验收

日期：2026-10-21。

## 本轮范围

- 整窗截图统一使用 Windows `PrintWindow`（`PW_RENDERFULLCONTENT`）读取窗口自身的合成表面，再按客户区裁剪；不再只对公式/试卷诊断模式启用原生截图。
- 截图路径不显示、不激活、不置顶、不移动窗口，也不改变窗口的扩展样式；应用可以停留在其他窗口后面完成截图。
- WinUI 菜单、下拉框等独立顶层弹层按 HWND 捕获后，按客户区坐标合成回整窗截图；对话框内容属于主窗口表面，随主窗口一起捕获。
- 公式键盘复制验收不再为测试 Ctrl+C 强制把应用切到前台。应用本来已在前台时保留真实键盘检查；在后台时明确记录 `SKIPPED-BACKGROUND`，其余检查继续执行。
- 更新 `src/OhMyStudy.App/README.md` 中已过时的“截图时临时置顶”说明。

## 自动截图证据

构建：

```powershell
dotnet build src/OhMyStudy.App/OhMyStudy.App.csproj -c Debug -p:Platform=x64 -o output/capture-bg-app
```

背景截图使用独立 Notepad 窗口覆盖应用，并用 `SetWindowPos` 把应用放到非置顶 z 序底部（这是验收脚本，不是应用代码）；验收脚本只轮询前台窗口和截图，不激活应用。

### 试卷页（含菜单）

```powershell
./output/capture-bg-app/OhMyStudy.App.exe --papers-smoke "$PWD/output/capture-bg-evidence/report.txt"
```

- 报告首行 `PASS`，35 项检查通过；42 张截图。
- `output/capture-bg-evidence/status.txt` 记录每次采样：`notepad foreground=True`、`appForeground=False`、`app topmost=False`。
- `output/capture-bg-evidence/desktop-1.png` 至 `desktop-4.png` 显示 Notepad 和终端覆盖应用；对应 `report.more-menu.png` 仍完整包含应用自身界面和“更多”菜单，没有复制覆盖窗口的内容。
- 已查看 `report.more-menu.png`、`report.detail.png` 等实际截图，菜单、标题、按钮与遮罩层级正常。

### 公式阅读（后台执行）

```powershell
./output/capture-bg-app/OhMyStudy.App.exe --preview-smoke "$PWD/output/capture-bg-preview/report.txt"
```

- 报告首行 `PASS`，21 张截图；`native-paragraph-actual-control-c=SKIPPED-BACKGROUND`。
- `output/capture-bg-preview/status.txt` 记录应用不是前台且不是置顶窗口；桌面截图显示其他窗口覆盖应用。
- 已查看 `report.list.png`、`report.bottom-dark.png`：原生公式、中文正文、深色主题和列表内容均被正确截取。
- 前台分支另见 `output/capture-bg-preview3/report.txt`：报告 `PASS`，`native-paragraph-actual-control-c=PASS`。

### 工作流

```powershell
./output/capture-bg-app/OhMyStudy.App.exe --workflow-smoke "$PWD/output/capture-bg-workflow/report.json"
```

- 报告 `Passed=true`，52 项检查中除 Debug 构建预期的 `native-aot=false` 外全部通过，生成 16 张截图。
- 已查看 `report.settings.png`、`report.dark.png`：整窗截图、深色主题、公式与设置页布局与前台运行时一致。

### 导入与任务

```powershell
./output/capture-bg-app/OhMyStudy.App.exe --import-smoke "$PWD/output/capture-bg-import/report.txt"
./output/capture-bg-app/OhMyStudy.App.exe --task-smoke "$PWD/output/capture-bg-task/report.txt"
```

- 两项均 `PASS` 并各生成 20 张截图；已查看 `output/capture-bg-import/report.paper.png` 与 `output/capture-bg-task/report.permission.png`，整窗布局和状态正常。
- 对话框的局部截图仍走 `RenderTargetBitmap`，不参与整窗置顶；同一运行中的整窗截图走 `PrintWindow`。

## 代码检查

- 截图相关路径没有 `SetWindowPos`、`SetForegroundWindow`、`HWND_TOPMOST` 或窗口显示/激活调用。
- 应用仍有的 `Activate()` 只用于正常启动时的窗口显示；`--inspect-ui` 的人工查看分支仍会显示并激活窗口，该分支不参与自动截图。
- `--smoke-test` 的截图生成正常（`ScreenshotCaptured=true`），但报告 `NativeNavigation=false`。同一工作区在本次改动前的构建 `output/capture-api-app-release` 也同样失败，且该检查发生在截图之前，属于现有试卷导航改动的问题，不是本轮截图改动引入；本轮未修改该导航。

## 未验证项

- 窗口最小化、锁屏、受保护内容、显示器休眠/断开时的截图。
- 高对比度、非 100% DPI、多显示器跨屏、Windows 10 的截图矩阵。
- 真实用户交互与物理显示器验收；本轮只验证应用自动截图和覆盖窗口场景。
- 元素局部截图仍使用 `RenderTargetBitmap`，不在本轮“整窗后台截图”范围内；它们同样不要求前台，但不包含独立顶层弹层。

启动无 cmd 窗口的实现与证据另见 [启动不再创建 cmd 窗口](startup-no-console.2026-10-21.md)。
