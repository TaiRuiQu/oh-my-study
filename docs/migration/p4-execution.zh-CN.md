# P4 任务与执行交接

更新：2026-10-18。代码实现与本机合成验收分开记录；Sandbox实机验收由用户负责，本轮未执行。

## 实现与边界

- `Application/Jobs/JobQueue`单读者队列持有执行槽直到任务和所属执行进程结束。提交前复制完整输入；快照与事件由`IJobStore`原子持久化，失败停队列。重新打开时将非终态任务记为`INTERRUPTED`，不自动重放。
- 每次结果保存经过队列提交屏障。取消立即撤销令牌，再提交终态；已保存内容保留。排队、运行、权限等待、完成、失败、取消及重启中断均有递增事件。事件订阅采用有界完整快照，发生序号间隙应重新读取任务列表。
- `PermissionBroker`只为已注册任务发出不可变、单次使用的`AuthorizedCall`，固定内容摘要绑定工具、代码、参数、目录和超时。单次允许不产生任务授权；本任务允许在结束时撤销；拒绝后不能改写调用重试。凭据和模型响应不进入诊断。
- `Infrastructure/Execution/WindowsProcessRunner`使用源生成P/Invoke，以挂起方式创建进程，在恢复前绑定私有Windows Job，设置kill-on-close；通过句柄白名单只传入标准输入输出。正常根进程退出、超时、取消和输出超限都会结束所属子进程树。
- `HostToolExecutor`只接受broker令牌。Python固定来自包内`runtime/python`，代码计算入口只有`execute.py`；PowerShell使用系统固定路径与编码包装，参数保持数组，不通过拼接把参数变成命令。环境按白名单创建，不继承供应商密钥、代理凭据或Python路径。
- UI在退出拦截中等待任务队列和资料库关闭；最小化不影响任务。切库前要求全部任务结束。

## 实际本机验证

首轮实际Native AOT测试产物：`output/migration/publish/execution-p4/OhMyStudy.Execution.Tests.exe`，报告`output/migration/evidence/execution-p4/execution-report.json`，9场景通过：权限内容绑定/单次消费/任务隔离/撤销、单任务与事件、迟到写屏障与执行槽、拒绝与继续、重启中断/存储失败停止、中文路径/参数/环境、非零退出、超时/取消/输出上限、正常根退出后的子进程清理。

后续新增私有Python的NumPy/SymPy/Matplotlib真实计算和PowerShell参数测试，由`packaging/Invoke-MigrationChecks.ps1`在发现准备好的私有运行时后执行。最终本轮统一结果以根`migration-progress.md`和`output/migration/latest-migration-run.json`为准，不把此前9场景记录自动扩成新通过数量。

队列审阅发现并修复：退出与正在持久化提交的竞争、CTS取消/释放竞争，以及外部可变输入列表进入排队快照。退出竞争另在整卷集成套件中通过确定性阻塞存储验证。

## Sandbox调用、归属与生命周期

`SandboxCapability.Detect()`只读检测版本、可执行文件及已知占用，不启用系统功能、不提权。`ProbeAsync(privateRuntime)`进一步检查固定的系统/Store执行别名`wsb.exe`、包内Python/两个worker以及`wsb list --raw`。只有可识别的空会话列表返回`Ready`；现有会话返回`Busy`，命令失败、未知JSON格式、旧系统或缺少CLI均保持未就绪。CLI要求Windows 11 24H2及已更新的Windows Sandbox；Windows 10的旧单实例客户端没有可证明的会话归属接口，故不启用这一路线。

`SandboxToolExecutor`每个已授权调用创建独立VM。它先检查没有现有会话，再生成随机GUID并通过`wsb start --id <GUID> --config <XML>`请求该新会话；只通过`wsb stop --id <同一GUID>`释放。归属不从进程名、窗口、前后列表差分或guest JSON推断。启动、取消和失败都尝试释放该精确ID；清理使用独立超时，不受任务撤销影响。释放失败保留本地`ownership.json`，并锁止当前应用进程的后续Sandbox启动，提示手动关闭后重开。不会自动接管上次进程遗留会话。普通退出仍由任务队列等待执行器清理；崩溃或强杀可能留下会话，下次检查会明确阻止启动。

