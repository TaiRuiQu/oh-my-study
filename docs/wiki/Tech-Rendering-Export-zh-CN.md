# 原生阅读、公式资源与受控 PDF 编译

[首页](Home-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Tech-Rendering-Export.md)

![PDF 版式：A4 纵向与 A3 横向双栏，题目版和答案版分别导出。](images/pdf-layouts.png)

## 显示与打印共享内容，不共享截图

领域块适配原生控件与 ExamDocument。屏幕可响应主题，打印固定纸型，不截当前窗口，缩放不等于打印比例。

文字、公式、表格、区域图独立，公式源保留供编辑/回退，小问不压成整图。

## 原生公式路径

VelociTex 负责阅读和打印 SVG。转换失败或过宽可保留源码、给诊断，不静默删除。这是保留证据，不保证任意 LaTeX 教科书排版，编译成功仍需检查。

屏幕阅读通过 `FormulaLineBreaks` 在适用公式的顶层运算符处拆分，保持括号和花括号组完整。不支持拆分的结构保留整体，不可拆分的部分适应可用宽度，复制仍使用完整原公式源码。此行为调整响应式阅读，不改变固定 PDF 版式。

## 受控打印模型

界面取当前显示已存题，拒绝快捷草稿，连续编号，将分值与题干同行打印，不再插入原题号段落。有生成图片时使用受管理资源，否则从原页裁图，并进行去重、预算、题干与评分项手写检查。

服务支持 A4 或 A3，栏数独立设置，题目版默认紧凑排版并省略答案/解析文字。可选主观题作答区根据答案长度、换行、分值与栏宽估算高度；另一独立选项通过测量已分页内容，将页面或栏内余量均分到这些区域。答案版使用标准答案/解释，不用 StudentAttempt 当键。

## 固定模板编译数据

Typst 由应用生成，用户/模型文字转义，图路径为生成资源，不接受任意路径。模板控几何、字体、编号、小问。

编译专用根、空包/缓存、清环境覆盖、进程超时、输出有界，不接受用户可执行模板，图和公式在受控区生成。

## 目标与诊断

界面先读完整 PDF，再写 picker 目标，将编译不全与最终写分离，但不承诺通用文件事务。

打印正文用 Windows NSimSun 重点支持中英，界面阅读默认使用界面无衬线字体，公式有独立轮廓。日韩界面不等于额外打印字体。

结构/契约用文档和媒体测试，再看实际字形、分式、长公式、表格、裁图、分页换栏。打印机设置不属编译器。

源码: [NativeQuestionPresenter.cs](../../src/OhMyStudy.App/Services/NativeQuestionPresenter.cs) · [VelociTexFormulaRenderer.cs](../../src/OhMyStudy.Documents/VelociTexFormulaRenderer.cs) · [TypstDocumentService.cs](../../src/OhMyStudy.Documents/TypstDocumentService.cs) · [MainWindow.Documents.cs](../../src/OhMyStudy.App/MainWindow.Documents.cs)

---

[上一章](Tech-Storage-Recovery-zh-CN.md) · [章节目录](Tech-for-nerds-zh-CN.md)
