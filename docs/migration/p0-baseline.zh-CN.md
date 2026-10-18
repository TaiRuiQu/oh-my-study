# P0 行为、数据与验证基线

日期：2026-10-18（Asia/Shanghai）。迁移依据为根目录 `migration.md`。本文件固定旧实现的可观察行为、合成对照资料和证据边界；C# 等价性、Native AOT、新客户端兼容矩阵及真实性能不能由旧版回归推导为通过。

## 路径与证据约定

用户补充架构要求后，旧Rust/Node/Web源码、依赖和开发工具已整体移到本地 `.reference/`，由Git忽略。根目录 `src/`、`tests/`、`probes/`、`packaging/` 现为C#原生工程；其构建、测试、发布和运行均不得读取或调用 `.reference/`。源码搬离当前工程不代表历史业务迁移或验收完成。

本文保留P0取证时的旧路径以便追溯固定提交。除明确列出的新夹具目录外，`src-tauri/...`、`src/...tsx`、`runtime/...`、`scripts/...`、`probes/typesetting/...`、旧Cargo/npm锁、`target/...`等均指起点提交中的路径；当前本地实物需加 `.reference/` 前缀。`domain/...`、`storage/...`、`ai/...`等短源码名相对于旧 `src-tauri/src/`。历史机器证据JSON中的命令、路径和哈希也保留取证时原文，不是当前根工程的依赖声明。

新工程已独立保存受版本控制的纯数据夹具：

- `tests/Fixtures/Compatibility/`：DTO、用例、资产Base64及P0历史证据JSON。
- `tests/Fixtures/LegacyDatabase/`：001/002原始schema的逐字节副本。
- `tests/Fixtures/ProviderProtocols/`：三协议合成JSON/SSE。

新克隆无需 `.reference/` 即应能完成原生检查。需要研究历史源码时，优先从Git提交 `0b5c5398e6e552792f3fe23cfaa12de7ad206f07` 读取或恢复到单独参考目录；当前本地 `.reference/` 不作为仓库可分发文件，也不重新接入C#构建。

## 1. 迁移起点与复现环境

- 固定提交：`0b5c5398e6e552792f3fe23cfaa12de7ad206f07`。开始盘点时 `git status --short` 仅有 `?? migration.md`；该文件是用户提供的未跟踪迁移决定，不能因不在起点提交中而忽略。
- `migration.md` 起始 SHA-256：`e17f51630d6472901c9b22b0b82789a47784863b895e6fbad0a6c4640dc77fdd`。
- 旧 `Cargo.lock`：`7c1163e6c949a440db5dc756d960b4cf965b7112fef4adf2d4294e6476b837f4`；旧根 `package-lock.json`：`44d81b829c8547daaca5a6e3e600b8d848fb31c84b0ccff600f0e620126924ce`，现均在 `.reference/`。历史依赖解析以锁文件为准，不用 `package.json` 的范围号代替。
- 本轮工具：`cargo 1.98.1`、`rustc 1.98.1`、Node `v22.23.2`、npm `10.9.8`、PowerShell `7.6.5`；前端构建报告 Vite `7.3.7`。宿主 OS API 返回 `Microsoft Windows NT 10.0.26200.0`。CIM 读取系统版别被拒绝，未将旧报告的 Pro 版别当成本轮确认。
- 旧 Rust 缓存原为 `tmp/cargo-home/`，现为 `.reference/tmp/cargo-home/`；首次直接 `cargo test --locked` 使用默认 Cargo home，获取 `pdfium-render` 时因 Schannel `SEC_E_NO_CREDENTIALS (0x8009030e)` 失败。改用项目已有缓存并加 `--offline` 后完整通过，没有改变系统证书或下载未锁定依赖。
- 原依赖来源：`Cargo.lock` 和 crates.io；根与 `probes/typesetting/` 各自的 npm 锁；`runtime/runtime-lock.json` 固定官方嵌入式 CPython 3.13.2 与 wheels 地址/哈希；`src-tauri/resources/pdfium-lock.json` 固定 PDFium 7881 win-x64 归档/哈希；排版字体固定在 `probes/typesetting/setup-font.mjs` 与既有专项文档中。
- 旧桌面程序使用 `desktop,custom-protocol` 特性构建并嵌入 `dist/`。搬迁前本轮构建成功（1分32秒），产物现为 `.reference/target/debug/oh-my-study.exe`，32,110,592字节，SHA-256=`566d1ad133960003070aa9eda90eb95954481ae73f2e8b9debc516c18c95637c`。它是行为对照工具，仍依赖Tauri/WebView2，不属于无浏览器新客户端。本轮未启动旧UI；构建成功不能替代运行验收。旧源已退出根工程，Git起点与本地参考保留对照依据。

以下是可选的旧版历史对照命令。必须先进入 `.reference/`；它们不属于新根工程的构建或验收步骤。搬迁后开发缓存中的绝对路径可能失效，需要旧工具链自行重建，不能将搬迁前的通过记录视为搬迁后已重跑：

