# OpenAI Responses 官方 SDK 迁移验收

> 后续变更：Google 模型支持已移除，当前仅保留 Responses 与 Messages。以下三协议相关内容是当时的验证记录，现状见 [移除记录](google-removal.2026-10-20.md)。

2026-10-20，本地源码改造完成，尚未制作安装包或发布。全部请求与响应均为合成数据或本机回环，没有使用真实供应商凭据，也没有产生实际模型费用。

## 决策依据

用户要求把自建的 HTTP 请求改为供应商官方 SDK。先在 .NET 10.0.402、ILC 10.0.13、`JsonSerializerIsReflectionEnabledByDefault=false`、IL2026/IL2104/IL3050/IL3053 视为错误的条件下实测三个官方 SDK：

| SDK | 严格 AOT 发布 | Native AOT 运行时实测 |
| --- | --- | --- |
| OpenAI 2.14.0 | 通过，自身无裁剪/AOT 告警 | 非流式与 SSE 流式均通过 |
| Google.GenAI 1.26.1 | 失败 | API key 路径运行时可用，但需豁免 `Google.GenAI` 与 `Google.Apis.Auth` 传递依赖的告警 |
| Anthropic 12.58.0（最新稳定版） | 失败 | 运行即失败：`FrozenDictionaryConverter`、`ImmutableEnumerableOfTConverterWithReflection` 缺少 AOT 元数据，`TrimmerRootAssembly` 也无法修复 |

因此本轮只迁移 Responses 协议，Messages 与 Gemini 保持现有实现。这是用户的明确选择，不是遗留 TODO。

## 实现结果

- 新增 `ResponsesSdkClient`，由官方 `ResponsesClient` 负责请求建模、认证、SSE 帧解析与响应模型；应用层只做契约映射。
- 请求字段通过 SDK 的类型化属性与 `JsonPatch` 设置：`model`、`input`、`max_output_tokens`、`stream`、`store=false`、`include=reasoning.encrypted_content`、工具定义、`reasoning.effort`、`instructions`，以及用户配置的其余原生参数。与已生成字段冲突或名称非法的原生参数仍然拒绝。
- 历史中的不透明上下文（Responses 推理加密内容、函数调用）经 SDK 的 `ModelReaderWriter` 往返；实测单项往返字节一致。带图片的函数输出使用 `JsonPatch` 写入原生块数组，避免 SDK 丢失图片。
- 模型列表沿用 SDK 的 `OpenAIModelClient`；分页游标通过按调用策略追加 `after` 参数。
- SDK 内置重试关闭（`ClientRetryPolicy(0)`），保留应用既有的显式、有上限、仅针对 HTTP 拒绝的重试；传输层歧义失败仍不重放。
- 流式：`response.completed` 前必须有终结事件；`error`、`response.failed`、`response.incomplete`、拒答分别映射为对应错误类别；截断流仍判为未完成。取消会关闭底层响应流以释放挂起的同步读取。
- 错误只携带分类结果与状态码，不保留供应商原始错误文本；`Retry-After` 仍然传递。
- 非流式响应、模型列表都限制在既有 32 MiB 上限内；流式响应通过包装响应流累计计数。
- 移除 Responses 专用的自建请求体、SSE 组装与端点/认证分支；`ProviderProtocol.BuildRequest` 对 Responses 转为调用 SDK 的请求序列化，使既有协议检查仍可断言实际线格式。

## 验证

- Provider 协议回归（Native AOT 发布后运行）：13 个场景全部通过，新增 “official SDK framing” 场景覆盖请求字段、不透明上下文、图片工具输出、原生参数冲突、终止/失败/不完整/拒答/截断流。`output/migration/provider-check/OhMyStudy.Provider.Tests.exe`。
- Grading 集成：242 项检查通过，包括通过真实队列与 SQLite 的 Responses 流式链路、部分输出保留、取消与迟到结果。`output/migration/grading-check`。
- 其余套件在 Native AOT 下全部通过：Domain 107、Storage 104、Execution、Media 26、Setup 47、Integration。
- App 以 Release Native AOT 发布成功，无裁剪或 AOT 告警：`output/migration/app-check/OhMyStudy.App.exe`。
- `dotnet format whitespace --verify-no-changes` 通过。
- 锁文件已按 AOT、AVX-512、self-contained、framework-dependent 四个发布配置重新生成。

## 未验证项

- 未使用真实 OpenAI 账户、真实密钥或真实模型调用；真实识别、工具回合与计费语义仍待用户授权后验收。
- 未在真实网络代理、企业网关或非官方兼容端点上验证 SDK 的端点拼接与错误映射。
- 本轮只替换了 OpenAI Responses 的传输路径，界面没有改动，因此不涉及界面视觉验收。设置页的“测试连接”“获取模型”等入口沿用既有界面。
- Google 与 Anthropic 的 SDK 迁移不在本轮范围；Anthropic 在当前 AOT 约束下无法通过官方 SDK 使用，后续若迁移需要单独方案。
