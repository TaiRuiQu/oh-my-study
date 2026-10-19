# 维护进度与交接

更新：2026-10-18（Asia/Shanghai）。本文件按项目约定持续维护当前状态、验证结果与下一步。

## 当前状态

C# / .NET / WinUI Native AOT 重写已完成 P0–P9 开发实现与本机自动检查。后续工作按产品维护、功能待办和独立验收推进，已完成阶段的计划与过程记录已从当前文档删除，历史可从 Git 查阅。正式构建不依赖 `.reference/` 中的旧代码。

当前开发分发已切换为 MSI，历史已公开 EXE 的通过记录不代表新 MSI 已完成验证。当前分发状态仍为 `release-candidate-user-acceptance-pending`。干净 Windows、真实资料与凭据、真实模型与样卷、Sandbox 实机、实际打印、DPI/输入法/读屏和参考设备性能由用户执行，尚未通过。具体步骤见 [验收清单](docs/acceptance.zh-CN.md)。

## 最近已记录的验证

### 视觉验收方式约定（2026-10-18）

按用户澄清，界面布局与视觉效果使用实际应用自动截图测试，Computer Use 仅用于功能测试。约定已同步到 `AGENTS.md` 与[设计原则](docs/design-principles.zh-CN.md)，后续每轮维护需记录截图生成方式、检查范围、证据、修复结果与未验证项，不得将 Computer Use 操作计为布局视觉验收。任务页本轮最终截图复查仍在进行，不能以此前构建、回归检查或 Computer Use 操作替代。

### 界面与打印字体替换（2026-10-19）

用户要求按设计文档应用字体：界面用微软雅黑（非衬线），打印 PDF 用衬线且只用新宋体。本轮已落地，不再只是约定。

此前界面混用字族：窗口外壳因 WinUI 的隐式字体解析落到微软雅黑，而题目正文与公式预览绑定随包的思源宋体（衬线），`output/ui-polish/final-workflow.png` 可见同一界面内两种字族并存；导出 PDF 也用思源宋体。现已按用户确认的两项决定实施：打印衬线字体取 **新宋体（NSimSun）**（`simsun.ttc` 中更适合打印的一款，实测可被 Typst 解析并以 `/BaseFont=NSimSun` 内嵌），并**彻底移除**随包分发的思源宋体（约 24 MB），使“不选用其他衬线字体”真正成立。

实现：`TypstToolchain` 去掉字体目录参数，导出与预览各自取用打印模板（NSimSun）与界面模板（微软雅黑）；排版编译不再用 `--ignore-system-fonts`，改为使用系统已安装字体。界面侧在 `Themes/WorkspaceResources.xaml` 中覆盖 `ContentControlThemeFontFamily` 与框架文本样式键（该字典在 `XamlControlsResources` 之后合并，故重定义生效且无需改动每处控件），题目与公式预览改用微软雅黑。新宋体缺失时 Typst 只发警告且退出码为 0，已补断言改为明确抛出中文提示，不再静默替换字体。随包的思源宋体及其许可从 lock、准备脚本、csproj、打包脚本与离线包校验清单中一并移除。

验证：Debug 与 Release（`PublishAot=false`）整个解决方案构建均 0 警告/0 错误。文档探针通过，实际生成的 4 份 A3/A4 题目与答案 PDF 内嵌字体实测为 `NSimSun` + `NewCMMath-Book`，抽看渲染页确认新宋体排版与公式清晰。界面视觉验收使用应用自动截图：`--workflow-smoke`（48 项，唯一不通过项为它专门要求的 `native-aot` 身份断言）、`--import-smoke` 与 `--task-smoke`，实际查看 `output/font-final/`、`output/font-import/`、`output/font-task/` 各截图，覆盖题目详情的空/单项/多项/长文本、试卷页、原件展开、导入与“先设置识别模型”及关于对话框、错误恢复、设置与执行/外观各卡片、任务页各状态，以及浅/深主题与宽窄窗口；确认题目正文与界面外壳同为微软雅黑、无混排，无截断、溢出或对齐问题。格式、仓库边界与 `git diff --check` 通过。

未验证：实际物理打印、干净系统与 Windows N/KN 版（无新宋体）下的降级提示、完整 Native AOT 发布与 MSI 重新分发；本机未安装 PowerShell 7，`Invoke-MigrationChecks.ps1` 与打包脚本本轮未实际执行（仅做了 UTF-8 解析检查与引用清理核对）。已发布的候选包不包含本轮源码修改。

