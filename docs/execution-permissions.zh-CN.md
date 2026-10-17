# 执行环境与权限设计

状态：依据最终需求定稿。设计适用于应用中的 AI 代码与命令执行，不是对当前开发机器执行操作的授权请求。本轮不启用系统功能、不启动沙箱或运行模型代码。

## 1. 三种用户模式

| 用户模式 | 运行位置 | 执行前确认 | 文件与网络权限 |
| --- | --- | --- | --- |
| 请求权限 | 宿主机，当前 Windows 用户 | 每次 Python／命令调用前确认，可仅允许本次或本任务 | 批准后使用当前用户权限；不是隔离环境 |
| 沙箱运行 | Windows Sandbox | 沙箱内调用不逐次确认 | 宿主仅暴露本任务映射；网络默认禁用，可在高级设置开启 |
| 绕过权限 | 宿主机，当前 Windows 用户 | 不逐次确认 | 不附加应用层文件／网络权限拦截，仍受 Windows 用户权限和 UAC 约束 |

模式控制自由 Python 和 shell 工具。查看本任务图片、裁图、旋转、查询标签、正常保存题目等业务工具无需逐次申请；其接口本来就只处理该任务可用数据。

绕过权限不表示应用自动以管理员运行，也不表示突破操作系统权限。超时、取消、输出体积和资源回收仍然适用，它们用于避免任务卡死，不是文件／网络权限限制。

## 2. 默认值与不可用处理

首次使用时，就绪的电脑默认沙箱运行；不支持或不可用时默认绕过权限，显示警告，并在运行界面持续展示“本机执行／未隔离”。用户主动选择的请求权限或绕过权限优先于默认值。

沙箱选项仅在当前可用时可选。若之前选择了沙箱，但当前机器不可用，实际模式改为本机绕过权限，记录原因并警告；不能继续显示为沙箱。该回退是用户明确要求的产品行为，不再额外增加逐次批准。

警告说明实际原因和影响，例如“不支持 Windows Sandbox，本次代码与命令将在本机使用当前账户权限执行，可访问该账户的文件和网络”。支持但未启用时说明“尚未启用”，不错误声称硬件不支持。

任务记录 preferred_mode、effective_mode、executor_kind、fallback_reason。任务启动时冻结权限配置；用户修改设置只影响新任务。未开始的任务可取消后按新模式重新提交。

运行中沙箱退出时，已完成结果保留，当前调用标记失败；不把可能已经执行过的同一调用自动在宿主机重放。后续重试重新判断实际模式并展示警告。

## 3. 支持检测

检测结果区分：

- Ready：功能已启用，当前版本与虚拟化条件满足，能够完成启动及桥接握手。
- Unsupported：Windows 版本／版本类型或硬件条件不支持，例如 Home 版。
- NeedsEnable／NeedsReboot：支持但功能尚未启用，或需要系统重启。
- VirtualizationUnavailable／PolicyBlocked：固件虚拟化或系统策略阻止启动。
- Busy：系统已有其他 Windows Sandbox，会话无法由本应用独占。
- Unknown／StartFailed：查询不足以判断或真实启动失败，展示具体原因。

依据微软文档核对系统条件：Windows 10 1903 及以上或 Windows 11、受支持的系统版本类型、固件虚拟化、至少两核及磁盘／内存条件。微软推荐 8 GB 内存，与本项目目标设备一致。

不把 WindowsSandbox.exe 存在或系统名称写着 Pro 当作完整就绪证明。启动后的 runner 握手是实际可用性的最终检查。

检测不自动启用 Windows 功能、不自动重启、不自动提权，不使用脚本给 Home 版强行安装沙箱。可以提供系统准备说明；没有沙箱不影响本机模式。

## 4. 兼容 Windows 10 的实现

基线实现采用 .wsb XML 配置、LogonCommand 和应用自带 runner，不要求新版 CLI。Windows 11 24H2 起的新 wsb CLI 可用于会话管理增强，但能力需要运行时检测。

不能依赖 wsb exec 直通 stdin／stdout。微软公开 CLI 文档明确指出其命令执行接口没有进程 I/O 返回能力，所以输入、日志、结果和图片统一经文件交换桥接，不为两个系统版本维护两套业务协议。

不把 Windows Sandbox 当作普通的隐藏 Python 子进程。Windows 10 的隔离桌面窗口可能出现；新版和旧版的启动、窗口与停止行为分别实测。普通宿主辅助进程使用无额外窗口的启动方式。

系统同时只允许一个 Windows Sandbox。应用不接管、修改或关闭用户已有会话，忙碌按不可用策略提示并处理。只有由本应用成功创建并完成会话身份确认的实例，才能由本应用停止。

## 5. 文件映射与通信

每个任务建立独立目录，生成完整、转义正确的绝对路径配置。基线不使用只有较新 Windows 才支持的映射路径环境变量展开。

| 宿主内容 | 沙箱映射 | 权限 |
| --- | --- | --- |
| 固定 Python、库、字体、runner | Runtime | 只读 |
| 本任务实际需要的材料副本 | Inputs | 只读 |
| 宿主创建的请求文件 | Requests | 只读 |
| 日志、响应、绘图和最终文件 | Results | 可写，仅此任务目录 |

不映射整个用户目录、Downloads、题库数据目录、凭据存储或应用配置。沙箱可写映射会真实写入宿主，因此 Results 不能指向这些位置。

