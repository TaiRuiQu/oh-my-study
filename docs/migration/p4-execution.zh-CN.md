# P4 任务与执行交接

更新：2026-10-18。代码实现与本机合成验收分开记录；Sandbox真实生命周期尚未通过。

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

## Sandbox保持独立状态

`SandboxCapability.Detect()`只读检测版本、可执行文件及已知占用，不启用系统功能、不提权、不接管已有沙箱。系统组件存在不等于本应用能可靠管理自己的虚拟机会话，因此检测不会凭可执行文件存在宣称`Ready`。

`SandboxMappings`与`SandboxSession`迁移最小映射、nonce握手和中断状态机；它们不提供操作系统会话所有权。当前不发行虚假的沙箱worker，也不启动/关闭用户沙箱。选择沙箱且未就绪时，开始对话框明确显示本机执行后果；一旦获得沙箱执行许可，宿主执行器拒绝接收，不会失败后重放到宿主。

P4尚待验收：真实Sandbox会话归属、系统状态矩阵、桥接及自动生命周期。协议状态机、主机执行测试通过不能替代这一项。
