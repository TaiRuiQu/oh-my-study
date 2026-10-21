# 启动不再创建 cmd 窗口

日期：2026-10-21。

## 本轮范围

- Debug 与 Release 统一使用 WinExe，双击或从资源管理器启动不再创建 cmd/控制台窗口。
- Debug 的控制台宿主只在应用由终端启动时附加父控制台，用于保留 UTF-8 日志和退出码；不调用 `AllocConsole`，不从资源管理器启动时创建窗口。
- UI 子进程（`--ui-process`）跳过父控制台附加，继续通过重定向管道把日志交给宿主。
- Release 不启用 Debug 宿主，保持 WinExe。

## 验证

构建：

```powershell
dotnet build src/OhMyStudy.App/OhMyStudy.App.csproj -c Debug -p:Platform=x64 -o output/capture-bg-app
```

- 构建 0 警告、0 错误。
- 生成的 `output/capture-bg-app/OhMyStudy.App.exe` PE Subsystem 为 `2`（Windows GUI），不是控制台子系统 `3`。
- 通过资源管理器快捷方式启动 `--papers-smoke`，运行期间按应用进程 ID 枚举可见的 `ConsoleWindowClass` 窗口：检测到 0 个；报告仍为 `PASS`。
- 证据：`output/explorer-launch/launch.log`、`output/explorer-launch/desktop.png`、`output/explorer-launch/report.txt`。
- 前台 `--preview-smoke` 通过，`native-paragraph-actual-control-c=PASS`（`output/capture-bg-preview3/report.txt`）。
- 后台 `--preview-smoke` 通过，`native-paragraph-actual-control-c=SKIPPED-BACKGROUND`（`output/capture-bg-preview/report.txt`）。
- 后台 `--papers-smoke` 通过，应用保持非前台、非置顶窗口（`output/capture-bg-evidence/status.txt`）。

## 未验证项

- 在独立 cmd/PowerShell 窗口中直接观察 Debug 日志显示；本轮验证的是不创建新窗口和原有报告链路，未把终端日志显示作为独立验收项。
- F5 调试、完整 MSI/快捷方式安装、Windows 10、无桌面会话和远程桌面场景。