### 字体选用约定（2026-10-18）

用户明确约定界面展示使用非衬线字体，打印输出使用衬线字体，且衬线字体只选用新宋体（Windows 自带的宋体）。规则已同步到 `AGENTS.md` 与[设计原则](docs/design-principles.zh-CN.md)。全部路径的替换已于 2026-10-19 完成，见上条；实际打印验收仍待用户执行。

### 普通桌面启动与原生异常日志（2026-10-18）

用户报告普通 `dotnet run --project src/OhMyStudy.App -c Debug -p:Platform=x64` 只输出启动一行便退出。上一轮隔离渲染与冒烟运行没有覆盖普通桌面的跨进程辅助功能查询，不能据此认定普通启动已验收。本轮读取用户 PID 76864 的本机转储和 Windows 事件：`Microsoft.UI.Xaml.dll` 原生退出码 `0xC000027B`，WER 记录 `0x8001010E`，调用栈经过 `uiautomationcore`、WinUI 与 `RoFailFastWithErrorContextInternal2`。该退出绕过 C# 未处理异常事件，因此原有控制台日志无法捕获它。

已替换缺少显式 STA 标记的自动生成入口，使用 `[STAThread]` 入口并安装 UI 同步上下文。命令行 Debug 保留独立控制台宿主，界面运行在无额外控制台的子进程中；宿主转发输出、等待并传递退出码，即使界面原生快速退出也会输出错误。启动阶段与异常日志同步保存到实际运行目录的 `logs/debug-日期-进程号.log`，启动时输出完整路径；控制台、调试器与文件三个输出互不阻断。F5 已附加调试器时直接运行界面，Release 不启动控制台宿主。所有更改均为源码维护，未发布安装包。

普通桌面已实际验证三轮启动（含一次用户原样命令，经过构建后启动），实际查看首页、读取辅助功能树、切换设置并正常关闭。原样命令的界面从 22:08:23 持续到 22:10:08，宿主记录 `ExitCode=0`；另两轮也正常关闭。实际桌面画面与辅助功能检查保留在本轮 computer-use 工具记录中，未以隔离截图代替普通启动；复制的完整日志为 `output/startup-review/desktop-exact-command.log`。

最终 Debug 与 Release（`PublishAot=false`）构建 0 警告/0 错误；隔离导入检查新增 UI STA 断言，10 项通过。额外在导入完成的隔离库中强制结束界面子进程，确认宿主在标准错误与共享日志同时记录 `0xFFFFFFFF` 并结束，没有遗留界面进程；这是异常进程退出的监视验证，不伪称再次制造了原来的 WinUI 崩溃。最终报告、日志及本轮实际 XAML 渲染在 `output/startup-review/final-*`，可提交摘要见[启动验证](docs/verification/startup.2026-10-18.json)。格式、仓库边界与 `git diff --check` 通过。旧的用户锁文件修改保持不变。

边界：本轮未配置或修改系统崩溃捕获策略，没有关闭辅助功能或后台软件，没有访问真实模型密钥。F5 断点路径、Ctrl+C、完整 Native AOT 发布、不同 Windows 版本仍待独立验证；之前的真实模型、DPI/输入法/读屏及安装包待验项继续保留。

### 试卷导入与 Debug 日志（2026-10-18）

当前工作树已调整为题库首页、顶部“导入试卷”、一次确认后自动识别并返回题库。识别保存的题目随进度刷新；原件只从“查看原件”或“定位原件”展开，窄窗口查看原件时收起题目区域。没有模型时提示前往设置，取消不会保存原件或伪称题目入库。损坏 PDF、识别未启动和题库刷新失败分别反馈真实保存结果。

修复普通构建未复制 `native/pdfium.dll` 导致 PDF 导入失败的问题；构建自动复制已准备的 PDFium、WebP、Typst 与字体，缺少 PDFium 时在构建阶段给出准备命令。Debug 使用控制台子系统，UTF-8 日志覆盖启动、XAML、导入阶段/耗时、后台识别与完整异常；不记录密钥或试卷内容。Release 保持 WinExe。