`wsb exec --id <GUID> --run-as System`只启动包内固定`runtime/sandbox-worker.py`，不连接或操作用户桌面。专用CLI控制器只回收本次命令行进程；不会将共享Sandbox服务放入宿主执行器的kill-on-close Job。VM始终通过精确ID释放。CLI环境按白名单创建，不继承供应商密钥和代理变量。

每次映射仅包含包内只读运行时、只读输入快照、只读请求和可写结果四个目录。快照限256个文件、512个条目、64MiB、8层目录，不接受链接、UNC、重叠映射或递归包含调用输出的工作目录。guest把输入复制到自己的可写工作区；代码应使用相对输入路径，原宿主绝对路径不会被改写或暴露。除独立结果目录外，不映射整个资料库或用户目录。网络设置来自任务冻结的`AuthorizedCall.SandboxNetworking`。默认禁用网络、剪贴板、音视频、打印重定向和GPU。

请求固定绑定protocol/job/call/nonce/digest，nonce使用32字节密码学随机数。worker先原子写握手，再运行获准的Python或PowerShell计算，处理超时、非零退出和总计1MiB输出上限。请求JSON上限2MiB；返回JSON上限8MiB以容纳字符转义，但解码后的总输出仍限1MiB。host验证握手后等待结果，先停止整个所属VM，才打开最终结果，拒绝错身份、错nonce、未知字段、无效JSON、未知状态、成功却非零退出和超长输出。guest数据从不变成宿主命令，也不自动导入guest生成的任意文件。

`PolicyToolExecutor`只根据已冻结的授权模式分派一次；`HostToolExecutor`拒收Sandbox许可。Sandbox失败没有宿主重试路径。UI应使用异步`ProbeAsync`获取开始对话框的能力状态，并以`PolicyToolExecutor(new HostToolExecutor(runtime), new SandboxToolExecutor(runtime))`注入任务队列。未就绪时仍由既有开始对话框明确说明本机执行后果；运行中失败只报告失败。

本机验证分两层：`SandboxChecks`使用可注入的隔离控制器验证最小映射、新会话ID、停止后读取、已有会话保护、无宿主重放、nonce/身份/JSON/状态/输出校验、取消/任务撤销、启动歧义、输入上限以及释放失败后的锁止；`sandbox_worker_checks.py`使用包内Python在宿主临时目录验证固定worker的中文输入、Python退出码/超时/输出上限及PowerShell数组参数。这些均不声明已运行实际Windows Sandbox。

2026-10-18实际发布并运行`output/migration/publish/execution-p4-complete/OhMyStudy.Execution.Tests.exe`，`RuntimeFeature.IsDynamicCodeSupported=false`，17组全部通过；其中worker组包含上述5个真实私有运行时计算场景。报告：`output/migration/evidence/execution-p4-complete/execution-report.json`。发布没有自有AOT/裁剪警告；出现NU1900（NuGet漏洞数据源网络不可达），不能将其记为漏洞审计通过。测试发布物直接包含正式worker源码副本，因此可以使用已有私有Python验证，不改写旧运行时的已封存manifest；正式生产包仍须通过更新后的`prepare-runtime.ps1`纳入worker及哈希。

运行统一证据：发布并运行`tests/OhMyStudy.Execution.Tests`的Release Native AOT产物，传入`<证据目录> --runtime <包含sandbox-worker.py的私有python目录>`；JSON报告保留`real-windows-sandbox-session-ownership`为未执行。固定worker也可单独验证：`<私有python>/python.exe -I -X utf8 tests/OhMyStudy.Execution.Tests/sandbox_worker_checks.py runtime/sandbox-worker.py <私有python目录>`。

用户负责、未执行：Windows 11实际CLI JSON格式/组件版本、真实新会话归属与映射、网络开关、启动失败与取消、退出和崩溃恢复；Windows 10/11系统状态矩阵。真实Sandbox和虚拟机资源开销不能由本机协议或计算测试替代。

实现依据：[Windows Sandbox CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli)与[配置文件](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。实际支持范围以用户实机结果为准。
