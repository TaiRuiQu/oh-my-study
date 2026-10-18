# Oh My Study

面向学生的原生 Windows 学习应用，用于整卷整理与批改、原件对照、题库和错题。主体采用 **C# / .NET 10 / WinUI 3 / Fluent Design 2**，以 **Native AOT** 发布。

当前已接通资料导入与保存、三种导入模式、三协议整卷任务、权限审阅、复杂题目编辑、来源定位、题库与错题、备份恢复、递归图文表格 PDF 导出，以及按用户安装、升级和卸载的原生离线安装器。各层已有实际 Native AOT 合成回归；真实模型语义、干净 Windows、真实 Sandbox、物理打印和辅助功能等外部验收由用户负责，尚未通过。准确状态见 [migration-progress.md](migration-progress.md)，迁移约束见 [migration.md](migration.md)。

## 工程

```text
OhMyStudy.slnx
src/
  OhMyStudy.App/             # WinUI 界面与应用组合
  OhMyStudy.Domain/          # 领域模型、规则与 JSON 契约
  OhMyStudy.Application/     # 任务队列、权限与用例契约
  OhMyStudy.Infrastructure/  # SQLite、资产、凭据、协议与执行
  OhMyStudy.Documents/       # 公式、PDF、字体、坐标与缓存
  OhMyStudy.Setup/           # 按用户离线安装、升级、启动与卸载
tests/                      # 可执行的 Native AOT 回归与独立夹具
runtime/                    # 私有 Python 的任务入口
probes/                     # 文档验证与工具锁
packaging/                  # 准备、验证与离线候选包脚本
docs/                       # 架构、约定和验证证据
.reference/                 # 忽略的旧实现；构建与发布不依赖
```

## 构建与验证

开发环境为 Windows x64、PowerShell 7、**.NET SDK 10.0.402**、MSVC x64 Build Tools 与 Windows SDK。工程、包与原生工具版本由仓库锁文件固定。

```powershell
./packaging/Test-RepositoryLayout.ps1

# 首次明确准备锁定的原生工具和私有计算运行时。
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1

# 串行还原、全新 Native AOT 发布并实际运行合成回归。
./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke

# 制作包含安装器、原生依赖、运行时、许可证和校验清单的离线候选 ZIP。
./packaging/Publish-OfflinePackage.ps1 -DesktopSmoke -WorkflowSmoke
```

首次准备和 NuGet 还原需要网络；缓存与证据保存在忽略的 `output/`。普通用户解压完整候选包，运行 `OhMyStudy.Setup.exe`，安装后从开始菜单打开；也可使用完整 portable 目录。安装器与应用均为 Native AOT，无需开发 SDK 或系统 Python/Node/TeX。安装使用独立版本目录和原子活动指针，卸载保留资料。候选状态为 `release-candidate-user-acceptance-pending`；打包、解压校验和本机桌面通过不能替代用户负责的干净系统安装验收。本轮不执行签名，详见 [发布说明](docs/migration/p9-release.zh-CN.md)。

应用默认使用 `%LOCALAPPDATA%/OhMyStudy/Library` 新资料库。旧资料通过菜单显式备份、恢复到新目录后切换；完整初始化失败时重新打开原库，不更新默认路径。真实资料切换由用户负责，操作前需退出旧版并保留完整备份。A3/A4题目与答案 PDF 已支持图片裁片、表格、选项及递归小问；不支持或不能完整呈现的内容仍会明确阻止导出。

最新本机回归为 Domain 107、Storage 93、Provider 11、Execution 17、Grading 211、Media 26、原生工作流28项及 Setup 40项；文档验证生成8份、合计45页PDF。三次独立顺序启动采样P95为636.3232ms，空库自然空闲约104–106MiB，释放资源后约201–202MiB，略超200MiB，不能宣称该预算通过。采样不强制GC，渲染回调也不等于GPU呈现；详见[P8记录](docs/migration/p8-experience.zh-CN.md)。

## 文档

GitHub会在推送`main`时自动检查、测试并上传构建产物；推送tag时自动发布Release，操作和本机复现见[CI与版本发布](docs/ci.zh-CN.md)。

- [架构](docs/architecture.zh-CN.md)、[实施与待验事项](docs/implementation-plan.zh-CN.md)、[应用使用与诊断](src/OhMyStudy.App/README.md)
- [离线安装与卸载](docs/migration/p9-installation.zh-CN.md)、[用户接手验收](docs/migration/user-acceptance.zh-CN.md)
- [设计原则](docs/design-principles.zh-CN.md)：产品界面保持简洁，技术细节与证据留在维护文档。
- [领域](docs/migration/p2-domain.zh-CN.md)、[存储](docs/migration/p3-storage.zh-CN.md)、[协议](docs/migration/p5-protocols.zh-CN.md)、[整卷](docs/migration/p5-grading.zh-CN.md)、[媒体](docs/migration/p6-media.zh-CN.md)

仅面向 Windows 10/11 x64。当前 Sandbox执行路线要求可证明归属的新 `wsb` CLI，每次调用使用独立VM，不跨调用保留任意状态；Windows 10或缺少该CLI的系统不会标为就绪。开始前可明确冻结为本机执行，沙箱执行失败后不在宿主重放。真实资料与凭据切换、真实样卷、干净系统、DPI/读屏/输入法、参考设备性能、真实Sandbox和物理打印均由用户独立验收，尚未通过。完整标签、高级筛选、复习卷生成与手机传图等产品待办不因重写而自动完成。

项目采用 [GPL-3.0-or-later](LICENSE)，分发组件保留各自许可证。
