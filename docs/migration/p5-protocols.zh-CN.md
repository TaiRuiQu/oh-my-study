# P5 三协议迁移与验收边界

日期：2026-10-18（Asia/Shanghai）。本记录覆盖三协议实现及合成协议回归；整卷用例、真实供应商、真实样卷语义及干净系统验收分别记录，不能由本记录推定通过。

## 实现范围

正式代码位于 `src/OhMyStudy.Infrastructure/Providers/`，依赖纯 C# 领域 DTO。正式构建不读取 `.reference/`，没有 Rust、Node 或供应商 SDK 的运行依赖。

- `ProviderProtocol`：三协议初始文字/图片、原生请求、结果、模型分页、工具回传、原始上下文及用量归一化；不会依据模型名称推断能力。
- `ProviderStreamAssembler` / `SseDecoder`：逐字节严格 UTF-8、LF/CRLF/CR、BOM、多行 data、原生完成条件、内容块状态、工具参数、思考及不透明签名。
- `ProviderClient` / `IModelClient`：按显式调用发送请求，支持注入 `HttpClient` 和合成 `HttpMessageHandler`；构造、读取配置及 UI 显示不会发起请求。
- `ProviderCredentials`：独立秘密参数，不提供序列化契约，`ToString()` 不含密钥。异常仅含本地错误说明、分类、状态和重试时间，不保留完整供应商错误或异常内部文本。

`ProviderProtocol.Initial` 创建原生历史；`BuildRequest` 校验能力和原生参数；`Parse` 形成 `Turn`；`ContinueWith` 严格匹配待处理工具调用后追加历史。一次结果校验失败不会部分修改历史。不透明推理项、Messages thinking/signature/redacted 内容和 Gemini `thoughtSignature` 复制到下一次工具上下文；公共增量仅包含输出文本及工具名。

Responses 使用 `store:false`、`include:["reasoning.encrypted_content"]`、原生 function_call_output。Messages 保留 adaptive/effort 和 enabled/budget 的不同参数。Gemini 保留 thinkingLevel 与 thinkingBudget 的不同参数，合并扩展 generationConfig 时拒绝覆盖管理字段。扩展参数碰撞明确报错。

## 网络和重试边界

默认创建的 `SocketsHttpHandler` 使用系统 TLS 验证且禁用重定向。基础地址必须是 HTTPS，只有 `localhost`、`127.0.0.1` 和 IPv6 环回可使用 HTTP；拒绝 URL 内凭据、查询和片段，拒绝通过自定义头覆盖认证/Host。注入的客户端由调用方负责维持 TLS、无认证转发及无敏感日志约束；本层无法检验任意自定义处理器内部行为。

默认 `CallAsync` 和 `ListModelsAsync` 每次只有一个网络尝试。可显式使用 `CallWithRetryAsync`，策略最多四次、延迟最多两分钟；只重试明确 HTTP 429/5xx 且错误分类可重试的拒绝。配额、认证、参数错误、拒答、结果未完成、原生流中错误、协议损坏和状态不明的网络失败不会自动重放。`Retry-After` 支持秒数与 HTTP 日期并受上限约束，等待、发送和正文读取均贯穿取消。

正文及总 SSE 字节数限制为 32 MiB。终止事件前 EOF 不等于成功；Messages 未关闭内容块、工具 JSON 不完整和 Responses 非 completed 状态均失败。Gemini 支持 finishReason 后另发用量，最终仍要求单个 STOP 候选。

## 用量与估价

Responses 输出已经包含 reasoning，不重复相加；Messages 输入为普通输入加缓存读写；Gemini candidates 输出加 thoughts 一次。保留原始用量，缺失项保持未知，溢出报协议错误。

`EstimateCost` 忠实保留旧版按配置的输入/输出每百万 token 单价估算规则。缺少用量或单价时不生成金额。这个估计未区分缓存等级、折扣或供应商账单，不表示最终扣费。

## 验证方法与状态

测试项目：`tests/OhMyStudy.Provider.Tests/OhMyStudy.Provider.Tests.csproj`，无新增测试框架包。测试读取正式 `tests/Fixtures/ProviderProtocols/` 合成夹具，只在内存中发送给 fake HTTP 处理器，不对供应商发起请求；夹具内 fake 签名不能外发。

已执行 Release 回归（2026-10-18，.NET SDK 10.0.402）：

```powershell
dotnet restore tests/OhMyStudy.Provider.Tests --ignore-failed-sources -p:NuGetAudit=false
dotnet run --project tests/OhMyStudy.Provider.Tests -c Release --no-restore
```

结果：10 个场景全部通过，编译未报告警告或错误。合成覆盖包括三协议 JSON/流式请求和模型游标、工具/图片/签名回传、原生思考参数、能力未知/拒绝/元数据、扩展参数碰撞、UTF-8 任意字节分块、用量/估价、流截断和非法状态、错误脱敏、有限重试、在途正文取消、响应上限及地址/头校验。

本机受限文件权限需要将 `DOTNET_CLI_HOME` 设置为仓库内目录；NuGet 使用已缓存的 `output/migration/nuget`。首次默认目录启动及无缓存路径还原分别因权限和 TLS 失败；后续通过上述隔离目录及缓存完成构建和执行。`NuGetAudit=false` 仅是本次离线验证命令选项，没有修改工程审计默认设置，不代表在线依赖审计通过。

三协议实际调用/流式/工具回传另在 `OhMyStudy.Grading.Tests` Native AOT 发布产物经过真实 ProviderClient 运行，见 `p5-grading.zh-CN.md`。该编译发现并修复了 Initial 的 JsonArray collection expression 隐含泛型 Add 反射问题，最终 AOT 无警告。协议专用 10 场景的 Native AOT 结果仍由集中构建单独记录。

独立待验收：真实供应商兼容性、授权真实样卷语义、实际账户模型能力/用量、系统代理和真实网络 TLS、干净 Windows 10/11；本测试不宣称这些已通过。

协议迁移以归档 Rust 源码及正式合成夹具作逐行为对照。2026-10-18 尝试通过官方文档检索与读取工具复核协议，但该工具未返回正文；未把空检索当作完成官方复核的证据。后续联网文档复核与真实供应商验收仍应独立记录。