```powershell
$p0PreviousCargoHome = $env:CARGO_HOME
Push-Location -LiteralPath .reference
try {
    $env:CARGO_HOME = Join-Path (Get-Location) 'tmp/cargo-home'
    npm run typecheck
    npm run lint
    npm run build
    cargo fmt --all -- --check
    cargo clippy --locked --offline --workspace --all-targets -- -D warnings
    cargo test -p oh-my-study-core --locked --offline
    cargo test -p oh-my-study-core --locked --offline --test grading_integration -- --ignored
    cargo test -p oh-my-study-core --locked --offline --test host_execution -- --ignored
    cargo test -p oh-my-study-core --locked --offline --test jobs_persistence -- --ignored
    ./runtime/staging/python/python.exe -I ./runtime/test_runner.py
    npm test --prefix probes/typesetting
    python -X utf8 tests/fixtures/migration/fixture_tools.py verify
    ./tests/fixtures/migration/verify_legacy.ps1
    cargo build --locked --offline --features desktop,custom-protocol --bin oh-my-study
} finally {
    $env:CARGO_HOME = $p0PreviousCargoHome
    Pop-Location
}
```

`--offline` 需要已有锁定缓存；干净开发机仅在确需历史对照时按参考目录中的旧专项文档准备依赖。Python/Rust/Node在上述命令中只是可选历史开发工具，不加入新客户端生产、构建或测试依赖。Windows凭据专项默认忽略，本轮没有运行，也没有读取用户密钥。新工程的入口为根 `packaging/Invoke-NativeChecks.ps1`，实际选项与证据见 `migration-progress.md`。

## 2. 命令边界清单

来源为 `src-tauri/src/desktop.rs` 的 `invoke_handler` 和 `src/lib/api.ts`；共 20 个产品命令。除标注外，多字参数使用 `snake_case`。C# 用例应覆盖语义，不要求继续保留 Tauri IPC。

| 命令 | 输入 → 输出 | 必须保留的边界与验证 |
| --- | --- | --- |
| `bootstrap` | 无 → documents/providers/settings/environment/jobs | 组合快照；从系统凭据状态重新计算 `has_key`，显示真实执行策略；既有桌面冒烟 |
| `list_documents` | 无 → `Document[]` | `created_at DESC,id`；页按 `page_index`；基础存储回归 |
| `select_import_files` | 系统选择器 → 本地绝对路径列表 | JPG/JPEG/PNG/WebP/PDF；取消为空列表；canonicalize/reparse 检查并登记授权集合；原生选择/拖放实机仍待矩阵 |
| `import_files` | paths/name/mode/reference_paths → Document | 仅选取或拖入已授权路径；材料必须非空；整批验证后原子导入；导入不调用模型 |
| `append_reference_files` | document_id/paths → Document | 只能构造 reference 输入；共用输入页/字节限制；在途材料不可编辑；不自动批改 |
| `update_pages` | document_id/`PageEdit[]` → Document | 顺序即新页序；只准 0/90/180/270；遗漏页解绑但原件保留；不准外部页/重复ID/删除被结果引用的页 |
| `read_asset` | asset_id/thumbnail → mime/bytes | ID 查询受控文件；字节数组而非任意路径；缩略图 MIME 为 PNG；拒绝逃逸和链接 |
| `save_provider` | provider/key? → Provider | UUID；URL先校验；忽略前端 `key_ref` 的替换企图；新密钥先入凭据库，SQL失败清理新项，成功后删除旧引用；空 key 表示保留旧密钥 |
| `refresh_models` | provider_id → `ModelConfig[]` | 独立读取凭据，三协议模型列表；合并已有手动配置/价格，元数据有来源才更新能力；最多20页/500模型，游标循环失败不覆盖旧配置 |
| `save_settings` | settings → Settings | 三权限模式；默认模型是 `provider_uuid:model_id`，split_once保留模型ID其余冒号；必须存在供应商和模型 |
| `list_jobs` | 无 → `Job[]` | `created_at DESC,id DESC`；快照用于断线/滞后恢复；单元/SQLite任务回归 |
| `cancel_job` | job_id → Job | 终态幂等；撤销授权/网络与执行信号、持久化 CANCELLED；保留已保存结果，阻止迟到提交 |
| `resolve_permission` | job_id/digest/allow_once\|allow_task\|deny → Job | 必须匹配具体待批准调用；本次/本任务授权不跨任务/重启；拒绝结束当前任务 |
| `queue_diagnostic_job` | storage\|permission → Job | 固定诊断；不接受从前端提交任意代码；缺失私有运行时明确失败 |
| `subscribe_jobs` | Tauri `Channel<JobEvent>` → 事件流 | 新订阅取代旧代次；256条广播缓冲；落盘后广播；滞后由 `list_jobs` 恢复，不能依赖内存通知保证持久性 |
| `queue_grading_job` | document_id/provider_id/model_id/thinking?/allow_unknown/reference_only → Job | 冻结输入与价格/策略；旧pending PDF拒绝；相应角色必须存在；密钥/能力校验；同材料不准同时有未终态任务 |
| `grading_result` | document_id → questions/coverage/references/model_calls | 读取已提交结果；材料改变后旧coverage不显示；不要求任务最终成功才显示结果 |
| `list_questions` | 无 → `SavedQuestion[]` | 当前基本题库/错题共用；跨材料查询没有稳定全局排序契约；每份材料内按questions.rowid |
| `edit_question` | id/question → SavedQuestion | 不准改根draft_key；所有作答递归标manual；严格业务校验；直接覆盖无历史版本；在途材料拒绝编辑 |
| `parse_pending_pdfs` | document_id → Document | 显式升级M1材料，保留原PDF和第一个source page ID；无pending时幂等；整批失败不留部分页面 |

