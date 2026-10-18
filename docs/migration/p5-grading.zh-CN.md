# P5 整卷编排合成集成验收

日期：2026-10-18（Asia/Shanghai）。本记录单列整卷 C# 编排与保存边界；协议回归见 `p5-protocols.zh-CN.md`。这里的题目、模型响应、凭据和像素全部为合成测试资料，不代表真实识别或评分正确率。

## 验证链路

`tests/OhMyStudy.Grading.Tests/` 是无新增测试框架依赖的 net10.0 / win-x64 console 验收程序。正式 `JobQueue` 调用正式 `GradingRunner`，经正式 `ProviderClient` 接收 fake HTTP 处理器产生的三种原生 SSE，再通过正式 `LibraryRepository` 保存到独立临时 SQLite/WAL 资料库。任务存储适配仅转发真实仓库接口，没有以内存字典替代数据库。

多数场景使用真实协议构建与解析；网络中断、无工具结果、60 轮和迟到响应场景使用注入的 `IModelClient` 精确控制边界。没有真实供应商请求，没有读取系统凭据或真实资料。fake 不透明签名只进入内存 HTTP 处理器，不能外发。

`ILibraryMediaProcessor` 和 `IGradingImageReader` 替身仅验证并传递正式合成 PNG 字节，记录 crop/rotation 参数，不提供识别结果，也不伪称验证实际图片裁切/旋转。供应商合成结果直接定义要验证的工具调用及字段；真实图片处理、授权样卷识别与评分语义另行验收。

## 覆盖范围

- Responses、Messages、Gemini 分别与教师批改、AI 批改、空白卷三模式组合；原生系统指令、整卷现有问题/参考答案上下文、read_page 工具图片、重复工具保存、用量和价格估计均经过实际链路。
- 教师结果优先、参考答案匹配不能被绕过、AI 不能伪造 manual 来源、手改/教师评分与证据在重批后保留，重批不能移除保护过的作答。
- 裁片不计为整页已读；错误覆盖不会写入，整页补读后可恢复。覆盖检查包括漏页、重复页、未知题目键、题目未实际引用本页、无原因 unreadable 和跨页来源修正。
- 不存在的页、裁片来源不符、范围越界、错误方向、未知参数产生可修复工具错误。finish 必须位于本轮最后；错误提前 finish 不会吞掉后续保存或冒充完成。
- 独立参考答案任务限制工具集合，只要求参考页覆盖，不能保存题目或执行代码。
- 网络失败、只输出文字及 60 轮上限不制造完成；已保存问题保留。故意忽略取消的迟到模型响应不能追加问题、用量、覆盖或完成事件；后续任务仍可运行。
- 真实 SQLite 快照和递增事件序列与队列终态一致，并运行完整性检查。
- 退出与正在提交任务竞争时，退出必须等待提交持久化收束，不能在返回后留下新 QUEUED 任务；专用阻塞存储适配器仍向正式 SQLite 转发实际提交。

## 执行状态

已在 2026-10-18 使用本机 .NET SDK 10.0.402 执行以下命令：

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
dotnet restore tests/OhMyStudy.Grading.Tests -p:Configuration=Release --ignore-failed-sources -p:NuGetAudit=false
dotnet run --project tests/OhMyStudy.Grading.Tests -c Release --no-restore
```

结果：Release/JIT 的 211 个断言全部通过，构建未报告警告。包括三协议×三模式完整组合、非法方向恢复、真实 SQLite 保存和事件、60 轮、取消迟到屏障，以及退出和任务提交竞争。临时资料库证据保留于 `C:\Users\XiaoQ\AppData\Local\Temp\OhMyStudy-Grading-e6548e1eef3a48d4b1a077718d65c153`。

已执行 Native AOT 发布并运行实际产物：

```powershell
dotnet publish tests/OhMyStudy.Grading.Tests -c Release --no-restore -o output/migration/grading-aot
& output/migration/grading-aot/OhMyStudy.Grading.Tests.exe
```

Native AOT EXE 同样 211 项全部通过，最终发布未报告裁剪或 AOT 警告。首次 Native AOT 编译发现 JsonArray collection expression 隐含选择反射泛型 Add；已改显式 JsonArray 构造后重新发布通过，没有关闭警告。AOT 执行临时资料库证据位于 `C:\Users\XiaoQ\AppData\Local\Temp\OhMyStudy-Grading-108aba482fa446958b7dc2d1b0b3d7a3`。

离线缓存还原使用的命令行 `NuGetAudit=false` 不改变工程默认设置，也不代表在线依赖审计通过。

独立未验收：真实模型兼容、真实授权样卷的漏题/串答/评分差异、真实图像裁切与旋转、桌面完整交互、干净 Windows 10/11 和真实执行/Sandbox 生命周期。
