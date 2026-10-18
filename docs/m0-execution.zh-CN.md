# M0：执行、权限与私有计算运行时

记录日期：2026-10-18。此阶段交付可测试的 Rust 执行核心、私有运行时、文件桥接和人工验证入口；尚未完成真实 Windows Sandbox 启停矩阵，不能据此宣布 M0 整体验收。

## 已实现的边界

- `ExecutionPolicy::freeze` 保存 preferred/effective mode、executor kind、fallback reason 和本机未隔离警告。Ready 默认沙箱，其他能力状态明确回退到本机绕过权限；显式请求权限优先。运行中断只返回当前不确定调用，不自动宿主重放。
- `PermissionBroker` 注册任务，逐次展示完整不可变调用，按所有字段 SHA-256 绑定批准。允许本次消费一次、允许本任务不跨任务；等待期间暂停后续调用；拒绝后禁止同任务改写规避。取消／关闭任务会撤销已发出但未执行的令牌，执行器运行中也检查撤销。
- `HostExecutor` 仅接受 broker 发出的令牌，不接受沙箱令牌。使用私有绝对路径 Python；重新构造环境，仅保留系统目录和任务临时目录等明确字段。不会自动继承模型密钥、代理凭据、PYTHONPATH 或数据库句柄。本机执行仍能使用当前账户的文件、网络与系统权限。
- Windows 根进程以 `CREATE_SUSPENDED | CREATE_NO_WINDOW` 创建，加入不允许 breakaway 的 kill-on-close Job 后再恢复。等待循环检查取消、超时、日志上限；根进程正常结束也关闭 Job，回收后代。没有按名称批量杀进程、重新按 PID 寻找进程、提权或 UAC 绕过。该同步 API 应由 M1 后台任务线程调用。
- `.wsb` 只映射 Runtime、Inputs、Requests、Results，前三个只读，只有当前任务 Results 可写；拒绝重叠和重解析点，转换 canonicalize 的 Windows extended prefix 为普通盘符路径。网络和共享设备默认关闭，内存 2048 MB。
- guest runner 以低频轮询读取原子发布请求，校验版本／任务／调用／超时，经私有 Python 或原生 PowerShell 执行后原子写响应。PowerShell 使用固定 `-EncodedCommand` 包装读取已批准 JSON，用 ScriptBlock 和参数数组执行，保持参数内容为数据，不修改执行策略。guest 使用 ctypes Windows Job 管理当前调用树，默认完成、取消和超时都回收后代。取消标记来自只读 Requests；guest 不能向宿主批准权限。
- 宿主桥接校验当前任务和调用、登记过的扁平产物文件名、响应大小、重解析点、真实路径和文件大小。Windows 读取期间拒绝并行写入／删除；PNG/JPEG 实际解码，限制边长、像素和解码内存；复制到新的宿主文件后才交资产服务登记。SVG 当前拒收，尚无可用 sanitizer，不能把“生成过 SVG”写成“SVG 导入已安全通过”。

这些模块尚未连接 Tauri 界面或持久化任务队列。`SandboxSession` 只处理桥接状态，不把 guest 握手当作操作系统会话所有权证明；实际自动启动／停止会话暂未开放，避免误关用户会话。

## 私有运行时与复现

`runtime/runtime-lock.json` 固定官方 CPython 3.13.2 x64 嵌入式 ZIP、4 个主计算库及全部 9 个间接依赖的下载地址、版本和 SHA-256。主库为 NumPy 2.2.4、SymPy 1.13.3、Pillow 11.1.0、Matplotlib 3.10.1。实际产物位于忽略目录 `runtime/staging/python`，归档缓存位于 `runtime/wheelhouse`。

构建时允许开发 Python 作为下载／解压 bootstrap；执行阶段只使用新目录中的 `python.exe -I`。构建校验所有归档，离线模式缺任一包即失败，不回退开发环境，不运行 pip 安装器。目标目录必须不存在，不覆盖旧构建。

```powershell
# 联网取得锁文件指定的归档后构建；只写仓库目录。
python runtime/build_runtime.py --cache runtime/wheelhouse --destination runtime/staging/python

# 已有归档时，可离线构建到一个新的目录。
python runtime/build_runtime.py --cache runtime/wheelhouse --destination tmp/runtime-offline --offline

runtime/staging/python/python.exe -I runtime/staging/python/runtime_smoke.py
runtime/staging/python/python.exe -I runtime/test_runner.py
runtime/staging/python/python.exe -I runtime/plot_probe.py --output tmp/执行与中文图 --font C:/Windows/Fonts/msyh.ttc
```

`plot_probe.py --hold-seconds 10` 可供资源采样。Matplotlib 使用 Agg，可写缓存位于结果目录；不写 Runtime。上述命令的一次开发验证使用已有微软雅黑字体，未复制／再分发该字体；另已使用固定 OFL 思源宋体完成同一图形验证，证据为 `output/measurements/私有Python绘图/runtime-smoke.json`。正式离线包的字体布局和只读沙箱 Runtime 仍需安装验收。

