# P9 离线候选包与验收脚本

日期：2026-10-18（Asia/Shanghai）。状态为 **release-candidate-unvalidated**。脚本与本机合成检查不能替代干净 Windows 安装、升级、真实资料或完整发布验收。

## 入口

`packaging/Invoke-MigrationChecks.ps1` 串行处理 Domain、Storage、Provider、Execution、Grading、Media、Documents 与 App，逐工程用 `Configuration=Release` 和锁模式还原，再在唯一新目录发布。控制台检查实际运行发布的 EXE；PE 检查要求 x64 且没有 CLR 头，发布目录不得带 CoreCLR。每个工程留下还原、发布、运行日志与 SHA-256 文件清单。不存在的工程、明确跳过的检查、未请求的桌面冒烟和尚未执行的真实验收都写入 `notRun`。早期失败后未尝试的工程也不会被列为已通过。

```powershell
# 默认不下载文档工具，也不访问真实凭据或资料。
./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke

# 已有依赖缓存时可只检查业务；跳过项会写入报告。
./packaging/Invoke-MigrationChecks.ps1 -SkipDocuments -SkipApp

# 仅在明确准备文档依赖时添加此选项；沿用锁定 URL/hash。
./packaging/Invoke-MigrationChecks.ps1 -PrepareDocuments -DesktopSmoke -WorkflowSmoke
```

脚本使用 `output/migration/dotnet-home` 和 `output/migration/nuget`，每次生成 `output/migration/runs/migration-<时间>-<唯一标识>`。`latest-migration-run.json` 指向本次证据。没有共享 Infrastructure 图的并发发布；调用者也应避免同时启动多个构建脚本。初次新测试工程需要先由维护者建立并审查 Release 锁文件，脚本不会静默放弃锁模式。

`packaging/Publish-OfflinePackage.ps1` 默认在新目录自行还原、发布 App；也可显式指定父流程刚发布的 App 目录。后者会记录来源，既有 EXE 与当前工作树源码的对应性需要调用者确认。脚本不会自行准备或下载 Python、PDFium、Typst 或字体；缺少锁定工具或运行时会失败，并保留失败报告。

```powershell
# 先明确准备父维护的私有运行时；根包含 python/ 和 runtime-manifest.json。
./packaging/prepare-runtime.ps1

# 自行发布 App 并制作新候选 ZIP。
./packaging/Publish-OfflinePackage.ps1 -DesktopSmoke -WorkflowSmoke

# 或使用当前检查流程刚生成的 App，避免再次编译同一工程图。
./packaging/Publish-OfflinePackage.ps1 `
  -AppPublishDirectory '<本次全新 App publish 目录>' `
  -RuntimeDirectory './output/migration/toolchain/runtime' `
  -OutputDirectory './output/releases' -DesktopSmoke -WorkflowSmoke
