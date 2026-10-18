# 执行环境与权限设计

更新：2026-10-18。本文描述当前C#权限与执行实现。Native AOT执行回归17组通过，包含固定worker的私有Python/PowerShell计算与可注入控制器协议测试；真实Windows Sandbox和系统矩阵由用户负责，尚未验收。本轮不启用系统功能或启动真实沙箱。本文不是对当前开发机器运行模型代码的授权。

## 1. 三种用户模式

| 用户模式 | 运行位置 | 执行前确认 | 文件与网络权限 |
| --- | --- | --- | --- |
| 请求权限 | 宿主机，当前 Windows 用户 | 每次 Python／命令调用前确认，可仅允许本次或本任务 | 批准后使用当前用户权限；不是隔离环境 |
| 沙箱运行 | Windows Sandbox，每次调用独立VM | 沙箱内调用不逐次确认 | 宿主仅暴露本次调用映射；网络默认禁用，策略在任务开始时冻结 |
| 绕过权限 | 宿主机，当前 Windows 用户 | 不逐次确认 | 不附加应用层文件／网络权限拦截，仍受 Windows 用户权限和 UAC 约束 |

模式控制自由 Python 和 shell 工具。查看本任务图片、裁图、旋转、查询标签、正常保存题目等业务工具无需逐次申请；其接口本来就只处理该任务可用数据。

绕过权限不表示应用自动以管理员运行，也不表示突破操作系统权限。超时、取消、输出体积和资源回收仍然适用，它们用于避免任务卡死，不是文件／网络权限限制。

## 2. 默认值与不可用处理

当前App新设置默认绕过权限，用户可选择请求权限或保存沙箱偏好。任务开始时异步探测能力并冻结实际执行策略；领域策略在未提供偏好时以沙箱为首选，这不等于App已实现自动选择默认模式。

用户可以保存沙箱偏好；是否就绪在开始任务时检查。若偏好沙箱但当前不可用，开始对话框明确说明本次将在本机执行及访问文件/网络的后果，用户开始后冻结为本机绕过权限，记录原因；不能继续显示为沙箱。该开始前回退不增加逐次批准，也不授权执行失败后的宿主重放。

警告说明实际原因和影响，例如“不支持 Windows Sandbox，本次代码与命令将在本机使用当前账户权限执行，可访问该账户的文件和网络”。支持但未启用时说明“尚未启用”，不错误声称硬件不支持。

任务记录 preferred_mode、effective_mode、executor_kind、fallback_reason。任务启动时冻结权限配置；用户修改设置只影响新任务。未开始的任务可取消后按新模式重新提交。

运行中沙箱退出时，已完成结果保留，当前调用标记失败；不把可能已经执行过的同一调用自动在宿主机重放。后续重试重新判断实际模式并展示警告。

## 3. 支持检测

检测结果区分：

- Ready：已发现所需的新CLI与私有运行时，并确认可识别的空会话列表；当前调用启动后的握手仍需另行验证，不表示真实系统矩阵已通过。
- Unsupported：Windows 版本／版本类型或硬件条件不支持，例如 Home 版。
- NeedsEnable／NeedsReboot：支持但功能尚未启用，或需要系统重启。
- VirtualizationUnavailable／PolicyBlocked：固件虚拟化或系统策略阻止启动。
- Busy：系统已有其他 Windows Sandbox，会话无法由本应用独占。
- Unknown／StartFailed：查询不足以判断或真实启动失败，展示具体原因。

能力模型保留上述细分状态，但当前探测不会凭不足的信息断言固件、重启或策略原因。系统版本与功能条件还须结合用户实机验证；8GB参考设备验证仍未执行。

不把 WindowsSandbox.exe 存在或系统名称写着 Pro 当作完整就绪证明。启动后的 runner 握手是实际可用性的最终检查。

检测不自动启用 Windows 功能、不自动重启、不自动提权，不使用脚本给 Home 版强行安装沙箱。可以提供系统准备说明；没有沙箱不影响本机模式。

## 4. 当前可证明归属的实现路线

