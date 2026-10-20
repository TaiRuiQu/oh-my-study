# 移除 Google 模型支持

2026-10-20，按用户要求清理 Google GenAI / Gemini 支持。本轮在工作区已有 OpenAI、Anthropic 官方 SDK 接入改动之上完成，保留这些改动及临时非 AOT 构建边界。

## 实现范围

- 删除 Gemini 协议枚举、两种思考配置及源生成 JSON 注册；协议校验明确拒绝未定义枚举值。
- 删除自建 Generate Content 请求、认证、模型分页、图片、工具续接、用量归一化和 SSE 归约分支；`ProviderClient` 只分派给 Responses、Messages SDK 适配器。
- 设置页仅保留 OpenAI Responses、Anthropic Messages。删除 Gemini 思考选项，预算输入仅在 Messages 下显示，使用无符号整数并要求至少 1024；不会将无效选择默认为其他协议。
- 删除正向 Gemini 协议夹具及枚举约定；原先使用 Gemini 验证的覆盖校验恢复场景改由 Messages 运行，继续验证缺页、重复、未知题目、错误来源、缺少说明及跨页题目恢复。
- 旧库读取跳过不再支持的协议，保留其原始配置字节，不删除数据、不转为其他协议。新增回归验证旧 Gemini 思考配置不会阻塞支持的供应商、旧库可重开、记录原样保留且已移除协议不可新增保存。源码中的 Gemini 字符串仅保留在这些拒绝和兼容读取测试中。
- 当前包引用及依赖锁文件没有 Google.GenAI 或 Google.Apis.Auth；无需额外修改依赖。WebP 解码工具及其下载源属于独立媒体依赖，保留。
- 更新当前需求、架构、验收说明和 README；旧 SDK 验证记录保留历史结果并标注后续变更。

## 构建与回归

所有请求、密钥和资料均为合成数据或本机回环，没有调用真实模型服务。

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| 完整解决方案 Debug 构建 | 0 警告、0 错误 | `output/google-removal/build.log` |
| 最终 App 构建 | 0 警告、0 错误 | `output/google-removal/app-build.log` |
| Provider | 14 组通过，涵盖剩余两种协议的请求、图片、不透明上下文、思考参数、缓存、流、分页、错误、重试、上限、取消及真实回环 socket | `output/google-removal/provider.log` |
| Domain | 105 项通过，含旧协议及思考配置拒绝 | `output/google-removal/domain.json` |
| Grading | 189 项通过，两种协议、三种模式、真实队列及 SQLite | `output/google-removal/grading.log` |
| Storage | 108 项通过，含旧配置读取和保留 | `output/google-removal/storage.log` |
| 实际 App 工作流 | 48 项功能检查通过；另记录 `native-aot=false` | `output/google-removal/workflow.json` |
| 源码与依赖扫描 | 正式源码无 Google 模型协议实现；锁文件无 Google SDK 依赖 | 对 `src`、`tests`、包引用及锁文件执行 `rg` |
| 补丁检查 | `git diff --check` 通过 | 本轮执行输出 |

复现构建与运行：

```powershell
dotnet build OhMyStudy.slnx -c Debug -p:Platform=x64 -p:PublishAot=false -p:PublishTrimmed=false -p:UseSharedCompilation=false -p:NuGetAudit=false -m:1 --no-restore
dotnet tests/OhMyStudy.Provider.Tests/bin/Debug/net10.0/win-x64/OhMyStudy.Provider.Tests.dll --managed-sdk-check
dotnet tests/OhMyStudy.Domain.Tests/bin/Debug/net10.0/win-x64/OhMyStudy.Domain.Tests.dll output/google-removal/domain.json
dotnet tests/OhMyStudy.Grading.Tests/bin/Debug/net10.0/win-x64/OhMyStudy.Grading.Tests.dll
# 临时测试库置于工作区，避免受限临时目录的目录提升限制。
$env:TEMP = Join-Path $PWD 'output/google-removal/temp'
$env:TMP = $env:TEMP
dotnet tests/OhMyStudy.Storage.Tests/bin/Debug/net10.0/win-x64/OhMyStudy.Storage.Tests.dll
& './src/OhMyStudy.App/bin/Debug/net10.0-windows10.0.26100.0/win-x64/OhMyStudy.App.exe' --ui-process --workflow-smoke "$PWD/output/google-removal/workflow.json"
```

## 自动截图视觉验收

使用修改后实际 Debug 应用的 `--workflow-smoke` 自动导航、等待渲染并通过 `CaptureWorkspaceAsync` 捕获完整 XAML 工作区，未使用 Computer Use。最终生成 16 张截图。受限沙箱首次运行阻止 WinRT 写入截图，随后在允许的本地进程中重跑成功；首次失败不计作通过。

已查看截图并检查设置与关联区域的对齐、字体、间距、层级、换行、截断、滚动边界、控件尺寸、禁用与选中状态及反馈：

- 空、单项、多项、长名称：`workflow.settings-empty.png`、`workflow.settings.png`、`workflow.settings-long.png`。
- 宽、窄和紧凑窗口（1440 / 720 / 460 宽）：`workflow.settings-narrow.png`、`workflow.settings-compact.png`、`workflow.model.png`、`workflow.model-compact.png`。
- 两种协议与思考预算：`workflow.messages-settings.png`、`workflow.messages-model.png`、`workflow.messages-budget-compact.png`；工作流同时断言只剩两个协议入口、Messages 预算值和强度选项正确、Responses 隐藏预算。
- 深浅主题：`workflow.settings-dark.png`、`workflow.messages-model-dark.png` 及对应浅色截图。
- 输入失败保留草稿和恢复提示：`workflow.settings-feedback.png`。
- 关联资料库、执行和外观区域：`workflow.library.png`。默认页和菜单源码扫描无 Google 模型入口；关于对话框保留简短产品说明。

首轮发现长列表截图仍在进入动画，延长该场景等待至 400ms 后重新渲染并检查；紧凑预算截图滚动位置调整为完整显示思考强度标题。复查后，长列表文字稳定，Messages 的预算与按钮可读，宽窄布局、空列表、主题和错误反馈未发现未修复的视觉问题。证据均位于 `output/google-removal/`；这次修改未改变正文和控件字体。

## 未验证范围

本轮为非 AOT 运行。既有 Anthropic 官方 SDK 的 AOT / 裁剪限制仍在，没有降低检查或声称 AOT 通过。没有重新发布安装包、在线漏洞审计、真实供应商计费或模型语义验收，也未覆盖干净 Windows 系统、多屏 DPI、输入法、读屏、Sandbox 和物理打印。真实用户资料和凭据未被读取或修改。