```

输出为唯一的 `output/releases/OhMyStudy-win-x64-rc-.../`，含 `portable/`、可解压运行的 ZIP、ZIP SHA-256、打包与验证报告。现有目录和用户资料不会被替换。包根直接包含 `OhMyStudy.App.exe`，无需 SDK、系统 Python、Node.js 或 TeX 来运行；该目标是否在各干净 Windows 系统成立仍需要逐项实测。

## 包内容与来源

- WinUI 自包含 Native AOT App、PRI/XBF、图标和发布的系统兼容组件。
- 发布出的 `e_sqlite3.dll`；其 NuGet 版本和完整依赖图元数据来自锁文件。
- `native/pdfium.dll`、完整 PDFium 第三方 notices；Typst 和 LICENSE/NOTICE 位于 `native/typst/`；固定字体与 OFL 位于 `native/fonts/`。
- `native/webp/dwebp.exe` 和 COPYING/PATENTS 来自锁定的 libwebp 1.6.0 官方发行，用于系统缺少 WebP codec 时的离线回退；不要求用户另装图片扩展。包中不需要开发期编码器 `cwebp.exe`。
- `runtime/` 原样复制父流程准备的私有 Python 根，含 `runtime-manifest.json`、`python/python.exe`、`execute.py` 及该运行时自身许可证/文件清单。运行时状态为 `computation-verified-sandbox-not-enabled`，不发行尚未实现的 Sandbox worker，也不填充空实现来满足清单；真实 Sandbox 生命周期单列未验证。
- 根 `LICENSE` 保持现有 GPL 原字节。`licenses/` 保存 .NET Native AOT 许可证/notices、NuGet 包附带的许可证或 nuspec/README 元数据和工具锁文件。
- `components.json` 记录固定版本、工具哈希和 NuGet content hash，明确依赖图也含构建工具，不把所有图项误称为运行时文件。部分上游包未附独立许可证正文，不能因保留元数据就声称分发许可审核通过。
- `source/source-snapshot.zip` 保存当前正式 C# 工作树源码、工程、测试、脚本、锁与维护文档，包含尚未提交的新实现；不打包 `.reference`、Git 数据、缓存、bin/obj、用户资料或凭据。已有发布物的源码对应性仍须审查。

`package-manifest.json` 对全部其他包文件记录相对路径、字节数和 SHA-256；清单明确排除自身以避免循环哈希。ZIP 哈希在包外报告记录。清单校验提供完整性检查，没有数字签名或防篡改来源认证的含义；正式签名及分发信任仍未验收。

## 离线验证范围

```powershell
./packaging/Test-OfflinePackage.ps1 -PackageDirectory '<portable目录>' -DesktopSmoke -WorkflowSmoke
./packaging/Test-OfflinePackage.ps1 -PackageZip '<候选zip>' -DesktopSmoke -WorkflowSmoke
```

ZIP 验证先解压到新的证据目录，再执行同一检查。验证拒绝越界清单路径、重复清单、链接/重解析点、未列出文件、大小/哈希不符，以及 Node、Rust、浏览器 EXE 或 CoreCLR 等非目标运行文件。它核对 App 的 PE、PRI/XBF 与私有运行时必需文件、原生工具固定哈希和 Python manifest 文件哈希。

WinUI 传递发布的可选 `WebView2Loader.dll` / `Microsoft.Web.WebView2.Core.dll` 单独列为 `optionalWinUiWebViewInteropFiles`；这些文件本身不等同于浏览器进程。脚本不凭文件名就删除框架组件，也不把这些文件存在误称为产品调用了浏览器。包内当前 App 对应源码还检查固定 WebView 控件和已知浏览器/旧工具启动入口。

桌面冒烟运行包内 EXE，并要求外置报告的 `Passed`、`IsNativeAot` 为真；同时以约 100 ms 的 CIM 采样追踪所观察到的后代进程，发现 Node、Rust 或浏览器进程则失败。CIM 不可用会明确列为未运行。采样可能漏掉寿命极短的进程，静态扫描也不是完整调用图证明；通用、经用户授权的 shell/Python 工具不被声称无法启动任意程序。正式进程跟踪和依赖验收仍需对应环境证据。

`-WorkflowSmoke` 另起一个包内进程执行正式仓储流程，在报告旁唯一合成目录验证 12 页导入、原件、编辑、来源、旋转、题库错题、未保存草稿保留、备份重开和 Native AOT。它与样板 UI 自检分开，不访问默认用户库或真实凭据。两种自检完成后再次核对全部包文件哈希和无新增文件，确保诊断不污染待分发目录。

验证报告始终存放在包外，以免报告自身改变包哈希。脚本不读取真实密钥，不主动切换真实资料；App 冒烟应使用与报告对应的隔离合成库。包验证不创建安装器、不设置系统服务、不启用 Sandbox、不提权，也不修改系统 Python 或 PATH。

## 当前证据与未验证矩阵

本轮完整流水线 `output/migration/runs/migration-20261018-152724-1b3b62f9/run.json` 的 Domain、Storage、Provider、Execution、Grading、Media、Documents 与 App 八个分项均退出 0；其中 Execution 已含私有 Python 真实计算。App 为同轮 fresh Native AOT 产物。可提交摘要见 [包验证证据](evidence.2026-10-18.package.json)，不含用户资料、凭据或个人路径。

最终候选位于 `output/releases/OhMyStudy-win-x64-rc-20261018-153255-d5509074/`。其 ZIP 为 `OhMyStudy-win-x64-rc-20261018-153255-d5509074.zip`，146267047 bytes（139.49 MiB），SHA-256 为 `cbe4b538c1f2e035b3e23f5ccecdf2c5589151d6975203930998aa55acdd5e56`。清单覆盖 4820 个文件（不含清单自身），Python runtime manifest 覆盖 4479 个文件。App EXE SHA-256 为 `f41fa9b15d218a26886f774ffb0e22fec455850047fbcab2d974239f3e5e8f6f`。

已实际完成 portable 目录校验、ZIP 重新解压校验，以及包内样板桌面和正式 12 页隔离仓储流程。两种模式均通过，正式流程 17 项为真；自检后全部包哈希仍一致。最初 sandbox 运行如实记录 CIM 不可用；随后在已授权的普通用户会话重新解压 ZIP 并运行两种自检，CIM 观察可用，未观察到禁止的后代进程。最终报告为 `output/migration/evidence/p9-final-zip-user-2026-10-18.json`，相关截图和隔离库在其引用的新证据目录。

最终普通用户流程的私有字节为 309387264、缓存 51840000 bytes、缓存翻页 P95 19.507 ms，仅是一次本机流程观察，不是空闲、冷启动、帧时间或参考机预算验收。包内源码快照取自打包时工作树；之后仅有 NativeQuestionPresenter 空格/换行格式修正及维护文档、证据更新。不能声称当前所有工作树字节与已编译 EXE 完全对应或可逐字节重现，正式源码对应性审查仍未完成。

较早的旧 App 脚本演练保留在 `output/releases/script-diagnostic/`，仅为打包脚本历史诊断，不是最终候选证据。三个新脚本均通过 PowerShell Parser 检查；本轮各层通过不能合并为 P9 整体验收完成。

| 验证项 | 当前边界 |
| --- | --- |
| 本机串行完整业务套件与 App | 同轮八分项全部退出 0，实际 Native AOT；真实语义仍单列未验证 |
| 本机离线 ZIP 完整性、解压、包内桌面与仓储流程 | 最终 ZIP 哈希、4820 文件、双自检及普通用户 CIM 观察通过；状态仍为候选 |
| Windows 10 22H2 / LTSC 2021 干净离线系统 | 未验证 |
| Windows 11 24H2 / 25H2 干净离线系统 | 未验证 |
| 无 SDK / 系统 Python / Node / TeX 环境 | 未验证；开发机通过不能替代 |
| 安装器、升级、卸载、签名与分发信任 | 未验证；当前仅 portable 候选包 |
| 旧目录与目标身份、真实凭据及资料切换 | 未验证；默认只读检查 |
| 断电、磁盘耗尽与真实恢复 | 未验证 |
| 真实模型语义、Sandbox 会话归属、物理打印 | 独立未验证，不由合成套件替代 |
| 高 DPI、读屏、输入法、性能预算 | 仍需实际场景矩阵 |
| 第三方分发义务与源码对应性审核 | 未完成；元数据和源码快照供审查 |

回退只面向新目录中的已验证一致副本：保留原库，退出使用该库的应用，验证完整备份后恢复到另一个新目录，再由用户显式选择。不得覆盖当前打开的库，不承诺新版写入后旧版可继续读取，也不把复制主 SQLite 文件但遗漏 WAL 当作备份。
