# 原生工程实施与待验事项

更新：2026-10-18。本文反映当前代码和验证范围；详细阶段任务与通过条件仍以 [migration.md](../migration.md) 为准，最新交接与原始证据见 [migration-progress.md](../migration-progress.md)。实现已经进入生产业务链路，阶段验收不能因代码或合成检查存在而自动关闭。

| 范围 | 当前实现与本机证据 | 尚需独立验证 |
| --- | --- | --- |
| P0 基线 | 固定旧起点、独立旧 SQL/JSON/协议夹具、行为边界 | 维护对照证据，不把旧未实现功能视为完成 |
| P1 工程与路线 | WinUI 自包含 Native AOT、资源发布、原生公式/PDF；旧实现不在构建图 | 干净 Windows 系统及完整兼容矩阵 |
| P2 领域 | 完整 DTO/JSON、评分、来源、覆盖、配置和权限摘要；AOT 107 断言 | 持续保持跨语言契约与边界回归 |
| P3 存储 | 生产仓储、v1/v2、资产/凭据协调、在线备份恢复；AOT 62 检查 | 真实资料/凭据与部署身份、磁盘耗尽/断电、旧进程竞态 |
| P4 队列与执行 | 单任务队列、事件、权限、取消提交屏障、Job Object 和私有 Python 真实计算；AOT 10 场景 | 真实 Sandbox 生命周期与会话归属 |
| P5 协议与整卷 | 三协议原生思考/SSE/工具、三模式整卷及保存；协议 10 场景、整卷 AOT 211 断言 | 真实供应商与样卷的漏题、串答和评分语义 |
| P6 文档与媒体 | 真实图片/PDF、WebP 私有回退、裁切/旋转/坐标、缓存；AOT 26 检查 | 目标系统资源预算、复杂排版、物理打印 |
| P7 应用流程 | 导入、参考答案、任务、编辑、题库错题、设置、页管理、备份恢复；隔离 12 页流程 17 检查 | 文件对话框/拖放/导航与关闭竞争等更多人工场景 |
| P8 体验与性能 | 原生标题栏、主题、编译绑定、局部更新；已记录流程内存/缓存观察 | DPI、输入法、读屏、高对比、冷启动/帧时间和参考机预算 |
| P9 发布与切换 | 同轮八分项通过，最终 portable ZIP 哈希、解压、包内双自检通过；依赖锁/许可证/源码快照与清单齐备 | 干净离线系统、安装升级/卸载、签名、分发审核、真实切换和回退 |

各数字来自不同检查层级，不能合并为识别正确率或完整发布验收。Execution 的合成进程边界不等于真实 Sandbox；媒体合成像素与整卷假响应也不等于真实样卷语义。

## 当前执行方式

从根目录使用 `packaging/Invoke-MigrationChecks.ps1` 串行进行 Release 锁定还原、全新 Native AOT 发布和实际运行。控制台覆盖 Domain、Storage、Provider、Execution、Grading、Media 与 Documents；App 桌面自检可通过 `-DesktopSmoke -WorkflowSmoke` 显式启用。准备好的私有 Python 会通过 Execution 的 `--runtime` 参数单列真实计算检查，缺少运行时则报告未执行。

```powershell
./packaging/Test-RepositoryLayout.ps1
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke
./packaging/Publish-OfflinePackage.ps1 -DesktopSmoke -WorkflowSmoke
```

脚本使用 `output/migration/` 的缓存和每轮唯一证据目录。仅编译、JIT 运行、旧产物重用或没有执行的检查不得记为 Native AOT 场景通过。`Invoke-NativeChecks.ps1` 保留为早期 P1 探针入口，不应替代当前业务套件。

## 接续顺序

1. 保留本轮完整流水线及最终包内样板桌面、隔离仓储流程和私有 Python 计算证据；对后续功能修复重新运行相关场景。
2. 继续审查已生成候选的分发许可、源码对应性与签名；本机 ZIP 校验及双自检通过后，报告仍保持 `release-candidate-unvalidated`。
3. 在无开发 SDK、系统 Node/Python/TeX 的干净 Windows 10/11 矩阵验证启动、导入、任务、预览、导出、升级与恢复。
4. 分别开展真实资料/凭据与安装身份、真实供应商样卷、Sandbox、物理打印及 DPI/辅助功能验证。
5. 在固定参考设备测冷启动、交互延迟、帧时间、空闲/峰值内存与资源回收；当前单次流程私有字节 324759552 和缓存约 51.8 MB 不代表预算通过。

旧资料切换必须由用户显式选择，先关闭旧版、创建包含 WAL 与原件的一致备份，再恢复到新目录并验证。合作实例 lease 不会锁住旧版。禁止覆盖打开的库或默认承诺新版写回后旧版仍可读；恢复边界见 [P3](migration/p3-storage.zh-CN.md)，候选分发边界见 [P9](migration/p9-release.zh-CN.md)。

完整标签、高级筛选、按日期选题、复习卷生成与独立校验、手机传图继续保留独立产品待办。当前 A3/A4 导出只处理可完整表达的内容，图片、表格、多层小问或不支持公式会明确拒绝，不能以截断内容来宣称完成。

用户界面始终遵守[设计原则](design-principles.zh-CN.md)：只展示任务、内容、操作和结果，保存失败、输入限制和恢复方法用普通语言说明。架构、引擎、测试规模和迁移证据留在维护文档。窗口拖动与系统按钮使用系统能力，不添加尚未实现的搜索或账户入口。
