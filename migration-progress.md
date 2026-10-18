# 原生迁移进度与接续说明

更新：2026-10-18（Asia/Shanghai）。执行依据：[migration.md](migration.md)。本轮继续完成剩余实现；每阶段更新本文件并以 `git -c commit.gpgsign=false commit` 提交。**用户已明确：干净系统、Sandbox实机、真实模型/样卷、打印及其他外部人工验收由用户执行，本轮跳过；跳过不计通过。** 实现、本机自动验证与用户验收分别记录。

## 本轮阶段交付（继续执行）

- **P3实现收尾完成**：新增 `LibraryMigrationService.CopyAndUpgradeAsync`，只读原库→在线一致备份→新目录恢复→仅副本升级→文档/题目/参考答案/覆盖/模型调用/任务事件/设置/凭据引用指纹校验。生成不含密钥的持久化收据；不改当前资料指针，不覆盖已有目录，取消和失败保留原库及已完成备份。
- **P3实际验证**：`output/migration/publish/storage-copy-upgrade/OhMyStudy.Storage.Tests.exe` 为实际 Native AOT；80项通过。新增18项覆盖v1/v2副本升级、原库字节不变、凭据引用、不读密钥、原版本备份恢复、覆盖/路径/取消拒绝。首次离线还原有NU1900漏洞数据源不可达提示，代码编译/AOT无诊断；最终统一联网还原与回归会另记。
- **历史验证纠正**：此前Provider测试工程缺少AOT发布属性，普通apphost也没有CLR头，旧PE检查错误地接受了它。此前“8组全部实际AOT”不适用于Provider分项。已补真实AOT设置与防apphost检查；新的实际AOT产物 `output/migration/publish/provider-aot-loopback/OhMyStudy.Provider.Tests.exe` 已通过11场景（含真实127.0.0.1套接字三协议/分块SSE/模型分页/禁止认证重定向/在途取消），随后P5提交会记录完整结果。
- **P4实现收尾完成**：新CLI按随机ID创建/停止自己会话；只读运行时/请求/输入、单独可写结果；nonce/调用摘要校验、stop成功后读结果，忙时拒绝/取消回收/失败不在宿主重放。不可确认归属的旧系统保持未就绪；不自动启用系统功能或提权。
- **P4实际验证**：`output/migration/publish/execution-p4-complete/OhMyStudy.Execution.Tests.exe`，17组实际AOT通过，证据`output/migration/evidence/execution-p4-complete/execution-report.json`。含6组隔离沙箱生命周期/协议、固定guest worker在宿主隔离运行的5项计算；这不等于Sandbox实机验收。私有运行时清单/缓存键/包校验已加入guest worker哈希；新版运行时从固定本地归档离线重建，不改系统环境。
- **P5实现收尾完成**：原协议/整卷回归之外，已接入真实环回套接字服务，不注入HTTP替身；三协议实际默认网络栈的请求、分块SSE、Unicode/分页、认证重定向拒绝和在途取消通过。专用测试实际AOT11场景通过；真实供应商和真实样卷由用户负责。发布检测已阻止旧apphost误判，锁文件同步。
- **P6文档管线收尾完成**：递归小问、选项、图像、宽/不等长表格、长题干与公式回退进入统一A3/A4题目/答案模板；预览/导出共享字体和公式拆分，不输出学生作答。含手写题图不会无提示进入题目卷。`output/migration/documents-complete-aot/verification{,-pdf}.json`：实际AOT文档探针通过，8份PDF共45页；519单元格、12层小问、图片、9000字符原文回退和隔离通过。独立检查字体嵌入/图像/逐字页界，45页全部Poppler渲染并查看联系图。App复杂导出适配和分级预取随P7界面集成提交；真实打印由用户负责。
- **P7实现收尾完成**：原生递归题目编辑/表格/公式/图像/选项/来源/多层小问、教师与作答隔离、保存失败留窗、保存期间继续输入保护；参考答案匹配/覆盖结果/来源定位；页面/题目/列表/焦点恢复、异步代次与取消、切库失败恢复原库，完整初始化后才更新资料指针；退出/切库对未保存修改明确提示。
- **P7实际验证**：`output/migration/publish/app-final/OhMyStudy.App.exe`，`output/migration/evidence/app-final/workflow-smoke.json` 28项全部通过。独立桌面冒烟同时通过标题栏/500题虚拟化/主题/打印API。已查看深浅色图片；分级缓存与受限预取接入，缓存9437184字节、20次缓存翻页P95=15.4756ms。运行末338427904私有字节不是空闲预算；性能阶段另记。新增性能入口仅显式自检启用，不影响默认用户流程。
- **P8实现与采样完成**：快捷键、窄窗口布局、原生焦点/高对比资源与局部取消；3次独立AOT性能采集完成，`output/migration/evidence/performance-final/summary.json`。顺序启动P95=636.3232ms，空库自然空闲约104–106MiB，阅读P95=20.65–23.98ms，UI分发P95=1.59–1.90ms。释放缓存后自然空闲约201–202MiB略高于200MiB门槛，未调整预算；Rendering回调不等于呈现帧。真实参考机/DPI/IME/读屏及GPU/ETW由用户验收。
- P6复杂导出与公式、P7递归编辑/阅读上下文正在收尾；P8与P9由主代理接续。代理不直接提交，避免混入不同阶段的在途文件。

