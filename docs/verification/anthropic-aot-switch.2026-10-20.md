# Anthropic SDK 切换为 AOT 兼容包

2026-10-20。按用户要求，把 Anthropic Messages 的传输从官方 `Anthropic` 12.58.0 换为用户提供的 `TairuiQu.Anthropic.Aot`，并恢复被临时阻止的裁剪与 Native AOT 发布。全部请求、密钥与资料仍为合成数据或本机回环。

## 切换内容

- `Directory.Packages.props`：包版本改为 `TairuiQu.Anthropic.Aot` 12.58.0-aot.1；`OhMyStudy.Infrastructure.csproj` 的 `PackageReference` 同步替换，不同时引用两个包。
- 该包是官方 C# SDK 的 AOT 分支，程序集名与命名空间仍是 `Anthropic.*`，`MessagesSdkClient` 的调用方式、顶层 `cache_control` 自动缓存与全部业务行为未改动。
- 删除临时的 `Directory.Build.targets`：不再为受影响工程打开 `JsonSerializer.IsReflectionEnabledByDefault`，也不再在裁剪/AOT 发布时主动报错。业务 JSON 继续只使用封闭的源生成上下文。
- `OhMyStudy.Provider.Tests` 移除 `--managed-sdk-check` 逃生门，恢复为只接受 Native AOT 可执行文件。
- 重新生成解决方案 Release 图、Infrastructure AOT 图、Media 两种图及 App 四个发布变体的锁文件；旧 `Anthropic` 条目已全部清除。

## 实际运行验证

| 范围 | 结果 | 证据 |
| --- | --- | --- |
| App Native AOT 发布 | 成功，无裁剪/AOT 告警；实际产物为无 CLR 头的 PE32+ x64 | `output/aot-switch/app-aot/OhMyStudy.App.exe` |
| App 工作流 | 51 项检查通过，`native-aot=true` | `output/aot-switch/workflow-aot.json` |
| Provider Native AOT | 14 组场景通过，含 Anthropic 自动缓存、工具续接、错误、上限与取消 | `output/aot-switch/provider-publish/OhMyStudy.Provider.Tests.exe` |
| Grading Native AOT | 189 项通过，真实队列与 SQLite | `output/aot-switch/aot-OhMyStudy.Grading.Tests` |
| Domain Native AOT | 105 项通过，`Native AOT: True` | `output/aot-switch/domain-aot.json` |
| Storage Native AOT | 108 项通过，含旧协议记录保留 | `output/aot-switch/aot-OhMyStudy.Storage.Tests` |
| 非 AOT 对照 | Domain 105、Grading 189、Storage 108、工作流 51 项同样通过 | `output/aot-switch/domain.json`、`workflow.json` |
| 构建 | Debug、Release 非 AOT、Release 默认 AOT 分析均 0 警告 0 错误 | 本轮命令输出 |
| 锁定还原 | 解决方案 Release 与 App 四个变体 `--locked-mode` 通过 | 本轮命令输出 |

Native AOT 发布使用已加载的 MSVC 环境并设 `IlcUseEnvironmentalTools=true`。本机 `vcvarsall.bat` 会向标准输出混入一行 `'vswhere.exe' is not recognized`，被 ILC 误当作链接器路径；该现象出现在本机 VS 工具链初始化期间；干净 CI 运行器是否存在相同问题尚未验证。该参数未写入仓库配置。

## 界面验收

AOT 版本的实际界面通过 `--workflow-smoke` 自动导航并截图 16 张，未使用 Computer Use。已查看设置页宽屏、Messages 思考预算、紧凑窗口与深色主题截图，布局、字体、间距、换行与控件状态与切换前一致，未发现新问题。正文与控件仍使用非衬线字体。

## 未验证范围

- 未使用真实供应商账户、密钥或模型调用；真实识别、工具回合、缓存命中与计费语义仍待授权后验收。
- 未重新发布安装包，也未执行完整 CI 的 MSI 回归。
- 未在干净 Windows 系统、多屏 DPI、输入法、读屏与物理打印上验收。
- 该包为第三方分支（MIT，非 Anthropic 官方支持），后续升级官方 SDK 时需评估其同步情况。

## 提交前复核

2026-10-20 续接复核：重新运行现有 Native AOT Provider（14 组）、Domain（105 项）、Grading（189 项）与 Storage（108 项）产物，全部通过。运行  `packaging/Invoke-CI.ps1 -Phase Checks`，仓库布局、脚本与结构化文件检查、发布参数回归、四个 App 变体和 Debug/非 AOT/Media/Documents/解决方案锁定还原全部通过；Release `-warnaserror` 构建 0 警告、0 错误。`git diff --check` 与 `dotnet format whitespace --verify-no-changes --no-restore` 通过。核对已有 App 工作流报告（51 项、`Passed=true`、`native-aot=true`），并复查 Messages 模型设置页实际截图；本次未修改界面或重新运行 App 工作流。
