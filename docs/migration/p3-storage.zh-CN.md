# P3 存储、资产与凭据交接

日期：2026-10-18（Asia/Shanghai）。本文件记录实现和隔离合成验证，不代表真实资料切换或 P3 整体验收完成。

## 实现边界

`LibraryRepository` 是生产存储入口。`OpenAsync` 明确区分 `CreateNew`、`ReadOnly`、`ReadWrite`；默认只读，不探测后自动切换用户真实库。旧目录仍由 `LegacyLibraryLocation.ResolveDefault` 纯解析到 `%APPDATA%/io.ohmystudy.desktop`，库名仍为 `study.sqlite3`，原件仍放在 `assets`。安装身份下的真实目录与凭据访问仍需 P9 验证。

所有 SQLite 操作由一个专用线程执行，队列最多 256 项，完成通知异步调度；媒体准备由独立的受限后台任务执行。没有在 UI 线程运行 SQL 或同步图像解码的设计路径。媒体处理通过 `ILibraryMediaProcessor` 注入原生解码及 PDF 渲染实现，仓储保留输入原字节；测试处理器仅接受明确的合成 PNG，不能用它宣称生产图片解码验收通过。

SQL 001/002 已复制到 `src/OhMyStudy.Infrastructure/Storage/Migrations`，作为工程资源编译；正式构建不读取 `.reference`。版本大于 2、版本 0 却已有业务表、结构不完整的资料库被拒绝。v1 写打开前用 SQLite online backup 留下唯一命名的 `study.sqlite3.pre-v2-*`，随后 ALTER、建表与 `user_version` 在同一事务提交；只读打开 v1 保持原版本并支持旧列读取。v2 不增加新表或改写原数据格式。

持久化保留资料、页序、旋转、材料/参考页角色、PDF 原件及原页索引、设置、供应商、题目、参考答案、覆盖、模型调用、任务与事件。`documents.mode` 仍存为 JSON 字符串文本（例如 `"ai_grading"`），不是裸枚举字符串。资料排序为 `created_at DESC,id`；页按 `page_index`；题按旧行顺序；任务/事件沿用旧排序及进度 JSON 百分数与 SQL 0–1 换算。

任务快照与事件在同一事务提交，事件序号不可复用。AI 结果写入再次检查任务为 RUNNING、输入资料快照仍相同；按 `(document_id,draft_key)` upsert 保持 ID，不重复插入题目。重批保留已有手动评分及教师模式的教师评分，禁止移除受保护的小问/作答、伪造手动来源或与其他题发生稳定键冲突；来源、参考答案、覆盖与评分校验委托领域规则。用户编辑设置手动来源，活动任务期间拒绝页和结果编辑。

每个写事务在开始执行及 COMMIT 前检查取消；取消发生在 COMMIT 前会回滚。COMMIT 已成功后返回成功，不把随后到达的取消误报为未保存。取消任务后，已成功提交的题目保留，迟到结果拒绝。

## 资产与恢复

保留旧限制：1–12 个输入、每个 1 byte–64 MiB、整批原件和页面缓存至多 256 MiB、图片至多 4000 万像素及单边 20000、缩略图至多 512。类型依据文件魔数而非扩展名。PDF 渲染页和原 PDF 分别持久化，旧 pending PDF 解析保留首个来源页 ID、旋转和角色；页数越界不修改资料。

托管路径只接受单个文件名，拒绝目录分隔、冒号、尾随点/空格和链接/重解析点；读原件校验记录大小。导入以 CreateNew 写原件并 flush 后，才在同一事务插入全部关联。失败回滚数据库并删除本次创建文件；进程突然终止可能留下未引用文件，`RecoverOrphanedAssetsAsync` 可先列出，显式删除仅处理 UUID 命名的托管文件，不自动删除用户放入的其他文件。

`BackupAsync` 使用串行线程。可写打开时以 SQLite `BEGIN IMMEDIATE` 阻止其他数据库写入，再由另一只读连接执行 online backup。只读打开时先拒绝已知旧版活跃进程，以同一只读连接的读事务固定快照，再执行 online backup；源库不升级、不执行写事务。两条路径均包含已提交 WAL，复制相同快照引用的所有原件/缩略图并记录 SHA-256 清单，全部完成后将唯一临时目录改名到新目标目录。现有备份不会被覆盖。凭据不导出。只读快照不能阻止检查后新启动的旧进程或外部工具更改文件；文件被移除或变化时备份应失败，不能据此宣称锁住旧版。

`RestoreBackupAsync` 校验清单、数据库和每个资产哈希，检查 SQLite integrity/foreign keys，并确认引用文件齐全；仅恢复到尚不存在的新目录，失败时原库和目标位置不被替换。切换资料库、替换当前运行库、回写旧版数据均不在这个 API 内。数据库一致备份不等于凭据跨账户迁移；新版本写入的数据也没有被默认承诺旧客户端可读。

## 单写入者与凭据限制

`.native-writer.lock` 仅排除遵守该约定的新版客户端。旧 Rust 客户端不使用此锁，因此可写打开另做旧可执行名进程检查（`oh-my-study` / `oh_my_study`），发现后拒绝并要求退出旧版。该检查无法阻止检查后启动、被改名的旧程序或其他 SQLite 工具；不能声称文件 lease 锁住旧版。真实库首次检查应只读；正式切换仍需确认旧版退出并控制其后续启动。