用户验收接续清单将在 `docs/migration/user-acceptance.zh-CN.md` 汇总；真实资料/凭据没有被本轮测试访问或切换。

## 最新统一验证与接续入口

### GitHub交接（2026-10-18）

用户要求保存当前更改、切换GitHub并配置CI。现有迁移已提交为`3a5bb2c`，`origin`已改为`https://github.com/TaiRuiQu/oh-my-study.git`（fetch/push）。GitHub仓库初次检查为空；未覆盖任何远端历史。

新增`.github/workflows/ci.yml`：`main`推送/PR/手动运行检查后构建并上传离线ZIP；tag推送通过同一链后发布对应Release和校验文件，删除tag不发布。只给发布job写权限，不使用真实模型密钥；官方Actions固定SHA，原生工具与actionlint固定版本/哈希。服务器CI不运行桌面/物理打印，把这些验收继续单列。详细复现见[CI说明](docs/ci.zh-CN.md)。

CI四阶段已在本机逐一执行通过：格式/actionlint、结构/语法/发布输入回归、0警告/0错误的解决方案构建；8组实际AOT套件全部通过（运行`migration-20261018-155333-e28e4650`），另生成并校验`output/ci/artifacts/`内的ZIP、SHA256SUMS与构建清单。新增基础设施6项为不访问真实凭据的检查，Windows凭据真实读写仍是此前独立证据。

推送后的服务端结果从仓库Actions中的`CI`查看，主工作流按提交固定源码构建，不能将本机成功当成服务端已通过。未创建版本tag或额外Release；后续由维护者选择实际版本号后推送tag。原生迁移的未关闭验收门槛保持不变。

**可运行候选包已生成并复验**：`output/releases/OhMyStudy-win-x64-rc-20261018-153255-d5509074/portable/OhMyStudy.App.exe`；同目录上层ZIP为`OhMyStudy-win-x64-rc-20261018-153255-d5509074.zip`，大小146,267,047字节（139.49MiB），SHA-256 `cbe4b538c1f2e035b3e23f5ccecdf2c5589151d6975203930998aa55acdd5e56`。4820个包文件及4479个私有运行时文件校验通过，ZIP重新解压后桌面与17项隔离资料流程再次通过。可提交摘要：[发布证据](docs/migration/evidence.2026-10-18.package.json)；完整普通用户验证：`output/migration/evidence/p9-final-zip-user-2026-10-18.json`。CIM采样可用，未观察到禁止的子进程；100ms采样可能漏掉短进程，不能当完整调用图或执行沙箱证明。

包内保存发布时的源码快照、许可证、组件与哈希清单。其后仅修正`NativeQuestionPresenter`一处初始化器的两处换行，并更新文档/证据；没有行为变化，不声称当前所有工作树字节已可直接重现该EXE。最终整解决方案Release构建0警告/0错误，空格格式核对通过（工作区加载有设计时警告，格式检查退出0），PowerShell语法与Git差异检查通过。未提交或推送Git变更。

