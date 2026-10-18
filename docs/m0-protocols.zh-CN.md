# M0 原生模型协议验证

状态：三种协议的 Rust 原型与确定性离线测试已实现；真实供应商调用尚未验收。没有读取用户密钥，没有产生在线模型费用。这里的“通过”仅指合成数据、状态机和本机 HTTP 传输验证，不能据此声明模型兼容性、识别能力或供应商验收通过。

## 实现边界

入口为 `src-tauri/src/ai/providers`。`responses`、`messages`、`gemini` 各自提供 `initial`、`request`、`parse`、`continue_with`；仅共享任务输入、工具描述、结果与用量类型，不转换成 Chat Completions。

| 路径 | 已实现 |
| --- | --- |
| Responses | `input`、函数工具及 `function_call_output` 的文字／图片数组；原样回送全部 output items，包括 encrypted reasoning |
| Messages | `messages`、`input_schema`、`tool_use`、`tool_result` 图片内容；保留 thinking/signature/redacted_thinking blocks |
| Gemini | `contents.parts`、`functionDeclarations`、`functionCall`／`functionResponse.parts.inlineData`；保持原始 thoughtSignature 位置与调用 ID |
| 思考配置 | Responses effort；Messages adaptive + output_config.effort 或 enabled + budget_tokens；Gemini thinkingLevel 或 thinkingBudget |
| 流式 | 增量 SSE 解帧、跨网络 chunk 的 UTF-8、原生事件归并、工具参数碎片、Messages signature delta、终态检查；原始事件存于内部 Turn.raw |
| 用量 | 原始 usage 及规范字段；缺失计数为未知；Responses 输出包含思考，Gemini 输出为 candidates + thoughts，Messages 输入合并独立缓存读／写计数 |
| 错误 | 明确拒绝（Refusal）、认证、余额、限流、不支持参数、无效请求、服务端、网络、取消、协议错误和未完成；保存 Retry-After；不自动改参数重发；列表接口的非 JSON 错误体仍按 HTTP 状态分类 |

`Turn` 中的原始块属于协议内部上下文，不应整包交给界面或日志。`Transport::call` 仅通过 `Progress::Text` 和 `Progress::Tool` 回调公开结果文字及工具名称，不把 thinking/signature 作为界面进度。

`RequestOptions.extra` 接受协议原生参数，和管理字段冲突时明确报错。Gemini generationConfig 仅合并非冲突字段。未知思考配置须显式主动测试或人工声明能力；已知不支持视觉／工具的模型被拒绝，模型名称不用于猜测能力。Messages 模型元数据中的 image_input、tool_use（如果实际提供）、adaptive effort 与 enabled budget 会优先读取；其他未建立的能力保留未知，API 原始元数据保留供后续能力表使用。

## 传输与模型列表

`transport::Transport` 接收协议、用户配置的包含版本的根地址和额外请求头；真实请求由 `call` 发起，密钥通过专用 `Credentials` 参数传入。该类型不实现 Debug 或 Serialize，也不读取环境变量、磁盘或系统凭据库。M1 再接操作系统凭据存储。请求头中的密钥标为 sensitive，HTTP 错误只输出本地通用说明，避免供应商错误体回显输入或密钥。自定义地址仅允许 HTTPS，本机回环 HTTP 用于协议测试；禁用重定向以避免密钥被带到其他地址。

三种地址示例分别为 `https://api.openai.com/v1/`、`https://api.anthropic.com/v1/`、`https://generativelanguage.googleapis.com/v1beta/`。Gemini 接受裸模型 ID 或 `models/ID`，拒绝路径穿越和非模型路径。不要把已有版本路径重复拼入地址。

`list_models` 每次返回一页及 next cursor。Messages 使用 `after_id`，Gemini 使用 `pageToken`；调用者显式翻页。官方 OpenAI Models API 当前未文档化分页游标，因此默认单页，兼容网关明确返回 has_more/last_id 时才返回下一游标并使用 after。手动 `Model::manual` 不要求 ID 出现在列表中；能力保持未知。列表缓存与设置界面属于 M1。

