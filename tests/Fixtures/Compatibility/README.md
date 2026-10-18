# C# 迁移兼容数据夹具

固定旧实现提交：`0b5c5398e6e552792f3fe23cfaa12de7ad206f07`。本目录只保存供新C#测试读取的合成数据与历史证据，全部纳入版本控制。新C#工程直接读取这些数据，构建、测试、发布和运行不调用 `.reference/`、Rust、Cargo、Node、npm或旧Python夹具工具。

全部夹具为原创合成数据，不含用户试卷、供应商录制流量或可用密钥。`synthetic.invalid`不会指向真实供应商；凭据引用只是格式样本，没有对应Windows凭据。完整来源、兼容规则、证据边界和回退门槛见 `docs/migration/p0-baseline.zh-CN.md`。

| 文件或相邻目录 | 用途 |
| --- | --- |
| `contracts.synthetic.json` | 精确字段名、枚举、可空值、递归题目、原件/参考/任务关联、时间/进度/用量、固定批准摘要 |
| `cases.synthetic.json` | 15 JSON、15评分、7区域、11题目/错题、9策略；19工作流指向旧测试；9公式与4性能规模仍需对应引擎验收 |
| `assets.synthetic.json` | 4×3 PNG与2页矢量PDF的Base64、字节数及SHA-256；PNG是定位占位图，不是PDF渲染缓存 |
| `evidence.2026-10-18.synthetic.json` | 搬迁前P0实际环境、命令、结果及未执行项；保留历史路径原文，不是新C#执行报告 |
| `../LegacyDatabase/001_initial.sql`、`002_grading.sql` | 旧schema逐字节副本；新C#集成测试从此读取，禁止引用 `.reference/src-tauri/migrations/` |
| `../Protocols/round-trips.synthetic.json`、`messages.synthetic.sse` | 三协议合成往返、opaque上下文及流式事件；假签名不得发送到真实服务 |

JSON中的 `source`、`tests`、旧 `fixture` 路径是来源标记，指固定提交中的历史目录。当前本地历史副本统一加 `.reference/` 前缀；例如 `src-tauri/src/domain/grading.rs` 对应 `.reference/src-tauri/src/domain/grading.rs`。协议数据在新布局中的实际目录为大小写明确的 `tests/Fixtures/ProviderProtocols/`。原生测试必须使用上表路径映射，不按历史字符串加载旧源码或启动旧工具。

从新仓库根执行基础Native AOT检查：

```powershell
./packaging/Invoke-NativeChecks.ps1 -SkipDocuments -SkipDesktopBuild
```

该命令发布并运行新C#基础设施检查，现有探针覆盖基础schema、事务/WAL/备份等范围；具体结果及完整文档、桌面、凭据选项见根 `migration-progress.md` 和本次运行报告。基础探针通过不代表本目录全部领域/协议用例已被C#实现或执行。P2/P3及后续阶段需逐项读取相同JSON/SQL，比较ID、关联、排序、字段、空值、序列化和业务结果，另行保存新C#证据。

关键兼容点：

- `documents.mode` 是含双引号的JSON文本，例如 `"ai_grading"`；JSON枚举本身是普通字符串。
- Job时间为Unix毫秒整数；Document/Question时间为RFC3339字符串。JSON进度为0–100，SQL投影为0–1。
- 严格题目/区域/执行调用拒绝未知字段；多数设置/供应商DTO忽略未知字段；`has_key`不得从输入信任。
- 权限摘要依赖固定字段顺序和Serde风格的紧凑UTF-8字节；准确 `canonical_json` 与SHA-256在 `contracts.synthetic.json/permission_digest`。
- 原PDF有2页；M1 `pending_pdf`只有一个来源条目，不能把未解析条目数当实际页数。空白卷attempt=null，不制造未作答记录。

可选历史工具已整体移到Git忽略的 `.reference/tests/fixtures/migration/`。只有需要追查旧实现时，才从仓库根单独运行：

```powershell
Push-Location -LiteralPath .reference
try {
    python -X utf8 tests/fixtures/migration/fixture_tools.py verify
    ./tests/fixtures/migration/verify_legacy.ps1
    # 构造旧版合成v1/v2库仅用于历史对照；目标目录必须不存在。
    python -X utf8 tests/fixtures/migration/fixture_tools.py seed --schema-version 1 --out tmp/migration-fixtures-v1
    python -X utf8 tests/fixtures/migration/fixture_tools.py seed --schema-version 2 --out tmp/migration-fixtures-v2
} finally {
    Pop-Location
}
```

以上工具不复制到新C#测试工程。它们在搬迁前验证了原SQL v1/v2、v1→v2、资产哈希、拒绝覆盖，以及旧Rust的15 JSON、15评分、7区域、6题目领域、9策略、3usage与1权限摘要；另5条错题谓词来自旧前端表达式。搬迁后的绝对路径缓存可能需要重建，历史通过记录不等于已重新执行。`.reference/`不会随新克隆分发，查阅历史可从上述Git提交恢复到独立参考目录。

工作流清单、公式样本和性能规模存在不代表已经运行。真实模型、Sandbox生命周期、物理打印、干净系统、数据切换与Native AOT产品性能继续单列验收。