2026-10-18执行`./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke`，运行ID **`migration-20261018-152724-1b3b62f9`**，退出0，8个发布/实跑分项全部通过，日志没有编译、裁剪、AOT或NuGet警告。入口：`output/migration/latest-migration-run.json`。`status=release-candidate-unvalidated`与`checkStatus=selected-checks-passed`故意分别记录发布验收和已选本机检查，不将未执行矩阵算通过。

| 分项 | 新C#实际Native AOT结果 |
| --- | --- |
| Domain | 107项：JSON、评分/来源/覆盖/配置、权限摘要及边界；未加载CoreCLR |
| Storage | 62项：v1/v2读写/迁移/只读备份恢复/资产/事务/凭据替身/手改保护；独立合成库 |
| Provider | 10协议场景：三协议、流式Unicode/签名/工具、用量/费用、错误/重试/取消；无供应商网络 |
| Execution | 10场景：单队列/授权/事件/取消/迟到保存/重启/进程树，额外实际私有Python绘图计算与PowerShell参数 |
| Grading | 211断言：三协议×三模式整卷，参考答案/教师/手改保护、保存/覆盖、失败保留/取消迟到、60轮与退出竞态 |
| Media | 26项：PNG/JPEG/WebP/PDF、原图裁片、四旋转/坐标、容器尺寸先验、缓存/并行/取消；系统WebP缺codec时固定dwebp回退 |
| Documents | 原公式/化学/非法输入与额外符号实排、A3/A4题答隔离4份PDF、PDFium栅格/文本；报告`documents/verification.json` |
| App | 标题栏/导航/双向绑定/500题真实虚拟化/主题/取消/打印API通过；持久化工作区17项通过 |

最终桌面原始报告和已查看截图位于该运行的`desktop-smoke.{json,png}`、`workflow-smoke.json`、`workflow-smoke.{png,dark.png}`。工作区实际导入12页、保持原件字节、保存旋转/题目、来源定位、空白卷不制造错题、备份重新打开、未保存草稿刷新保护及主题/输入焦点均通过。缓存返回20次P95 **17.1538ms**，工作负载末私有字节 **338,112,512**、像素缓存 **51,840,000**；这不是空闲/参考设备/帧预算验收。

本轮新工程为69个C#文件、16个解决方案/项目/构建文件；旧代码继续只在忽略的`.reference/`。完整新构建不需要Rust、Node或旧夹具脚本。默认真实资料库不会自动接管旧目录；开发验证只使用隔离合成资料。未修改用户真实资料、未读取真实密钥、未上传样卷、未启用或启动Sandbox，也未实际打印。

阶段接续文档：[P2领域](docs/migration/p2-domain.zh-CN.md)、[P3存储](docs/migration/p3-storage.zh-CN.md)、[P4执行](docs/migration/p4-execution.zh-CN.md)、[P5协议](docs/migration/p5-protocols.zh-CN.md)、[P5整卷](docs/migration/p5-grading.zh-CN.md)、[P6媒体](docs/migration/p6-media.zh-CN.md)、[P7界面](docs/migration/p7-workflows.zh-CN.md)、[P8体验](docs/migration/p8-experience.zh-CN.md)、[P9候选包](docs/migration/p9-release.zh-CN.md)。

## 当前执行：领域、存储、任务、协议与真实工作区

用户已要求继续剩余阶段。将可在本机隔离验证的实现继续推进；P1的干净系统、真实公式语义与物理打印等门槛继续保留，不把新代码通过记成这些验收已完成，也不切换用户真实资料。