Transport 每次只进行一次 HTTP 请求，默认 180 秒总超时、32 MiB 响应上限。调用者可针对 retryable 错误制定有限退避；本层不会自动重试、切换模型或回落到其他协议。CancellationToken 可在发送前和接收途中中止请求，取消后不自动重放。长度上限或提前 EOF 视为失败，不能当作完成。

## 可复现检查

```powershell
$env:CARGO_HOME = Join-Path (Get-Location) 'tmp/cargo-home'
cargo test -p oh-my-study-core ai::
```

合成样本在 `tests/fixtures/protocols/`，文件名、JSON provenance 或 SSE 注释明确标记 SYNTHETIC；所有签名、reasoning 与 ID 均为测试假值，不得用于真实调用。这次没有供应商录制样本。Responses／Gemini SSE 使用相同 JSON fixture 按原生事件结构在测试中构造，Messages 的分片事件另存为 `.sse`。

自动验证包含三协议的“文字＋图片 → 工具调用 → 工具结果＋图片 → 完成”，普通与流式解析一致、原始上下文保留、用量避免重复统计、任意字节拆分、输出截断、未闭合 block、重复 block、工具结果错配、未知能力与参数冲突。真实 reqwest 在回环合成 HTTP 服务上验证三种 URL／认证头／请求体／分页游标、chunked SSE、401／429／503 不隐式重试、错误脱敏、网络失败和传输中取消。这些用例均不访问供应商。

## 真实调用验收（待完成）

用户自行配置密钥后，由调用方从私有凭据存储构造 Credentials，依协议建立 native history 和 RequestOptions。先刷新模型列表，记录 API 元数据；未列出的手动 ID 需标记能力未知并由用户显式选择主动测试。

1. 选择具有视觉和工具能力的模型，记录精确 model ID、供应商地址与额外头名称、参数及测试时间，不记录密钥值。
2. 分别关闭／启用流式，发送自建无个人信息的文字和图片，声明 `read_image` 工具。验证供应商确实发出工具调用后，将自行准备的图片作为工具结果回送，再验证完整最终结果。
3. 对该模型实际支持的每种思考配置重复测试，确认原始上下文完整回送、签名未被改写、公开进度不泄漏协议内部块。
4. 用分页模型列表、手动未知能力 ID、不支持参数、认证失败、429、输出 token 上限与取消覆盖失败路径。不可为测试故意制造大量供应商限流请求；429 可使用供应商可控测试条件或脱敏录制数据补充。
5. 保存精确模型与配置、原始 usage、规范 usage、HTTP 状态、取消阶段、调用轮数和人工判断；如需录制协议样本，先脱敏再提交，并标记来源。价格与费用快照属于 M1；没有价格时应显示费用未知。

尚待实测的重点是供应商模型对工具图片的实际支持、思考参数组合、Gemini 流式签名实际分片形态与真实模型的完成率。当前模块没有整卷业务 Agent、执行工具、UI 或真实调用脚本；它提供调用与往返原语，不执行模型返回的任意工具命令。实现计划的 M0 真实协议验收仍保持待测。

## 官方资料

资料核对日期：2026-10-18；OpenAI 部分按 OpenAI Docs 技能核对官方文档。

- OpenAI：[Function calling](https://developers.openai.com/api/docs/guides/function-calling)、[模型列表](https://developers.openai.com/api/reference/resources/models/methods/list)。
- Anthropic：[工具定义](https://platform.claude.com/docs/en/agents-and-tools/tool-use/define-tools)、[流式事件](https://platform.claude.com/docs/en/build-with-claude/streaming)、[思考](https://platform.claude.com/docs/en/build-with-claude/extended-thinking)、[模型列表与能力](https://platform.claude.com/docs/en/api/models/list)。
- Google：[Generate Content／FunctionResponse／UsageMetadata](https://ai.google.dev/api/generate-content)、[Function calling](https://ai.google.dev/gemini-api/docs/function-calling)、[Thought signatures](https://ai.google.dev/gemini-api/docs/thought-signatures)、[Models](https://ai.google.dev/api/models)。
