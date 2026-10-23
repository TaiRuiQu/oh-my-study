# 供应商、模型、思考与费用配置

[首页](Home-zh-CN.md) · [功能详解](Feature-Guide-zh-CN.md) · [English](Features-Models.md)

![数据边界：资料库保留原件与记录，供应商接收图片与上下文，可选代码遵循独立执行策略。](images/data-boundaries.png)

## 区分供应商与模型

供应商是连接配置：名称、API 类型、地址、凭据引用。模型是该连接下的配置：精确 ID、显示名称、上下文信息、图片/工具能力、思考、价格、货币。同一 ID 在不同供应商处可能端点和价格不同，开始前核对组合。

先保存供应商再添加模型。导入模型获取服务列出的 ID；服务未列出已知模型时可手动填。列表成功不代表支持试卷图片或可靠工具调用。

## 核对关键字段

| 字段 | 使用方法 |
| --- | --- |
| ID | 精确服务标识，不用显示名称替代。 |
| 显示名称 | 可选易读标签。 |
| 上下文窗口 | 已知时填正整数元数据，不保证任意试卷都能放下。 |
| 图片/工具能力 | 按实际端点确认，不确定就保留未知。 |
| 思考 | 只保存支持的档位/预算，启动时从中选择。 |
| 价格 | 所选货币的每百万 token 非负单价。 |

Messages 预算思考至少 1,024 token 整数。两种协议表达不同，熟悉的档位名不证明模型支持。

## 将目录信息当草稿

从 models.dev 填充按精确 ID 查找，保存前检查能力、价格单位、货币和窗口。目录可能描述上游而非转售商条款，填充与保存是不同动作。

测试连接发送小请求，能发现错误密钥、端点、ID、选项，但不能验证手写识别和批改质量；需用已知小样本检查。

## 正确读费用

估算需普通输入、缓存读、缓存写、输出数量，各用独立单价，缺失缓存统计不能当零。

例如未缓存输入 1,000 token、单价每百万 2，输出 500 token、单价 8，且缓存数量明确为零，合计 0.006 个货币单位。必要用量缺失时无估算是合理结果。人民币换算是带日期的参考，有时使用缓存；供应商账单为准。

## 维护配置

切换选项前保存表单。删除模型/供应商保留旧资料。换 Windows 账户需重新填写凭据，资料库只带配置引用而非可迁移密钥。求助截图不包含密钥。

实现与参考: [MainWindow.Models.cs](../../src/OhMyStudy.App/MainWindow.Models.cs) · [MainWindow.ModelImport.cs](../../src/OhMyStudy.App/MainWindow.ModelImport.cs) · [ProviderProtocol.cs](../../src/OhMyStudy.Infrastructure/Providers/ProviderProtocol.cs)


## 输出上限与提示配置

按模型设置 **最大输出 token (max_tokens)** 可覆盖新任务自动上限，Responses 使用 `max_output_tokens`，Messages 使用 `max_tokens`，后续轮次保持一致。填写支持的正整数，固定思考预算须低于输出上限。从目录填充保留此覆盖值。

识别每任务选择一次本地提示配置；未知版本使用家族通用配置，未知名称使用通用提示。见[配置与来源](../model-prompts.zh-CN.md)。

## 可选 Files API

仅当供应商支持图片文件 ID 及必要过期契约时启用 **支持 Files API**，默认关闭，仅影响新任务。按需上传页面、裁片、生成图片，相同图片每任务只上传一次，不降低分辨率。

Responses 上传使用 `purpose=user_data`（包括 DeepSeek），并请求创建后一小时过期；Messages 使用独立上传契约。完成、失败、取消后均尝试删除。拒绝上传时明确失败，不创建永久文件或静默回退内联。

文件 ID 减少传输量，不免除图片 token 限制或可能的存储费用。见 [DeepSeek Files API](https://api-docs.deepseek.com/guides/files_api/) 和 [OpenAI 上传](https://developers.openai.com/api/reference/resources/files/methods/create)。向量/重排在[语义搜索](Features-Search-zh-CN.md)独立配置。

---

[章节目录](Feature-Guide-zh-CN.md) · [下一章](Features-Import-zh-CN.md)
