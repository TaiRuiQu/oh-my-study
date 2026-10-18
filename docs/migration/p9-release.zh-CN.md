# P9 离线发布与资料交接

更新：2026-10-18。当前产物状态为`release-candidate-user-acceptance-pending`。本轮完成安装/升级/可恢复卸载和本机离线包自动验证；用户已明确接手干净系统、真实身份/资料/凭据及其他外部验收，未执行项不计通过。

## 构建与验证

`Invoke-MigrationChecks.ps1`串行锁定还原并发布Domain、Storage、Provider、Execution、Grading、Media、Infrastructure、Documents、Setup、Installation、App，共11个分项。Setup只发布；其余控制台与显式桌面流程都实际运行。检查x64/CLR头之外还拒绝入口同名DLL、deps和runtimeconfig，避免将普通apphost误认AOT。缺少必需项目、跳过与失败分开报告。

```powershell
./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke
./packaging/Publish-OfflinePackage.ps1 -DesktopSmoke -WorkflowSmoke
./packaging/Test-OfflinePackage.ps1 -PackageZip <ZIP绝对路径> -DesktopSmoke -WorkflowSmoke -InstallSmoke
```

`Publish-OfflinePackage.ps1`默认从当前工作树新发布App和Setup，也可传入明确的`-AppPublishDirectory`和`-SetupPublishDirectory`。重用旧发布目录时调用者负责核对源码对应；脚本不能凭当前源码快照证明旧EXE由这些字节生成。CI从固定Git提交检出并新发布。包清单记录`sourceCommit`和`sourceTreeClean`，源码快照包括完整正式源码/项目/锁/测试/工具/文档，不带`.reference`、Git数据、缓存或用户文件。

离线包包含自包含WinUI、PRI/XBF、图标、SQLite、PDFium及notices、Typst及许可证、固定字体、dwebp及COPYING/PATENTS、私有Python及全部计算库与许可证。私有运行时清单逐文件SHA-256，包含`execute.py`和`sandbox-worker.py`；guest worker被打包不代表已在Sandbox实机通过。正式代码不调用浏览器、Rust或Node；包检查会拒绝相应程序与CoreCLR，WinUI传递的可选WebView互操作文件单独列出。

每个包有完整文件清单、`components.json`、许可证、`source/source-snapshot.zip`。清单只能发现损坏，不能代替发行者数字签名。Git跳过签名不等于二进制已签名；当前未配置发行代码签名。依赖上游元数据/notices被保留，不能将其存在等同法律分发审核通过。

## 离线安装与恢复

解压后双击`OhMyStudy.Setup.exe`，或直接运行同目录`OhMyStudy.App.exe`。安装器为独立C# Native AOT、`asInvoker`，不需要PowerShell、.NET或管理员权限。正常安装位于`%LOCALAPPDATA%/Programs/OhMyStudy`；原生开始菜单/HKCU卸载入口不依赖脚本宿主。

版本目录按包清单SHA-256隔离，先完整复制/校验，再原子切换`active.json`；升级保留旧版本并使用稳定启动器。首次复制失败只清理自己创建的文件，不删除预先存在的目标。四个阶段升级故障可恢复原指针与入口。卸载先保存可恢复日志，允许在部分文件已删后继续，存在文件仍需校验；仅删除归属清单内文件，保留用户资料、额外文件和备份。异常中断后从原完整包执行`--uninstall --root <安装目录>`继续。详细边界和命令见[P9安装器](p9-installation.zh-CN.md)。

`Test-OfflinePackage.ps1 -InstallSmoke`使用新的隔离安装目录和实际包内安装器，禁用快捷方式/注册表集成；再从安装后的真实路径启动28项工作流，创建合成用户保留文件并卸载，确认用户文件仍在且活动指针已移除。它不修改用户真实开始菜单、HKCU入口、资料或凭据。

## 资料迁移

只读原库→包含WAL及资产的一致备份→不存在的新目录恢复→仅升级副本→全部原始表列/JSON/历史记录与实体指纹核对→完整初始化成功才写活动指针。失败重新打开原库，保留备份和副本，不覆盖用户原数据。复杂合成旧库93项AOT回归已通过。真实资料/凭据/安装身份与回退按用户要求交接，参见[验收清单](user-acceptance.zh-CN.md)。

## 最新证据

最终统一运行、候选ZIP/哈希、解压及安装运行卸载报告以根[migration-progress.md](../../migration-progress.md)和`output/migration/latest-migration-run.json`为准。初轮安装器40项实际AOT通过，原始报告`output/migration/setup-aot-tests.json`；复杂PDF为8份45页，三次性能采样单独见P8。

历史候选`OhMyStudy-win-x64-rc-20261018-153255-d5509074.zip`和早期八分项报告仅作为历史，不能替代本轮含安装器/guest worker/复杂导出的最终包。不得使用历史包给新源码作通过证据。