原边界没有通用文件、Shell、SQLite 或 HTTP IPC。`Storage::delete_provider` 虽存在，未在上述20命令中公开；不能把内部API误写成既有产品入口。应用退出先关闭新操作入口、等待在途配置/存储写入、取消并等待队列退出，再执行完整性检查；最小化不触发这一退出流程。

## 3. 实际资料位置与资产契约

| 项目 | 旧行为/来源 | 迁移处理 |
| --- | --- | --- |
| 正式数据根 | `desktop.rs` 用 Tauri `app.path().app_data_dir()`，identifier=`io.ohmystudy.desktop` | Windows旧安装通常对应当前用户Roaming应用数据下的identifier；切换前由旧程序的 `bootstrap.environment.data_directory` 确认绝对目录。不要仅凭新包身份推算或创建空目录后视为空资料库 |
| 开发覆盖 | 仅 `debug_assertions` 接受 `OH_MY_STUDY_DATA_DIR` | 新客户端必须提供独立合成资料入口；测试不得默认打开正式目录 |
| SQLite | `<data root>/study.sqlite3`，活动时可能有 `-wal` / `-shm` | 采用SQLite一致性备份或停止写入后检查点；禁止活动时只复制主文件 |
| 资产 | `<data root>/assets/`，UUID扩展名，数据库仅保存单文件名 | 保留原字节、资产ID和关联；拒绝绝对路径、`..`、分隔符、符号链接、junction/reparse；预览缓存允许重建 |
| 任务原件/日志 | `<data root>/jobs/<job_id>/...`；调用子目录含host请求、stdout/stderr等 | 保留本地任务记录与执行输出，不将其映射为计算沙箱中的整资料库 |
| 私有Python | Debug=`runtime/staging/python`；Release=`resource_dir/runtime/python` | 固定应用路径，不替换为系统Python；按需启动；不注入供应商密钥 |
| PDFium | Debug=`src-tauri/resources/pdfium/bin/pdfium.dll`；Release=资源目录相对同路径 | 7881显式加载，不回退系统DLL；进程内绑定一次且完整PDF操作串行 |
| 凭据 | `OhMyStudy/provider/{uuid}`，Generic，UserName=`api-key`，LocalMachine persistence | Blob为密钥UTF-8字节，不是UTF-16；同一Windows账户/新部署身份需要实测可访问性；DB/夹具只有不透明引用 |

输入按魔数识别 PNG/JPEG/WebP/PDF；扩展名不决定格式。单文件 `1..64 MiB`，每批原件/页面缓存合计256 MiB，材料与参考答案解析后合计最多12页，超限整批拒绝。图片最大4000万像素、每边20000，解码分配上限256 MiB，缩略图最大512×512 PNG。材料名上限512 **UTF-8字节**，供应商名200字节、模型ID512字节；C# `string.Length` 的UTF-16单位不能直接替代Rust `String::len()`。

PDF原件独立保留，渲染PNG的 `original_asset_id` 指原PDF，`pdf_page_index` 从0开始；`page_index` 只是当前材料排序。默认栅格宽2400、最大高3600。M1 `pending_pdf` 条目不是“PDF只有一页”，宽高可为空，升级需真实读取页数。来源区域坐标始终相对于未旋转栅格，不能随显示旋转改写。

导入普通I/O或SQL失败会撤销本次记录并删除已创建文件。文件与SQLite没有跨系统事务：进程崩溃可能遗留未引用文件；旧版不自动删潜在原件。新实现的一致性恢复与孤立文件处理是P3新增验收，不能由普通回滚测试冒认通过。来源：`storage/assets.rs`、`documents/mod.rs`、`storage/mod.rs`、`credentials.rs`。

## 4. SQLite 与排序

两个原始SQL文件是旧schema的取证来源；新工程直接读取 `tests/Fixtures/LegacyDatabase/` 中独立保存的逐字节副本，不引用 `.reference/src-tauri/migrations/`：

- `001_initial.sql` SHA-256 `5bd843262b2e7db4cdaa9598bfb3a2ce7ed63e51d47e0d65ee6ffa98a6b097e8`。
- `002_grading.sql` SHA-256 `a0e3234b314eae49b4a93506f91cff6a42ef4ccf64fb382f9d93e71616c94a10`。

`Storage::open` 使用单个专用拥有者线程，busy_timeout=5秒，`journal_mode=WAL`、`foreign_keys=ON`、`synchronous=NORMAL`。版本0按001→002，版本1按002，最终`user_version=2`；每次升级独立事务，`>2`拒绝。不准为强行打开资料而降低版本。新单写入者进程锁/一致性备份尚不是旧功能；应在P3实现，旧SQLite忙超时不能代替它们。