本轮验证：最终 Debug、Release（`PublishAot=false`）构建均为 0 警告/0 错误；导入专项 9 项、媒体 26 项通过。Debug 工作流 47 项功能检查通过，唯一不通过项为它专门要求的 `native-aot` 身份断言，因此工作流总报告仍为 `Passed=false`，不冒充 Native AOT 验收。确认 Debug PE subsystem=3、PDF 依赖位于实际运行目录，损坏 PDF 的中文、堆栈与底层错误码实际输出到标准错误。格式、仓库边界与 `git diff --check` 通过。详见[本轮摘要](docs/verification/import-flow.2026-10-18.json)。

视觉检查使用最终应用的实际 XAML 渲染：题库空、单题、多题、空错题、长答案、试卷页、原件展开/收起、导入及未配置模型对话框、错误恢复、设置和关于；覆盖浅/深主题及 1440、720、460 像素宽度。修复了列表更新期间反复布局造成的重叠与异常、重复题号、侧栏未展开的空白、窄窗口工具栏对齐与空间、保存按钮被滚动边界截断、无任务时的多余控件，以及深色主题下导入对话框仍为浅色的问题，均重新渲染复查。原始证据在忽略的 `output/import-review/`，包含 `import.*.png`、`workflow.*.png`、导入报告与 `console.log`；摘要保存截图文件名与哈希。

导入专项使用真实 PDF/PNG 解码和同一导入方法，识别任务为隔离模拟结果，不访问真实凭据或外部模型。本轮没有验证真实模型识别质量、真实桌面文件选择/拖放输入、DPI/输入法/读屏与完整目标系统矩阵；原有待验项目继续保留。没有重发 Native AOT、ZIP 或 MSI，已发布版本不包含本轮源码修改。下一步使用真实样卷和已配置模型复核识别结果，再按发布流程构建候选包。

以下为 2026-10-18 已有报告，本次文档清理没有重新构建或运行应用。

- 统一运行 `migration-20261018-165025-f15e94a5`：11 分项通过；Setup 为发布检查，其余为实际运行。Domain 107、Storage 93、Provider 11、Execution 17、Grading 211、Media 26、Setup 40；Documents 生成 8 份、45 页 PDF。[核心摘要](docs/verification/core.2026-10-18.json)。
- 最后 App 增量及最终离线包：桌面与 30 项工作流通过；ZIP 解压、逐文件校验、隔离安装、从安装目录运行和卸载保留合成用户文件通过。[包摘要](docs/verification/package.2026-10-18.json)。
- 三次顺序启动 P95 为 636.3232ms；空库自然空闲约 104–106MiB，释放资源后约 201–202MiB，超过首轮 200MiB 门槛。未强制 GC，Rendering 回调不代表 GPU 呈现，不能宣称性能预算通过。[采样摘要](docs/verification/performance.2026-10-18.json)。性能采样针对摘要中标明的产物，不能替代最终包或后续版本的测量。

最近已验证候选：`OhMyStudy-win-x64-rc-20261018-170306-ed27d9ee.zip`，150542938 字节，SHA-256 为 `90ff04d3c7c8f3ce5ca5b14741295aa94a5885029e0e543eb70c1876dd1e26cd`。源码提交为 `06665ead878cd5ebb6830474234dff9f216604a7`；之后的文档和 CI 测试等待预算修改未重建产品二进制。该包不代表当前工作树重新构建的结果。

原始本机报告在忽略的 `output/`：统一运行入口为 `output/migration/latest-migration-run.json`，包入口为 `output/migration/latest-package.json`，安装验证为 `output/migration/evidence/p9-final-delivery.json`。这些路径不是新克隆附带的报告；可提交摘要保存在上述 `docs/verification/`。

## 维护与验证入口

