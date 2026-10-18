# 实施进度与交接

更新：2026-10-18（Asia/Shanghai）。执行依据：[实施计划](docs/implementation-plan.zh-CN.md)。

## 当前阶段

**M0 验证工程已落地，本地自动化通过，整阶段验收未通过；M1–M5 尚未开始。** 仓库从纯文档起步，当前交付可重跑的执行、协议、排版与测量探针。真实供应商、Windows Sandbox 兼容矩阵和实际打印仍缺证据，不能进入“已完成 M0”状态。门槛见 [M0 验收说明](docs/m0-acceptance.zh-CN.md)。

| 阶段 | 状态 | 交付与剩余条件 |
| --- | --- | --- |
| M0 执行环境 | 原型与本机验证通过／沙箱待测 | 三模式、权限绑定、私有运行时、宿主进程树与文件桥接测试；未启动真实 Sandbox |
| M0 原生协议 | 合成与环回验证通过／真实调用待测 | 三套原生适配、正常／流式往返、思考上下文、模型分页、错误和取消；14 项协议测试 |
| M0 正式排版 | 本地技术路径通过／集成和打印待测 | MathJax SVG + native Typst；A3 / A4 四份 PDF 共 8 页逐页核验；当前是 Edge 开发探针 |
| M0 资源基线 | 开发探针已测／产品待测 | 私有 Python、Node + Edge 进程树及组件文件字节已记录；无产品资源达标结论 |
| M1 应用基础 | 未开始 | Tauri / React、SQLite、资产、设置与任务界面 |
| M2 整卷闭环 | 未开始 | 导入、整卷 Agent、参考答案、入库与编辑 |
| M3 题库与错题 | 未开始 | 查询、标签、错题联动、本地日历日范围 |
| M4 复习卷 | 未开始 | 原题快照、新题独立校验、正式 PDF |
| M5 发布完善 | 未开始 | 手机上传、备份恢复、离线安装包与干净系统验收 |

## 约束

- 仅整卷入口，不开发单题 Agent。
- 不自动启用 Windows 功能、提权或操作用户已有沙箱；沙箱中断不在宿主重放。
- 不读取或提交用户密钥和样卷；真实供应商验收与合成协议测试分开。
- 开发工具与探针产物不等于完整离线运行时或发布包。

## 已完成任务

- [x] Rust 工作区与 `Cargo.lock`；统一格式、严格 Clippy、本地测试脚本。尚未初始化 Tauri／React。
- [x] 三模式冻结快照与有效环境警告；所有不可用状态的明确回退；沙箱失联不在宿主重放。
- [x] 权限批准绑定完整调用，允许本次／本任务／拒绝、等待暂停、取消撤销、跨任务和重复调用保护。
- [x] Rust 宿主 Windows Job 执行器：先挂起再绑定进程树，正常退出／超时／取消回收后代，环境白名单，日志限制，中文及 PowerShell 参数数据边界。
- [x] 官方嵌入式 CPython + 13 wheels 的完整地址／SHA-256 锁；离线组装与私有路径执行，固定计算库成功导入。
- [x] `.wsb` 导出、随机会话身份、四个最小映射、原子请求／响应、guest Job runner；宿主响应及 PNG/JPEG 解码／路径／大小校验。真实 OS 会话启停尚未实现。
- [x] 私有 Python 的中文路径、Agg 绘图、可写缓存、函数图／几何图 PNG 与 SVG；另用排版探针固定的 OFL 思源宋体实际出图并看图。
- [x] Responses / Messages / Gemini 原生 JSON 与 SSE、图片工具往返、thinking／reasoning／signature 保留、用量和错误分类；实际环回 HTTP 的端点／头／分页／取消／分块传输验证。
- [x] 独立审查并修复：拒绝响应成为空成功、纯文本关键字被误判为图片、模型列表非 JSON 错误丢失状态。
- [x] MathJax + mhchem 自包含公式 SVG（含中文轮廓和基线）、字段白名单、固定 Typst 数据模板、题答与学生作答隔离。
- [x] A3 横向双栏／A4 纵向单栏的题目卷和答案册；真实 PDF 尺寸、字体嵌入、题答对应与泄漏检查，全部八页视觉核验。
- [x] 资源采样器自检；开发探针实测及磁盘分组件清单。缺失项显示未知，未冒充产品指标。

