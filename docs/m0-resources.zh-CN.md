# M0 资源测量

`scripts/measure-resources.ps1` 是 Windows PowerShell 7 只读采样器，不启动或关闭应用或沙箱。明确指定应用 PID，脚本按创建时间绑定根进程与已观察到的子进程，避免 PID 复用混入其他程序。输出原始采样 JSON、CSV 和同一采样时刻合计的峰值，不把不同进程各自的峰值相加。

```powershell
./scripts/measure-resources.ps1 -Scenario 'host-python-plot' `
  -RootProcessIds 12345 -DurationSeconds 30 `
  -ComponentsManifest ./scripts/components.example.json `
  -OutputDirectory ./output/measurements/host-python-plot
```

把 `12345` 换成实际进程 ID。组件清单的路径相对清单文件；复制示例并指向实际构建目录。未安装或缺失的组件记录 `missing` 和空值，不记作 0 字节。目录树遇到重解析点立即停止，避免跟随其他目录造成不实统计。

可用 `-VmProcessIds`、`-SystemProcessIds` 显式加入已经确认属于本次工作的 VM／相关系统进程，独立列出私有字节；不按进程名猜测所有权。未指定时记录 `not_measured`。私有字节并非虚拟机物理驻留内存；完整沙箱验收仍需 Hyper-V／系统指标，并记录来源和权限不足。脚本不提权，不把缺失指标当作零开销。

采样可能错过短时子进程和瞬时峰值。进程树枚举或指标不可读时，报告包含警告，数值只能作为下界。实际场景需保持足够时长，记录冷启动时间、材料尺寸与页数、工具版本和采样间隔，不把单次空载结果当作峰值目标通过。

## 待测矩阵

| 场景 | 证据状态 | 说明 |
| --- | --- | --- |
| 空窗口 | 待 M1 | 当前没有桌面应用 |
| 打开题库 | 待 M3 | 当前没有持久化题库 |
| 查看大页／12 页材料 | 待 M2 | 使用授权材料，记录分辨率 |
| 私有 Python 绘图 | 开发探针已采样 | 官方嵌入式 CPython + 锁定计算包，中文数学 PNG／SVG |
| Windows Sandbox 启动与绘图 | 待环境验证 | 宿主、VM、系统开销分别采样 |
| 生成 A3／A4 PDF | 开发探针已采样 | Node + Edge + native Typst；最终 Rust 引擎待集成 |
| 组件／离线安装包磁盘占用 | 待 M5 | 开发依赖不代表分发包 |

## 2026-10-18 实际开发基线

| 场景 | 采样 | 观测最大私有字节 | 范围 |
| --- | --- | ---: | --- |
| 私有 Python 绘图及结果保留 | 40 次，16,170ms | 164,798,464（157.16 MiB） | 一个私有 Python 进程树，含冷导入、Matplotlib 绘图及部分绘图后保留期 |
| 开发排版探针 | 65 次，28,022ms | 774,356,992（738.48 MiB） | Node + Edge 进程树，含浏览器和 native Typst 缓存保留期 |

两次采样均无进程枚举／指标读取 warning；VM 和系统额外开销均未测量。私有 Python 使用明确的系统环境白名单启动，无供应商密钥；可见 PNG 已检查中文和数学标签，原始结果位于 `output/measurements/私有Python绘图/`。排版结果位于 `output/measurements/typesetting-probe/`。每次间隔设置为 200ms，实际受进程枚举耗时影响。单次 PDF 编译仅约 20–29ms，不能声称捕获其瞬时峰值。两组数据均不代表 Tauri 产品、12 页材料或目标内存已达标。

`scripts/components.m0.json` 可重测当前开发组件；本次磁盘盘点如下，原始 JSON 在 `output/measurements/components/`。不同组件不能直接相加作为产品安装大小，缓存不属于最终安装内容。

| 组件 | 实测文件字节 |
| --- | ---: |
| 私有 CPython 和计算包 | 156,339,152（构建脚本后续同步可能有少量变化） |
| Python 构建归档缓存 | 44,214,162 |
| 固定思源宋体 | 24,543,332 |
| MathJax 开发包 | 34,300,253 |
| Typst 开发 native 绑定包 | 52,186,426 |
| 应用／PDFium／WebView2 离线组件 | 缺失，未测量 |

完整离线包、VM 内存、相关系统开销和最终 Rust 排版路径仍待后续验证。脚本自检的 PowerShell 进程数字仅用于校验采样器，未混入上表。

脚本行为依据 [Get-Process 官方文档](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.management/get-process?view=powershell-7.5)。沙箱 `MemoryInMB=2048` 是配置而非实测，见 [Windows Sandbox 配置](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。
