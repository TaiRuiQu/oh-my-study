# 导入模式、限制与原件页面管理

[首页](Home-zh-CN.md) · [功能详解](Feature-Guide-zh-CN.md) · [English](Features-Import.md)

![学习流程：模型设置、本地导入、识别、对照原件、题目集合和 PDF 导出。](images/study-workflow.png)

## 上传前准备

先自行打开，解密加密 PDF，检查页序、小字、笔迹。支持 PDF、PNG、JPEG、WebP；不支持的办公文档应先在原应用转换。

限制分层：试卷与参考共 12 页、单文件 64 MiB、批次 256 MiB、图片 4,000 万像素，派生图片也有处理预算。小 PDF 仍可能超页数，单页图仍可能超尺寸。

## 模式是评分契约

空白模式提取题目，不创建作答，即使有参考答案。已作答模式保留作答并区分实际空白与无法辨认。教师模式优先明确教师结果，未知部分才用 AI。

例如红笔扣分符号缺少上下文时，不应凭空变成数值评分。满分未知不能捏造得分。参考答案用于证据匹配，不允许覆盖明确教师结果。

## 保存什么

导入复制原件到受管理资源，准备本地页图/缩略图，识别另行执行。外部文件断开不影响库内副本，但删除试卷会删除该副本。

PDF 页面保留原资源与原页号关系。旋转/排序写为元数据，不改写外部 PDF。

## 导入后管理页面

打开试卷 → 更多 → 管理页面，上下移动或左右旋转，检查后保存。只能删除未引用页，至少留一页。

引用包括题目来源、配图、学生区域、教师证据、小问、答案来源。被阻止时应检查该页用途，绕过会破坏定位。更改结构前结束处理任务。

## 识别没有完成

查看任务，不反复导入。原件已存与任务失败/取消可同时成立，修正错误后重识别，保留已有成果。源材料确有缺陷时获取更清晰副本，并在替换或重处理前安排编辑保护。

实现与参考: [MainWindow.Library.cs](../../src/OhMyStudy.App/MainWindow.Library.cs) · [LibraryAssets.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryAssets.cs) · [MainWindow.Documents.cs](../../src/OhMyStudy.App/MainWindow.Documents.cs)

---

[上一章](Features-Models-zh-CN.md) · [章节目录](Feature-Guide-zh-CN.md) · [下一章](Features-Workspace-Editing-zh-CN.md)
