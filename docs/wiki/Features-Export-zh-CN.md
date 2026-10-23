# PDF 版本、版式、配图与公式检查

[首页](Home-zh-CN.md) · [功能详解](Feature-Guide-zh-CN.md) · [English](Features-Export.md)

![PDF 版式：A4 纵向与 A3 横向双栏，题目版和答案版分别导出。](images/pdf-layouts.png)

## 确定范围

打开目标试卷、题库或错题，保存后导出并读数量。范围为当前显示的已存题及小问，不只焦点行。

服务支持 1–500 道顶层题，还有文字/图预算，单题有效也可能需分批。

## 选择版本

| 版本 | 内容 | 用途 |
| --- | --- | --- |
| 题目 | 题干、选项、配图、小问、作答空间 | 不展示答案的练习。 |
| 答案与解析 | 题目和标准答案、解释 | 检查与引导复习。 |

学生作答不是答案键。识别答案仍需人工核实。导出连续编号，正文保留原印刷题号。

## 选择纸型

A4 纵向，A3 横向双栏。图为组织示意，不精确预测分页；长题、大图、公式会影响换页。

题目和答案分别导出。用 PDF 阅读器打印时，还需检查打印机纸型与缩放。

## 正确处理配图

配图优先使用受管理生成图片，否则来自库内原页裁片，核对坐标轴、单位、图例齐全，不带邻题答案。相同解码图片去重，并限制总量。

题目版阻止有手写标记的图（含小问），避免已知学生作答进入练习。改标记不清像素，应使用实际无笔迹区域；没有干净来源时需有意识调整内容，不宣称应用能去手写。

## 公式回退与失败

支持的 LaTeX 转为打印资源，不支持或过宽时可能保留源码并警告。PDF 写成功不证明公式保真，核对分数、负号、根式、上下标、矩阵与换栏。

失败先保存，再查区域/手写，缩小批次、检查目标。缺损本地工具需修复或完整重解压。导出不调用模型、不花模型 token。

实现与参考: [MainWindow.Documents.cs](../../src/OhMyStudy.App/MainWindow.Documents.cs) · [TypstDocumentService.cs](../../src/OhMyStudy.Documents/TypstDocumentService.cs) · [ExamDocument.cs](../../src/OhMyStudy.Documents/ExamDocument.cs)

---

[上一章](Features-Tasks-zh-CN.md) · [章节目录](Feature-Guide-zh-CN.md) · [下一章](Features-Library-zh-CN.md)
