# OCR、切题、并行解题与独立评分

[首页](Home-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Tech-Model-Loop.md)

## 固定阶段

识别已改为各模型上下文分离的工作流：MinerU 识别原始页面，小模型切题并绑定材料，选定的解题模型按题独立求解；有作答的题目再进入独立评分调用，可配置另一评分模型。仅整理参考答案时，只识别、匹配参考答案，不解题。

MinerU 使用 `https://mineru.net/api/v4/file-urls/batch` 申请上传地址，以签名 PUT 上传，并轮询 `extract-results/batch/{batch_id}`。官方接口要求 Bearer Token，不能用 Access Key / Secret Key 对替代。原页资源保留在本地；上传渲染后的页面而非原始 PDF，明确页面身份及坐标对应。

OCR 与本地题框检测可并行运行。每页保存 MinerU 原始 ZIP、Markdown、内容列表 JSON 及引用图片；`full.md` 按页序合并并给图片链接加页命名空间。`ocr.json` 包含归一化来源区域和托管图片资源；`regions.json`、`split-plan.json`、逐次解题和评分 JSON 也保存在资料库的 `workflow_artifacts` 表。数据库版本 7 仅新增该表，升级前备份数据库。资料库备份包含这些产物，重试回滚会删除失败轮次的产物。

## 切题与区域兜底

切题模型从已保存的供应商中选择，与解题和评分模型独立。任务保存供应商、模型、地址和凭据引用快照。直连 DeepSeek 使用 `/responses`，关闭思考并通过严格 JSON Schema 限定输出；其他供应商使用其已配置的 Responses 或 Messages 协议，不提供工具。输入包括 OCR、原页图、已有稳定键、参考答案、题框。切题必须逐一处理全部 OCR 块和题框，关联跨页续题、共享阅读材料、配图、表格，并识别作答区域。整卷 Markdown 留存，归一化的 OCR 块作为结构化切题输入。

YOLO26N Question Segmentation v2 是单类别题目框检测器。安装 `uv` 后运行 `./packaging/prepare-question-regions.ps1`，将固定版本的公开权重转换为 ONNX。转换工具保存在忽略的 `output/`；应用使用 ONNX Runtime 检测，无需 Python/PyTorch。准备好的 `native/question-regions/base.onnx` 自动启用；设置中可覆盖其路径并增加中文模型。两个模型独立提供候选框，不把题框模型称为手写识别模型。没有安装模型时，任务明确显示无区域兜底，仍使用 OCR 和原图审阅。

ONNX 输入为固定 float32 RGB `[1,3,1280,1280]`，以 114 填充 letterbox；输出为端到端 `[1,N,6]`（四角坐标、置信度、类别）。导出使用 `nms=False`、opset 17、静态尺寸，检测阈值 0.20。存储坐标相对未旋转整页归一化；MinerU 的 0–1000 四角坐标只转换一次。显示旋转不改证据坐标。

[中文数据集评估](../question-regions-dataset.zh-CN.md)决定暂不使用候选数据集进行生产微调。本次没有生成中文微调权重。

## 独立解题和评分

切题先保存待复核草稿，再开始付费解题。每个解题上下文仅接收绑定后的题目及原始题区截图，返回答案、解析、知识点和不确定项，不能改题目或评分项稳定键。空白模式不创建作答；已作答和教师批改模式逐题评分；自动模式对切题阶段发现笔迹、疑似笔迹、作答区域或已有作答的题目评分。

评分接收独立解法、已核实参考答案、附整页坐标的原始作答截图；未定位作答裁片时发送相关整页，避免题框排除空白答题区后遗漏笔迹。看不清的笔迹标为不确定；教师/自动模式保护明确教师结果，不能把 AI 解法充作学生答案。仓储继续保护手动修改的作答、人工分数、教师证据和笔记。

解题端唯一工具是 `python`：AST 解释器只接受数学表达式、简单赋值和批准的数值/SymPy 函数，不允许导入、属性访问、文件、网络、shell 或字符串求值。符号计算只接收表达式对象。变量仅在单次调用内有效；执行仍经过现有权限和取消边界，Python 执行闸门避免并行题目破坏共享工作进程。模型不再调用读页、保存、完成或 shell 工具。

## 边界、费用与失败

默认并行 3 题，可设 1–8 题；每题最多 4 次解题调用，可设 1–8 次；单次受控计算限 30 秒，任务总限时 30 分钟。切题输出预算默认为 32,768 token，并受模型上限限制；直连响应被截断时保留原生响应及 token 用量。分类、切题和评分各允许最多三次校验尝试，关闭传输层自动重试。简短任务提示与 API 输出 Schema 分开传入。模型响应先经过格式归一化，再检查 Schema 和领域规则；原始及归一化产物均可查阅。真实证据缺失或评分矛盾通过有界反馈要求修正，存储 JSON 契约与页面覆盖规则仍严格校验。

切题、解题、评分均记录 token 用量。各模型按供应商配置单价估算，人民币与美元金额先换算再汇总；MinerU 和未定价调用明确不计入显示估算。导入确认说明全部上传对象及各阶段可能计费。密钥仅进入 Windows 凭据管理器，不进入任务快照和识别产物。

任务失败后可保留 OCR、题目草稿和已完成结果。重试使用现有恢复记录回滚该轮次；重新识别则保留已有编辑，两者可能产生新费用。Files API 上传和清理限制在各题独立客户端内，避免并行题目混用上传状态。

源码：[运行器](../../src/OhMyStudy.Infrastructure/Grading/GradingRunner.cs)、[MinerU](../../src/OhMyStudy.Infrastructure/Grading/MinerUClient.cs)、[切题](../../src/OhMyStudy.Infrastructure/Grading/QuestionSplitter.cs)、[提示](../../src/OhMyStudy.Infrastructure/Grading/WorkflowPrompts.cs)、[检测器](../../src/OhMyStudy.Documents/QuestionRegionModel.cs)。