当前Sandbox执行要求Windows11 24H2起且已更新的`wsb` CLI。`ProbeAsync`检查固定系统路径或Store执行别名、私有Python与worker，以及`wsb list --raw`的可识别空列表。Windows10、旧Windows11或缺少CLI时不会返回Ready；`.wsb`单实例启动不能提供本实现要求的可证明会话归属，故不作为兼容回退路径。本机模式仍面向Win10/11。

不能依赖 wsb exec 直通 stdin／stdout。微软公开 CLI 文档明确指出其命令执行接口没有进程 I/O 返回能力，所以输入、日志、结果和图片统一经文件交换桥接，不为两个系统版本维护两套业务协议。

不把Windows Sandbox当作普通Python子进程。每次授权调用生成随机GUID，通过`wsb start --id <GUID> --config <XML>`请求独立VM，随后只通过`wsb stop --id <同一GUID>`释放。归属不从进程名、窗口、会话列表差分或guest JSON推断。普通CLI辅助进程隐藏启动；真实CLI行为由用户实测。

应用发现已有会话时按Busy处理，不接管、修改或关闭它们。启动前后与清理只使用本次明确请求的会话ID；释放失败保留归属记录并锁止当前应用进程的后续Sandbox启动，提示用户处理后重新打开。崩溃遗留会话不会被下一进程自动接管。

## 5. 文件映射与通信

每次调用建立独立目录，生成转义正确的绝对路径配置。输入是当前调用需要的只读快照，不映射可变的整个任务工作区；快照限256文件、512条目、64MiB和8层目录，拒绝链接、UNC、重叠映射和包含调用输出的递归工作目录。

| 宿主内容 | 沙箱映射 | 权限 |
| --- | --- | --- |
| 固定 Python、库、字体、runner | Runtime | 只读 |
| 本次调用实际需要的材料副本 | Inputs | 只读 |
| 宿主创建的请求文件 | Requests | 只读 |
| 握手、日志和响应 | Results | 可写，仅此调用目录 |

不映射整个用户目录、Downloads、题库数据目录、凭据存储或应用配置。沙箱可写映射会真实写入宿主，因此 Results 不能指向这些位置。

宿主用`wsb exec --id <GUID> --run-as System`启动包内固定`sandbox-worker.py`。worker把输入复制到guest可写工作区，再执行获准的私有Python或PowerShell；不连接或操作用户桌面。代码应使用相对输入路径，不改写或暴露原宿主绝对路径。

请求绑定协议版本、job/call、随机nonce、内容摘要、代码、参数与超时。握手与返回采用原子完成文件，宿主异步轮询；请求上限2MiB，返回JSON上限8MiB，解码后的总输出仍限1MiB。宿主先停止所属VM，再读取最终结果，核对身份、nonce、字段、状态、退出码和输出限制。

沙箱中的整个环境都视为可被自由代码修改，runner 或结果内容不能成为宿主权限批准来源。返回的日志、JSON、图片和路径均是待校验数据，不在宿主当命令或脚本执行。

当前执行器仅接受已校验的状态和文本结果，不自动导入guest生成的任意文件或将其登记为资产。guest提交的“允许权限”或任意宿主路径不具有授权效力。

## 6. 网络、设备与运行时

默认配置：Networking、ClipboardRedirection、AudioInput、VideoInput、PrinterRedirection 和不需要的 vGPU 关闭；沙箱内只需 Python 计算，不需要这些共享。

在线模型请求、供应商密钥、数据库写入均留在 C# 宿主。沙箱禁网不影响模型继续看图、调用工具或返回答案。完整计算依赖和字体随离线包提供，沙箱中不必临时 pip 下载才能绘图。本文保留业务权限设计；C#实现与真实Sandbox验收状态见`migration-progress.md`，不能按设计文字视为已通过。

设置页提供“允许沙箱访问网络”，默认关闭；保存后只影响新任务，任务开始时冻结`SandboxNetworking`。联网标志只改变guest网络，不扩大宿主映射；沙箱不可用而在开始前明确选择本机回退时，本机网络仍是当前账户权限。

Matplotlib 使用 Agg 后端，配置和字体缓存写在沙箱内部可写目录；不能尝试写只读 Runtime。Windows 原生命令使用可用的 PowerShell 或可执行程序，不依赖 Bash、Windows Terminal 或 WSL。

## 7. 请求权限模式

