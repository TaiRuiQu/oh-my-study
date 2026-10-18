# 原生迁移进度与交接

更新：2026-10-18（Asia/Shanghai）。依据 [migration.md](migration.md) 和根 [AGENTS.md](AGENTS.md)。

**本轮剩余实现已完成，最终统一11分项回归全部通过，正在完成离线包解压/安装运行检查。** 用户明确要求：干净系统、真实模型/样卷、Sandbox实机、实际打印及外部人工验收由用户执行，本轮跳过；这些未执行项不算通过。下一位维护者先看“接续入口”和[用户验收清单](docs/migration/user-acceptance.zh-CN.md)。

## 不变约定

- 正式工程仅C#/.NET/WinUI Native AOT；不依赖Rust、Node或浏览器运行路径。旧实现仅在Git忽略的`.reference/`，不得作为构建输入。
- 产品只展示任务、内容、操作和结果；技术诊断留在维护文档。标题栏/窗口按钮采用系统原生能力。
- 自动验证只用隔离合成库与合成凭据；未访问用户真实密钥、未上传真实样卷、未切换用户资料、未启用Sandbox或创建打印作业。
- 每阶段以`git -c commit.gpgsign=false commit`提交。此要求只跳过Git签名；发行二进制当前未配置代码签名。

## 阶段状态与提交

| 阶段 | 已交付实现与本机证据 | 本轮提交 / 用户接续 |
| --- | --- | --- |
| P0 行为基线 | 固定旧版起点、20命令与数据/协议/领域夹具，旧实现隔离 | 旧起点`0b5c539`；历史见P0文档 |
| P1 原生路线 | 自包含WinUI AOT、PRI/XBF、SQLite/凭据/公式/PDF/打印API实际运行；无浏览器 | 基础`3a5bb2c`；干净系统/真实输入/打印交用户 |
| P2 工程与领域 | 分层、锁定版本、源生成JSON/契约、评分/来源/覆盖/配置/权限摘要、诊断 | 107项AOT；基础`3a5bb2c` |
| P3 存储与迁移 | 一致在线备份、新目录恢复/升级、全部原始表列/JSON/历史记录指纹、原件和凭据引用保留 | `495f18b`；本轮最终93项AOT，原始字段增强随P9提交；真实切库交用户 |
| P4 任务与执行 | 队列/事件/权限/取消屏障、Windows Job、私有Python、新CLI按ID创建/释放沙箱、nonce桥接 | `844cab9`；17组AOT；实机沙箱交用户 |
| P5 协议与整卷 | 三协议原生上下文/SSE/工具/用量、三模式整卷与手改保护、真实环回HTTP | `eddcbd3`；Provider11场景、Grading211断言；真实语义交用户 |
| P6 文档与公式 | 图片/PDF/四向坐标、公式回退、递归题/表格/图像、统一A3/A4题答模板 | `b7b39d5`；媒体26项、8份45页PDF，实际打印交用户 |
| P7 原生工作流 | 复杂原生编辑、参考匹配与覆盖、分级缓存/受限预取、每页视口/焦点、导航/切库失败恢复 | `3e7b66e`；28项AOT流程及独立桌面通过 |
| P8 体验与性能 | 原生快捷键/窄窗口/主题/辅助名称、3次独立启动与阅读/分发/Rendering/GC/内存曲线 | `759075f`；参考机/DPI/IME/读屏/GPU交用户 |
| P9 发布与退役 | C# AOT离线安装器、原子升级/失败回退、可恢复卸载、资料保留、源码/许可证/哈希、CI | 40项AOT及候选包安装运行卸载通过，最终源码对应包整理后提交；系统集成身份交用户 |

迁移不包含原先未实现的完整标签、高级筛选、按日期选错题、M4复习卷生成/独立校验和手机传图。这些仍是独立产品计划，不是本轮遗留Rust业务。

## 接续入口

```powershell
./packaging/Test-RepositoryLayout.ps1
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1
./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke
./packaging/Publish-OfflinePackage.ps1 -DesktopSmoke -WorkflowSmoke
# 对最终ZIP检查解压、桌面、隔离安装/运行/卸载和资料保留：
./packaging/Test-OfflinePackage.ps1 -PackageZip <ZIP绝对路径> -DesktopSmoke -WorkflowSmoke -InstallSmoke
```

每次证据写入`output/migration/runs/<唯一ID>/`；最新完整结果指针为`output/migration/latest-migration-run.json`。打包报告在`output/releases/<候选ID>/package-report.json`，产物和每个校验清单都保留实际SHA-256。开发缓存与`.reference/`均不入Git。

构建环境锁`.NET SDK10.0.402`、运行时/ILCompiler10.0.13、Windows SDK26100及MSVC x64。**restore必须显式`-p:Configuration=Release`**；不要对整个解决方案全局加RID改变类库锁图；不要并行修改同一工程obj。首次准备需要网络，产品本地运行无需开发SDK/系统Python/Node/TeX。WinUI自身PRI/XBF为必需发布资源。

## 本轮关键证据与边界

最终统一运行 **`migration-20261018-165025-f15e94a5`** 退出0，11分项通过（Setup为发布通过，其余实际运行通过），全部为新目录Native AOT发布。Release整解决方案0警告0错误；空格格式/actionlint/仓库边界/PowerShell解析/发布输入干运行通过。此前NU1900网络审计提示在正常用户联网锁定还原后消失，未关闭默认NuGet审计。摘要：[本轮核心证据](docs/migration/evidence.2026-10-18.final-core.json)。