| 表 | 持久化契约 |
| --- | --- |
| providers | `id` + 完整`config_json`；按id升序；has_key存false并在边界重查；不存真实密钥 |
| settings | 仅id=1，`settings_json`；缺记录默认`default_model=null, execution_mode=bypass_permission, sandbox_networking=false` |
| assets | ID、MIME、正字节数、可空宽高、唯一original_file、可空thumbnail_file；原件仍被未显示资产ID引用 |
| documents | ID、名称、`mode`、RFC3339创建时间、pending位；**mode存JSON字符串，如`"ai_grading"`（含双引号），不是裸文本** |
| source_pages | document/asset外键、材料内唯一非负序号、四种旋转、preview_status、material/reference；v2增可空original_asset_id/pdf_page_index |
| jobs | 完整`snapshot_json`为恢复依据，另有kind/state/time/progress/message投影；JSON进度0–100，SQL进度=JSON/100为0–1；时间TEXT通常存Unix毫秒的十进制 |
| job_events | `payload_json`是完整JobEvent，内含完整Job `payload`，并非只有payload；`(job_id,sequence)`唯一且严格递增；与任务快照同事务 |
| grading_runs | job_id主键/外键、document_id、冻结`input_json`、可空coverage/references JSON；失败/取消前已保存内容仍可读 |
| questions | 稳定ID、document_id、draft_key、content_json、RFC3339更新时间；唯一(document_id,draft_key)；重保存保留旧ID和rowid |
| model_calls | (job_id,ordinal)主键；details_json含规范/原始usage、价格快照、估算、重试数；不保存密钥和完整推理响应 |

资料列表按 `documents.created_at DESC,id`；来源页按page_index；供应商按id；JobManager按数值created_at DESC、id DESC。任务恢复SQL按created_at,id升序读取，随后由任务管理器排序。每份材料题目按rowid；全题库 `SELECT DISTINCT document_id` 没有全局排序保证。参考答案取最近一次非空references_json，按jobs.created_at DESC、jobs.rowid DESC决胜；coverage同样取最新并对比当前Document是否等于冻结输入；model_calls按jobs.created_at、ordinal。

C#读写必须保持记录/标识/关联和这些可观察次序；若改为显式序号以移除rowid依赖，应另立schema/升级/回退契约，不在无说明下重排。

## 5. JSON、枚举、时间与数值

新工程完整字段样本为 `tests/Fixtures/Compatibility/contracts.synthetic.json`；旧取证副本保留在 `.reference/tests/fixtures/migration/`。源类型为起点的 `domain/mod.rs`、`domain/grading.rs`、`ai/providers/mod.rs`、`jobs/mod.rs`、`execution/*.rs`。P2需让同一夹具通过C#源生成序列化与领域测试。

| 类型/字段 | 固定约束 |
| --- | --- |
| Document/SourcePage | snake_case字段；created_at为保存原样字符串；宽高、original_asset_id、pdf_page_index可null；page_index u32、rotation u16 |
| Provider/ModelConfig | protocol字符串；key_ref可null；has_key在反序列化时忽略输入并为false；能力vision/tools/messages_budget为bool?；思考列表不可凭模型名推断 |
| Question | draft_key/number/subject/question_type/content/options/points/standard_answer/explanation/knowledge_points/sources/uncertainties/attempt/parts；parts递归；points double?；attempt可null，集合不能缺失为默认空 |
| Content | `kind`内部判别：text{text}、formula{latex}、table{rows}、image{region,contains_handwriting,caption} |
| Attempt | student_answer/student_regions/teacher?/reference_key?/ai_solution/conflict?/grade；教师证据与最终结果分别保存 |
| Grade | verdict、earned?/possible?、source、reason；null表示未知而非0 |
| Reference/Coverage | 参考答案有稳定key/匹配draft_key或null/未匹配原因/角色reference的sources；coverage有page_id/status/question_keys/reason |
| Job/JobEvent | 时间与sequence为u64 JSON整数，时间是Unix毫秒；state大写下划线；Job.input缺失默认null；事件payload为完整Job |
| Thinking | `kind`判别：responses{effort}、adaptive{effort}、budget{tokens:u64}、gemini_level{level}、gemini_budget{tokens:i64}；-1是Gemini自动预算，不能无符号化 |
| 执行调用 | protocol_version/job_id/call_id/tool/content/arguments/working_directory/timeout_ms，全字段参与批准摘要 |

枚举大小写必须逐字一致：

- 导入：`teacher_graded / ai_grading / blank`；协议：`responses / messages / gemini`。
- 执行：`request_permission / sandbox / bypass_permission`；执行器：`host / windows_sandbox`。
- 状态：`QUEUED / RUNNING / WAITING_PERMISSION / COMPLETED / FAILED / CANCELLED / INTERRUPTED`；任务种类：`storage / permission / grading / references`。
- 判定：`correct / partial / incorrect / unanswered / uncertain`；评分来源：`teacher / ai_reference / ai_solution / manual`。
- 其余能力状态与完整枚举清单在`contracts.synthetic.json/enum_values`。