`WindowsCredentialStore` 保留 `OhMyStudy/provider/<UUID>`、Generic / LocalMachine 和 UTF-8 blob，拒绝空/NUL/超过 2560 byte 密钥。`ProviderCredentialService` 串行处理供应商凭据变更：不替换密钥时复用数据库旧引用；替换时先写全新目标，数据库提交失败则删除新目标，原引用和原密钥保持可用；提交成功后再清理旧目标。删除供应商先提交数据库删除，再清理凭据；清理失败单独报告，不能误报数据库没有保存。

SQLite 与 Credential Manager 没有跨系统事务。进程在两者之间崩溃或清理失败可能遗留未引用凭据；实现不能承诺二者崩溃原子性，也不枚举/自动删除用户凭据。自动测试仅使用内存合成 vault，未读取、替换或删除真实凭据。目标安装身份下凭据访问、系统 vault 错误及清理恢复仍需授权实机验证。

## 本轮收尾与用户接续

`LibraryMigrationService.CopyAndUpgradeAsync`只读原目录，先生成一致备份，再恢复到一个不存在的新目录；仅升级该副本。它比较备份与升级副本的文档、题目、参考答案、覆盖、模型调用、任务/事件、设置和供应商引用的SHA-256指纹，原件由备份/恢复清单逐文件校验。成功收据`migration-receipt.json`不含密钥，不读取系统凭据，也不切换活动资料指针。活动指针仅在App明确选择并打开新库后更新。

2026-10-18实际Native AOT产物`output/migration/publish/storage-copy-upgrade/OhMyStudy.Storage.Tests.exe`执行80项通过。新增18项涵盖v1/v2源库字节不变、副本升级/凭据引用、收据、原版本备份回退、已有路径/包含路径拒绝、取消无残留。首次离线还原有NU1900网络审计数据不可达提示，没有代码/AOT诊断；最终统一检查另记。

旧版不遵循原生写入租约，因此产品迁移只写独立副本，不与旧版共享写入目标；真实资料切换与凭据身份验证按用户决定由用户执行，不计自动通过。

## 已执行验证

独立 `tests/OhMyStudy.Storage.Tests` 控制台工程使用复制的旧 SQL 和语言无关合成资料；所有生成库位于唯一临时目录。2026-10-18 首轮 Release/JIT 执行 54 项检查通过，包括：

- v1/v2 只读、升级及写入；原 ID、关联、页序、旋转、JSON mode 和原件字节。
- 升级中途 SQL 冲突回滚 ALTER/user_version；未来版本拒绝。
- 插入资产失败后数据库和本次文件一并回滚；真实路径被文件占用时 I/O 失败无资料残留；遍历路径拒绝；孤立文件恢复。
- 活跃 WAL 的在线备份、完整恢复、哈希损坏拒绝、不覆盖现有目录。
- 并发写有序；取消后成功题目保留、迟到写拒绝；任务事件序号和快照一致；AI upsert、手改保护、参考答案及覆盖保存。
- 合成 vault 的保存失败、数据库失败后的新凭据清理、引用复用、替换和删除。

追加实际发布验证：同日按 `Configuration=Release` 还原后，`dotnet publish -c Release --no-restore` 日志出现 `Generating native code`，发布到 `output/migration/storage-aot-native`；实际运行 `OhMyStudy.Storage.Tests.exe` 退出码 0，60 项检查全部通过。新增项包括提交前取消无残留、PDF 原件与页关联、旧 pending PDF 首页 ID/方向保留。发布没有 IL/trim/平台警告。PDF 页面像素仍为明确标识的合成存储替身，不能据此宣称生产渲染通过。工具链为仓库锁定的 .NET SDK 10.0.402、Native AOT/runtime 10.0.13；未访问真实用户库或系统凭据。

随后为用户显式选择旧库副本的流程增加只读备份路径，再发布到 `output/migration/storage-aot-readonly` 并实际运行 Native AOT EXE；退出码 0，62 项检查通过，额外覆盖 v1/v2 只读 online backup、恢复后的 ID/关联与 schema 保留，源库不升级。

仍未验证：真实磁盘耗尽/断电和硬件故障、文件路径在检查与使用间被恶意替换的竞态、真实库/旧进程改名启动竞态、生产媒体处理器的所有格式与资源预算、旧 pending PDF 的实机渲染、Windows Credential Manager 失败恢复、安装身份与干净 Windows 10/11 矩阵。文件失败合成回归不能替代真实磁盘耗尽验收。

复现需先按 Release 配置还原新测试工程（使 Native AOT 工具目标进入还原图），再 publish / 运行：

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
dotnet restore tests/OhMyStudy.Storage.Tests/OhMyStudy.Storage.Tests.csproj -p:Configuration=Release --locked-mode
dotnet publish tests/OhMyStudy.Storage.Tests/OhMyStudy.Storage.Tests.csproj -c Release --no-restore -o output/migration/storage-aot-native
& ./output/migration/storage-aot-native/OhMyStudy.Storage.Tests.exe
```
