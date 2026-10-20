# 试卷、模型和任务界面调整

日期：2026-10-20。

## 本轮范围

- 试卷入口为独立的虚拟化列表，支持标题/学科搜索、学科筛选，以及导入时间、标题、学科、得分率排序。列表保留筛选状态，打开试卷后可返回列表。
- 原顶部“导入试卷”和更多操作仅在试卷列表及试卷详情中显示。具体试卷的页面、答案、打印和导出操作只在详情中出现。
- 试卷记录导入时间、学科、得分、满分和题目数量，标题/学科/分数可修改。未知分数显示横线，不能冒充零分。识别工具增加 `save_paper_info`，只依据明确资料保存整卷信息；手动信息不会被识别覆盖。
- SQLite schema 3 新增 `paper_info`。v1/v2 升级前建立一致性数据库备份，升级事务包含全部新结构，失败回滚；原始数据和扩展字段保持不变。选择已有资料库接受正式 v2/v3 库，备份恢复仍走原来的保护流程。
- 供应商模型列表先分页取候选，再用可搜索的多选弹窗确认。默认不选模型，过滤时保留已选项，取消不写入，现有模型不被覆盖。
- models.dev 精确匹配模型 ID；填充显示名、上下文、价格、图片/工具能力、明确的思考强度，并保留输入/输出模态、最大输出量、思考支持信息。仅有 reasoning 布尔值不生成强度；Messages 只接受其协议支持的强度。
- models.dev 目录和美元/人民币汇率使用按本地自然日持久缓存：同一天重复查询不请求网络，顺序化并发查询，限制响应大小，原子替换最后成功的内容。失败保留旧数据并标示缓存；无有效汇率时不显示虚假的人民币金额。失败当天不重复请求，下一自然日再更新。
- 模型原始价格可选 USD/CNY，编辑页另显人民币参考价格，任务预估费用用人民币展示，并保留汇率日期。原始价格和任务历史中的货币不会被改写。
- 识别模型下拉框在创建时确定宽度，列表项也受同一宽度约束；长名称省略并提供完整提示，滚动不改变宽度。
- 任务页使用平面统计与图标轨迹。模型回复改为文字展开/收起，移除嵌套卡片；回复去掉末尾空行，视口上限只限制长回复，不给短回复设置高度。
- 题目详情将作答、评分、教师批注、解题过程、判断差异和待确认事项分区展示。字段名称使用小标题和 Fluent 图标；正文继续经过原有公式呈现路径。窄窗口中单题列表按内容收缩。
- 题目核心字段仍为 draft_key、number、content、sources；辅助字段、教师批注及证据、作答补充信息、小问列表等允许省略。省略的新信息使用空值或未知评分；重识别省略字段保留旧内容。明确的教师/手动评分继续保护，非有限数、越界分数、无效原图坐标、伪造手动评分、未匹配参考答案仍拒绝。

## 自动截图与视觉检查

使用修改后真实 WinUI 窗口的自动截图入口，未用 Computer Use 验收布局。所有诊断使用 `output/` 下隔离合成资料，不请求识别供应商或读取真实模型凭据。受限进程调用 Windows 截图保存 API 返回拒绝访问，因此通过已授权的普通桌面进程运行截图。

```powershell
./output/papers-app/OhMyStudy.App.exe --papers-smoke "$PWD/output/papers-visual/report.txt"
./output/papers-app/OhMyStudy.App.exe --task-smoke "$PWD/output/tasks-visual/report.txt"
```

检查覆盖：空/单项/241项试卷、长名称、搜索无结果、学科组合筛选、返回列表、其他页面无试卷操作、模型选择的空选/跨搜索选择/无结果/窄窗口、下拉框滚动前后宽度、USD/CNY 参考价格、题目反馈分区；任务的空/运行/短回复/长回复/完成/取消/失败/执行请求，浅色/深色及宽、中、窄、短窗口。

证据位于 `output/papers-visual/report.*.png`、`output/tasks-visual/report.*.png` 及各目录报告。逐张检查本轮受影响的状态，第一轮修复了单题窄窗口列表多余留白、任务无用量时的空容器间距、深色主题小标题的静态颜色和模型候选空列表的留白；补查了实际保存错误反馈，并调整价格区域与原生弹层截图的捕获方式。修复后重新运行并复查。

主要截图：

- `output/papers-visual/report.many.png`、`report.compact.png`、`report.dark.png`
- `output/papers-visual/report.model-select.png`、`report.model-select-filtered.png`、`report.model-select-empty.png`、`report.model-select-compact.png`
- `output/papers-visual/report.model-dropdown-start.png`、`report.model-dropdown-end.png`
- `output/papers-visual/report.model-usd.png`、`report.model-cny.png`
- `output/papers-visual/report.question-sections.png`、`report.question-dark.png`、`report.question-compact.png`
- `output/tasks-visual/report.short-reply.png`、`report.dark.png`、`report.compact.png`、`report.failed.png`、`report.permission.png`

## 验证结果

- Debug 构建成功；试卷与模型诊断、任务诊断通过。完整工作流 51 项功能检查通过（该轮运行于 Debug，Native AOT 标记如实为 false）。
- Native AOT 领域 105 项、存储 112 项、供应商协议 15 组、识别集成 211 项通过。测试仅使用合成临时资料和模拟供应商网络。
- `packaging/Test-RepositoryLayout.ps1` 通过；`git diff --check` 无空白错误。
- 应用 Native AOT 发布与该版本的工作流结果待发布结束后补记。
- 构建时 NuGet 安全公告服务不可达产生 NU1900 警告；锁定包的还原与编译仍成功，未关闭裁剪/AOT 分析。

未验证项：真实识别供应商/真实试卷的学科判断、真实模型供应商返回的候选质量、物理打印、跨系统与 DPI/辅助功能矩阵。汇率适配已核对实际公开接口格式；画面与回归用固定示例汇率，不能作为实时汇率证据。

## 外部资料

- [models.dev 字段定义](https://github.com/anomalyco/models.dev/blob/dev/packages/core/src/schema.ts)：reasoning_options、modalities、limit 等字段。
- [Frankfurter API](https://frankfurter.dev/)：使用 `/v2/rate/USD/CNY` 的 date/base/quote/rate 字段。展示最近可用日期，节假日不冒充当日数据。
