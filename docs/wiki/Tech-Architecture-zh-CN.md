# 架构、构建约束与验证

[首页](Home-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Tech-Architecture.md)

![学习流程：模型设置、本地导入、识别、对照原件、题目集合和 PDF 导出。](images/study-workflow.png)

## 按边界分职责

Domain 定义文档/题目/评分有效性，Application 控制任务运行、暂停、提交、取消。Infrastructure 提供 SQLite、供应商、凭据、文件和进程。Documents 处理栅格化、坐标、公式、打印。App 转换用户操作并承载 Velopack 更新客户端。打包与发布脚本位于 `packaging/` 和 `.github/`，不再有独立的 Setup 源码项目。

因此错误分数由共享规则拒绝，不只某个输入框；供应商与存储的行为也不依赖 WinUI。

## 跟踪一个操作

导入从 MainWindow.Library 收集模式/模型，进入媒体与存储。任务含输入与执行策略快照，JobQueue 驱动 IJobRunner，GradingRunner 请求模型和工具。保存经 JobContext 到仓储和领域校验，事件更新界面；导出从已存题进入文档服务。

每步是信任/持久化边界。模型没有数据库连接或任意保存回调，只能请求定义好的操作，参数由应用检查。

## 异步不等于全面并发

数据库保留单所有者线程，任务队列一次处理一份试卷；试卷内的独立题目按配置上限并行解答与评分。Python 计算经过共享执行闸门，数据库提交仍串行；媒体和阅读保留各自取消边界。

MainWindow 分部按功能组织，结合视图模型，不宣称整个应用是纯声明式 MVVM。

## AOT 与依赖

global.json 固定 SDK，包集中管理并按发布配置锁定。源生成 JSON 列举类型含多态内容，不靠运行时任意发现类型。

原生 PDF、公式、打印、图像、计算组件需一同分发，便携是目录不是单 EXE。AOT、自包含、框架依赖改变运行时要求，不改领域概念。

## 构建与检查

CI 仅在推送新标签时触发，各 publish job 直接构建、打包并发布 Native AOT、自包含或框架依赖版本。自动测试、回归套件、冒烟启动模式和 CI Lint 已移除。本地空白检查的时间上限为 30 秒。界面和 PDF 改动使用真实内容进行视觉检查。

源码: [OhMyStudy.slnx](../../OhMyStudy.slnx) · [Directory.Packages.props](../../Directory.Packages.props) · [global.json](../../global.json) · [Invoke-CI.ps1](../../packaging/Invoke-CI.ps1)

---

[章节目录](Tech-for-nerds-zh-CN.md) · [下一章](Tech-Document-Pipeline-zh-CN.md)