Region/Content/Grade/TeacherMark/Attempt/Question/ReferenceAnswer/Coverage/ExecutionCall以及工具输入有`deny_unknown_fields`，未知字段报错。Provider、ModelConfig、AppSettings、Document、SourcePage、GradingInput、Job、Thinking等多数类型按Serde默认忽略未知字段，重新保存后不会保留被忽略字段；不能全局统一为“都拒绝”或“都保留”。Option字段缺失通常按null，未标default的普通字段/集合缺失报错。`has_key`是特殊skip_deserializing字段。

保留u64上限解析与溢出拒绝；不要经过JavaScript double中转。业务分数为有限非负double，不能默认四舍五入为整数。评分一致容差`<0.0001`；区域右/下边界允许`<=1.000001`。序列化不承诺统一美化、键顺序或RFC3339格式归一化，但权限摘要是下列例外。

`ExecutionCall::digest()` 对结构声明顺序的紧凑JSON **UTF-8无BOM字节** 做SHA-256；不按对象键排序。中文保持UTF-8，反斜线/换行采用Serde JSON转义。C#默认Unicode转义或属性顺序变化会改变摘要。固定合成调用的摘要为：

```text
49df49be73030b3ed8ed2f8d25430e8854106f0a394d927d8ee13cdd81e2f942
```

`contracts.synthetic.json/permission_digest`保存准确canonical_json与顺序；本轮已由旧Rust实际序列化验证。重启后旧授权全部失效，不应恢复一个旧批准以规避摘要差异。

## 6. 业务与任务规则对照

| 规则 | 确定行为、来源 | 对照验证 |
| --- | --- | --- |
| 整卷入口 | `ai/grading.rs`一个整卷原生上下文；不创建逐题Agent；工具read_page/save_references/save_question/python/shell/finish | 三协议环回整卷循环测试 |
| 三导入模式 | 教师结果优先；AI模式以匹配参考答案评分并保留冲突；blank的所有题/小问attempt=null | grading_integration四项及新grade/question夹具 |
| 教师证据 | raw非空+来源区域非空；teacher.grade来源为teacher；教师模式最终grade逐字段等于teacher.grade | teacher_evidence_and_manual_grades_survive_regrading |
| 人工保护 | 编辑递归改manual；AI不可新造manual；重批保留manual grade；教师模式保留已有明确教师证据；受保护小问不得静默移除 | 同上；注意保留grade不等于保护整个文本免受重批覆盖 |
| 作答区别 | 已作答材料必须有attempt；未作答用unanswered；读不清用uncertain且earned=null、reason非空；blank没有未作答记录 | score_parts_blank_and_source_rules_reject_inconsistent_results |
| 错题 | 根题有attempt且verdict为incorrect/partial/unanswered进入；correct/uncertain/blank排除；现有视图不独立列递归小问 | `src/features/grading.tsx:QuestionLibrary`；既有M2桌面共享视图用例 |
| 分数 | 有得分必须知道满分；earned≤possible；correct等于满分容差；partial在0和满分之间；incorrect/unanswered有确定得分时必须为0；uncertain不可有确定得分 | 新15项评分夹具由旧Rust执行 |
| 题目结构 | 根/小问键不重复，draft_key非空≤128字节，深度≤8，最多1000节点；题号、内容和来源非空；单题序列化≤1MiB；小问满分/得分合计一致 | domain/grading.rs；grading_integration |
| 来源 | page必须属于当前Document及正确角色；有限归一化坐标；原图/作答/教师/题图来源分别校验；旋转只影响视图 | 新7项区域夹具、页级PDF专项、既有高亮冒烟 |
| 参考答案 | ≤1000，稳定唯一key，同题不可多条冲突匹配，answer/sources非空；未匹配需原因；已被评分引用的key不能丢弃；独立整理不批改 | reference_matching_is_separate_and_ai_reference_requires_a_match |
| 自动入库 | 每次save_question立即提交；同材料draft_key更新稳定ID；重复导入始终独立Document；失败/取消不撤销已提交题目 | save_idempotence_is_scoped_to_document_and_cancel_blocks_late_writes |
| 覆盖完成 | 目标页全部实际read_page、coverage唯一完整；covered材料页必须列本页已保存题键与来源；无题/读不清要原因；本轮保存根键集合等于全部已有根题；不能引用不存在题目 | 三协议whole-paper测试；只能验证工具状态，不能证明视觉没漏题 |
| 队列 | 全局单线程任务串行，等待权限保留执行位；任务设置/输入冻结；取消后需执行器实际退出才让下一项运行 | jobs::tests九项 |
| 取消/失败 | JobContext提交锁与cancel共享；DB又检查RUNNING和冻结Document；终态拒绝迟到结果；已落库不丢失 | 队列竞态、批改存储、在途网络取消 |
| 重启/退出 | 未终态转INTERRUPTED，清pending批准，不重放模型/代码；退出取消排队/运行/待权限任务并等待线程；最小化继续 | jobs::tests、jobs_persistence；既有桌面退出/重启用例 |
| 原子事件 | sequence递增、snapshot与event同事务，成功后broadcast；失败停止队列/撤销执行，避免无审计执行 | job_snapshot_and_event_commit_together_without_reused_sequences、队列落盘顺序测试 |
| 权限 | 具体调用digest；allow_once消费，allow_task仅当前活任务；拒绝/取消使令牌失效；每个job/call ID不可重复 | permissions单元、队列digest/授权测试 |
| 宿主执行 | 挂起创建→绑定独占Job→启动；timeout/cancel/output limit/正常退出均回收后代；中文路径/参数与环境白名单；只用私有运行时 | 本轮显式private_host_execution_lifecycle和真实批准后Python测试 |
| Sandbox | 策略ready可选sandbox；不可用时显式host回退/警告；真正sandbox执行器尚未接入整卷/诊断，不能宿主重放或伪造成功 | policy9态与sandbox unit；真实OS生命周期未验收 |

