# 原生 Windows 应用

WinUI 3 App 已组合正式 C# 领域、仓储、任务、协议和文档服务。普通启动使用真实的新资料库；合成样例只用于明确的诊断模式。当前代码与本机证据不代表所有迁移阶段或目标系统已经验收，最新状态见 [migration-progress.md](../../migration-progress.md)。

## 当前流程

- 导入 PNG、JPEG、WebP、PDF，选择教师批改、AI 批改或空白卷模式；材料和参考答案合计最多 12 页。
- 资料选择、页管理、旋转和移除持久化；仍被题目/参考答案引用的页不能直接移除。原件与 PDF 原页关系保留。
- 原件缩放、平移与题目来源高亮共用坐标变换；按页/分辨率缓存有字节预算。WebP 无可用系统 codec 时使用私有 dwebp。
- 题目通过原生文字、公式、表格和图像呈现；答案、备注及完整编辑通过显式保存进入资料库，重批保留手动/教师评分。刷新保留未保存编辑。
- 设置支持三协议供应商、密钥、模型、执行方式；整卷任务单个运行，其余排队，支持取消及具体执行请求审阅。供应商请求由用户刷新模型或启动任务触发，工具调用遵守所选执行策略。
- 题库与错题使用保存结果，空白卷不会因未作答被误标错题。完整标签和高级筛选仍为独立待办。
- A3/A4 题目或答案 PDF 导出支持可完整呈现的文字/公式和受支持小问。图片、表格、多层小问或不支持公式会明确阻止导出，避免丢内容。
- 原件打印通过用户点击打开原生系统打印界面；分页/预览已接线，尚无物理打印验收。
- 资料备份、恢复及旧资料复制使用完整新目录，不覆盖正在使用的库。

## 资料与安全边界

默认资料库为 `%LOCALAPPDATA%/OhMyStudy/Library`。用户显式选择其他恢复副本后，路径记在同一应用目录的 `active-library.txt`。旧版目录仍解析为 `%APPDATA%/io.ohmystudy.desktop`，不会因发现旧数据就自动切换或就地升级。

“导入旧资料”由用户选择旧目录，只读在线备份并恢复到新的 `Imported-*` 目录，再切换。应先退出旧版并保留备份；此流程在合成旧库测试过，尚未对用户真实资料及目标安装身份验收。新版 lease 仅约束合作实例，旧进程名称检查不能阻止检查后启动或改名程序。

密钥保存在 Windows Credential Manager，库内只存引用。自动回归使用合成 vault，不读取真实凭据。凭据清理失败与数据库保存结果分开报告，跨账户迁移和系统凭据失败恢复仍需授权验证。

界面只显示用户任务和结果；未保存、输入限制和失败恢复需要明确说明。框架/引擎、存储与测试规模不进入默认页、设置、菜单、关于或操作反馈。标题栏使用 WinUI TitleBar/系统窗口能力，没有自绘系统按钮或未实现的搜索/账户入口。

## 构建和完整部署

版本由根 `global.json`、`Directory.Packages.props` 及逐工程锁文件固定；Windows x64、Windows App SDK 自包含、Release Native AOT。

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
dotnet restore src/OhMyStudy.App/OhMyStudy.App.csproj -p:Configuration=Release --locked-mode
dotnet publish src/OhMyStudy.App/OhMyStudy.App.csproj -c Release --no-restore -o output/migration/app-local

# 明确准备固定原生工具/计算运行时，再由脚本补全可运行目录。
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
./packaging/Publish-OfflinePackage.ps1 -AppPublishDirectory ./output/migration/app-local -DesktopSmoke -WorkflowSmoke
```

完整目录属于部署产物，不能只复制 EXE。发布目标包含 `OhMyStudy.App.pri`、`App.xbf`、`MainWindow.xbf`、主题 XBF 和图标；打包脚本加入 SQLite、PDFium/notices、Typst、字体、dwebp 及 `runtime/python`。普通用户运行完整 portable 目录中的 EXE，不需要开发 SDK 或系统 Python/Node/TeX。

公式、PDF、预览、导出及打印没有浏览器控件路径；WinUI 传递发布的可选 WebView2 interop 文件单列在包检查报告，不等同于浏览器进程。候选包的目标系统兼容性及完整离线矩阵尚未关闭，见 [P9](../../docs/migration/p9-release.zh-CN.md)。

## 诊断入口

所有诊断使用可写的绝对报告路径，报告和临时资料应位于发布目录之外。

```powershell
# 样板 UI/虚拟化/绑定/主题自检；不作为业务仓储验证。
OhMyStudy.App.exe --smoke-test C:\absolute\evidence\desktop.json

# 样板模式附加真实合成 PDF 预览与翻页。
OhMyStudy.App.exe --smoke-test C:\absolute\evidence\pdf.json --preview-pdf C:\absolute\synthetic.pdf

# 在报告旁新建隔离合成库，验证正式导入、编辑、备份和重开链路。
OhMyStudy.App.exe --workflow-smoke C:\absolute\evidence\workflow.json
```

`--smoke-test` 明确加载合成表示数据，验证窗口、编译绑定、虚拟化、主题、旋转/来源及任务取消演示，并输出当前 App 自身 XAML 树截图。可选 PDF 输入请使用合成资料。`IsNativeAot` 来自 `!RuntimeFeature.IsDynamicCodeSupported`；JIT 运行不能冒充 AOT 通过。启动失败会尽力写出同目录 `.startup-error.txt`，调用方应将超时或缺少报告视为失败。

`--workflow-smoke` 使用报告旁唯一 `synthetic-workflow-*` 目录，经过正式媒体、仓储及界面操作，不读取默认真实库，不请求供应商，不访问真实凭据。已有 fresh AOT App 在隔离库通过 12 页导入、原字节、翻页、旋转保存、题目编辑、来源高亮、题库错题、缓存预算、备份重开和主题/焦点等 17 项检查。后续修复必须以对应新报告确认，不把旧截图当作新构建证据。

单次流程记录私有字节 324759552、缓存约 51.8 MB，仅为运行中观察；没有证明空闲预算、冷启动 P95、帧时间或长期内存回收达标。真实文件对话框、拖放、输入法、读屏、高对比系统开关、DPI/多屏、Win10/11 干净系统、真实供应商、Sandbox 与物理打印需独立验收。

维护细节见[架构](../../docs/architecture.zh-CN.md)、[设计原则](../../docs/design-principles.zh-CN.md)、[存储](../../docs/migration/p3-storage.zh-CN.md)与[媒体](../../docs/migration/p6-media.zh-CN.md)。
