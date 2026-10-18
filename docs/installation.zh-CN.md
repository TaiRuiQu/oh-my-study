# 离线安装、升级与卸载

日期：2026-10-18。状态：C#安装器和隔离目录自动回归已通过本机Release Native AOT验证；实际用户安装身份、干净Windows和系统应用列表人工验收由用户负责，本轮未执行。

## 安装入口与分发集成

`src/OhMyStudy.Setup/`为独立纯BCL、`net10.0`、`win-x64`、Native AOT WinExe，运行不需要.NET、PowerShell、脚本宿主、Node或系统Python。`app.manifest`固定`asInvoker`，不请求管理员权限。安装确认、成功和错误使用系统原生消息框；技术诊断只写显式请求的报告文件。

发布输出为`OhMyStudy.Setup.exe`。打包时将它复制到portable根目录，**然后**生成`package-manifest.json`，使安装器本身也纳入逐文件哈希清单。安装器读取与现有portable相同的`schemaVersion=1`、`architecture=win-x64`、`entryPoint=OhMyStudy.App.exe`及`files[{path,bytes,sha256}]`，可选`version`用于系统应用列表。无需自解压封装；用户解压整个目录后双击安装器。

正常安装位置为`%LOCALAPPDATA%/Programs/OhMyStudy`。安装器提供开始菜单启动/卸载快捷方式以及HKCU卸载注册项。启动快捷方式指向根目录稳定安装器的`--launch`，再解析当前版本的真实应用路径。快捷方式使用ShellLink原生COM vtable，不使用动态COM、WSH、PowerShell或外部脚本。自动回归只在隔离目录创建真实`.lnk`，没有修改当前用户的开始菜单或卸载注册表。

项目已加入`OhMyStudy.slnx`并提供各自`packages.lock.json`。开发构建：

```powershell
dotnet restore src/OhMyStudy.Setup/OhMyStudy.Setup.csproj -p:Configuration=Release --locked-mode
dotnet publish src/OhMyStudy.Setup/OhMyStudy.Setup.csproj -c Release --no-restore -o output/migration/publish/setup
dotnet restore tests/OhMyStudy.Setup.Tests/OhMyStudy.Setup.Tests.csproj -p:Configuration=Release --locked-mode
dotnet publish tests/OhMyStudy.Setup.Tests/OhMyStudy.Setup.Tests.csproj -c Release --no-restore -o output/migration/publish/setup-tests
./output/migration/publish/setup-tests/OhMyStudy.Setup.Tests.exe output/migration/setup-aot-tests.json output/migration/publish/setup/OhMyStudy.Setup.exe
```

以下命令用于隔离自动回归，不注册开始菜单或系统卸载项；完整路径通过`ProcessStartInfo.ArgumentList`或原生参数传递：

```text
OhMyStudy.Setup.exe --install --source <portable绝对路径> --root <隔离安装绝对路径> --no-shortcuts --quiet --report <报告绝对路径>
OhMyStudy.Setup.exe --launch --root <安装绝对路径>
OhMyStudy.Setup.exe --uninstall --root <安装绝对路径> --quiet --report <报告绝对路径>
```

## 版本切换、恢复与资料边界

逐文件验证长度和SHA-256后，将文件复制到`versions/.staging-<随机标识>`，复制后再次校验并同步写盘。完整清单原始字节的SHA-256作为不可变版本目录名。新目录完成后才通过同卷重命名进入`versions/<hash>`；再原子替换`active.json`。安装写入由独占文件句柄串行化。索引/杀毒对新可执行文件造成的短暂占用只做有限重试，不取消路径与哈希检查。

安装记录`installation.json`跟踪产品标识、安装器哈希和归属版本。首次安装拒绝非空且无产品记录的目录；升级保留旧版本和稳定启动器，允许旧应用进程继续使用自己的旧版本目录。安装暂存、切换前、切换后、系统入口完成四个位置均有故障注入回归，失败恢复原`active.json`、原安装记录和系统入口事务，仅清理本次创建且验证归属的文件。首次失败后可重新安装，不留下会阻止重试的空版本目录。根启动器以CreateNew创建，半复制或写入失败时只清理本次成功创建的文件；创建失败时不删除已有文件。

清单哈希用于完整性与损坏检测，不能代替发行者签名或证明下载来源。Git 提交默认使用 SSH 签名；发行二进制当前仍未签名，不能表述为已签名可信发行。稳定启动器和清单格式升级时需另行兼容验证。

卸载前验证所有已知版本的原清单哈希、仍存在的文件内容、根启动器哈希和文件占用。首次删除前原子写入`uninstall.json`，保存原安装记录和各版本完整清单字节，清单仍须匹配原版本哈希。卸载中途失败后再次运行卸载会接受已经删除的文件，并重新校验所有仍存在的产品文件；即使旧清单、根启动器或安装记录已删，也能从恢复记录继续。恢复记录在全部删除成功后才移除，卸载进行中禁止启动应用和覆盖安装。仅删除清单列明的产品文件、安装记录和仍归属本安装的系统入口；不递归删除安装根目录。用户添加的文件，即使位于版本目录内也保留。应用资料目录、旧版资料、密钥、备份与当前资料指针均不在删除集合内。系统入口已被用户修改时不会按旧哈希删除修改后的快捷方式。

运行中的可执行文件不能直接删除自身，因此安装器自卸载时复制到独立临时目录，启动原生助手并等待本次父进程退出，再执行同一卸载引擎。助手不会结束其他进程；若应用文件仍占用，会停止并提示关闭后重试。该临时助手可执行文件由系统临时目录清理策略回收，本实现不启动脚本循环来删除正在执行的自身。

路径只能是安全相对文件路径，拒绝`..`、绝对路径、反斜线别名、ADS、设备名、尾随点/空格、大小写重复清单项及跨边界路径；检查源、目标及祖先的重解析点，拒绝符号链接与junction。已安装版本中出现重解析点会停止卸载。未声称提供抵御同一用户恶意并发替换目录的通用文件系统沙箱。

## 验证与发布

安装器已有 40 项本机 Native AOT 隔离回归。最近候选包的安装、运行、卸载验证与精确哈希见 [维护进度](../migration-progress.md)，干净系统、开始菜单、真实资料和故障恢复仍按 [验收清单](acceptance.zh-CN.md)执行。

打包使用 `packaging/Publish-OfflinePackage.ps1`，默认新发布 App 与 Setup；如指定已有发布目录，调用者须核对二进制与源码对应。包包含完整 portable 目录、原生依赖、私有计算运行时、许可证、源码快照和逐文件哈希清单。源码快照不包含 `.reference/`、Git 数据、缓存和用户资料。CI 与版本发布步骤见 [CI 文档](ci.zh-CN.md)。

恢复卸载可从原完整解压包运行 `OhMyStudy.Setup.exe --uninstall --root <原安装绝对路径>`。保留的恢复记录只覆盖受限产品文件集合，不将未知用户文件加入删除列表。故障注入回归不能代替实际磁盘耗尽、断电或文件系统损坏验证。