策略层`freeze(None)`默认偏好Sandbox，但`AppSettings::default()`明确bypass_permission；不能混淆两个默认。旧桌面启动硬编码capability=`unknown`并说明自动启停/归属未验收，因此界面实际仍为本机执行。Sandbox网络选项随任务快照冻结，未实现的OS能力不能因有字段而算完成。

## 7. 三协议与模型调用

来源为起点的 `ai/providers/{responses,messages,gemini,transport,stream}.rs`，当前实物位于 `.reference/src-tauri/src/ai/providers/`。新工程合成样本在 `tests/Fixtures/ProviderProtocols/`，旧取证副本在 `.reference/tests/fixtures/protocols/`。新实现分别保留原生协议，不统一改为Chat Completions。

| 项目 | Responses | Messages | Gemini |
| --- | --- | --- | --- |
| 端点 | POST responses | POST messages | models/ID:generateContent；流式streamGenerateContent?alt=sse |
| 密钥头 | Authorization: Bearer | x-api-key；默认anthropic-version=2023-06-01 | x-goog-api-key |
| 上下文/工具 | input/output items/function_call_output | messages/content/tool_use/tool_result | contents/parts/functionCall/functionResponse.parts.inlineData |
| 思考 | reasoning effort | adaptive + output_config.effort；enabled + budget_tokens | thinkingLevel或thinkingBudget |
| 不透明块 | encrypted reasoning原样回送 | thinking/signature/redacted_thinking原样保留 | thoughtSignature留在原part位置、保留调用ID |
| 模型分页 | models；网关has_more/last_id时after | models?after_id | models?pageToken |
| 规范用量 | output含reasoning一次；缺cached细项时input_uncached未知 | input=uncached+缓存读+写；缺缓存字段视0但raw保留缺失；reasoning未知 | output=candidates+thoughts一次；input_uncached饱和减缓存 |

能力未知需用户显式allow_unknown主动测试；已知false视觉/工具仍拒绝；不靠模型名推断。Messages预算至少1024且小于max_output_tokens；Gemini预算≥-1；原生extra与管理字段冲突明确报错，不静默删除或跨协议重试。模型刷新保留未被新元数据确立的能力与已有价格。

HTTP仅HTTPS或loopback HTTP，拒绝URL嵌入凭据/query/fragment、重定向及额外头伪装认证。每次180秒、响应/流32MiB上限。增量UTF-8/SSE必须跨任意字节边界，工具参数/签名分片保持正确；EOF、未闭合块、错误工具关联、token截断、拒绝不能成为空成功。公开进度不泄漏思考/签名。

传输层每次一次请求；整卷业务最多60轮、固定16384输出token、20分钟在调用边界检查；retryable仅network/server/rate_limit等，最多额外2次，Retry-After数字或退避并上限30秒；认证/余额/参数错误终止。20分钟不是打断一切的硬deadline。取消中止在途请求/退避，取消后不能保存usage/题目。估算仅当input/output与两种价格都存在时为`(input*p+output*q)/1_000_000`，否则null；保留币种与价格快照。

真实供应商图文工具往返、真实签名分片形态与真实样卷正确率仍未验收。合成opaque值不能发送到真实服务；本轮无供应商调用或费用。

## 8. 新合成夹具与已有回归资产

`tests/Fixtures/Compatibility/README.md` 是新工程纯数据夹具入口。表中JSON文件位于该目录；两个可选历史工具仅在 `.reference/tests/fixtures/migration/`，不放入新根测试依赖：

| 文件 | 用途/已验证范围 |
| --- | --- |
| contracts.synthetic.json | 一套完整跨语言DTO、稳定UUID、题目/参考/任务/事件、枚举、原始权限摘要、规范usage |
| cases.synthetic.json | 15 JSON、15评分、7区域、11题目/错题、9策略、19工作流、9公式、4性能规模；工作流为回归清单，公式/性能为待测样本 |
| assets.synthetic.json | 73字节PNG与659字节2页PDF，Base64+SHA-256；纯合成无用户内容；PNG不是实际PDF渲染结果 |
| .reference/.../fixture_tools.py | 可选历史标准库工具；创建全新隔离v1/v2 SQLite和资产，检查WAL/外键/原SQL升级/字节；拒绝覆盖已存在目录 |
| .reference/.../legacy_contract.rs + verify_legacy.ps1 | 可选历史Rust oracle；临时产物在参考目录tmp；不供C#构建/运行调用 |
| evidence.2026-10-18.synthetic.json | 搬迁前P0实际命令、结果与未执行项；历史路径不改写，不代表搬迁后重跑 |

