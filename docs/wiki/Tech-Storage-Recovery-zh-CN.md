# SQLite 结构、资源、搜索索引与恢复

[首页](Home-zh-CN.md) · [技术内幕](Tech-for-nerds-zh-CN.md) · [English](Tech-Storage-Recovery.md)

## 数据库 v6

资料库包含 `study.sqlite3` 与受管理 `assets`，当前 SQLite `user_version` 为 **6**。升级已有可写旧库前，生成 `study.sqlite3.pre-v6-…` 数据库快照，再事务迁移。v4 将旧递归题目转换为公共题干与扁平评分项，并增加列表摘要列；v5 用 `question_assets` 记录题目生成图片的所有权。

题目仍保存类型化 JSON，列表摘要列支持搜索、筛选、排序，无需逐一反序列化全部题目。参考答案使用 `matched_item_key`；评分回滚记录支持显式任务重试；语义索引表独立缓存向量。旧应用不能打开升级后的数据库。

v6 为作答附件增加独立的 `student` 页面角色，保留原页面 ID、顺序、旋转和 PDF 关联。学生作答与可为空的错因分析继续保存在题目 JSON 中。

## 所有权与索引

LibraryRepository 使用单所有者线程、有序操作、事务、外键、WAL 和写租约，拒绝不支持的结构和非法资源路径。成功保存才建立持久状态，界面草稿独立。

处理图片归属于试卷并保留原件来源区域。仓储在保存、导出前校验所有权，删除和重试时依据引用清理受管理资源。

语义搜索对合格题目文字和向量模型配置取哈希，有效向量可复用；文字或模型变化需重新索引。配置后识别保存可触发后台向量化。本地精确余弦相似度检索可搭配远端重排，无需向量扩展。见[语义搜索](Features-Search-zh-CN.md)。

## 一致复制与恢复

备份服务拒绝已存在目标及源内目标，暂存 SQLite 在线快照、复制资源、写哈希清单、验证后完成新目录。在线备份包含已提交 WAL 数据，运行中只复制主数据库不具备该保证。

复制并切换使用备份服务且保留原库。用户手动备份仍为保存、退出、复制整个目录。恢复时保留备份，选择工作副本为已有资料库。pre-v6 数据库快照本身不是完整资源备份。内部恢复、旧库迁移服务不代表存在专用菜单按钮。

供应商和搜索密钥存于 Windows 凭据管理器，不随库复制。语言偏好和当前库位置属于本机设置。日志可含学习文字与代码，分享前需检查。

源码：[仓储与迁移](../../src/OhMyStudy.Infrastructure/Storage/LibraryRepository.cs)、[SQL 迁移](../../src/OhMyStudy.Infrastructure/Storage/Migrations)、[备份](../../src/OhMyStudy.Infrastructure/Storage/LibraryBackup.cs)、[旧题转换](../../src/OhMyStudy.Infrastructure/Storage/LegacyQuestionFormat.cs)、[语义存储](../../src/OhMyStudy.Infrastructure/Storage/LibrarySemanticSearch.cs)、[重试](../../src/OhMyStudy.Infrastructure/Storage/LibraryJobRetry.cs)。