- `src/OhMyStudy.Domain/`已重写旧DTO、严格/宽松JSON、评分/来源/覆盖与权限摘要；独立Native AOT领域测试107项通过，报告`output/migration/domain-aot-tests.json`，说明见`docs/migration/p2-domain.zh-CN.md`。
- `Infrastructure/Storage/`已加入生产仓储、迁移、事务、资产、凭据协调与备份恢复；62项实际Native AOT检查通过，包含v1/v2只读备份→新目录恢复。
- `Application/Jobs/`与`Application/Execution/`实现单任务队列、权限、取消提交屏障；`Infrastructure/Execution/`实现挂起创建后绑定Windows Job、受限输出与进程树回收。首轮实际AOT9场景通过，私有Python计算/PowerShell参数新增场景在本次统一检查也已通过。
- 三协议C#请求/响应/SSE/上下文/取消已实现，实际AOT协议10场景和整卷211断言通过，未调用真实供应商。
- 原生媒体、来源变换、有界缓存、WebP自包含回退已接入，26项AOT检查通过。资料导入、编辑/来源/任务/模型/备份恢复界面已连接真实C#服务；12页隔离工作区首轮16检查通过，正在统一最终复验。
- 私有计算运行时由新PowerShell脚本按独立锁下载校验，实际验证NumPy/SymPy/Matplotlib/Pillow。未启用或启动Windows Sandbox。

下列标题栏与P1记录保留历史证据。当前状态优先采用本节、阶段表与最终候选包记录。

## 最新交接：一体式原生标题栏

用户要求标题与顶部导航参照Microsoft Store等原生软件。已将经典系统标题条和导航内重复标题合并为一行48逻辑像素的WinUI `TitleBar`：应用图标/名称只显示一次，菜单切换移到顶部；侧栏保留原导航。最小化/最大化/关闭与标题拖动继续由系统处理，没有自绘按钮或添加无功能搜索框。

- 改动位于`src/OhMyStudy.App/MainWindow.xaml(.cs)`、桌面报告及`Assets/`。图标为独立保存的现有品牌资源，工程复制发布，不引用`.reference/`。
- 窗口按钮随实际浅/深主题同步前景、悬停、按下及非活动颜色；高对比设置监听采用面向桌面窗口的`Microsoft.UI.System.ThemeSettings`。初次使用旧`AccessibilitySettings.HighContrastChanged`在本机无包身份进程订阅失败，已替换并实跑修复。实际高对比开关、多DPI、鼠标拖动/贴靠仍需P1系统矩阵人工验证。
- 实际Release Native AOT产物：`output/migration/publish/app-store-titlebar/OhMyStudy.App.exe`。桌面专项通过：`UnifiedTitleBar=true`、`TitleBarHeight=48`、`TitleBarPaneToggle=true`、`TitleBarCaptionTheme=true`，原有导航/绑定/虚拟化/主题/旋转/取消/打印API保持通过。
- 证据及已查看的界面图：`output/migration/evidence/store-titlebar/desktop-smoke.{json,png}`。截图为应用XAML内容，系统绘制的窗口按钮不会被该截图方式捕获；实际窗口保留它们。侧栏切换后等待原生动画完成再截图。
- `output/migration/latest-desktop.json`指向本次通过的产物。本轮只检查变更的桌面层，没有重复未变更的数据库或排版引擎回归，也没有进行真实资料/模型操作。P1完整门槛与P2–P9阶段不变。

原生标题栏与精简文案原则均已记录在`AGENTS.md`和`docs/design-principles.zh-CN.md`，供后续界面延续。

## 界面技术说明精简

用户于2026-10-18明确追加长期设计原则：**技术细节上精简，产品不向用户展示无助于操作的实现说明**。规则已写入根`AGENTS.md`和[产品设计原则](docs/design-principles.zh-CN.md)，并同步需求文档。技术证据继续保留在维护文档，不能为了界面简洁删除迁移状态或误称功能完成。

- 已移除界面中的压力测试副标题、开发页脚、AOT/框架/引擎说明、重复来源、“本次会话”字段后缀、内存/坐标变换状态及诊断任务菜单/永久状态。
- 设置和关于只显示用户信息；打开/旋转/定位成功不再输出实现过程，失败仅提供简短可执行提示。
- 保留一条“示例资料，修改不会保存”告知真实限制；“保留编辑”改为“完成”，反馈“已修改，尚未保存”。打开自己的原件后明确示例题目与文件无关，隐藏不可用的定位操作，读屏名称改为“原件”。
- 重新发布实际Native AOT：`output/migration/publish/app-simple-ui/OhMyStudy.App.exe`。示例与PDF两组桌面回归均通过，已查看两份应用自身截图：`output/migration/evidence/simple-ui/{workspace,pdf}-smoke.{json,png}`。导航、绑定、虚拟化、主题、旋转、取消、打印API及PDF翻页保持通过；本轮没有重复运行未变更的数据库/文档引擎套件。
- 最新桌面产物入口：`output/migration/latest-desktop.json`；`latest-run.json`仍表示下文最后一次完整检查，不把本轮界面专项伪装成新的全量验收。P1剩余门槛和P2–P9状态不变。