以下旧资产已退出活动工程，保留于 `.reference/` 或Git起点：14项协议测试与JSON/SSE合成夹具；`ai/grading_tests.rs`三项整卷测试；四项批改存储、十项基础存储、一项任务重启；权限/进程/Sandbox状态机；`m1_fixture`/`m2_fixture`与M1/M2桌面冒烟；`probes/typesetting/sample.mjs`九原创题和十二长小问；固定Typst模板、MathJax/mhchem样本、PDF检查器；`runtime/test_runner.py`；三个check脚本。所需SQL/JSON/SSE数据已独立复制到新 `tests/Fixtures/`，后续C#回归直接消费这些版本控制内文件。

本轮新Rust oracle实际运行15个JSON、15个评分、7个区域、6个题目领域、9个策略、3个协议usage和1个权限序列化/digest；另5条错题谓词断言来自旧前端筛选表达式，没有执行React UI。夹具工具校验通过不等于C#已实现同样规则；19工作流案例指向旧测试，不能把“清单存在”当作额外19项运行结果。

## 9. 搬迁前P0实测与沿用证据

下表是整体搬迁到 `.reference/` 之前取得的P0证据。没有在本次文档调整中重新执行旧Rust/Node工具；路径变化与新C#工程验证由当前迁移进度分别记录。

| 检查 | 2026-10-18本轮结果 | 限制 |
| --- | --- | --- |
| npm typecheck / lint / build | 各退出0；Vite 2673模块 | 生产chunk>500kB警告；不代表新UI性能 |
| 旧桌面对照构建 | desktop,custom-protocol退出0；32,110,592字节 | Debug产物；本轮未启动，依赖WebView2，不是新AOT客户端 |
| cargo fmt / core Clippy | 退出0，Clippy `-D warnings` | core all-targets；desktop特性Clippy本轮未跑 |
| Rust默认测试 | **53通过**：38单元+4批改存储+1任务存储+10基础存储；4 ignored | 凭据、PDFium、Host、真实Python原为ignore；按下三行分别补证 |
| PDFium ignored专项 | **1通过**，7.57秒 | 自建PDF真实页渲染/原件/限制/旧升级；不代表真实用户PDF全集 |
| Host ignored专项 | **1通过**，2.35秒 | 真实私有Python、Shell参数、取消/超时/输出/后代；不涉及Sandbox |
| 任务私有Python ignored专项 | **1通过**，1.18秒 | 实际批准后运行科学计算；不是真实AI批改 |
| 私有Python runner | **9通过**，7.720秒 | 私有计算/桥接，不代表真实OS沙箱生命周期 |
| 排版Node测试 | **5通过** | 白名单/题答隔离/模板；本轮未重新生成PDF或打印 |
| 新夹具自检 | v1/v2、v1→v2SQL、2资产哈希、拒绝覆盖、固定digest通过 | 仅标准库夹具完整性 |
| 新Rust oracle | 上述55项Rust业务/解析断言+5错题谓词+digest通过 | 不代表C#对照已通过 |
| Windows凭据ignored专项 | **本轮未执行** | 没有访问用户凭据；旧progress记载曾用独立合成项通过，需发布身份再验 |
| 旧桌面M1/M2 | **本轮未启动** | 既有progress记载M1=8、M2=9；WebView2合成冒烟，物理DPI/干净系统未验 |

上表Rust oracle“55项”是15+15+7+6+9+3；不应与默认53套测试相加形成新测试总数。普通Rust、显式native、Python、Node和夹具分别计数。旧记录的桌面截图/报告属于历史 `output/`；其本地产物若已归档，需按归档位置读取或在参考目录重建，不能当作根 `output/migration/` 的新C#证据，也不能据此承诺本轮已逐图复核。

## 10. 未验收项、性能方法与兼容矩阵

P0确定的是既有行为及可复用证据；以下保持独立未通过状态：

- M2真实数学三栏、生物跨页续题、化学教师批改的漏题/串答/评分冲突，无真实准确率结论。
- 三协议真实供应商图文→工具→图片回送→完成、思考配置、真实usage和签名。
- Windows Sandbox真实自动启动/握手/归属/取消/退出；不得提权启用功能、接管或按进程名关闭用户已有沙箱，失败不宿主重放。
- 无浏览器公式完整覆盖（含mhchem、中文、长式与非法输入）、固定字体/基线、原生PDF预览/导出/实际A3/A4打印。
- 新客户端实际Release Native AOT产物与AOT/裁剪警告，干净系统离线发布、升级、卸载、恢复；调试/JIT不能替代。
- M3完整标签/筛选/分页/日期、M4复习卷与独立校验、M5手机传图等原路线功能未因迁移变为完成。

