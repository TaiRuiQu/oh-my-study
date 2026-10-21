# 试卷“更多”菜单精简与删除试卷

日期：2026-10-20。

## 本轮范围

- 试卷页右上角“更多”菜单改为默认向下展开（右缘对齐），移除打印原件、备份资料、恢复备份、导入旧版资料和关于入口。
- “导出题目”“添加参考答案”移入试卷标题下的工具栏；工具栏按钮补充 Fluent 图标，并用分组分隔、单一强调色和危险色区分操作层级。
- “管理页面”“整理参考答案”保留在“更多”；新增“删除试卷”，列表行右键也提供同一操作。
- 删除试卷同时移除该卷题目、参考答案、识别任务和资料库内托管原件，并删除对应列表行；确认后不可恢复。
- “查看示例”只在 Debug 构建中出现。Production/Release 构建中该菜单项不存在；列表页因此没有可用的“更多”项时隐藏“更多”按钮。
- 修正“更多”菜单精简后可能出现的多余分隔线：详情菜单仅在普通操作与删除之间保留一条分隔线；列表菜单不再出现悬空分隔线。

## 自动截图与视觉检查

使用修改后的真实 WinUI 窗口自动截图，未用 Computer Use 验收布局。截图来自隔离合成资料库，不请求识别供应商、不读取真实凭据。

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
dotnet build src/OhMyStudy.App/OhMyStudy.App.csproj -c Debug -p:Platform=x64 -o output/papers-app2
./output/papers-app2/OhMyStudy.App.exe --papers-smoke "$PWD/output/papers-visual2/report.txt"
```

Debug 截图（`output/papers-visual2/`）：

- `report.detail.png`、`report.detail-medium.png`、`report.detail-compact.png`：宽、中、窄窗口下的试卷工具栏和顶部操作。
- `report.detail-dark-compact.png`、`report.question-dark.png`：深色主题。
- `report.more-menu.png`、`report.more-menu-dark.png`、`report.more-menu-compact.png`：详情“更多”菜单；浅色、深色、窄窗口，向下展开。
- `report.more-menu-list.png`：列表“更多”菜单，只剩“查看示例”。
- `report.paper-context-menu.png`：列表行右键删除菜单。
- `report.delete-confirm.png`、`report.after-delete.png`：删除确认与删除后的空列表状态。
- `report.question-sections.png`、`report.question-compact.png`：题目区域的工具栏与内容布局。

Production 形态的 Release 构建（关闭 AOT 以缩短诊断构建，仍使用 Release 条件编译）：

```powershell
dotnet build src/OhMyStudy.App/OhMyStudy.App.csproj -c Release -p:Platform=x64 -p:OhMyStudyPublishVariant=self-contained -o output/papers-app-release
Start-Process -FilePath ./output/papers-app-release/OhMyStudy.App.exe -ArgumentList @('--papers-smoke', "$PWD/output/papers-visual-release/report.txt") -Wait
```

Release 证据（`output/papers-visual-release/`）：

- `report.single.png`：列表页只有“导入试卷”，没有“更多”按钮。
- `report.more-menu.png`：详情“更多”只有“管理页面”“整理参考答案”、一条分隔线和“删除试卷”，没有“查看示例”。
- `report.detail-compact.png`、`report.question-sections.png`：Release 下工具栏与题目布局一致。

逐张检查了按钮图标与文字对齐、按钮分组和间距、窄窗口两行工具栏、浅色/深色菜单对比度、删除项危险色、右键菜单定位、确认对话框文案与按钮、删除后的空状态和状态栏反馈，以及“更多”菜单是否向下展开。未发现遗留的多余分隔线。

## 验证结果

- Debug `--papers-smoke`：PASS，30 项检查通过，含 `more-menu-opens-down`、`more-menu-actions`、`more-menu-open`、`more-menu-dark-open`、`list-more-menu`、`paper-context-menu`、`paper-context-request-opens`、`delete-cancel-keeps-paper`、`delete-paper`。
- Release `--papers-smoke`：PASS，29 项检查通过，含 `list-more-hidden`（Production 列表页不显示空的“更多”入口）、`paper-context-request-opens`、`delete-paper`。
- 存储回归 119 项通过，覆盖删除运行中试卷被拒绝、删除后题目/参考答案/任务/页面/资产行与托管文件清理、重复删除被拒绝。识别集成 211 项、领域 105 项通过。
- `dotnet format whitespace OhMyStudy.slnx --verify-no-changes --no-restore` 通过。
- 真实鼠标右键手势未作为独立脚本执行；自动检查通过 `ContextRequested` 处理路径验证了菜单会打开，并用同一菜单构造路径截取了右键菜单。真实文件选择器和真实资料仍由用户验收。

## 未验证项

- 真实试卷、真实模型识别与真实资料库删除的最终用户验收。
- 删除时后台文件被系统占用的情况：数据库删除已提交，残留托管文件会作为孤儿由后续清理入口报告，不影响列表结果。
- 高对比度、DPI/多屏和真实触控/鼠标手势矩阵。