- P3最终独立产物：`output/migration/publish/storage-verified/OhMyStudy.Storage.Tests.exe`，93项通过。覆盖v1/v2只读、WAL、原件、迁移回滚、完整备份/恢复、新副本升级、复杂历史双run/事件/嵌套教师题/未知JSON/扩展表/REAL/BLOB/NULL。迁移不改源库字节；收据证明业务内容与原始值，不是SQLite文件逐字节相同。所有路径在比较前去尾分隔符。
- P4：`output/migration/evidence/execution-p4-complete/execution-report.json`，17组通过，包括按新随机ID归属、忙时不接管、nonce/摘要/输出校验、取消/撤销/停止失败锁止，以及固定worker在宿主隔离环境的5种计算。后者不是Sandbox实机证明。只有Win11新CLI可确认归属时就绪；旧系统保持未就绪，不自动启用功能，不在执行失败后宿主重放。当前每次授权调用独立VM，不跨调用保留任意状态。
- P5：`output/migration/publish/provider-aot-loopback/OhMyStudy.Provider.Tests.exe`，11场景实际AOT。真实127.0.0.1随机端口服务+默认SocketsHttpHandler覆盖JSON/分块SSE/分页/认证重定向拒绝/取消；没有供应商请求。
- P6：`output/migration/documents-complete-aot/verification.json`、`verification-pdf.json`，8份45页全部Poppler渲染及联系图已查看。519表格单元、12层导出小问、图像、长题干与9000字符完整公式原文保留；字体嵌入、图片、逐字页面边界和题答隔离通过。产品领域小问上限仍为8层，导出独立组件测试更深结构不改变领域规则。未知TeX宏保留原文；含手写图像要求干净题图。
- P7：`output/migration/evidence/app-final/workflow-smoke.json`，28项通过。包含复杂编辑/失败不覆盖、保存期间新输入、教师证据隔离、每页视口、分辨率升级不回退、导航竞态、备份重开、坏设置初始化失败恢复原库与写入锁释放。已查看深浅主题截图。缓存返回P95=15.4756ms，缓存9437184字节；运行末338427904私有字节不是空闲预算。
- P7最后补齐：`output/migration/publish/app-release-final/OhMyStudy.App.exe`，SHA-256 `819951623c6940b16b6c3c4f3f78da68f7cc2b8d734ccc88a68a5843baee00a0`；`output/migration/evidence/app-delivery/{desktop,workflow}-smoke.json`均通过，工作流现为30项。增加沙箱联网开关、设置串行修改避免快速操作丢字段、完整模型分页及重复/超限边界。变化仅App层，已针对新产物复验桌面和工作流，不重复未变化的核心套件。
- P8：`output/migration/evidence/performance-final/summary.json`，3次独立实际AOT采集成功；摘要已提交为`docs/migration/evidence.2026-10-18.performance.json`。顺序启动P95=636.3232ms；空库自然空闲约104–106MiB；阅读P95=20.65–23.98ms；UI分发P95=1.59–1.90ms。释放缓存后自然空闲约201–202MiB略高于200MiB门槛。没有强制GC/清系统缓存；Rendering回调不是GPU呈现帧，预算未替用户验收。
- P9：`output/migration/setup-aot-tests.json`，40项实际AOT通过。两版本安装、四点升级失败回退、半复制清理/重试、七处卸载中断恢复、篡改/链接/并发/占用拒绝、用户文件保留、原生ShellLink、实际CLI安装与自卸载助手。没有修改真实开始菜单/HKCU入口。完整包仍将单独做安装/运行/卸载验证。

历史更正：此前Provider工程缺AOT属性，旧检查仅看PE的CLR头，误把普通apphost当成AOT。`eddcbd3`已补发布属性、运行时dynamic-code断言与同名DLL/deps/runtimeconfig拒绝。旧“八组全部实际AOT”仅Provider分项无效，不能沿用；本轮已实跑纠正。

## 发布和资料保护

`origin`为`https://github.com/TaiRuiQu/oh-my-study.git`。已有`f419ba6`配置main/PR的lint、checks、test→构建artifacts，tag走同链后发布Release。官方Actions固定SHA；本轮新增安装器回归和guest worker缓存键/清单。用户本轮仅要求提交，尚未自动创建tag或Release。

默认新库`%LOCALAPPDATA%/OhMyStudy/Library`；旧目录`%APPDATA%/io.ohmystudy.desktop`只读检查。显式导入：一致备份→新目录恢复/仅副本升级→原始字段/实体/资产校验→完整初始化成功→原子更新`active-library.txt`。失败重新打开原库且不改指针。凭据只保留旧引用，不导出密钥；换Windows账户需重新配置。

安装入口为完整解压目录中的`OhMyStudy.Setup.exe`，普通用户安装到`%LOCALAPPDATA%/Programs/OhMyStudy`；也可直接运行`OhMyStudy.App.exe`。版本按包清单哈希隔离，升级保留旧版本；卸载仅删除本安装清单文件并保留所有资料/备份/用户文件。发行产物尚未代码签名；固定哈希是完整性检查，不是来源认证。

## 用户负责的验收

按[用户接续清单](docs/migration/user-acceptance.zh-CN.md)记录系统版本、场景、结果和证据。干净Win10/11离线系统/安装身份与系统入口、真实资料与凭据/回退、真实模型与样卷语义、Sandbox会话实机、纸张打印、高DPI/跨屏/IME/读屏和参考设备性能均为**用户负责、尚未执行**；不会阻塞本轮已授权实现，也不记为通过。

历史交接归档于[迁移历史](docs/migration/history.2026-10-18.zh-CN.md)。阶段细节见`docs/migration/p0-*`至`p9-*`；后续修改须更新本文件的实际产物、报告、状态、失败与下一步。
