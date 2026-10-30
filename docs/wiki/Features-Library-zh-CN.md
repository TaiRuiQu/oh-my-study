# 资料库位置、备份、恢复与删除

[首页](Home-zh-CN.md) · [功能详解](Feature-Guide-zh-CN.md) · [English](Features-Library.md)

![数据边界：资料库保留原件与记录，供应商接收图片与上下文，可选代码遵循独立执行策略。](images/data-boundaries.png)

## 了解保护对象

默认 %LOCALAPPDATA%/OhMyStudy/Library，实际位置看设置 → 资料库，打开文件夹最直观。库含 SQLite 与原件、渲染页、缩略图等资源。

PDF 只是派生产物，不能保留所有编辑字段、参考关系、原资源或任务历史。

## 复制并切换

1. 保存题目和设置。
2. 结束任务及模型请求。
3. 更改位置，选父文件夹。
4. 核对 OhMyStudy资料库 目标。
5. 确认复制并切换，等待。
6. 检查显示路径并打开已知试卷。

目标不能已存在。应用制作一致副本并切换，原库保留。复制后切换失败时读提示，不假设副本被删或当前库已改变。

## 手动备份

保存、退出、复制完整文件夹到独立位置。SQLite 的 WAL 可能含已提交事务，运行时只复制 study.sqlite3 会漏数据；只复制数据库也缺原件。

空间需足够，不覆盖唯一好备份，用日期和应用版本标记。同盘副本可防误编辑，不能防磁盘损坏。

## 打开与恢复

选择已有资料库打开完整当前版本库，不合并。保留备份，选择工作副本；拒绝时检查版本，不在原位置创建新库覆盖。

凭据绑定账户，不随库导出，换机/账户可能需重填。旧库需兼容迁移工具，内部迁移服务并非全部有当前菜单命令。

## 删除范围

删除试卷会在确认后移除题目、参考、任务和库内原件，无应用撤销。删除供应商/模型仅删配置，保留学习资料。

删除前可导出复习 PDF；未来还需编辑原件则保留整库。外置盘不可用时重连或选完整副本，不把盘缺失当作覆盖资料许可。

实现与参考: [MainWindow.LibrarySettings.cs](../../src/OhMyStudy.App/MainWindow.LibrarySettings.cs) · [LibraryBackup.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryBackup.cs) · [LibraryPapers.cs](../../src/OhMyStudy.Infrastructure/Storage/LibraryPapers.cs)


## 当前资料库升级

当前结构为 v7。受支持旧版可写资料库按需迁移，包含公共题干/有序评分项、生成图片所有权、独立的学生作答页面角色，以及逐次运行的识别产物，保留原页面 ID、顺序、旋转与 PDF 关联，并保留升级前 `study.sqlite3.pre-v6-…` 数据库快照。该快照不是完整资源备份；升级前保存、退出、复制整个资料库。旧应用不能打开转换后的库，可能回退时保留完整旧备份。见[存储与迁移](Tech-Storage-Recovery-zh-CN.md)。

---

[上一章](Features-Export-zh-CN.md) · [章节目录](Feature-Guide-zh-CN.md) · [下一章](Features-Troubleshooting-zh-CN.md)
