# 原生 Windows 应用

WinUI 3 App 已组合正式 C# 领域、仓储、任务、协议和文档服务。普通启动使用真实的新资料库；合成样例只用于明确的诊断模式。当前代码与本机证据不代表所有迁移阶段或目标系统已经验收，最新状态见 [migration-progress.md](../../migration-progress.md)。

## 当前流程

- 从题库顶部导入 PNG、JPEG、WebP、PDF，选择教师批改、AI 批改或空白卷模式后自动识别并加入题库；材料和参考答案合计最多 12 页。
- 资料选择、页管理、旋转和移除持久化；仍被题目/参考答案引用的页不能直接移除。原件与 PDF 原页关系保留。
- 原件缩放、平移与题目来源高亮共用坐标变换；按页/分辨率缓存有字节预算。WebP 无可用系统 codec 时使用私有 dwebp。
- 题目通过原生文字、公式、表格和图像呈现；完整编辑支持选项、表格单元格、配图、来源及最多八层小问。标准答案、学生作答与教师批注保持独立，重批保护手动/教师评分；刷新和导航保留草稿，保存期间新输入不被覆盖，校验失败保留窗口。退出或切库前说明未保存后果。
- 设置使用供应商列表与详情布局，支持三协议供应商、密钥、多模型、显示名、上下文窗口、图片/工具能力、思考选项及价格；模型列表获取与模型连接测试独立，连接测试发送一次小请求，可能产生费用。models.dev 信息由用户主动获取，按模型 ID 精确匹配并选择价格来源，填充后需保存，不从 reasoning 布尔值猜测思考强度。目录来源见 [models.dev API](https://github.com/anomalyco/models.dev#api)。
- 设置支持执行方式；整卷任务单个运行，其余排队，支持取消及具体执行请求审阅。供应商请求由获取模型、测试连接或启动任务触发，工具调用遵守所选执行策略。
- 题库与错题使用保存结果，空白卷不会因未作答被误标错题。完整标签和高级筛选仍为独立待办。
- A3/A4题目或答案PDF导出支持文字、公式、选项、表格、配图裁片和递归小问，题答分开排版。不能完整呈现的公式或其他内容仍明确阻止导出，避免丢内容。
- 原件打印通过用户点击打开原生系统打印界面；分页/预览已接线，尚无物理打印验收。
- 资料备份、恢复及旧资料复制使用完整新目录，不覆盖正在使用的库。

## 资料与安全边界

默认资料库为 `%LOCALAPPDATA%/OhMyStudy/Library`。所有正式导入的图片、PDF 原件与参考答案都复制到库内，查看、打印和整理不依赖原文件路径。设置中的“资料库”显示保存位置，可打开文件夹、更改位置或选择已有资料库。

更改位置会在选定文件夹下新建 `OhMyStudy资料库`，使用完整备份复制数据库及全部受管原件，保留原库；禁止覆盖已有目录或复制到当前库内部。只有全部初始化成功后才原子更新同一应用目录的 `active-library.txt`，失败重新打开原库。重启发现已选位置不可用时，不另建空库冒充原库，可在设置选择已有资料库恢复。旧版目录仍解析为 `%APPDATA%/io.ohmystudy.desktop`，不会因发现旧数据就自动切换或就地升级；关闭发现提示后记录选择，设置页不显示该资料页提示。

“导入旧资料”由用户选择旧目录，只读在线备份并恢复到新的`Imported-*`目录，仅升级副本，核对指纹并写迁移收据。完整初始化成功后才更新默认路径；初始化中途失败会释放新会话并重新打开原库。应先退出旧版并保留备份；合成旧库通过不代表真实资料及安装身份验收。新版lease仅约束合作实例，旧进程名称检查不能阻止检查后启动或改名程序。

Sandbox使用新wsb CLI按每次调用创建和停止明确ID的独立VM，不跨调用保留任意状态；Windows10、旧Windows11或缺少CLI时不会标记就绪。开始对话框可说明影响后冻结本机回退，Sandbox执行失败不在宿主重放。真实会话和系统矩阵由用户验收。

密钥保存在 Windows Credential Manager，库内只存引用。自动回归使用合成 vault，不读取真实凭据。凭据清理失败与数据库保存结果分开报告，跨账户迁移和系统凭据失败恢复仍需授权验证。

界面只显示用户任务和结果；未保存、输入限制和失败恢复需要明确说明。框架/引擎、存储与测试规模不进入默认页、设置、菜单、关于或操作反馈。标题栏使用 WinUI TitleBar/系统窗口能力，没有自绘系统按钮或未实现的搜索/账户入口。

## 构建和完整部署

版本由根 `global.json`、`Directory.Packages.props` 及逐工程锁文件固定；Windows x64、Windows App SDK 自包含、Release Native AOT。

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
# 先准备固定原生工具/计算运行时，再构建和打包。
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
dotnet restore src/OhMyStudy.App/OhMyStudy.App.csproj -p:Configuration=Release --locked-mode
dotnet publish src/OhMyStudy.App/OhMyStudy.App.csproj -c Release --no-restore -o output/migration/app-local
./packaging/Publish-OfflinePackage.ps1 -AppPublishDirectory ./output/migration/app-local -DesktopSmoke -WorkflowSmoke
```

完整目录属于部署产物，不能只复制EXE。发布目标包含PRI/XBF和图标；打包脚本加入SQLite、PDFium/notices、Typst、dwebp、两个计算worker及`runtime/python`（排版字体使用系统字体，不随包分发）。安装格式改为 MSI，打包脚本通过 WiX 生成标准向导；旧自写安装器不纳入 portable 或 MSI。

普通用户双击 MSI，在标准向导选择安装位置和桌面快捷方式；默认位置为`%ProgramFiles%/OhMyStudy`。升级、进度、回滚、修复和卸载由 Windows Installer 管理，学习资料保留。旧 EXE 安装需先卸载，再安装 MSI。portable ZIP 直接运行应用，不含旧安装器。具体构建、验证与兼容边界见[安装说明](../../docs/installation.zh-CN.md)。

公式、PDF、预览、导出及打印没有浏览器控件路径；WinUI 传递发布的可选 WebView2 interop 文件单列在包检查报告，不等同于浏览器进程。候选包的目标系统兼容性及完整离线矩阵尚未关闭，见 [安装说明](../../docs/installation.zh-CN.md)。

## 诊断入口

所有诊断使用可写的绝对报告路径，报告和临时资料应位于发布目录之外。

```powershell
# 样板 UI/虚拟化/绑定/主题自检；不作为业务仓储验证。
OhMyStudy.App.exe --smoke-test C:\absolute\evidence\desktop.json

# 样板模式附加真实合成 PDF 预览与翻页。
OhMyStudy.App.exe --smoke-test C:\absolute\evidence\pdf.json --preview-pdf C:\absolute\synthetic.pdf

# 在报告旁新建隔离合成库，验证正式导入、编辑、备份和重开链路。
OhMyStudy.App.exe --workflow-smoke C:\absolute\evidence\workflow.json

# 隔离性能原始采样；默认三次独立顺序启动。
./packaging/Measure-NativePerformance.ps1 -Executable C:\absolute\publish\OhMyStudy.App.exe
```

`--smoke-test` 明确加载合成表示数据，验证窗口、编译绑定、虚拟化、主题、旋转/来源及任务取消演示，并输出当前 App 自身 XAML 树截图。可选 PDF 输入请使用合成资料。`IsNativeAot` 来自 `!RuntimeFeature.IsDynamicCodeSupported`；JIT 运行不能冒充 AOT 通过。启动失败会尽力写出同目录 `.startup-error.txt`，调用方应将超时或缺少报告视为失败。

`--workflow-smoke`使用报告旁唯一`synthetic-workflow-*`目录及专用默认路径文件，不读取真实库、不请求供应商、不访问真实凭据。最新已记录的 Native AOT 工作流30项通过，涵盖12页导入、复杂编辑与小问、答案/教师证据隔离、来源、题库、草稿与保存竞争、切库初始化失败恢复、页面/分辨率与阅读状态等。后续修复以新报告确认，旧截图不作为新构建证据。

`--performance-smoke <报告路径>`在空库初始化及分发器响应后写同名`.ready`，随后采样12页三种分辨率、500题连续滚动缩放、Rendering间隔、后台UI排队延迟及自然内存回收曲线；外层脚本只管理自身启动的PID。本轮三次顺序启动P95为636.3232ms，空库自然空闲约104–106MiB，释放资源后约201–202MiB，略超200MiB，不能声称该预算通过。没有强制GC；Rendering回调不等于GPU呈现/ETW，三次顺序样本不等于冷/热启动分布。

真实文件选择/拖放、输入法、读屏、高对比、DPI/多屏、干净Win10/11、真实资料与凭据、供应商、Sandbox、物理打印、GPU/ETW和参考设备性能均由用户负责，当前未标记通过。最新各层数量为Storage93、Provider11、Execution17、Grading211、Media26、Documents8份45页及Setup40；原始结果以根进度与对应报告为准。

维护细节见[架构](../../docs/architecture.zh-CN.md)、[设计原则](../../docs/design-principles.zh-CN.md)、[验收清单](../../docs/acceptance.zh-CN.md)。

## 试卷导入与调试（2026-10-18）

默认打开题库，从顶部“导入试卷”选择 PDF 或图片，在同一个对话框中选择试卷类型与模型并确认“导入并识别”。保存原件后自动开始识别、返回题库，已保存题目随任务进度刷新。未设置模型时明确提示前往设置，不把原件保存伪装成题目入库。原件从“查看原件”或“定位原件”主动展开；窄窗口查看原件时暂时收起题目区域，关闭原件即可继续编辑。

入口使用显式 `[STAThread]` 并设置 UI 同步上下文。Debug 使用控制台子系统，直接在终端运行 `dotnet run --project src/OhMyStudy.App -c Debug -p:Platform=x64` 即可查看 UTF-8 日志，同时写入实际运行目录的 `logs/debug-日期-进程号.log`，启动时打印完整路径。命令行 Debug 的控制台宿主与界面分开，即使 WinUI 原生 fail-fast 绕过 C# 异常处理，也能输出、落盘并传递非零退出码；Ctrl+C 或关闭宿主会结束其界面子进程。F5 已附加调试器时直接运行界面，保留断点调试。内部 `--ui-process` 参数用于避免递归启动；用户仍使用原有启动命令。

导入记录文件类型、字节数、阶段、耗时和关联标识；失败包含异常类型、堆栈和内部异常。识别后台异常在队列转为普通失败提示前记录，资源加载、窗口创建/激活、XAML 与未处理异常也进入日志。不记录密钥、请求正文或试卷内容。Release 仍为 WinExe，不启用控制台宿主。

普通构建自动把已准备的 PDFium、WebP、Typst 复制到实际运行目录，缺少 PDFium 时在构建阶段报出准备命令。没有把开发目录作为运行时回退路径。损坏、加密 PDF 显示可执行的恢复提示，底层错误码保留在日志。

`--import-smoke <报告绝对路径>` 使用隔离资料库、真实 PDF/PNG 解码与模拟识别任务，验证导入自动提交、题库追加、取消、未配置模型与损坏文件恢复，并保存实际 XAML 渲染截图；不会访问真实凭据或调用外部模型。报告及截图默认由调用者选择位置，本轮放在 `output/import-review/`。这项检查不能替代真实模型识别质量验收。
