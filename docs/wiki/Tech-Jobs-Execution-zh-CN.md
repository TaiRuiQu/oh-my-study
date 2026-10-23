# 任务生命周期、重试恢复与代码隔离

[首页](Home-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Tech-Jobs-Execution.md)

## 队列与持久提交

JobQueue 使用有界通道和单个 AI 工作器。JobContext 协调进度、活动、提交、取消与授权执行。评分写入前，仓储检查活动任务状态及文档页面、顺序、旋转快照。

取消停止后续工作并保留已成功提交。启动时将未完成持久任务标记为中断，不重放模型会话和旧授权。

## 显式任务重试

仅失败或中断任务可重试。根据记录的修改前后状态，回滚本轮题目、参考答案、试卷信息、生成资源和任务文件，然后原子替换为排队的新任务。旧任务的用量与日志记录移除，新任务重新选择模型设置并获得新的执行策略。

试卷忙、已有更新评分轮次、已开始但缺少恢复记录、或回滚会覆盖已编辑题目时，仓储拒绝重试。已有题目的笔记保留。被拒绝时使用重新识别保留当前结果。供应商费用及本机代码在任务目录外的修改无法回滚。

## 授权与执行状态

许可代理将决定绑定到不可变调用快照和摘要，授权仅消费一次，取消会撤销授权，任务级许可不跨重启。工作目录由运行器提供。

默认直接在本机运行；询问模式先审批再在本机运行。沙箱模式中 Python 使用任务专属 AppContainer，PowerShell 每次使用独立 AppContainer；可访问输入副本、只读私有运行时和专属可写结果目录。网络默认关闭。请求/响应身份、nonce 与摘要检查将结果绑定到获准调用。

Python 的导入、变量、函数和图片在任务内保留，普通异常不清空已执行赋值；超时、取消、输出超限或工作进程退出会重置命名空间。PowerShell 共享文件，不共享进程变量。生成的 PNG/JPEG/WebP 可成为题目受管理资源，原件证据仍保留。

提交前检查沙箱可用性，并说明拟采用的本机回退。可用沙箱内执行失败不静默改用本机。进程清理有界，清理失败可能需重启。供应商请求独立于沙箱网络许可。

源码：[队列](../../src/OhMyStudy.Application/Jobs/JobQueue.cs)、[重试回滚](../../src/OhMyStudy.Infrastructure/Storage/LibraryJobRetry.cs)、[许可](../../src/OhMyStudy.Application/Execution/PermissionBroker.cs)、[Python 会话](../../src/OhMyStudy.Infrastructure/Execution/PythonSessionExecutor.cs)、[沙箱](../../src/OhMyStudy.Infrastructure/Execution/SandboxToolExecutor.cs)。
