# 功能详解

[首页](Home-zh-CN.md) · [快速开始](Quick-Start-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Feature-Guide.md)

## 按工作流阅读

- [供应商、模型、思考、上传与费用](Features-Models-zh-CN.md)
- [导入模式、限制与页面管理](Features-Import-zh-CN.md)
- [阅读、导航与结构化编辑](Features-Workspace-Editing-zh-CN.md)
- [文字与语义搜索](Features-Search-zh-CN.md)
- [参考答案、评分证据与重新识别](Features-References-zh-CN.md)
- [任务进度、重试与执行请求](Features-Tasks-zh-CN.md)
- [PDF 版本、版式、图片与公式检查](Features-Export-zh-CN.md)
- [资料库位置、备份、恢复与删除](Features-Library-zh-CN.md)
- [故障处理、外观与语言](Features-Troubleshooting-zh-CN.md)

## 当前行为速查

先配置 Responses 或 Messages 供应商和模型，再导入 PDF、PNG、JPEG、WebP。试卷、补充作答与参考答案合计最多 12 页，单源文件 64 MiB、单批 256 MiB、单张输入图 4,000 万像素。自动识别让模型自行判断试卷类型；空白试卷无作答，待批改试卷保留学生作答，教师模式保护明确教师评分。

阅读页先显示公共题干，再显示扁平且分别评分的项目。可本地搜索题目，或独立配置向量/重排服务进行语义搜索。待确认须另行检查，仅不确定性不代表确定错误。

识别批量读页和存题。失败/中断任务可通过回滚检查后重试；重新识别保留当前结果再开始新轮次。导出前保存修改和笔记，分别导出题目版与答案版，支持 A4 纵向或 A3 横向。本地导出不调用模型；AI 识别与语义搜索会发送数据到配置服务并可能计费。

代码默认不逐次询问而在本机运行，新任务前可改为每次询问或沙箱运行。Files API 默认关闭，兼容上传请求一小时过期并在任务结束尝试删除，详见模型和任务章节。

资料库保存在本地，当前数据库 v6。升级或大量重识别前退出并备份整个目录。密钥保存在 Windows 凭据管理器。另见[安装](../installation.zh-CN.md)、[使用指南](../usage.zh-CN.md)、[模型提示配置](../model-prompts.zh-CN.md)。
