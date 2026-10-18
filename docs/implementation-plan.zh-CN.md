# 原生工程实施与待验事项

更新：2026-10-18。本文反映当前代码和验证范围；详细阶段任务与通过条件仍以 [migration.md](../migration.md) 为准，最新交接与原始证据见 [migration-progress.md](../migration-progress.md)。实现已经进入生产业务链路，阶段验收不能因代码或合成检查存在而自动关闭。

| 范围 | 当前实现与本机证据 | 尚需独立验证 |
| --- | --- | --- |
| P0 基线 | 固定旧起点、独立旧 SQL/JSON/协议夹具、行为边界 | 维护对照证据，不把旧未实现功能视为完成 |
| P1 工程与路线 | WinUI 自包含 Native AOT、资源发布、原生公式/PDF；旧实现不在构建图 | 干净 Windows 系统及完整兼容矩阵 |
| P2 领域 | 完整 DTO/JSON、评分、来源、覆盖、配置和权限摘要；AOT 107 断言 | 持续保持跨语言契约与边界回归 |
| P3 存储 | 生产仓储、v1/v2、资产/凭据、在线备份、仅副本升级与指纹验证；AOT 93检查 | 用户负责真实资料/凭据与部署身份、磁盘耗尽/断电、旧进程竞态 |
| P4 队列与执行 | 单任务、权限、提交屏障、Job Object、私有Python与独立调用VM；AOT 17组 | 用户负责真实Sandbox会话、映射、系统与资源矩阵 |
| P5 协议与整卷 | 三协议原生思考/SSE/工具、三模式整卷及保存；协议11场景、整卷AOT 211断言 | 用户负责真实供应商与样卷的漏题、串答和评分语义 |
| P6 文档与媒体 | 图片/PDF/WebP、裁切/旋转、分辨率缓存；Media 26检查；复杂递归导出8份45页PDF | 用户负责目标系统资源预算、授权材料和物理打印 |
| P7 应用流程 | 原生复杂编辑、参考答案、任务、题库错题、页管理、备份恢复、阅读上下文和切库失败恢复；隔离工作流28项 | 用户负责文件选择/拖放、实体输入与连续人工场景 |
| P8 体验与性能 | 标题栏、主题、焦点、局部更新；三次隔离启动及页面/滚动/缩放/分发器/内存原始采样 | 用户负责DPI、IME、读屏、高对比、GPU/ETW和8GB参考机预算；释放后内存略超200MiB |
| P9 发布与切换 | Native AOT Setup安装/升级/卸载与回退40项；离线包清单、运行时、许可证和源码快照 | 用户负责干净系统、真实安装和切换回退、许可与分发审核；本轮不签名 |

各数字来自不同检查层级，不能合并为识别正确率或完整发布验收。Execution的控制器/worker与合成进程边界不等于真实Sandbox；媒体合成像素与整卷假响应也不等于真实样卷语义。所有外部验收由用户负责，未执行或未通过项不补记为通过。

## 当前执行方式

从根目录使用`packaging/Invoke-MigrationChecks.ps1`串行进行Release锁定还原、全新Native AOT发布和实际运行。控制台覆盖Domain、Storage、Provider、Execution、Grading、Media、Documents及Setup安装回归；App桌面自检通过`-DesktopSmoke -WorkflowSmoke`显式启用。准备好的私有Python通过Execution的`--runtime`参数单列真实计算检查，缺少运行时则报告未执行。

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
2. 保留安装器40项回归与候选包证据；本轮不签名，状态为`release-candidate-user-acceptance-pending`，分发许可、源码对应性与来源信任由用户验收。
3. 在无开发 SDK、系统 Node/Python/TeX 的干净 Windows 10/11 矩阵验证启动、导入、任务、预览、导出、升级与恢复。
4. 分别开展真实资料/凭据与安装身份、真实供应商样卷、Sandbox、物理打印及 DPI/辅助功能验证。
5. 使用`packaging/Measure-NativePerformance.ps1`保留自动采样原始报告。本轮三次顺序启动P95为636.3232ms，空库约104–106MiB，释放后约201–202MiB，略超200MiB，不能算预算通过。GPU/ETW、真实冷/热启动与8GB参考设备继续由用户测量。

旧资料切换必须由用户显式选择，先关闭旧版、创建包含 WAL 与原件的一致备份，再恢复到新目录并验证。合作实例 lease 不会锁住旧版。禁止覆盖打开的库或默认承诺新版写回后旧版仍可读；恢复边界见 [P3](migration/p3-storage.zh-CN.md)，候选分发边界见 [P9](migration/p9-release.zh-CN.md)。

完整标签、高级筛选、按日期选题、复习卷生成与独立校验、手机传图继续保留独立产品待办。A3/A4支持图片裁片、表格、选项和递归小问；未知公式保留完整原文，缺失图片/无效结构/超限明确拒绝。复杂导出基础不等于M4复习卷功能完成。

Sandbox采用每次调用独立VM的新生命周期，以明确会话ID证明归属，不跨调用保留任意环境状态。Windows10或缺少新wsb CLI的系统不能标记Ready；任务开始前可明确冻结本机回退，执行中的Sandbox失败不在宿主重放。旧原型未完成的同任务环境复用不再作为现有行为承诺。

用户界面始终遵守[设计原则](design-principles.zh-CN.md)：只展示任务、内容、操作和结果，保存失败、输入限制和恢复方法用普通语言说明。架构、引擎、测试规模和迁移证据留在维护文档。窗口拖动与系统按钮使用系统能力，不添加尚未实现的搜索或账户入口。
