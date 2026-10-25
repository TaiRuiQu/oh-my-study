# 模型提示词配置

[使用指南](usage.zh-CN.md) · [English](model-prompts.md)

每个识别或参考答案整理任务在开始执行时，根据所选模型 ID 选择提示词配置，调整指令、上下文位置及现有学习工具的说明。所选模型、接口类型、思考设置、价格、工具操作及参数校验保持原有配置。

选择时不区分大小写，识别网关前缀、常见版本分隔符及带日期的模型 ID。例如，`anthropic/claude-opus-5.5` 与 `claude-opus-5-5` 使用相同配置。没有专用配置的新版本使用所属系列的通用指引；无法识别的名称使用通用学习提示词。名称仅用于选择配置，不代表已验证模型的开发者或能力。

每个任务只选择一次配置，各轮调用的工具、指令和历史消息保持不变，以保留可复用的缓存前缀。Gemini 将具体任务放在文档上下文之后。选择过程在本地完成，不会在任务启动时获取在线文档或额外调用模型。

保存题目通常每次调用包含 4～8 题。短题、简单题可适当增加批量；长题、多小问、图表或解析较多时减少批量。题目识别完成后及时保存，失败时仅重试失败条目。

## 支持的配置

| 模型 ID | 提示词和工具指引 |
| --- | --- |
| GPT-4.1 及其变体 | 明确的工具流程、转录前先读取证据，以及读取和保存顺序的具体示例。 |
| GPT-5.4 | 简洁的执行约定、独立操作批处理，以及有依赖关系的操作顺序。 |
| GPT-5.4 mini/nano | 编号步骤、明确的歧义处理，以及先修复错误再执行依赖操作。 |
| GPT-6 系列，包括 GPT-6.1 | 完成整项任务、依据证据作出常规判断、区分指令和文档数据，并在完成校验后停止。 |
| 其他包含独立 `gpt` 标记的 ID | OpenAI 通用指引，明确操作与完成条件。 |
| OpenAI o1/o3/o4 系列 | 直接说明目标与约束，保留内部推理，在保存字段中提供有用的解题说明。 |
| Claude Haiku 5.5 | 明确的完成清单、检查实际执行结果，并持续调用工具直到 `finish`。 |
| Claude Opus 5 | 简洁说明、限定任务范围，以及有针对性的检查，避免额外复查循环。 |
| Claude Opus 5.5 | 避免仅汇报进度就提前停止；对密集图表或细小标签使用裁剪与图像处理。 |
| Claude Sonnet 5.5 | 完成所有要求、使用准确的工具参数、检查执行结果，并避免扩大任务范围。 |
| 其他 Claude 版本 | Claude 通用指引，明确工具用途、效果及依赖关系。 |
| Gemini 2.5 | 结合图像证据，并提供未知分数与无小问题目的处理示例。 |
| Gemini 3 系列 | 直接且结构清晰的指令、明确图片与页面的对应关系，调用函数前无需固定文字前言。 |
| 其他 Gemini 版本 | Gemini 通用指引，使用准确的函数参数，并将任务放在上下文之后。 |
| 无法识别的 ID | 通用学习指令和工具说明。 |

所有配置均保留原件保护、语言与数学格式规则、Python 笔记本式共享状态、用户修改保护，以及服务端校验。模型专用措辞调整工具使用方式；写入资料库仍通过相同的已校验保存操作完成。

## 试卷信息、标准解法与学生作答

`save_paper_info.name` 是可选且可为空的试卷标题，按原文转录。省略、null 或空白名称保留导入名称；手动修改的试卷信息受到保护。模型重命名不使页面快照或覆盖记录失效，失败任务重试会恢复此前的标题。

每个可辨认的小问都要求生成 `answer` 和 `explanation`，空白试卷也一样，但仍保留 `attempt=null`。解析包含简洁完整的标准解法；旧字段 `attempt.ai_solution` 继续兼容读取，无需重复输出。无法确定答案时记录具体的待确认原因，不编造内容。`attempt.error_analysis` 可为 null，只针对错误或部分正确的作答说明有证据支持的错因；错因不明、答案正确或没有作答时留空。

补充作答页面使用 `role=student`。模型将其匹配到已有小问的稳定键，并通过 `attempt.student_page_ids` 记录作答证据，不重复题干或创建重复题目。手动录入的作答会保留，同时允许更新 AI 评分与错因分析。空白卷补充作答后启用自动批改；修改录入作答会清除旧结果。

## 指引与验证

查阅日期：2026-10-23。各配置依据以下官方资料，针对试卷整理与批改进行调整：

- OpenAI：[提示词设计](https://developers.openai.com/api/docs/guides/prompt-engineering)、[GPT-4.1 提示词](https://developers.openai.com/cookbook/examples/gpt4-1_prompting_guide)、[GPT-5.4 提示词](https://developers.openai.com/api/docs/guides/latest-model?model=gpt-5.4)、[GPT-6 提示词](https://developers.openai.com/api/docs/guides/latest-model#prompting-best-practices)，以及[推理模型最佳实践](https://developers.openai.com/api/docs/guides/reasoning-best-practices)。
- Anthropic：[通用提示词最佳实践](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/claude-prompting-best-practices)、[Haiku 5.5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-haiku-5-5)、[Opus 5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5)、[Opus 5.5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-opus-5-5)，以及[Sonnet 5.5](https://platform.claude.com/docs/en/build-with-claude/prompt-engineering/prompting-claude-sonnet-5-5)。
- Google：[提示词设计策略](https://ai.google.dev/gemini-api/docs/prompting-strategies)与[函数调用最佳实践](https://ai.google.dev/gemini-api/docs/function-calling#best-practices)。

这些配置是持续维护的模板，不代表已经证明识别准确率最优。真实试卷的准确率与成本改善需要在所选供应商和模型上评估。仅在模型官方文档或实测行为提供依据时添加版本专用指引，其余版本保留系列通用配置。
