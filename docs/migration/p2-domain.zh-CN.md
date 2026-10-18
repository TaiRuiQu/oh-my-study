# P2 领域模型与 JSON 兼容记录

日期：2026-10-18。当前为领域层合成回归与本机 Native AOT 证据，不代表 P1 外部系统矩阵、完整业务迁移或真实模型语义验收。

## 已实现

`src/OhMyStudy.Domain/` 保持纯 C#，不依赖存储、界面、旧工程或网络。平铺命名空间为 `OhMyStudy.Domain`；DTO 保留旧 Rust 类型名，C# 属性为 PascalCase，JSON 为 snake_case。记录采用 `record`、`init` 和 `List<T>`，便于用 `with` 生成新快照。

- 资料、页、资产、供应商、模型配置、设置、题目、内容联合、来源、作答、教师批注、评分、参考答案和覆盖 DTO。
- 三协议共享的模型、能力、五种原生思考配置、请求选项、图像、工具、调用、结果、用量、回合、错误和分页 DTO；不在领域层执行 HTTP。
- 任务、完整事件快照、权限审阅、执行调用、冻结策略及枚举。Job 时间与序号使用 `ulong`；进度保持 JSON 的 0–100，SQL 的 0–1 投影由存储层处理。资料与题目时间保留原字符串。
- 严格题目、来源、评分、参考答案、覆盖及执行调用拒绝未知字段；其他旧 DTO 继续忽略未知字段。必填项、可空项、集合中的非空元素、已知字段重复、大小写敏感枚举及整数范围按旧契约处理。`has_key` 输入不受信任，任意 JSON 值均忽略，只能由凭据查询结果设置输出值。
- 来源验证未旋转原图归一化坐标及 0.000001 容差；评分保留 0.0001 严格误差、教师优先、参考答案匹配、手动来源保护、空白卷区别、递归深度与数量限制、小问分值及得分合计、错题谓词、完整逐页覆盖。
- 字符串长度限制按 UTF-8 字节计；单题 1 MiB 使用 Serde 字符串转义和双精度数表达长度，避免中文、控制字符或整数型双精度值导致边界偏差。
- `ExecutionCall.Digest()` 使用固定字段顺序、Serde 紧凑 UTF-8 转义与 SHA-256，涵盖代码、参数、目录和超时。它不依赖通用序列化器的编码器设置。

## 主要调用接口

```csharp
DomainJson.Serialize<T>(T value);
DomainJson.Deserialize<T>(string json);
DomainJson.ToElement<T>(T value);
GradingValidation.ValidateRegion(region, document, role);
GradingValidation.ValidateGrade(grade);
GradingValidation.ValidateQuestion(question, document, references, manual);
GradingValidation.ValidateCoverage(document, coverage, questions, referenceOnly, viewed);
GradingValidation.QuestionKeys(question);
GradingValidation.QuestionByteSize(question);
GradingValidation.IsWrongQuestion(question);
ConfigurationValidation.ValidateProvider(provider);
ConfigurationValidation.ValidateSettings(settings);
requestOptions.Validate(protocol, vision, tools);
executionCall.Validate();
executionCall.Digest();
ExecutionPolicy.Freeze(preferred, capability, networking);
job.State.IsTerminal();
```

领域规则失败抛出 `DomainValidationException`；输入 JSON 失败抛出 `JsonException`。`DomainJsonContext` 是完整注册 DTO、派生联合、数组和列表的源生成上下文；未注册 CLR 类型直接失败，不回退到反射序列化。`AppSettings.Default`、`GradingResult.Empty`、`Capabilities.Unknown` 与 `Model.Manual(id)` 提供原默认值。

## 已执行验证

`tests/OhMyStudy.Domain.Tests` 是无测试框架依赖的控制台项目。构建和运行只读取正式 `tests/Fixtures/Compatibility/*.json`，不调用 `.reference/`、Rust、Node 或旧夹具工具。

本机 .NET SDK 10.0.402、Windows x64，Release Native AOT 发布并直接运行产物：**107 项通过，0 失败**。包括原固定夹具的 15 JSON、15 评分、7 来源、11 题目/错题和 9 执行策略，以及 50 项新增验证：精确权限摘要与各字段变化、Unicode 转义、枚举、全部思考联合、DTO 往返、未知能力与原生思考边界、重复与空字段、完整覆盖、教师与参考优先、递归、UTF-8 长度和单题大小等。

实际执行报告：`output/migration/domain-aot-tests.json`。报告含夹具 SHA-256、逐项结果、反射 JSON 禁用、动态代码开关与 CoreCLR 模块状态；实际原生进程 `native_aot=true`、`coreclr_loaded=false`。发布目录 `output/migration/domain-aot/` 不含托管运行时。仅看 `IsDynamicCodeSupported=false` 不足以证明实际 AOT，因为设置 `PublishAot` 的 JIT 运行也可能关闭该开关。

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
dotnet restore tests/OhMyStudy.Domain.Tests/OhMyStudy.Domain.Tests.csproj -p:Configuration=Release --locked-mode
dotnet publish tests/OhMyStudy.Domain.Tests/OhMyStudy.Domain.Tests.csproj --no-restore -c Release -p:TrimmerSingleWarn=false -o output/migration/domain-aot
./output/migration/domain-aot/OhMyStudy.Domain.Tests.exe output/migration/domain-aot-tests.json
```

本次首次还原的 NuGet 漏洞数据源遇 TLS/凭据错误 `NU1900`；依赖均从项目缓存还原。最终独立发布使用一次性的 `-p:NuGetAudit=false --ignore-failed-sources` 还原，构建与 AOT 分析 0 警告、0 错误；**本次漏洞数据库审核未完成**。仓库默认审核设置没有关闭，也没有全局压制 AOT/裁剪警告。

## 验收边界

固定夹具中的 19 项工作流是后续 Application/Infrastructure 的集成目标；领域回归没有把其清单当作已执行证据。真实供应商、任务队列提交屏障、权限代理、进程树、Sandbox 生命周期、干净 Windows 10/11 矩阵、打印、真实样卷语义、全应用性能与数据切换均不由本报告宣称完成。协议原生 HTTP/SSE 的执行证据由独立协议测试提供。
