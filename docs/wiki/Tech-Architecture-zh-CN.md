# 架构、构建约束与验证

[首页](Home-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Tech-Architecture.md)

![学习流程：模型设置、本地导入、识别、对照原件、题目集合和 PDF 导出。](images/study-workflow.png)

## 按边界分职责

Domain 定义文档/题目/评分有效性，Application 控制任务运行、暂停、提交、取消。Infrastructure 提供 SQLite、供应商、凭据、文件和进程。Documents 处理栅格化、坐标、公式、打印。App 转换用户操作并承载 Velopack 更新客户端。打包与发布脚本位于 `packaging/` 和 `.github/`，不再有独立的 Setup 源码项目。

因此错误分数由共享规则拒绝，不只某个输入框；测试也可不打开 WinUI 就检查供应商与存储。

## 跟踪一个操作

导入从 MainWindow.Library 收集模式/模型，进入媒体与存储。任务含输入与执行策略快照，JobQueue 驱动 IJobRunner，GradingRunner 请求模型和工具。保存经 JobContext 到仓储和领域校验，事件更新界面；导出从已存题进入文档服务。

每步是信任/持久化边界。模型没有数据库连接或任意保存回调，只能请求定义好的操作，参数由应用检查。

## 异步不等于全面并发

数据库单所有者线程，AI 单 worker，媒体/阅读各有服务和取消。敏感修改串行，重活移出 UI，不承诺任意识别并行。

MainWindow 分部按功能组织，结合视图模型，不宣称整个应用是纯声明式 MVVM。

## AOT 与依赖

global.json 固定 SDK，包集中管理并按发布配置锁定。源生成 JSON 列举类型含多态内容，不靠运行时任意发现类型。

原生 PDF、公式、打印、图像、计算组件需一同分发，便携是目录不是单 EXE。AOT、自包含、框架依赖改变运行时要求，不改领域概念。

## 按风险验证

| 边界 | 验证 |
| --- | --- |
| 分数/内容 | 领域、批改可执行测试。 |
| 数据/升级 | 存储、集成、迁移检查。 |
| 协议 | 供应商解析/用量测试。 |
| 执行 | 执行与原生运行时检查。 |
| 输出 | 媒体/文档、探针、视觉验收。 |
| 安装 | Velopack 软件包和更新回归检查。 |

CI 入口分 lint/check/test/build，普通分支/PR 使用托管供应商和评分 SDK 套件，标签/手动运行启用完整 Native AOT 验证并构建全部三种版本。桌面与物理打印另验。应报告实际运行情况，不能把有测试当已通过。

源码: [OhMyStudy.slnx](../../OhMyStudy.slnx) · [Directory.Packages.props](../../Directory.Packages.props) · [global.json](../../global.json) · [Invoke-CI.ps1](../../packaging/Invoke-CI.ps1)

---

[章节目录](Tech-for-nerds-zh-CN.md) · [下一章](Tech-Document-Pipeline-zh-CN.md)