## 当前仓库与用户追加决定

用户要求旧代码全部移入Git忽略的`.reference/`，正式仓库呈现标准C#项目。已完成：

- 根目录为`OhMyStudy.slnx`、`global.json`、集中构建/包配置、`.editorconfig`，以及`src/`、`tests/`、`probes/`、`packaging/`、`docs/`。
- 旧`src/` Web界面、`src-tauri/`、Rust/Node清单与锁文件、旧脚本、Python运行时构建代码、排版探针、测试、依赖缓存和产物完整归档到`.reference/`。
- `.gitignore`包含`/.reference/`；`git ls-files -- .reference`为空，`git check-ignore`确认旧Cargo/npm文件被忽略。当前差异中旧路径显示删除是这次归档的预期结果；未改写Git历史，未提交/推送。
- 新构建不读取`.reference/`。旧SQL、DTO和协议样本已按原字节独立保存到`tests/Fixtures/{LegacyDatabase,Compatibility,ProviderProtocols}/`。旧Rust/Python对照工具仅留在本地参考目录，不是新测试依赖。
- 已**临时移开`.reference/`后构建整个Release解决方案**：退出0、0警告、0错误；随后参考目录已恢复。日志：`output/migration/reference-absent-build.log`。

这项仓库整理不代表业务重写或真实资料切换完成。新克隆不会携带`.reference/`；需要旧实现时，从迁移起点Git提交读取或恢复独立参考副本。

迁移起点：`0b5c5398e6e552792f3fe23cfaa12de7ad206f07`，提交时间`2026-10-18T12:14:09+08:00`。初始唯一未跟踪文件是用户提供的`migration.md`；原始计划副本本机保存在`.reference/migration.original.md`。当前计划已同步追加的根目录C#架构决定。

## 阶段状态

| 阶段 | 状态 | 交付或下一道门槛 |
| --- | --- | --- |
| P0 行为基线 | 完成 | 20命令、数据/JSON/协议/任务/权限清单，合成夹具，旧版回归，回退/兼容/性能测量方法 |
| P1 技术路线 | 本机通过，完整验收未关闭 | 实际Release Native AOT、原生工作区、SQLite/凭据、公式/PDF、打印API；系统矩阵/必要公式兼容等仍待验收 |
| P2 工程与领域 | 已实现，本机AOT兼容通过 | 完整旧DTO/评分/来源/覆盖/配置/JSON与摘要；107项通过 |
| P3 存储与凭据 | 实现完成，本机80项AOT通过 | 只读原库、一致备份、新目录升级、实体指纹与回退；真实切换由用户验收 |
| P4 任务与执行 | 实现完成，本机17组AOT通过 | 主机执行与按ID归属的Sandbox执行器；Sandbox真实系统验收由用户负责 |
| P5 协议与整卷 | 实现完成，实际环回与AOT回归通过 | 三协议11场景与整卷211断言；真实供应商/样卷由用户验收 |
| P6 文档管线 | 实现完成，复杂PDF实际AOT通过 | 原生图片/坐标/公式、8份45页复杂PDF；实际打印/授权样卷由用户验收 |
| P7 应用工作流 | 实现完成，28项实际AOT流程通过 | 递归编辑、完整结果、阅读状态、导出/恢复；实体输入和选择器由用户验收 |
| P8 体验与性能 | 实现与3次本机自动采样完成 | 原始启动/阅读/Rendering/分发/GC/内存曲线；参考机及实体交互由用户验收 |
| P9 发布与数据切换 | 离线候选包本机通过，发布验收未关闭 | portable/ZIP重解压双冒烟和完整哈希通过；干净系统/真实切换/安装矩阵未验 |

## 此前P1整链路通过的验证（历史记录）

最终整链路命令（仓库根目录，正常交互用户会话）：

```powershell
./packaging/Invoke-NativeChecks.ps1 -DesktopSmoke -Credentials
```