## 验证与产物

当前已运行：Rust 单元测试 **25 通过**（其中协议 14 项）；Rust 宿主实际集成测试 **1 通过**；排版 Node 测试 **5 通过**；私有 Python runner 测试 **9 通过**。`cargo fmt --all -- --check`、`cargo clippy --locked --workspace --all-targets -- -D warnings` 通过。宿主集成测试默认 `ignore`，本次已显式运行；默认跳过不能当作通过。

```powershell
# 从根目录运行；首次准备步骤见专项文档。
./scripts/check-m0.ps1 -Runtime -Typesetting
```

专项交接文档：[执行与私有运行时](docs/m0-execution.zh-CN.md)、[协议](docs/m0-protocols.zh-CN.md)、[排版](docs/m0-typesetting.zh-CN.md)、[资源](docs/m0-resources.zh-CN.md)。排版的 `verify.py` 还需要开发 Python 的 pypdf／pdfplumber 和 Poppler，按排版文档单独执行。

本地生成物均被忽略，不提交运行时二进制、依赖、原件或测试输出：

- `runtime/staging/python/`：私有计算环境；`runtime/wheelhouse/`：14 个锁定归档，可离线重建到新目录。
- `probes/typesetting/output/{a3,a4}-{questions,answers}.pdf`：四份样卷；同目录保存 SVG、PNG 页图、metrics 与 verification。
- `output/measurements/`：资源原始 JSON／CSV；Python 观测最大 157.16 MiB，开发排版 Node + Edge 738.48 MiB，均非产品峰值。
- `output/sandbox-capability.json`：普通用户只读检测。Win11 Pro 26200，HypervisorPresent=true，Sandbox 可执行文件未发现，功能查询需提权；最终 **Unknown**，不声称 BIOS 关闭或功能已禁用。
- `output/sandbox-tasks/m0-manual/`：已成功导出的 `.wsb` 与初始请求，**未启动**。
- `tmp/宿主 执行-*/`：宿主集成测试的调用与日志，仅固定测试代码及合成凭据。

## 下一位接手者的顺序

1. 重跑上述本地检查，先阅读专项文档的验证边界；生成目录不存在时按锁文件重建，不依赖本机残留环境。
2. 在准备好的 Windows 10／11 系统上验证 `.wsb` 启动、真实映射、中文字体、握手、取消、会话所有权和退出回收；补齐不支持／未启用／重启／忙碌等状态矩阵。**不得用 guest 握手代替 OS 所有权，不得按进程名关闭沙箱。** 当前机器无可用的已验收环境，未自动启用 Windows 功能。
3. 用户自行配置供应商密钥后完成三协议真实图文工具往返，保存模型／配置／usage 与脱敏证据；当前没有真实供应商调用，也没有读取密钥。
4. 先建立最小 WebView2／Rust Typst 风险探针，复核同一 SVG／模板路径；这不等于交付 M1 桌面基础。A3 100% 与 A4 实际纸张打印继续作为独立待测项，最迟在 M4 验收完成。Node native Typst 探针只证明开发路径。
5. M0 门槛有证据后更新此文件，进入 M1 的静态 React 桌面、SQLite 专用线程、资产、设置与任务队列。M2–M5 按计划依赖继续；不要把当前验证工程当作已经可用的桌面程序。

明确剩余实现：真实 Sandbox 自动生命周期及所有权、SVG 产物 sanitizer（目前拒收）、超高单块排版预检、完整离线字体／PDFium／WebView2 分发，以及 M1–M5 全部产品功能。
