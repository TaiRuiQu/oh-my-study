# Chinese Exam Question Regions：采用结论

[English](question-regions-dataset.md)

评估日期：2026-10-30。来源：[数据集说明](https://huggingface.co/datasets/ccace/chinese-exam-question-regions)、[标注规则](https://huggingface.co/datasets/ccace/chinese-exam-question-regions/blob/main/ANNOTATION_POLICY.md)、[格式说明](https://huggingface.co/datasets/ccace/chinese-exam-question-regions/blob/main/FORMAT.md)、[原始检测模型](https://huggingface.co/erayyapagci/yolo26n-question-segmentation-v2)。

数据集声明包含 2,360 页、12,431 个题目矩形框，覆盖九科；训练/验证/测试分别为 2,094 / 130 / 136 页，其中数学 911 页。按整卷及同场考试分组、保留续题 ID、同时提供像素与 YOLO 标签、记录来源哈希，这些设计适合题框任务。上述数量来自发布者，未独立核实为真实金标统计。

标注采用 AI 逐页生成和交叉复核，格式明确为 `human_verified=false`；没有说明人工审核金标子集，也没有独立标注准确率/漏标率。此次人工查看了六个测试集页面：数学 `b2-05e43f57ff92-p01`、`p02`，语文 `b2-cbefdf713429-p01`、`p03`，物理 `b3-22228d77166b-p01`、`p03`。所见题框总体贴合印刷题面，包含配图题和跨栏片段；这不证明整套数据的完整性、划分独立性、拍照适应性或手写召回率。

对本功能最关键的问题是：标注规则明确排除长空白答题区，所看页面也没有真实学生作答。用这些标注微调得到的是印刷题区检测器，可能恰好裁掉学生写答案的空白区。原始 YOLO 模型同样只有 `question` 类别。因此题框和作答区域需要分别核验，不能将二者等同。

结论：本次暂不将该数据集用于生产中文微调。抽样题面质量有参考价值，但尚不足以作为已核验的 OCR 兜底数据，更不适合单独用于作答裁图训练。没有生成中文权重、宣称准确率或进行本地基准测试。应用接入原模型，并接受后续中文 ONNX 模型；评分始终独立保留原始作答证据，不依赖题框是否覆盖答题空白。

后续采用前，应分层人工核验九科、单/双栏、共享阅读材料、跨页续题、拍照旋转卷、图表题，检查来源分组和跨集合近重复试卷；另建包含真实手写作答、空白答题区、涂改及教师批注的金标集。增加作答区域类别或专用检测器，不把题框标签改名充当作答框。训练时保留整卷分组，并在获准使用的训练机器上对独立核验集评估。

标注许可为 CC BY 4.0，原卷图片权利仍归各权利人。本报告判断标签质量与任务适配，不替代图片权利核验。