构建准备见 [README](README.md#开发与验证)，完整流水线见 [CI 与版本发布](docs/ci.zh-CN.md)。包内验证命令：

```powershell
./packaging/Test-OfflinePackage.ps1 -PackageZip <ZIP绝对路径> -DesktopSmoke -WorkflowSmoke -InstallSmoke
```

每次修改后运行对应场景，并更新本文件与实际产物、报告、失败和下一步；不能以旧报告给新构建记通过。自动检查使用隔离合成资料与凭据，不替代真实资料验收。

## 下一步与已知边界

- 按验收清单逐项记录系统、包哈希、结果与证据。尚未执行项保持未执行。
- 性能释放后内存超出门槛，需要后续优化并在参考设备复测；GPU、冷/热启动分布与长期稳定性仍待测量。
- 完整标签、高级筛选、按日期选错题、复习卷生成与独立校验、手机传图仍为产品待办。固定模板 PDF 导出不等于复习卷功能完成，详见 [需求](docs/requirements.zh-CN.md)。
- Sandbox 每次调用独立 VM，要求可证明归属的新 `wsb` CLI；Windows 10 或缺少 CLI 时不标为就绪。开始前明确确认本机回退，执行中失败不在宿主重放，详见 [执行与权限](docs/execution-permissions.zh-CN.md)。
- 旧资料只读备份、恢复到新目录并仅升级副本，全部初始化成功才切换；失败重新打开原库。真实切库前退出旧版并保留完整备份。凭据不随备份导出，跨账户需重新配置。
- 发行二进制尚未配置代码签名；哈希只证明完整性。干净系统安装及分发审核尚待完成，见 [安装说明](docs/installation.zh-CN.md)。
- CI 的操作和复现以 [CI 文档](docs/ci.zh-CN.md)为准，服务端实际结果从 Actions 查看，本机通过不能替代 CI 通过。

本次文档维护：删除完成阶段计划、阶段记录、迁移历史和被最终报告取代的证据；保留当前操作说明、兼容规则、最新可用验证摘要与未通过事项。

本次清理验证：当前 11 份说明文档的本地链接及 3 份验证 JSON 检查通过；仓库边界、打包脚本 PowerShell 语法和 `git diff --check` 通过。本次未重新执行应用回归。

Git 签名配置（2026-10-18）：本仓库显式设置 `gpg.format=ssh`、`commit.gpgsign=true`，使用当前账户已有 ED25519 密钥，并配置本地公钥验签文件。独立测试提交对象签名与 `git verify-commit` 验证通过，未改变分支历史。GitHub 验证还需账户登记对应 Signing Key。此前 `b7c1ab5` 未签名，不因配置更新自动补签；后续不得关闭签名提交。

0.1.0 发布准备（2026-10-18）：本次仅更新签名约定与交接文档；仓库边界检查、Release 干运行及五项无效输入回归、git diff --check 通过。计划推送 v0.1.0 标签，由 CI 对标签提交重新检查、构建并发布 ZIP 与校验清单；实际结果以该标签的 Actions 与 Release 为准，现有人工验收待办继续保留。

0.1.0 发布完成（2026-10-18）：SSH 签名提交 d28742c962e89223317c3904153b739f226af555 已推送至 main，v0.1.0 标签指向该提交。GitHub Actions 39119056807 的检查与测试、离线包构建、Release 发布全部成功：https://github.com/TaiRuiQu/oh-my-study/actions/runs/39119056807 。正式 Release：https://github.com/TaiRuiQu/oh-my-study/releases/tag/v0.1.0 ，已上传 OhMyStudy-v0.1.0-win-x64.zip（149301546 字节；SHA-256：3a4bcdb83ec7cb273a4cfeee411d79bb195b60edbfff447984f32f6bbd17aadc）、SHA256SUMS.txt 和 build-manifest.json。该版本按普通 Release 发布；构建保留人工验收待完成状态，不能据此宣称人工验收或二进制签名已通过。下一步继续执行上述验收与产品待办。


多版本发布流程（2026-10-18）：正在扩展四项构建（普通 Native AOT / AVX-512 Native AOT / 非 AOT self-contained / 非 AOT framework-dependent），每项同时生成 portable ZIP 与独立 installer EXE。通用原生安装器内嵌相同 ZIP，安装后保留小启动器；发布作业要求四项及八件套全部通过。用户要求同时补充 0.1.0，保留已有标签，新增文件记录当前源码提交。已通过：安装器实际 Native AOT 回归 47 项；完整八件套发布干运行及缺失/无效/混合提交等拒绝用例；非 AOT framework-dependent WinUI 实际编译。正在验证完整 CI 与四项打包，不能将这些新变体记为已正式发布或已通过人工验收。

本机补充验证：Lint（C# 格式及 actionlint）与 Checks（仓库边界、全部 PowerShell 语法、JSON/XML、八件套发布回归、解决方案警告视为错误编译）通过，编译为 0 警告/0 错误；显式 x86-64,-avx512 的通用安装器 Native AOT 编译通过。下一步推送并手动运行 release_tag=v0.1.0 的 CI，待完整四项成功再记录已发布。

首次补充发布 CI 39121014511：完整质量检查通过；两项 AOT 构建在锁定还原失败，原因是全局 PublishAot=true 给引用库增加 ILCompiler 依赖。已将运行时属性限制在 App 工程，四种 profile 均以 --force --locked-mode 实际还原通过，并加入 CI Checks 防止增量还原掩盖此问题。本机非 AOT 桌面检查及含内嵌 ZIP 的实际原生 installer 隔离安装/卸载也通过。完整矩阵重跑结果待记录。

首轮两项非 AOT 构建均已成功完成 portable、独立 installer、安装后校验及卸载；发布因 AOT 两项失败而正确跳过。修复后本机 Checks（含四种强制锁定还原）和解决方案编译全部通过，0 警告/0 错误。

四种构建补充发布完成（2026-10-18）：最终源码 b431f6f91b8078ba99f5abc645d8faf58a6976ed；CI 39121738918 的完整检查/回归、四项构建、各项 portable 校验和独立 installer 实际安装/安装后逐文件校验/卸载、统一发布全部成功：https://github.com/TaiRuiQu/oh-my-study/actions/runs/39121738918 。0.1.0 Release 已公开四种构建的 4 个 ZIP + 4 个 EXE，两个清单均已下载复核为八个文件且来源提交一致。[发布资产与哈希快照](docs/verification/release-variants.2026-10-18.json)。保留原 v0.1.0 标签 d28742c，旧单一 ZIP 继续保留；新增八件套及其真实来源在 Release 说明与新构建清单中明确。工作流现已默认每次发布完整八件套。AVX-512 App 未在托管运行器执行，人工验收和二进制代码签名待办继续保留。

MSI 安装向导完成（2026-10-18）：按用户最终选择改为“所有用户”安装，默认 `%ProgramFiles%/OhMyStudy`，安装由 Windows 请求管理员确认。使用固定 WiX 5.0.2 和 Windows Installer 标准向导，支持安装位置、桌面快捷方式选项与实际安装进度。此前自写 Win32 配置界面已撤回；四种构建的流水线现产出 ZIP + MSI，portable 不再包含旧 Setup。旧 Setup 与测试只作历史 EXE 兼容维护，不参与当前发布包构建或执行。旧 EXE 安装需先卸载，学习资料保留。

本机验证：12 项管理员上下文的 MSI 回归通过，覆盖中文/空格位置、桌面选项与保留、升级、拒绝降级、失败升级回滚、同版本构建切换、卸载和额外文件保留。完整 AOT MSI 已实际安装、校验全部 4799 个载荷文件并卸载；交互检查通过欢迎/许可、目录浏览、桌面选项取消和安装确认页（有管理员盾牌），预览未开始安装并已关闭。PowerShell 语法、WiX XML、actionlint、仓库边界、ZIP+MSI 八件套发布干运行（含拒绝旧 EXE）和 git diff --check 通过。所有用户方案没有出现先前免管理员实验的回滚注册/卸载问题；该旧实验未交付，使用完整组件重装后卸载完成清理，保留失败证据。

本地试用产物：`output/msi-preview/OhMyStudy-win-x64-aot-rc-20261018-195451-a7a2843d/OhMyStudy-win-x64-aot-rc-20261018-195451-a7a2843d-installer.msi`，112394361 字节，SHA-256 `7a9077d322758777ba8aa9ba3ec133305d214dcee076d5e24b6d0c605c75c122`。App 为已发布 v0.1.0 普通 AOT 的原二进制，源提交 b431f6f，当前 HEAD 与该提交的 src 无代码差异；只重做分发与安装器，源码快照包含本次工作树。原包 ZIP 哈希和验证摘要见 [MSI 摘要](docs/verification/msi.2026-10-18.json)。之后的交接摘要更新未重做 MSI，不能将其解释为新的二进制构建。

下一步：在新提交上运行完整四项 CI 构建并发布 MSI；当前只验证完整 AOT MSI 和四格式发布规则，不能把其余三种 MSI 或 GitHub Actions 记为已通过。GitHub 已有 v0.1.0 Release 仍为此前 EXE，未覆盖。继续执行干净 Windows 10/11、真实资料、凭据、Sandbox、打印、辅助功能与性能验收；二进制代码签名仍待配置。

设置与资料库更新（2026-10-18）：按用户示意图改为左侧供应商列表、右侧供应商与模型配置，窄窗口上下排列且占满可用宽度。每个模型独立编辑显示名、ID、上下文窗口、图片/工具能力、思考强度或预算及价格；显示名用于任务选择，实际请求仍使用模型 ID。获取列表、单次模型连接测试和 models.dev 信息填充分开，填充按 ID 精确匹配并由用户选择价格来源，不猜测思考强度。供应商与模型的独立保存保留另一部分的草稿，切换、退出前提示未保存后果，设置页 Ctrl+S 保存设置。设置标题和操作栏独立，旧版资料发现提示只属于资料页，关闭后持久记录；示例提示也恢复正确标题。

资料库设置显示保存位置，可更改、打开文件夹或选择已使用的新版资料库。导入原件本来就保存独立副本，本次明确保留这条正式导入链路并增加删除源文件后的读取回归；资料库不可用时不再退化为未保存的普通预览。更改位置通过完整备份复制数据库与全部受管原件，保留原库，初始化成功才原子更新默认路径，失败恢复原会话。重启遇到不可用路径时不静默创建空库。选择已有资料库只读检查新版标记和版本，备份与旧资料继续走原有恢复/复制升级流程，避免就地升级旧资料。

本次验证：Domain Native AOT 107 项、Storage Native AOT 104 项、Provider Native AOT 12 组通过；最终 App Native AOT 46 项完整工作流及桌面检查通过，已目视检查宽屏、窄屏、模型与资料库截图。覆盖原件独立保存、路径持久化、重启、原库保留、切换失败回退、不可用路径恢复、模型元数据保存与价格校验、两部分草稿互不覆盖、旧提示关闭和窄屏宽度。早期 Native AOT 工作流两次遇到备份目录提升时“拒绝访问”，原库与默认路径未变；现为访问/共享占用加入总计最多约 1.44 秒的可取消重试，不重复复制或覆盖目标。实际 Windows 文件句柄占用、释放后恢复和取消保留数据回归通过，最终 App 回归通过。最终 EXE SHA-256 为 `0b368589822a3ddf991ac1dc0bd35d584c899a65c15922b4cfc0894f35881266`。[本次摘要及源码文件哈希](docs/verification/ui-library.2026-10-18.json)。

验证环境边界：NuGet 漏洞数据服务连接失败，本次本地锁定还原显式使用 NuGetAudit=false，不记漏洞审计通过。保留开始工作时已有的 App packages.lock.json 修改；AOT 验证从 HEAD 复制锁文件，通过忽略目录中的临时 MSBuild 导入指定该副本，未覆盖原修改。C# 格式验证、仓库边界和 git diff --check 通过。真实供应商/凭据/费用、此主机的 models.dev 实际请求、安装器与完整四变体、物理打印、Sandbox、辅助功能和性能未在本次验收。

下一步：用户试用本次设置和资料库流程；真实供应商与自定义目录权限/磁盘空间继续验收。本次变更尚未提交、打包或发布，已安装版本及上述 MSI 状态不因此更新。

界面细节打磨（2026-10-18）：修正用户截图指出的两处多余留白：供应商列表不再以 80px 最小高度撑开，供应商反馈为空时折叠。相同处理覆盖模型、目录、执行说明等可选文本和空模型列表；宽屏不为未使用的网格行保留间距，设置/任务使用完整内容区，保留统一的卡片内边距。显示名与 ID 并排，价格与货币同排，空间不足时转单列；操作按钮按实际可用空间排列，思考选项完整显示。添加长名称悬停提示、未选择供应商/模型时的按钮状态，清理重复的主题/原件标签及关于卡片；修复系统导航“设置”在主题或模板更新后变回英文的问题。布局检查约定已补入设计原则。

本轮验证：最终 Native AOT App 构建通过，现有 46 项工作流和桌面检查通过；C# 格式、仓库边界和 git diff --check 通过。按正常配置、未配置供应商、多供应商与长名称、模型编辑、资料库、720px 窗口、460px 窗口及其模型编辑、深色主题、错误反馈检查了 10 组实际 XAML 树截图，保留原始文件；布局改动没有另写镜像样式的单元测试。最终 EXE SHA-256 为 `83b868fbe41a42fc889b0158c49b154c7cfa0a38f5ce7ad4c590a96f4c21837b`。[本轮证据与截图索引](docs/verification/ui-polish.2026-10-18.json)。

本轮沿用上文的隔离锁文件与 NuGetAudit=false 环境约束，没有改动原有锁文件修改；Domain、Storage、Provider 未因本轮 UI 调整重复运行，原有结果作为历史证据保留。真实 DPI/输入法/读屏及性能、完整安装与发布仍待独立验收。本轮仍为未提交、未发布的本地修改，下一步由用户试用更新后的界面，再按发布流程生成新安装包。

思考强度补充（2026-10-18）：按用户要求在 Responses 模型配置中加入 `max`，保留 Messages 已有的 `max`；宽窗口七个选项同排，窄窗口自动换行。Native AOT App 构建及 48 项工作流通过，新增覆盖选项可见、保存后重新载入仍勾选；Provider Native AOT 12 组回归通过，补充 Responses/ Messages 的 `max` 请求参数传递用例。已目视检查宽、窄模型编辑截图，C# 格式、仓库边界与 git diff --check 通过。证据位于 `output/model-max/`，App EXE SHA-256 为 `18fb4b49a29c46790a8408c518a74852cd5a617946ee103a7813809c1a36124c`。本次沿用隔离锁文件及 NuGetAudit=false 的环境约束，未连接真实供应商；模型实际支持范围仍由用户按供应商能力配置。当前仍为未提交、未打包、未发布的本地修改，下一步试用新增选项。

视觉验收规范补充（2026-10-18）：按用户要求在 `AGENTS.md` 与 `docs/design-principles.zh-CN.md` 写入强制约定：每次 UI 修改，无论大小，都须实际运行渲染并视觉检查；逐项修复留白、布局、重复信息、间距等细节，修复后重新渲染复查，达到完美视觉效果、最佳实践与直觉要求才能完成。维护记录保留检查范围、证据和未验证项，不能用构建或测试代替视觉验收。本次仅修改规范文档，已审阅内容及 Markdown 差异，`git diff --check` 通过；未改 UI，未重复运行应用或构建。下一次 UI 修改起持续执行上述验收约定。

源码提交交接（2026-10-18）：按用户要求，本轮交付以 SSH 签名提交并推送到 `main`，范围包含设置重写、资料库位置与原件保留、细节打磨、`max` 选项、回归用例及视觉验收规范；上文“未提交”为各轮验证当时状态。沿用最终 App Native AOT 48 项工作流、Provider Native AOT 12 组及已审阅截图的证据，提交前复核差异、仓库边界与证据完整性。开始工作前已有的 App `packages.lock.json` 改动不纳入本次提交，保留本地。此次不生成安装包、不发布 Release；下一步在提交后的源码上执行完整发布流程与尚未完成的真实环境验收。

CI 锁定还原与 Lint 修复（2026-10-19）：Actions 39187253785 在 Lint 前的 Release 还原失败（NU1004）：App 默认锁文件被非 AOT 还原覆盖，缺少 Microsoft.DotNet.ILCompiler 10.0.13 及其 Windows 运行时依赖。已按锁定 SDK 重新生成 Release AOT 锁文件；Debug 和显式 PublishAot=false 改用已有 self-contained 锁文件，framework-dependent 继续使用独立锁文件。在 Checks 中增加 Debug、显式非 AOT 的强制锁定还原，并强制重新还原最终 Release 解决方案，避免增量还原掩盖不一致。另移除 MainWindow.Library.cs 与 MainWindow.xaml.cs 的 UTF-8 BOM，并统一 LF，满足现有格式规则；两文件除此之外内容未变，没有界面行为或布局修改，本轮不涉及视觉验收。

本地验证：原样执行 Invoke-CI.ps1 的 Lint 与 Checks 均通过，包含 C# 格式、固定版本 actionlint、仓库边界、PowerShell 语法、JSON/XML、八件套发布回归、四种发布配置及 Debug/显式非 AOT/最终 Release 的强制锁定还原；解决方案 Release 编译为 0 警告、0 错误，NuGetAudit 保持开启。日志位于 output/ci/lint-fix.log 和 output/ci/checks-fix.log，git diff --check 通过。下一步 SSH 签名提交并推送，跟进新 Actions 的 Lint 与质量作业；远端测试、完整四项打包及发布尚未验证，本次不发布 Release。
