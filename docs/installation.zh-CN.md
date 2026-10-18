# MSI 安装、升级与卸载

更新：2026-10-18。当前开发版改用 Windows Installer（MSI）；已公开的 v0.1.0 EXE 是旧安装器，不会因为源码修改而自动更新。所有用户方案的 12 项 MSI 回归和完整 AOT MSI 安装/校验/卸载已通过；真实系统验收仍待执行。验证结果与本地产物见[维护进度](../migration-progress.md)。

## 安装与下载

四种构建均提供 `-portable.zip` 与 `-installer.msi`。需要安装向导时双击 MSI，依次阅读许可、选择安装位置和是否创建桌面快捷方式，再开始安装。向导显示 Windows Installer 的实际进度，完成后从开始菜单或所选桌面快捷方式打开应用。

默认安装到 `%ProgramFiles%/OhMyStudy`，按用户确认的“所有用户”方式安装；开始安装时由 Windows 请求管理员确认。自选位置需要管理员能够写入。开始菜单始终提供启动和卸载入口。应用资料、凭据、备份和资料库路径仍按原有规则保存在用户目录，选择程序安装位置不会搬迁学习资料。

| 文件标识 | 适用情况 |
| --- | --- |
| `aot` | 普通 Native AOT，无需另装 .NET，不启用 AVX-512；不确定时选择此项 |
| `aot-avx512` | 需要处理器和系统支持 x86-64-v4（AVX-512 F/BW/CD/DQ/VL） |
| `self-contained` | 非 AOT，自带 .NET 运行时 |
| `framework-dependent` | 非 AOT，需要先安装 Microsoft .NET 10 x64 Runtime |

四种应用均附带 Windows App SDK、本地排版和私有计算运行时。MSI 本身使用系统 Windows Installer，不要求安装 .NET 或 WiX。依赖 .NET 的 App 仍需用户准备相应运行时，安装器不会自动下载。

portable ZIP 直接解压运行 `OhMyStudy.App.exe`，不注册系统安装项，也不再附带旧的 `OhMyStudy.Setup.exe`。不要混用同一目录中的便携版和 MSI 管理的程序文件。

## 升级、修复与卸载

关闭应用后运行新 MSI。四种构建共用 UpgradeCode，按单个产品升级或切换构建，不并排安装；升级默认沿用已保存的位置和桌面快捷方式选项。相同 MSI 版本允许替换，以支持切换构建；更低的三段版本被拒绝。MSI 版本只使用 major.minor.patch，预发布后缀不参与 Windows Installer 的版本排序。

升级的旧产品移除发生在 MSI 事务内；失败由 Windows Installer 回滚。安装文件直接位于所选目录，不再使用自写安装器的版本目录、活动指针或稳定启动器。维修或卸载可通过原 MSI 或 Windows“已安装的应用”进行。

卸载只处理 MSI 登记的程序文件、快捷方式和安装设置；学习资料、凭据、备份及用户自行添加的其他文件不属于 MSI 删除集合。没有配置递归清空资料或安装目录的操作。真实资料、干净系统、断电和磁盘故障仍需按[验收清单](acceptance.zh-CN.md)验证。

如果已安装旧 EXE 版本，MSI 会提示先卸载旧版。先退出应用，使用 Windows 设置或旧版的卸载入口卸载，再安装 MSI；旧版卸载保留资料，不必将资料复制到程序目录。旧目录中有未完成卸载时，先使用保留的旧安装器完成恢复卸载，不让 MSI 接管旧版的自定义安装记录。

发行二进制尚未代码签名。发布的 SHA-256 用于校验文件完整性，不能代替发行者签名；Git 的 SSH 提交签名是另一项机制。

## 构建与验证

`packaging/msi/` 保存 MSI 定义、WiX 标准向导的导航与带快捷方式选项的目录页；安装进度、事务、修复和卸载使用 Windows Installer。没有自写 Win32 安装窗口或自定义文件复制执行器。构建工具固定为 WiX 5.0.2，NuGet 原始包哈希见 `wix.lock.json`；只在构建机下载至忽略的 `output/`。派生向导文件的来源与 MS-RL 许可同目录保留，分发包的 `licenses/WiX-MS-RL.txt` 也保留许可。

```powershell
./packaging/prepare-msi-tools.ps1
./packaging/Build-MsiPackage.ps1 -PackageDirectory <完整portable目录> -OutputFile <输出MSI绝对路径>
./packaging/Test-MsiPackage.ps1 -InstallerMsi <MSI绝对路径>
./packaging/tests/Test-MsiInstaller.ps1
```

构建前校验输入清单、文件大小、哈希和目录边界。每个文件使用稳定组件标识和文件 KeyPath；安装设置使用 HKLM，空目录仅由 RemoveFolder 清理。完整发布仍使用 `Publish-OfflinePackage.ps1`，同时产出 portable ZIP 和 MSI；发布矩阵与清单规则见 [CI 文档](ci.zh-CN.md)。

MSI 回归使用独立产品标识和注册表子键，快捷方式重定向至项目内的测试目录。覆盖中文与空格路径、桌面选项及保留、升级、同版本切换、拒绝降级、失败回滚和卸载保留额外文件。完整 MSI 可用 `Test-MsiPackage.ps1 -InstallSmoke -VerifyPayload` 实际安装并逐文件校验，再卸载；若已有相关产品，测试会停止，避免升级现有安装。Windows Installer 的系统缓存不能在受限文件沙箱内写入，此测试需要正常的管理员执行环境，不能以沙箱越权代替 Windows 管理员确认。

`src/OhMyStudy.Setup` 与其测试暂留用于旧 EXE 兼容维护，当前 MSI/portable 发布不构建、不嵌入、不调用该安装器。历史 EXE 的验证不能替代 MSI 的验证。
