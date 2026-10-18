# M1 本地数据与资产

`src-tauri/src/storage` 通过一个名为 `study-database` 的工作线程独占 SQLite connection。异步接口只发送操作并等待 oneshot 返回，SQL、文件写入和图片解码均不占用 Tauri 的异步执行线程。`Storage::open(data_dir)` 仅供应用初始化使用；队列的同步 JobStore 接口仅供队列工作线程使用。

数据库保存在应用私有数据目录 `study.sqlite3`，原件与缩略图在同目录的 `assets/`。不将这个目录映射给计算沙箱。前端 DTO 只有 ID 和元信息，没有本机存储路径。

## 迁移与事务

- `migrations/001_initial.sql` 建立供应商、设置、原件、材料、来源页、任务及事件表；`PRAGMA user_version=1`。数据库高于支持版本时拒绝打开，避免旧版本修改新数据。
- 每个连接打开时显式开启 WAL、外键和 5 秒 busy timeout。迁移在一个事务中执行，所有用户输入均作为 SQL 参数传递。
- 导入先验证整批输入，再写原件与缩略图，最后事务登记材料、资产和来源页；发生普通 I/O 或 SQL 错误时回滚数据并移除本次已创建文件。
- 文件系统与 SQLite 无法组成真正的跨系统事务。进程在写文件后、提交数据库前突然退出，可能留下未被数据库引用的文件；M1 不会把这些文件展示为材料，也不会自动删除可能需要恢复的原件。后续备份／缓存维护需处理孤立文件。
- `verify_integrity()` 执行真正的 integrity_check、foreign_key_check、foreign_keys 和 journal_mode 检查。

## 稳定接口

| 接口 | 结果与约束 |
| --- | --- |
| `list_providers / upsert_provider / delete_provider` | 协议、地址、模型能力、价格和凭据引用；不存储明文密钥 |
| `get_settings / save_settings` | 默认模型、权限模式和沙箱联网选项；默认本机绕过权限，实际环境由执行层判定 |
| `list_documents / get_document` | 材料列表／详情，`pages` 同时包含 material 与 reference 角色 |
| `import_document(name, mode, sources)` | 输入 `ImportSource { name, bytes, role }` 来自 Rust 原生选择器边界，非可直接反序列化的前端路径请求 |
| `append_document_sources(document_id, sources)` | 在已有材料后附加原页或参考答案；遵守整份输入限制 |
| `update_pages(document_id, edits)` | 按 `PageEdit { id, rotation }` 列表原子更新顺序与旋转；未列出的页解除材料关联，原件保留；拒绝外部材料页与重复 ID |
| `get_asset(asset_id)` | MIME、字节数、宽高等元信息 |
| `read_asset(asset_id, thumbnail)` | 返回受控 MIME 与字节；按 ID 查询内部文件引用；前端无法传入任意文件路径 |
| `save_job_event(snapshot_json, event_json)` | 任务完整快照与完整事件在同一事务中写入，严格递增序号；同步，仅队列线程调用 |
| `load_job_snapshots / list_job_events` | 恢复完整任务快照与顺序事件；由任务层执行重启后的中断恢复逻辑 |

权限模式值为 `request_permission / sandbox / bypass_permission`，导入模式为 `teacher_graded / ai_grading / blank`。任务状态、权限上下文与恢复策略由 `jobs` 模块定义；存储层完整保留 JSON，不尝试自行重放任务或模型调用。

## 输入与文件安全

类型依据魔数识别 JPEG、PNG、WebP 和 PDF，不依据扩展名。图片完整解码后生成最大 512 × 512 PNG 缩略图，原始字节保持不变。每个文件最大 64 MiB、单批最大 256 MiB、每张图片最大 4000 万像素、每边最大 20000 像素；解码另设 256 MiB 分配上限，先验证尺寸再解码。

默认最多 12 个输入条目，包括参考答案。M1 的 PDF 只登记原件，`pending_pdf_pages=true`，来源条目标记 `preview_status=pending_pdf`，宽高为空；该条目不代表已识别一页。PDF 真实页数、渲染、逐页限制和覆盖检查留给 M2；缩略图请求返回明确的待支持错误，原件仍可读取。

所有资产名由 Rust 生成的 UUID 组成。读取还会验证 DB 引用为单个文件名、规范路径仍在资产根目录；拒绝符号链接、Windows reparse point 和 junction，包括指向资产目录内部的链接。数据目录自身与 assets 目录也不允许为 reparse point。对本机恶意进程在检查后竞态替换路径的防护，不属于这些字符串与元信息检查的保证。

供应商地址允许 HTTPS 或 loopback HTTP，拒绝用户名、密码、query 和 fragment。凭据引用严格为 `OhMyStudy/provider/{uuid}`；真实密钥由 Rust 凭据模块管理，`has_key` 仅由应用边界查询系统凭据后组装。配置中的模型 ID 不推断能力；未设置的能力保留 unknown。

## 验证

执行 `cargo test -p oh-my-study-core --test storage_integration`。Windows 受限执行环境可能拦截 `canonicalize` 或临时目录；需要允许测试访问 Windows 临时目录和规范路径检查。

测试覆盖迁移、WAL／外键、设置／材料／供应商／任务重启持久化、参数化含引号输入、三类图片魔数与缩略图、原件字节保留、页序旋转删除、路径逃逸与目录 junction 拒绝、超大像素头提前拒绝、PDF 明确 pending、输入条目上限、SQL 失败时的文件与事务回滚、事件失败时快照回滚、重复事件序号拒绝、供应商 URL 与凭据引用限制。