最终运行：**`20261018-140356-5ade080b`，退出0，`status=passed`，没有跳过项**。本机原始目录：`output/migration/runs/20261018-140356-5ade080b/`；最新入口：`output/migration/latest-run.json`。可提交的摘要及EXE/PDF/截图SHA256见 [native证据](docs/migration/evidence.2026-10-18.native.json)。这是本机合成技术检查通过，不是P1全部系统验收通过。

| 检查 | 实际结果与范围 |
| --- | --- |
| C#仓库结构 | 21个C#源文件、10个工程/构建文件；活动源码无Rust/Node实现，`.reference/`忽略和构建隔离 |
| 基础设施AOT | 7项通过；dynamic-code=false、源生成JSON、旧目录纯解析、合作实例锁、两份旧SQL/参数/外键/回滚/WAL/在线备份、随机合成UTF-8凭据读写删除 |
| 文档AOT | dynamic-code=false；9数学/化学/长式样本、11非法/超限输入、69额外符号实际排版，超长小问标签拒绝；PDFium栅格/文字/旋转/预取消 |
| PDF | A3题目2页/答案1页、A4题目3页/答案2页；尺寸、题答/学生作答隔离通过。本轮早期独立字体嵌入/逐字页面边界及8页视觉检查通过，历史QA原始产物在`output/migration/documents-aot/` |
| 原生桌面AOT | 真实窗口、资料/题库/错题/设置导航、双向编译绑定、主题切换、来源/旋转、取消、自身截图通过 |
| 虚拟化 | 500题，起始13个容器、末尾9个；实际滚到第500题再返回，native类确认为ItemsStackPanel、垂直方向 |
| 原生PDF界面 | 实际加载3页合成PDF，当前栅格1131×1600，翻到第2页并返回；后台PDFium+内存位图，用户页不写临时文件 |
| 打印API | GetForWindow、PrintDocument.DocumentSource、事件注册/注销通过；未打开打印对话框、未创建作业、未进行纸张打印 |
| 工程检查 | `dotnet format whitespace OhMyStudy.slnx --verify-no-changes --no-restore`、x64解决方案配置、XML/PowerShell语法及差异检查通过；整解决方案Release构建0警告/0错误 |

旧版P0回归在搬迁前实际完成：Rust默认53通过、4默认忽略；另PDFium/宿主/私有Python队列3项显式通过；私有runner9、旧排版单元5、前端type/lint/build、fmt/core Clippy通过。新增旧核心oracle为55解析/领域/策略/usage用例+1权限digest+5错题谓词；各套件不能合并为新客户端测试总数。完整旧证据见 [P0基线](docs/migration/p0-baseline.zh-CN.md) 和`tests/Fixtures/Compatibility/evidence.2026-10-18.synthetic.json`。

桌面报告`SyntheticDataOnly=false`仅表示传入了外部PDF路径，程序无法自行证明其来源；本轮传入的是同一运行的文档探针原创PDF，未访问用户资料或真实模型。

## 工具与环境

- SDK锁定`.NET 10.0.402`，运行时/ILCompiler`10.0.13`，MSBuild`18.9.15`，VS Build Tools`18.11.12306.390`，MSVC`14.51.36231`，已装Windows SDK`10.0.26100.0`。
- WinUI组件`2.3.9`、C#/WinRT`2.3.1`、Windows SDK .NET引用`10.0.26100.87`；中央版本及各`packages.lock.json`记录完整依赖。原生工具/字体URL、SHA256与许可在`probes/native-documents.lock.json`。
- 开发机：Win11 Pro build26200 x64，i5-12400F，物理内存34,160,545,792 bytes，RTX5060Ti/驱动32.0.16.1692，2560×1440/179Hz。原始`output/migration/environment.json`；不是8GB/60Hz性能参考机，未报告产品预算达标。
- 受限命令令牌无法使用Schannel、凭据和部分系统查询；还原/凭据/桌面实跑使用正常用户环境。未修改系统证书、管理员设置或Sandbox功能。

## 重要实现记录

