# 供应商适配、批量工具、流式响应与用量

[首页](Home-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Tech-Model-Loop.md)

## 一份试卷，一个会话

Responses 与 Messages 使用独立 SDK 适配器，共享轮次契约。运行器发送文档元数据、去除个人笔记的已有题目和参考答案，按需加入页面图片。每任务只选择一次模型提示配置；未知版本使用家族通用配置，未知 ID 使用通用配置。见[模型提示配置](../model-prompts.zh-CN.md)。

工具、指令和历史消息在轮次间保持稳定，各协议保留自己的推理与工具调用上下文。流式文字仅用于显示活动，不能保存题目。

| 工具 | 契约 |
| --- | --- |
| `read_pages` | 按序批量读取整页/裁片，返回元数据、图片、本地文件。只有整页读取计入覆盖。 |
| `save_references` | 替换完整参考列表，匹配稳定评分项键，保留已有作答引用的参考键。 |
| `save_paper_info` | 推断学科/总分并保护手动试卷信息，仅整卷任务可用。 |
| `save_questions` | 批量保存公共题干与评分项，通常每次 4–8 题。逐项返回结果，部分失败不回滚成功项，仅整卷任务可用。 |
| `python`、`shell` | 按任务执行策略计算或处理图片，可用 `outputs` 返回最多八张图片及可复用资源 ID。 |
| `finish` | 校验整页覆盖与保存结果，成功后结束任务。 |

仅整理参考答案的任务提供页面读取、参考保存、计算和完成工具，不能保存题目或试卷信息。代码默认超时 60 秒，`timeout_ms` 接受 1–300,000 毫秒。Python 在任务内共享笔记本状态，PowerShell 每次启动新进程。

题目内容支持文本、公式、结构化表格、图片和 Mermaid 流程图。图片与 Mermaid 的 `width_percent` 可选，范围为 1–100。省略时，PDF 默认最大宽度为 9 cm，且不超过当前栏宽；显式值覆盖该默认值。Mermaid 必须提供代码与原始材料区域；生成图片同样保留证据区域。省略的宽度在存储中保持未指定；此前保存的数值宽度会保留。在题目编辑器中清空数值宽度可恢复默认值。提示要求优先用 Markdown/结构化表格、Mermaid 流程图和 Matplotlib PNG 替代不清晰的裁片，并说明精确的字体指令与 `^`、`<`、`>` 整单元格合并标记。Mermaid 输出限于使用简单节点和带标签箭头的原始 `flowchart`/`graph` 代码，以便受限的原生渲染器显示。

## 边界与恢复

最多 60 个模型轮次，关联截止时间 20 分钟。连续两次纯文字回复会收到继续提示，第三次失败。工具参数错误返回纠正反馈；不能因部分条目失败而无故重复已成功项。参数不是单个 JSON 对象（包括被截断或格式错误的流式参数）时不会执行，也不会结束任务：每次这类调用都会收到纠正提示，模型可以据此重试。

临时网络、流中断、服务错误与限流允许每轮最多四次尝试，通常等待 1、2、4 秒；Retry-After 可延长到 30 秒。不执行未完成响应。鉴权、参数、拒绝与明确输出上限错误不自动重试。保留先前已完成轮次。重试用量未报告时，该次调用不计价，任务合计仍保留已经计价的调用。

## 文件上传与缓存

Files API 按供应商显式启用，新任务保存该设置。会话内按 MIME 和内容哈希去重页面、裁片及生成图片。Responses 上传使用 `purpose=user_data`、`expires_after[anchor]=created_at` 和 `expires_after[seconds]=3600`；Messages 使用自己的上传格式与 `expires_in_seconds=3600`。后续调用引用文件 ID，避免反复发送 base64。成功、失败、取消后均尝试逐个删除上传文件。拒绝上传时明确失败，不静默回退为内联或永久文件。

OpenRouter 使用固定端点会话；Messages 显式缓存固定系统指令并自动缓存增长的会话；OpenRouter 上的 Gemini 使用隐式缓存。缓存效果仍取决于供应商与模型。

Codex 模式是供应商的可选开关，并随新任务保存。协议为 Responses 且开关打开时，每一轮都发送 `prompt_cache_key`，值为该试卷的文档 ID，并在整个任务中保持不变。开关关闭、协议为 Messages 或 ID 为空时不发送。该字段名已被保留，同名的额外参数会被拒绝。

用量记录保存配置价格、货币、供应商报告的 token 分类、重试信息和估算。缺少输出数量时省略该项费用。缺少输入分类、输入拆分与总量不一致、已知非零用量没有单价，或重试用量未报告时，该次调用的估算保持未知。任务合计只相加已知估算。文件存储与语义搜索费用不属于识别 token 估算。

源码：[运行器](../../src/OhMyStudy.Infrastructure/Grading/GradingRunner.cs)、[工具](../../src/OhMyStudy.Infrastructure/Grading/GradingTools.cs)、[重试活动](../../src/OhMyStudy.Infrastructure/Grading/ModelActivity.cs)、[文件会话](../../src/OhMyStudy.Infrastructure/Providers/ProviderFileSession.cs)、[协议](../../src/OhMyStudy.Infrastructure/Providers/ProviderProtocol.cs)。