自由代码在执行前必须准备好可审阅的内容。对话框展示完整代码／命令、工作目录、所属任务和“宿主当前用户权限”的含义，提供：

- 允许本次：仅执行当前内容完全相同的一次调用。
- 允许本任务：允许当前任务后续 Python／命令调用，任务结束即失效。
- 拒绝：不执行，返回明确拒绝结果；Agent 不得通过改写命令规避拒绝。

批准绑定到任务、工具类型、代码／命令内容、参数和工作目录；批准后不能把内容换成另一条。任意 Python／shell 执行可能间接访问其他文件和网络，不能把一次代码批准误称为对每一个文件访问都能拦截。

等待时任务处于 WAITING_PERMISSION，后续调用暂停，模型不会因等待不断重试产生费用。用户可取消任务，取消会撤销所有未执行批准。业务数据格式、分值和来源校验仍按正常规则执行。

## 8. 生命周期、取消与所有权

仅在需要自由代码调用时启动执行环境；纯浏览、搜索和编辑不启动Sandbox。当前每次工具调用使用独立VM，调用结束即释放，连同一任务的相邻调用也不保留任意变量、安装包或文件状态。后续调用需要的已保存输入必须由宿主重新提供。这是基于可证明归属的新生命周期；旧原型的同任务环境复用没有完成验收，不作为迁移后已实现行为。

宿主执行使用独立Job Object管理进程树；guest worker处理调用超时和输出限制。取消或失败后，宿主用独立清理超时停止该精确会话ID，不受任务取消令牌立即撤销清理的影响。释放失败保留记录并阻止当前进程再次启动Sandbox，不按进程名批量终止程序。

应用退出停止本应用拥有的执行环境。用户手动关闭沙箱或运行器退出时，任务标记中断，成功保存的题目和产物保留。

## 9. 资源和离线包

沙箱 MemoryInMB 初值为 2048；这是一项虚拟机配置，并不等于已经测量出的宿主物理常驻内存。根据微软文档，低于启动所需内存会自动提升到至少 2048 MB，不能用较小数字声称满足 1 GiB 总内存目标。

当前自动采样覆盖App进程，不运行Sandbox、模型或私有Python工作负载：三次顺序启动P95为636.3232ms，空库约104–106MiB，释放后约201–202MiB，略超200MiB，不能算空闲预算通过。沙箱模式应由用户分别测量App、VM与系统开销，以及合计峰值、启动时间和磁盘占用；8GB参考设备和真实计算峰值仍未验收。采样全程不强制GC。

应用离线包包含 Python、库、字体与 runner，不重新打包分发 Windows Sandbox 系统镜像。Windows 功能启用或 Store／Windows Update 准备可能需要系统安装源、网络及重启，不承诺未准备系统也能完全离线启用沙箱。本机模式的安装和使用不依赖此准备步骤。

## 10. M0 必测矩阵

- Windows10、旧Windows11和缺少新CLI：不得显示Ready，开始前回退提示与冻结策略正确，本机流程可用。
- 具备已更新CLI的Windows11：真实空会话列表、明确ID创建与停止、只读映射、worker握手、中文路径、取消、清理失败锁止与崩溃遗留处理。
- Home、不启用功能、虚拟化关闭、策略阻止、需要重启、已有沙箱会话：原因准确、警告可见、实际模式显示正确、本机功能可用。
- 请求权限：允许、拒绝、允许本任务、取消、内容变化、批准不得跨任务使用。
- 绕过权限：应用不额外阻拦用户账户可执行的文件或网络操作，不偷偷提权。
- 沙箱失联：不自动重放不确定调用，不杀用户已有会话，产物验证后保存。
- 同一批 12 张材料在各模式下测量时间、内存与结果一致性。

上述真实系统矩阵由用户负责，当前未执行或未通过，不以17组本机AOT合成回归替代。验证摘要见[维护进度](../migration-progress.md)，实机步骤见[验收清单](acceptance.zh-CN.md#windows-sandbox)。

## 11. 官方资料

- [Windows Sandbox 概述与系统版本类型](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)。
- [安装条件与 Windows 功能准备](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install)。
- [.wsb、映射、网络、设备与内存配置](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。
- [版本差异](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-versions)、[CLI 与 I/O 限制](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli)。
