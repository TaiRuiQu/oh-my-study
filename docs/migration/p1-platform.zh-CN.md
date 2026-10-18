# P1 原生平台与 AOT 证据

日期：2026-10-18（Asia/Shanghai）。本文件记录实际工具和验证边界；阶段状态以 [migration-progress.md](../../migration-progress.md) 为准。

## 锁定工具与依赖

| 项目 | 本轮固定版本 | 来源与用途 |
| --- | --- | --- |
| .NET SDK | 10.0.402 | `global.json`，rollForward=disable |
| .NET / ILCompiler | 10.0.13 | SDK 还原的官方 AOT 编译器及静态运行时 |
| MSBuild | 18.9.15 | `dotnet --info` |
| VS Build Tools | 18.11.12306.390 | 本机 vswhere；MSVC 14.51.36231 |
| 已安装 Windows SDK | 10.0.26100.0 | WinRT 代码生成需要实际 Platform.xml，不仅是目标框架声明 |
| Windows SDK BuildTools 包 | 10.0.28000.2705 | 集中 NuGet 版本，构建工具 |
| Windows SDK .NET 引用 | 10.0.26100.87 | App WindowsSdkPackageVersion；避免默认旧版投影的裁剪告警 |
| Windows App SDK WinUI 组件 | 2.3.9 | 官方 2.5.1 组合所用 WinUI 版本；只引用此组件及其必要传递项 |
| C#/WinRT | 2.3.1 | 源生成及 AOT 优化，未选 3.0 预览版 |
| Microsoft.Data.Sqlite.Core | 10.0.13 | AOT SQLite ADO.NET；无 EF / 反射实体映射 |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.2 | 固定 native SQLite，锁文件保留传递版本与包 hash |
| Typst | 0.14.2 | 独立原生 EXE；与旧排版对照引擎版本一致，并非宣称最新版 |
| PDFium | chromium/7881 win-x64 | 源生成 P/Invoke；按已固定 DLL SHA256 校验并显式加载 |
| Source Han Serif SC | 2.003R Regular | OFL，固定文件 SHA256；不依赖机器已装字体 |

App 以 `net10.0-windows10.0.26100.0` 编译，`TargetPlatformMinVersion=10.0.19041.0`。编译 API 版本、技术最低版本和项目实际系统支持矩阵是三件事；目前不能据此承诺 Windows 10 已通过。Infrastructure 为纯 `net10.0` 加 Windows 平台声明，仅用 Win32 P/Invoke，避免意外引入其不需要的 WinRT 投影。

使用组件包避免无用 AI / ML / Search / Widgets 原生载荷。WinUI 本身传递引用 WebView2 支持包；应用未创建 WebView 控件，未采用浏览器渲染。必须继续以运行链路、进程和干净系统证据验证无浏览器要求，不能仅用包名判断，也不能为隐藏包名而删掉未经验证的框架文件。

## 当前环境

正常用户只读采集得到：Windows 11 专业版 `10.0.26200` x64，Intel i5-12400F（6核/12逻辑），物理内存 `34,160,545,792` bytes，NVIDIA RTX 5060 Ti，驱动 `32.0.16.1692`，显示 `2560×1440 / 179 Hz`。原始文件 `output/migration/environment.json`。这台开发机不是计划要求的 8 GB / 60 Hz 性能参考环境；未报告启动 P95、帧时间或产品内存达标。

受限命令令牌下 CIM、Windows Credential Manager 和 Schannel TLS 不可用；正常用户执行环境可用。未更改系统策略、证书信任、Sandbox 功能或提权管理员设置。还原、凭据和桌面实跑采用正常用户环境，缓存和结果仅写仓库隔离输出。

## 已验证基础设施

真实 Native AOT 集成 EXE 已确认 dynamic-code=false；源生成 JSON、旧资料路径纯解析、协作原生实例单写入者、旧版两份 SQL 的建表、带 SQL 特殊字符的参数、外键拒绝、事务回滚、WAL 模式、SQLite online backup 与完整性检查均通过。凭据回归创建一个随机 `OhMyStudy/provider/<uuid>` 合成项，按旧版 UTF-8 bytes 保存、读取并删除；未枚举或读取真实项。

`SqliteLibrary` 目前是 P1 引擎验证边界，不包含 P3 的生产迁移/仓储/有序写入/资产事务。`LibraryWriterLease` 只能排除合作的新实例；旧版不识别锁，不能宣称已实现旧新互斥。

## 构建中发现并修复的问题

1. Debug 默认还原后，Release `publish --no-restore` 可只做裁剪而没有引入 AOT targets。改为 Release 属性还原，再断言 EXE、发布目录没有 CoreCLR、真实运行 dynamic-code=false。
2. 以 19041 TFM 生成 WinRT 需要缺失的 SDK Platform.xml。改为本机实际 26100 编译 SDK，保留最低目标版本；锁定 .NET WinRT 引用 26100.87。
3. 非界面基础设施误引用旧 WinRT 投影产生第三方 IL2104。改为只引用实际需要的纯 .NET/Win32 能力，未屏蔽警告。
4. 首次 WinUI AOT 运行发生 `0xC000027B` / `XamlParseException`。定位到 publish 漏掉自身 PRI/XBF，现以显式 MSBuild 发布目标纳入资源并在缺失时失败；不是依赖JIT或手工复制才能启动。仅明确 smoke 参数才写启动异常诊断，最终桌面结果见进度文档。
5. 发布脚本的restore不能用全局`-r`污染类库的锁文件图；RID留在可执行工程声明。解决方案显式设x64配置。跳过项单列为`notRun`并返回`selected-checks-passed`，App所需PDFium及其notices不会因为跳过文档探针而遗漏。
6. Native AOT下，原生对象可能先被投影为基类，普通`is`和对象值DP/Tag拆箱不能作为真实类型或交互证据。面板用IInspectable确认native类并以`WinRT.CastExtensions.As<T>`显式投影；导航使用原生菜单向量的身份定位。桌面自检实际滚到500号再返回并验证过滤/导航，避免只检查构造出的VM。

## 尚需独立验收

- Windows 10 22H2 / LTSC 2021、Windows 11 24H2 / 25H2 的干净系统和无 WebView2 场景。
- 普通安装身份的旧目录、旧凭据可见性、离线依赖完整性与 VC++ 运行库。
- 实际文件选择、拖放、IME、焦点恢复、读屏、高对比度、减少动画、DPI 与多显示器。
- 实际打印对话框和纸张输出；本机GetForWindow、PrintDocument.DocumentSource及事件注册/注销的AOT探针已通过，没有创建打印作业。产品级12页高分辨率阅读、内存与帧时间仍未测。
- 完整 LaTeX / mhchem 语义需求与不支持语法的用户可读性；子集通过不等于完整替代。

## 官方参考

- [.NET Native AOT](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [WinUI Native AOT](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/native-aot)
- [Windows App SDK stable channel](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/stable-channel)
- [Windows App SDK 系统要求](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/system-requirements)
- [Windows App SDK 自包含部署](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps)
- [Microsoft.Data.Sqlite backup](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/backup)
- [Windows Credential API](https://learn.microsoft.com/en-us/windows/win32/api/wincred/)

包版本和完整传递内容以集中版本文件及 `packages.lock.json` 为准；原生引擎及字体以 `native-documents.lock.json` 的 URL/SHA256 为准。
