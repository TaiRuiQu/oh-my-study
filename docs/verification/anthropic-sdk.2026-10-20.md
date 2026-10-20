# Anthropic 官方 SDK 接入与自动缓存

> 后续变更：Google 模型支持已移除，当前仅保留 Responses 与 Messages。以下三协议相关内容是当时的验证记录，现状见 [移除记录](google-removal.2026-10-20.md)。

2026-10-20。按用户要求，在现有 OpenAI 接入的工作区改动之上接入官方 `Anthropic` 12.58.0。用户确认官方包目前不支持 Native AOT，允许先完成接入；只有用户后续通知后才换为其兼容包 `TairuiQu.Anthropic.Aot`，不能提前替换或假设该包已经发布。

## 实现

- `MessagesSdkClient` 隔离 SDK 依赖。官方 `AnthropicClient.Messages.Create/CreateStreaming` 承担请求序列化、认证、SSE 帧解析与模型读取；工具定义、消息、思考配置和缓存使用 SDK 模型。应用保留严格的工具 JSON、终止事件及内容块生命周期检查，SDK 解析出的事件直接进入状态归约器，不重新编码或解析 SSE。
- `ProviderClient` 将 Messages 调用和模型列表分派给该适配器。模型列表通过 SDK 的 `Models.WithRawResponse.List` 读取一页，保留未知能力和原生元数据，不自动翻页。配置的 URL 前缀、分页游标与自定义头仍有效。
- 所有 Messages 请求默认携带顶层 `cache_control: {"type":"ephemeral"}`，启用自动前缀缓存（用户所称隐式缓存），沿用供应商默认 5 分钟 TTL。工具续接请求同样携带，不向各个历史块添加显式断点、不修改历史或不透明签名。不主动启用成本更高的 1 小时 TTL。
- 保留 `cache_read_input_tokens`、`cache_creation_input_tokens` 和原始用量；不把启用缓存等同于缓存命中，也不伪造节省金额。实际命中受供应商的最小缓存长度、前缀一致性和有效期约束。
- `cache_control` 与 `model/messages/max_tokens/stream/tools/thinking/output_config` 一样属于应用维护字段，额外参数不能覆盖。`system` 等其他原生参数继续透传。
- SDK 内置重试设置为 `MaxRetries=0`。只有应用显式重试策略允许的 HTTP 429/5xx 拒绝可重试；传输歧义和 SSE 内错误不重放。错误与 `Retry-After` 转为现有契约，异常不保存供应商原始错误或密钥。重定向不跟随。
- 响应流包装器对普通回复、模型列表、错误体和流式传输累计执行 32 MiB 上限，调用使用既有取消令牌。SDK 客户端按调用释放，不释放调用者注入的 HttpClient。

参考：[官方 C# SDK](https://github.com/anthropics/anthropic-sdk-csharp)、[自动缓存](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)。

## 临时构建边界与后续切换

官方 SDK 在普通 .NET 执行时也依赖默认反射序列化。`Directory.Build.targets` 仅为直接依赖该 SDK 或引用 Infrastructure 的非 AOT 工程开启此运行时默认值；业务序列化仍显式使用源生成上下文。没有关闭 AOT/裁剪分析或降低告警级别。同一文件为这些工程的 AOT 或裁剪发布给出明确构建错误（同时覆盖 App 自身设置的 Release/AOT 属性，不只依赖全局属性传播），避免已知不兼容的程序流入安装包。

普通构建/发布显式选择 `-p:PublishAot=false -p:PublishTrimmed=false`；App 可选 `-p:OhMyStudyPublishVariant=framework-dependent` 或 `self-contained`。原有 AOT 发布及整体验收暂不可通过。

用户通知兼容包完成后：

1. 在 `Directory.Packages.props` 将 `Anthropic` 换为 `TairuiQu.Anthropic.Aot`，版本以用户提供的信息为准。
2. 在 Infrastructure 工程替换 PackageReference。保留 `MessagesSdkClient` 的 SDK 公共调用方式和自动缓存行为，不同时依赖两个包。
3. 移除临时反射默认值与发布阻止目标，保留原有严格 AOT/裁剪分析。
4. 重新生成全部发布变体的锁文件，发布并运行 Native AOT Provider、Grading 与整套仓库验收，检查工具、图片、不透明上下文、缓存、错误、取消、分页及实际桌面工作流。

## 验证与范围

- Provider：非 AOT 实际运行，14 组场景通过，记录为 `output/anthropic-sdk-provider-tests.log`。新增 Anthropic 专门回归覆盖普通/流式首轮与后续工具回合、顶层缓存和无块级标记、缓存读写用量、签名与 redacted thinking、分片工具 JSON、图片、自定义端点与头、HTTP/SSE 分类与脱敏、显式重试和歧义失败不重试、响应上限、取消和缺失终止事件。本机 socket 回归同时覆盖三协议的普通/逐字节分块响应与分页，并验证 Messages 缓存字段、不跟随重定向和挂起连接取消。
- Grading：非 AOT 实际运行，242 项检查通过，记录为 `output/anthropic-sdk-grading-tests.log`，覆盖三协议、三模式、真实队列和 SQLite，含 Messages 两轮图片工具处理及持久化。
- App：Release、`framework-dependent` 配置最终构建成功，0 警告、0 错误，记录为 `output/anthropic-sdk-app-build.log`。利用仓库已有 `output/migration/nuget` 作为还原回退目录；首次还原在线源访问出现 NU1801，缓存就绪后的最终构建未产生该告警。检查实际生成的 App、Provider 和 Grading runtimeconfig：仅 JSON 反射默认值按需开启，区域性、BinaryFormatter 及其他运行时开关保持原值。
- 锁文件已重新还原解决方案 Release 图、已有 AOT 库图及 App 四种发布变体；这只验证依赖解析，不代表 AOT 可以运行。实际 AOT 构建已验证在明确的兼容性错误处停止（`output/anthropic-sdk-aot-guard.log`）。
- 修改涉及的 C# 文件通过 `dotnet format whitespace --folder --verify-no-changes`；使用 folder 模式避开当前沙箱禁止的 MSBuild 格式化宿主命名管道。
- 全部数据与密钥均为合成夹具或本机回环；没有使用真实账户、真实资料或产生模型费用。未验证真实缓存写入/命中、真实模型输出及计费。
- 本轮没有修改用户界面，不涉及 UI 截图验收；不以本轮后端测试代替未来 UI 视觉检查。

复现命令：

```powershell
dotnet build tests/OhMyStudy.Provider.Tests -c Debug -p:PublishAot=false -p:PublishTrimmed=false -p:UseSharedCompilation=false -m:1
dotnet tests/OhMyStudy.Provider.Tests/bin/Debug/net10.0/win-x64/OhMyStudy.Provider.Tests.dll --managed-sdk-check
dotnet build tests/OhMyStudy.Grading.Tests -c Debug -p:PublishAot=false -p:PublishTrimmed=false -p:UseSharedCompilation=false -m:1
dotnet tests/OhMyStudy.Grading.Tests/bin/Debug/net10.0/win-x64/OhMyStudy.Grading.Tests.dll
```

本机 NuGet 在线源访问受限，后端验证利用已有包缓存并仅在命令行指定 `-p:NuGetAudit=false`；仓库的审计设置保持开启。在线漏洞审计未完成。
