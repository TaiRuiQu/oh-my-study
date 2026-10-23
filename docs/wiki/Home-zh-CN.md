# Oh My Study 维基

[English](Home.md) · **简体中文**

Oh My Study 将试卷 PDF 和图片整理为保存在本地、可以编辑的题库与错题集。你可以对照原件检查结果、纠正识别和批改内容，再导出复习 PDF。

![学习流程：模型设置、本地导入、识别、对照原件、题目集合和 PDF 导出。](images/study-workflow.png)

每种语言有 **20 篇专题章节**，另有分区总览。插图为概念示意，不是截图，也不代表精确屏幕或打印布局。

## 快速开始

[快速开始](Quick-Start-zh-CN.md) 分四篇教程：[准备模型](Quick-Start-Setup-zh-CN.md)、[识别试卷](Quick-Start-First-Exam-zh-CN.md)、[核对修正](Quick-Start-Verify-zh-CN.md)、[复习、导出与备份](Quick-Start-Review-Export-zh-CN.md)。总览保留所有功能速查。

主要流程是 **配置模型 → 导入试卷 → 查看识别进度 → 核对并编辑 → 复习错题 → 导出**。已保存的资料存放在资料库中；AI 处理会将材料发送给所选供应商，并可能产生费用。

## 详细指南

[功能详解](Feature-Guide-zh-CN.md) 介绍模型设置、三种导入模式、原件阅读、结构化编辑、参考答案、重新识别、任务进度、执行权限、费用、导出、资料库管理、外观和故障处理。

九篇指南分别给出字段、步骤、例子、边界和恢复建议。可从[模型与费用](Features-Models-zh-CN.md)开始，或直接查看[编辑](Features-Workspace-Editing-zh-CN.md)、[任务](Features-Tasks-zh-CN.md)、[PDF](Features-Export-zh-CN.md)、[资料库恢复](Features-Library-zh-CN.md)。

下载选择、升级和卸载请参阅[安装指南](../installation.zh-CN.md)。

## Tech for nerds

[技术内幕](Tech-for-nerds-zh-CN.md) 按完整流程解释各功能的设计与实现：文档读取、模型工具调用、校验、评分、任务执行、存储、凭据、数学公式、PDF 生成与安装打包，并提供源码链接。

七篇章节串联[架构](Tech-Architecture-zh-CN.md)、[文档处理](Tech-Document-Pipeline-zh-CN.md)、[模型协议](Tech-Model-Loop-zh-CN.md)、[评分不变量](Tech-Validation-Grading-zh-CN.md)、[执行](Tech-Jobs-Execution-zh-CN.md)、[存储](Tech-Storage-Recovery-zh-CN.md)、[渲染](Tech-Rendering-Export-zh-CN.md)。

本维基描述仓库当前实现，较早版本可能不同。AI 任务完成表示通过了处理检查，并不保证识别或批改正确。请对照原件核查不清晰的文字、公式、分数和参考答案匹配关系。

[源代码](https://github.com/TaiRuiQu/oh-my-study) · [发布版本](https://github.com/TaiRuiQu/oh-my-study/releases) · [反馈问题](https://github.com/TaiRuiQu/oh-my-study/issues)

## 仓库维护与近期变化

Wiki 现与代码一起维护于 `docs/wiki`，完整章节见[目录](_Sidebar.md)，来源见[迁移说明](README.md)。

当前功能包括公共题干/有序评分项、独立题目阅读页、本地文字与可选[语义搜索](Features-Search-zh-CN.md)、[任务重试恢复](Features-Tasks-zh-CN.md)、批量读页/存题、一小时文件上传复用、持久 Python、生成学习图片以及 SQLite v5，旧发行版可能不同。