- Release还原必须带`-p:Configuration=Release`，否则`publish --no-restore`可能只做裁剪而未导入AOT targets。检查要求EXE存在、没有CoreCLR并实际运行dynamic-code=false。
- App以26100编译SDK构建，最低目标19041；目标版本不等于Win10兼容性已通过。基础设施为纯.NET加Windows平台声明，不引入不需要的WinRT投影。
- WinUI AOT原先漏自身PRI/XBF导致`0xC000027B`；现通过MSBuild显式发布并在资源缺失时失败，不依靠手工复制。
- WinRT对象可能先投影为基类或IInspectable，普通`is`/对象DP和Tag拆箱曾造成误判。现以明确投影、native类名及原生向量身份处理；设置项在模板加载后赋中文名称。实际导航/滚动检查已验证。
- 图片解码前限制边长/像素并同时限制解码宽高；导出小问标签纳入文字预算。独立review发现项均已修复。
- 采用WinUI组件包去除无用AI/ML/Search/Widgets载荷。框架传递的WebView2支持文件不是应用浏览器调用；无WebView2干净系统仍需实测。
- 脚本每次使用全新发布目录。跳过项单列`notRun`并记`selected-checks-passed`；跳过文档探针仍要求App的PDFium/hash/notices存在。

## 未通过门槛与接续步骤

1. **P1/P9外部矩阵**：补齐Win10 22H2/LTSC2021、Win11 24H2/25H2干净离线系统、部署身份、VC++/原生依赖与无浏览器链路证据。当前只有开发机及portable本机检查；不默认改为JIT或浏览器。
2. **P6/P7本地剩余功能**：扩充原生题目编辑为复杂内容/选项/配图/小问编辑，保持来源与手改保护；现有编辑主要覆盖根题干、答案、解析、作答、知识点和评分。完整图片/表格/多层小问PDF导出尚未实现，当前明确阻止不完整导出。补受限预取、各页焦点/视口恢复的完整人工行为，避免把缓存通过等同全部阅读行为完成。
3. **公式必要覆盖**：基于授权既有题目建立必要LaTeX/mhchem语法与参考渲染。当前有界子集支持数学/化学固定样本，不支持的宏/环境保留原式，不能宣称完整TeX/mhchem替代。
4. **P4 Sandbox**：实现并实机验证仅属于本应用的会话启动/归属/握手/桥接/退出；当前只有只读检测、映射与协议状态机，不能把协议探针记成真实能力，不接管用户已有沙箱，不自动启用系统功能或提权。
5. **P5真实语义**：在用户授权的模型、密钥和样卷条件下分别验证数学多栏、生物续题、教师批改化学的漏题/串答/评分；现有合成HTTP行为并未上传真实材料。不要使用夹具的假签名/密钥。
6. **P8与打印**：人工文件选择/拖放、IME/读屏/焦点/高对比/减少动画/DPI/跨屏、实际打印UI/纸张、12页高分辨率与长时任务；按参考机采集启动与帧P95/P99/全进程资源。当前一次工作负载私有字节约322MiB，不能视为空闲预算通过，需跟踪和优化。
7. **真实切换与发布**：生产仓储/资产恢复已经交付，但旧版不识别新lease；已知旧进程检查仍有“检查后启动旧版”竞态。当前UI通过只读备份→独立新目录恢复避免双方写同库。目标身份下真实凭据、旧目录、恢复/回退、安装升级/卸载及正式分发义务仍需演练，不能直接覆盖真实库。
8. 本机源码接续入口见本文件最新节链接。后续subagent按用户偏好显式选medium/high/xhigh，优先high；构建/还原/发布共享工程图必须串行，独立编码可以并行。

重新验证或打开最终程序：

```powershell
./packaging/Test-RepositoryLayout.ps1
./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke
./packaging/Publish-OfflinePackage.ps1 -DesktopSmoke -WorkflowSmoke
$package = Get-Content output/migration/latest-package.json -Raw | ConvertFrom-Json
& (Join-Path $package.packageDirectory 'OhMyStudy.App.exe')
```

每次结束维护本文件的阶段、准确结果、失败/限制及下一步。真实资料必须在版本检查、一致性备份和恢复演练之后切换；不能复制活跃SQLite主文件冒充完整备份。完整标签/筛选、复习卷、手机传图等未实现产品功能继续独立待办。