沙箱内 LogonCommand 启动可信 runner。runner 读取请求，调用私有 Python 或 PowerShell，把结构化响应和图形写回 Results；宿主 Agent 自主决定何时调用，不需要用户进沙箱桌面操作。

请求包含协议版本、job_id、call_id、代码／命令、工作目录与超时。文件写入采用临时文件加原子完成标记，响应匹配当前调用；文件通知不可用时采用低频异步轮询，不忙等。

沙箱中的整个环境都视为可被自由代码修改，runner 或结果内容不能成为宿主权限批准来源。返回的日志、JSON、图片和路径均是待校验数据，不在宿主当命令或脚本执行。

宿主只接收当前调用已登记的产物，校验实际路径、重解析点、类型和体积；复制后再登记资产。SVG 去除外部引用和脚本。guest 提交的“允许权限”或任意宿主路径不具有授权效力。

## 6. 网络、设备与运行时

默认配置：Networking、ClipboardRedirection、AudioInput、VideoInput、PrinterRedirection 和不需要的 vGPU 关闭；沙箱内只需 Python 计算，不需要这些共享。

在线模型请求、供应商密钥、数据库写入均留在 Rust 宿主。沙箱禁网不影响模型继续看图、调用工具或返回答案。完整计算依赖和字体随离线包提供，沙箱中不必临时 pip 下载才能绘图。

用户可在任务开始前开启沙箱联网；此设置增加沙箱自身的网络能力，不因此扩大宿主目录映射。联网找实验图不作为首版核心路径，默认采用文字描述。

Matplotlib 使用 Agg 后端，配置和字体缓存写在沙箱内部可写目录；不能尝试写只读 Runtime。Windows 原生命令使用可用的 PowerShell 或可执行程序，不依赖 Bash、Windows Terminal 或 WSL。

## 7. 请求权限模式

自由代码在执行前必须准备好可审阅的内容。对话框展示完整代码／命令、工作目录、所属任务和“宿主当前用户权限”的含义，提供：

- 允许本次：仅执行当前内容完全相同的一次调用。
- 允许本任务：允许当前任务后续 Python／命令调用，任务结束即失效。
- 拒绝：不执行，返回明确拒绝结果；Agent 不得通过改写命令规避拒绝。

批准绑定到任务、工具类型、代码／命令内容、参数和工作目录；批准后不能把内容换成另一条。任意 Python／shell 执行可能间接访问其他文件和网络，不能把一次代码批准误称为对每一个文件访问都能拦截。

等待时任务处于 WAITING_PERMISSION，后续调用暂停，模型不会因等待不断重试产生费用。用户可取消任务，取消会撤销所有未执行批准。业务数据格式、分值和来源校验仍按正常规则执行。

## 8. 生命周期、取消与所有权

只有第一次需要自由代码时才启动执行环境；纯浏览、搜索、编辑和原题复现不启动 Windows Sandbox。同一任务的工具调用复用一份环境，任务结束后释放，不跨任务保留包安装和任意代码状态。

宿主执行使用独立进程树管理；沙箱内 runner 管理各次执行的子进程。取消先通知 runner；无响应时按本应用拥有的会话停止整个沙箱。新版本采用已确认的会话 ID，旧版启动／终止路径需在 M0 验证，不能按进程名批量结束其他程序。

应用退出停止本应用拥有的执行环境。用户手动关闭沙箱或运行器退出时，任务标记中断，成功保存的题目和产物保留。

## 9. 资源和离线包

沙箱 MemoryInMB 初值为 2048；这是一项虚拟机配置，并不等于已经测量出的宿主物理常驻内存。根据微软文档，低于启动所需内存会自动提升到至少 2048 MB，不能用较小数字声称满足 1 GiB 总内存目标。

空闲约 300 MiB、本机执行峰值约 1 GiB 仍作为原定验证目标。沙箱模式分别记录应用进程、沙箱虚拟机和相关系统开销，并报告合计峰值、启动时间与系统磁盘占用。8 GB 机器上的运行体验进入验收，不通过时先优化或明确提示模式资源差异。

应用离线包包含 Python、库、字体与 runner，不重新打包分发 Windows Sandbox 系统镜像。Windows 功能启用或 Store／Windows Update 准备可能需要系统安装源、网络及重启，不承诺未准备系统也能完全离线启用沙箱。本机模式的安装和使用不依赖此准备步骤。

## 10. M0 必测矩阵

- Windows 10 支持版本：.wsb 启动、只读／可写映射、runner 握手、中文路径、Matplotlib、取消和停止。
- Windows 11 旧版与 24H2 以后：基线桥接相同，新 CLI 只用于可用的管理能力。
- Home、不启用功能、虚拟化关闭、策略阻止、需要重启、已有沙箱会话：原因准确、警告可见、实际模式显示正确、本机功能可用。
- 请求权限：允许、拒绝、允许本任务、取消、内容变化、批准不得跨任务使用。
- 绕过权限：应用不额外阻拦用户账户可执行的文件或网络操作，不偷偷提权。
- 沙箱失联：不自动重放不确定调用，不杀用户已有会话，产物验证后保存。
- 同一批 12 张材料在各模式下测量时间、内存与结果一致性。

## 11. 官方资料

- [Windows Sandbox 概述与系统版本类型](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)。
- [安装条件与 Windows 功能准备](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install)。
- [.wsb、映射、网络、设备与内存配置](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。
- [版本差异](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-versions)、[CLI 与 I/O 限制](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli)。
