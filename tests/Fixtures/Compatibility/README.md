# 兼容数据夹具

本目录保存当前 C# 回归使用的合成 JSON 和资产。兼容来源固定为旧实现提交 `0b5c5398e6e552792f3fe23cfaa12de7ad206f07`，构建与测试不调用旧工具或 `.reference/`。全部夹具为原创合成数据，不包含用户试卷、真实供应商流量或可用密钥。

| 文件或相邻目录 | 用途 |
| --- | --- |
| `contracts.synthetic.json` | 字段名、枚举、可空值、递归题目、关联、时间/进度/用量和权限摘要 |
| `cases.synthetic.json` | JSON、评分、区域、题目/错题和执行策略边界；来源路径仅为历史标记 |
| `assets.synthetic.json` | 4×3 PNG 与两页矢量 PDF 的 Base64、字节数及 SHA-256 |
| `../LegacyDatabase/001_initial.sql`、`002_grading.sql` | 固定旧 schema 副本，与生产迁移 SQL 独立 |
| `../ProviderProtocols/round-trips.synthetic.json`、`messages.synthetic.sse` | Responses 与 Messages 合成往返、上下文及流式事件；假签名不得发送到真实服务 |

从仓库根执行 `./packaging/Invoke-MigrationChecks.ps1 -DesktopSmoke -WorkflowSmoke` 运行当前完整检查。旧路径字符串不作为加载源码或启动工具的路径；测试使用当前夹具目录。

关键兼容规则：

- `documents.mode` 为含双引号的 JSON 文本，例如 `"ai_grading"`；JSON 枚举自身是普通字符串。
- Job 时间为 Unix 毫秒整数；Document/Question 时间为 RFC3339 字符串。JSON 进度为 0–100，SQL 投影为 0–1。
- 严格题目/区域/执行调用拒绝未知字段；多数设置/供应商 DTO 忽略未知字段；`has_key` 不从输入信任。
- 权限摘要依赖固定字段顺序和紧凑 UTF-8 字节，准确字节与 SHA-256 见 `contracts.synthetic.json` 的 `permission_digest`。
- 原 PDF 有两页；未解析来源条目数不等于实际页数。空白卷 `attempt=null`，不制造未作答记录。

夹具存在不代表场景已运行；当前验证范围见 [维护进度](../../../migration-progress.md)，真实模型、Sandbox、打印、干净系统及真实资料另行验收。
