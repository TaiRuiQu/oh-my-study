# 技术内幕

[首页](Home-zh-CN.md) · [快速开始](Quick-Start-zh-CN.md) · [功能详解](Feature-Guide-zh-CN.md) · [English](Tech-for-nerds.md)

## 沿实现阅读

- [架构、构建与验证](Tech-Architecture-zh-CN.md)
- [文档准备、页面身份与坐标](Tech-Document-Pipeline-zh-CN.md)
- [供应商适配、批量工具、流与用量](Tech-Model-Loop-zh-CN.md)
- [评分项、评分约束与编辑保护](Tech-Validation-Grading-zh-CN.md)
- [任务生命周期、重试恢复与代码隔离](Tech-Jobs-Execution-zh-CN.md)
- [SQLite v6、资源、搜索索引与恢复](Tech-Storage-Recovery-zh-CN.md)
- [原生阅读、公式资源与 PDF 编译](Tech-Rendering-Export-zh-CN.md)

## 项目边界

| 项目 | 职责 |
| --- | --- |
| Domain | 类型化题目/评分项、评分、供应商契约、校验、源生成 JSON。 |
| Application | 任务队列、授权、编排、搜索契约与服务。 |
| Infrastructure | SQLite、凭据、供应商 SDK、文件上传、评分工具、进程、搜索索引。 |
| Documents | PDFium 栅格化、坐标、VelociTex 数学、Typst 导出。 |
| App | WinUI 工作区、结构化编辑、列表/详情导航、本地化、Velopack 更新客户端与视觉冒烟。 |
| 打包与 CI | `packaging/` 和 `.github/` 中的 Velopack 打包及发布脚本，生成 Setup、便携 ZIP 和更新包。 |

## 处理与持久化

导入校验限制并保留原件，再将一份试卷放入一个模型会话。模型调用 `read_pages`、`save_references`、`save_paper_info`、`save_questions`、可选 `python`/`shell` 与 `finish`。仅参考任务不允许写题目/试卷信息。每次保存均经过类型校验、活动任务和取消边界。

题目结构为公共题干与有序扁平 `QuestionItem`。参考引用评分项键，覆盖引用题目键，生成图片保留原件证据。数据库当前 v6。显式重试仅在恢复检查允许时回滚失败轮次；重新识别保留现有结果及受保护手动/教师评分。

识别、文件上传、可选后台向量化与重排具有不同数据路径。[语义搜索](Features-Search-zh-CN.md) 使用缓存向量、本地余弦扫描与可选远端重排。供应商/搜索密钥在库外 Windows 凭据管理器中。

## 构建与验证

技术栈为 C# 14、.NET 10、WinUI，使用 [global.json](../../global.json) 固定的 SDK 10.0.402，集中管理版本及锁文件。Native AOT/裁剪要求源生成 JSON 和明确支持的契约。

按 [CI 设置](../../.github/actions/setup-native/action.yml) 与[工作流](../../.github/workflows/ci.yml)准备：

```powershell
dotnet restore OhMyStudy.slnx -p:Configuration=Release --locked-mode
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
./packaging/Invoke-CI.ps1 -Phase Lint
./packaging/Invoke-CI.ps1 -Phase Checks
./packaging/Invoke-CI.ps1 -Phase Test
```

可执行测试覆盖领域、存储、供应商、执行、评分、媒体/文档、集成、打包。普通分支/PR 使用托管供应商/评分 SDK 测试；标签/手动运行执行完整 Native AOT 验证并打包三种版本。应用冒烟提供独立视觉证据，打印仍需检查实际输出。见[架构](Tech-Architecture-zh-CN.md)和[安装](../installation.zh-CN.md)。