性能先固定参考CPU/GPU/8GB RAM/SSD、Windows构建、屏幕刷新率、DPI、电源/主题及产物哈希；使用Release Native AOT。`cases.synthetic.json/performance_datasets`固定12页2400×3600（10材料+2参考）、5000/20000根题各3小问、100次浏览/编辑/取消/排版循环。数据集规模是测试设计，本轮未生成高分辨率产品材料或取得指标。

启动冷/热各至少30次，记录请求→可操作；操作使用真实输入/视觉响应时间戳；UI帧追踪分别记录P95/P99、掉帧、UI线程、解码/布局/渲染、分配/GC；使用明确PID与创建时间绑定整个所属进程树，同一采样时刻私有字节相加，工作集/GPU/VM/系统另列。保留原始ETW/CSV/JSON、采样间隔、权限缺失和瞬时峰值遗漏说明。可沿用`measure-resources.ps1`作200ms采样辅助，不能靠它证明16.7ms帧或瞬时峰值。

初始预算沿用migration.md：启动P95≤1.5s、操作反馈≤50ms、缓存页返回P95≤100ms、60Hz帧16.7ms/120Hz专项8.3ms、空闲目标≤150MiB首轮≤200MiB、常规本机总峰值约1GiB、长时间资源可回收。**全部是目标，没有本轮产品通过结果。** 旧Python157.16MiB与Node+Edge738.48MiB仅为既有开发探针观测，不充当C#产品基线。

| 兼容维度 | 必测组合 | 本轮状态 |
| --- | --- | --- |
| Windows 10 | 22H2 x64、LTSC2021 x64 | 未测 |
| Windows 11 | 24H2/25H2 x64，记录具体构建/版别 | 当前构建26200仅旧core/native测试，未形成系统通过矩阵 |
| DPI/显示 | 100/125/150/175/200%，跨屏；60Hz/高刷 | 未测；旧900px仅逻辑视口 |
| 交互/辅助 | 鼠标/触控板/键盘、中文IME、读屏、焦点、高对比、主题/透明/减少动画 | 新客户端未测 |
| Sandbox/打印 | ready/unsupported/needs_enable/needs_reboot/busy/failed；打印机缺失 | 状态机有覆盖，真实OS/打印未测 |
| 干净系统 | 无开发SDK/系统Node/Python/TeX，断网本地核心流程 | 未测 |
| 资料/身份 | 旧Roaming目录、MSIX/非打包身份、旧凭据、中文空格路径 | 合成资产/宿主中文路径通过；真实安装连续性未测 |
| 失败恢复 | 只读/磁盘不足、活动WAL、一致性备份、单写入者、升级中断/恢复 | 普通事务回滚通过；完整备份/切换/回退未实现 |

技术API最低系统版本、官方维护支持与本项目实测矩阵是不同承诺；P1按实际锁定Windows App SDK/.NET版本补表。P0不从目标框架推断旧Windows可用。

## 11. 数据切换与回退门槛

1. 先只用合成v1/v2资料，保持旧/新客户端资料根隔离。旧版只有SQL单连接线程，不提供跨进程单写入保护；P3须新增明确的库占用锁，禁止两个客户端同时写同库。
2. 真实切换前记录旧程序返回的数据绝对根、安装身份、DB版本/完整性、全部ID关联/排序、资产字节哈希、旧凭据引用可访问性；批准前不向新默认目录搬运用户资料。
3. 停止旧写入，使用SQLite一致性backup或停机检查点后完整复制，连同assets/jobs创建带版本/时间/哈希清单的不可变备份。在副本先验证v1/v2读取、编辑、原件、重启、取消、恢复；活动DB不可只复制study.sqlite3。
4. 新版如保持旧schema仍需旧版实读/编辑对照。任何schema或JSON写入变更都单独记录最低读取版本，不假定旧版可读。没有旧版可读证据时，回退使用切换前备份，明确新版新增资料的导出/合并方式。
5. 以下任一发生即停止切换：未知版本、完整性/外键错误、原件哈希不符/来源丢失、凭据因身份不可见、ID/页序变化、取消后仍提交、权限/Sandbox执行环境误报、AOT关键能力或离线依赖缺失。保留出错副本及脱敏诊断，不对唯一原库继续试错。
6. 恢复演练必须验证旧客户端在备份副本可启动并读回题目/原件/任务；新旧锁释放可靠；凭据引用和原件均连续可用。旧源码已经按用户要求移出活动工程；在这一演练、P1路线门槛和P9安装验收完成前，继续保存Git起点、必要的本地参考和唯一恢复备份，不将旧工具重新接入根工程。

接手顺序：先阅读迁移进度与P1阻塞，从根原生检查入口验证C#工程，直接使用 `tests/Fixtures/` 的SQL/JSON/SSE记录兼容差异；确需追查旧行为时才在独立参考目录运行可选历史工具。P1关键路线通过后再推进P2/P3及后续业务，真实模型/OS/打印证据继续单列。
