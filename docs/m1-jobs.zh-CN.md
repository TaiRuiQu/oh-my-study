# M1 本地任务与执行权限

M1 提供真实本地诊断任务，用来验收任务队列、SQLite 事件、Tauri 进度通道、权限和取消。诊断不会调用任何供应商，也不是 M2 的整卷识别或批改任务。

- `storage` 在专用数据库线程执行真实 SQLite 自检，并读回刚写入的任务快照。
- `permission` 通过 M0 `PermissionBroker` 和 `HostExecutor` 执行两段固定私有 Python 自检代码：解释器隔离设置，以及 NumPy / SymPy / Pillow / Matplotlib。工作目录为应用生成的独立任务目录；输出与代码调用原件留在本地诊断目录中。运行时缺失时任务明确失败，不借用系统 Python。
- 当前未接入 Windows Sandbox 执行器。如果冻结策略的有效执行器是 Windows Sandbox，诊断明确失败，不自动改用宿主执行。能力 Unknown 下的宿主回退来自现有执行策略，并随任务快照保留“本机执行／未隔离”警告。

`JobManager` 持有一个后台执行线程，所有任务全局串行。等待权限期间保留当前执行位。取消立即撤销 M0 权限令牌，发出取消信号并持久化 CANCELLED；队列等待已拥有的执行进程实际退出后才启动下一项。取消、进度与结果提交共享写入锁，终态不会被迟到结果覆盖。退出钩子必须调用 `shutdown()`，使排队、运行和等待权限任务全部取消，并等待执行线程退出。

任务状态为 QUEUED、RUNNING、WAITING_PERMISSION、COMPLETED、FAILED、CANCELLED、INTERRUPTED。启动时所有未完成任务均转为 INTERRUPTED，清除待批准请求，不自动恢复代码、命令或任务级授权。

每条事件与最新任务快照在同一 SQLite 事务中保存，成功后才广播。事件含任务内严格递增 `sequence`，`payload` 为完整 Job。Tauri 订阅应转发这些持久化事件；订阅断开或 broadcast 接收滞后时前端重新读取 `list_jobs` 和持久化事件。持久化失败会停止队列并撤销执行信号，避免继续执行没有审计记录的代码。

权限请求保留完整 ExecutionCall 与其 SHA-256 digest，包括协议版本、任务和调用 ID、工具种类、代码／命令、参数、工作目录、超时。批准必须提交一致 digest。`allow_once` 仅批准当前调用；`allow_task` 仅在当前任务有效；`deny` 结束任务，禁止改写后重试。取消后或重启前的批准请求均失效。

同步 `JobManager` 操作会等待数据库工作线程，桌面命令应使用 `spawn_blocking` 调用；私有 Python 仅在任务后台线程执行。`JobStore` 和 `DiagnosticExecutor` 接口用于真实竞态测试，不是产品中的虚假供应商实现。

验证命令：`cargo test -p oh-my-study-core jobs::tests` 和 `cargo test -p oh-my-study-core --test jobs_persistence`。覆盖全局串行、取消与迟到写入、权限 digest、单次／单任务授权、拒绝、重启中断、退出取消、持久化与广播顺序、存储失败停止队列、沙箱不伪装、运行时缺失失败和真实 SQLite 重启读取。

组装好 `runtime/staging/python` 后，显式运行 `cargo test -p oh-my-study-core --test jobs_persistence -- --ignored`，验证真实私有 Python 在批准后执行，输出包含解释器隔离标记与科学计算依赖版本。未运行此项时不能把 mock 执行器测试写成真实 Python 集成已验收。