`capture_lock.py` 只供开发者显式刷新锁：读取已选择版本的 wheels，与 PyPI 元数据 SHA-256 对照；CPython 来自官方 HTTPS 下载并记下字节摘要。安装和运行不调用刷新脚本。M0 固定基线不构成发布版本承诺；发布前仍需审核 Python 和所有组件的安全更新、来源、许可证、wheel 布局和安装清单。运行时原生 DLL 不注册到系统。

## 已执行的验证

| 项目 | 结果与限制 |
| --- | --- |
| 固定归档离线组装 | 14 个归档校验并成功解压，私有 Python 可导入四个计算库；`sys.flags.isolated == 1` |
| Rust 核心 | 模式回退、内容绑定／跨任务／撤销、最小 XML、失联不重放、恶意路径及截断 PNG 拒绝等单元测试通过；宿主真实测试由 `src-tauri/tests/host_execution.rs` 单列 |
| private runner 自动化 | `runtime/test_runner.py` 的 UTF-8 输出／异常、合成密钥不继承、超时、取消、跨任务／多余字段拒绝、正常根进程完成后清理后代、shell 参数原样传递以及握手／原子发布／停止标记均通过；这在宿主上模拟文件目录，不是真实沙箱映射权限验证 |
| 图形与中文路径 | 私有 Python 生成中文标题的函数图／几何图 PNG 和 SVG、分式数学标签；PNG 已实际打开检查无缺字，符号积分返回 `x**3/3` |
| 只读 Sandbox 检测 | `output/sandbox-capability.json` 为 Windows 11 Pro build 26200，HypervisorPresent=true，CPU 三项虚拟化字段=false，Sandbox exe 不存在，可选功能查询需要提权。最终 Unknown，不能从这些字段断言固件关闭、功能已禁用或 Ready |
| 沙箱真实启动 | 未执行。没有启用功能、重启、提权或关闭任何用户会话 |

受限开发进程中 CIM／canonicalize 可能被环境本身拒绝；复核时须分别记录“检测权限不足”和真实系统状态。只读探测脚本自身既不提权也不启动沙箱，失败保留具体证据。Hypervisor 已运行时，CIM 的 false 不能直接判成固件虚拟化关闭。

## 人工沙箱桥接入口

先运行只读探测。若本机策略不允许运行 `.ps1`，可在当前 PowerShell 中读取该**仓库自有只读脚本**执行，不修改系统或用户执行策略：

```powershell
& ([scriptblock]::Create((Get-Content -Raw runtime/probe-sandbox.ps1))) -OutputPath tmp/sandbox-capability.json

# 只生成文件，绝不启动 Sandbox；job-id 必须全新。
runtime/staging/python/python.exe -I runtime/export_sandbox.py --runtime runtime/staging/python --tasks-root tmp/sandbox-tasks --job-id m0-manual
```

导出后审阅 `tmp/sandbox-tasks/m0-manual/session.wsb`、`requests/probe.json` 和宿主留存的 `session-host.json`。若要自定义代码，传 `--code-file`，或额外复制指定材料时重复 `--input`。默认不联网，`--networking` 是显式的任务启动设置。

仅在确认系统条件、无已有会话且记录人工测试计划后，由测试人员打开生成的 `.wsb`。应出现 `results/handshake.json` 和 `probe.response.json`，匹配 job／nonce／call，再检查日志。将**宿主**请求目录写入 `probe.cancel` 验证运行中取消，写入 `stop.marker` 仅请求 runner 退出。该标记不会声称停止整个 Sandbox；测试人员关闭自己刚创建的窗口。应用在会话归属验证完成前没有通用“强制关闭 Sandbox”命令。

必须继续实测 Windows 10 支持版本、Windows 11 旧版及 24H2 后版本：只读／可写映射、中文路径、LogonCommand、握手和文件通知降级、真实沙箱内绘图／字体、用户关闭、取消／进程树、窗口与启动耗时、所有权及释放后的内存。已有 Sandbox 应报告 Busy，不接管。Home／未启用／待重启／策略阻止／虚拟化不足／启动失败也分别记录。当前 Unknown 机器不能替代此矩阵。

资源基线、12 页材料和安装包大小见主 M0 记录；2048 MB 是沙箱配置值，不能记成实测 VM 常驻或声称满足 1 GiB 总峰值。真实 Windows Sandbox、完整离线包字体布局、SVG sanitizer、完整安装和干净机器兼容性仍待验收。

## 采用的官方约束

- [Windows Sandbox 配置](https://learn.microsoft.com/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)：XML、映射、LogonCommand、设备、网络与内存配置。
- [Windows Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)：当前任务进程树归属、kill-on-close 与禁止 breakaway。
- [Python Windows 嵌入式发行](https://docs.python.org/3/using/windows.html#the-embeddable-package)：私有目录和 `_pth` 隔离，不假定开发环境／在线 pip 可用。
