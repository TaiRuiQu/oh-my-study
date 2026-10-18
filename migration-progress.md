# 维护进度与交接

更新：2026-10-18（Asia/Shanghai）。本文件按项目约定持续维护当前状态、验证结果与下一步。

## 当前状态

C# / .NET / WinUI Native AOT 重写已完成 P0–P9 开发实现与本机自动检查。后续工作按产品维护、功能待办和独立验收推进，已完成阶段的计划与过程记录已从当前文档删除，历史可从 Git 查阅。正式构建不依赖 `.reference/` 中的旧代码。

当前分发状态仍为 `release-candidate-user-acceptance-pending`。干净 Windows、真实资料与凭据、真实模型与样卷、Sandbox 实机、实际打印、DPI/输入法/读屏和参考设备性能由用户执行，尚未通过。具体步骤见 [验收清单](docs/acceptance.zh-CN.md)。

## 最近已记录的验证

以下为 2026-10-18 已有报告，本次文档清理没有重新构建或运行应用。

- 统一运行 `migration-20261018-165025-f15e94a5`：11 分项通过；Setup 为发布检查，其余为实际运行。Domain 107、Storage 93、Provider 11、Execution 17、Grading 211、Media 26、Setup 40；Documents 生成 8 份、45 页 PDF。[核心摘要](docs/verification/core.2026-10-18.json)。
- 最后 App 增量及最终离线包：桌面与 30 项工作流通过；ZIP 解压、逐文件校验、隔离安装、从安装目录运行和卸载保留合成用户文件通过。[包摘要](docs/verification/package.2026-10-18.json)。
- 三次顺序启动 P95 为 636.3232ms；空库自然空闲约 104–106MiB，释放资源后约 201–202MiB，超过首轮 200MiB 门槛。未强制 GC，Rendering 回调不代表 GPU 呈现，不能宣称性能预算通过。[采样摘要](docs/verification/performance.2026-10-18.json)。性能采样针对摘要中标明的产物，不能替代最终包或后续版本的测量。

最近已验证候选：`OhMyStudy-win-x64-rc-20261018-170306-ed27d9ee.zip`，150542938 字节，SHA-256 为 `90ff04d3c7c8f3ce5ca5b14741295aa94a5885029e0e543eb70c1876dd1e26cd`。源码提交为 `06665ead878cd5ebb6830474234dff9f216604a7`；之后的文档和 CI 测试等待预算修改未重建产品二进制。该包不代表当前工作树重新构建的结果。

原始本机报告在忽略的 `output/`：统一运行入口为 `output/migration/latest-migration-run.json`，包入口为 `output/migration/latest-package.json`，安装验证为 `output/migration/evidence/p9-final-delivery.json`。这些路径不是新克隆附带的报告；可提交摘要保存在上述 `docs/verification/`。

## 维护与验证入口

构建准备见 [README](README.md#开发与验证)，完整流水线见 [CI 与版本发布](docs/ci.zh-CN.md)。包内验证命令：

```powershell
./packaging/Test-OfflinePackage.ps1 -PackageZip <ZIP绝对路径> -DesktopSmoke -WorkflowSmoke -InstallSmoke
```

每次修改后运行对应场景，并更新本文件与实际产物、报告、失败和下一步；不能以旧报告给新构建记通过。自动检查使用隔离合成资料与凭据，不替代真实资料验收。

## 下一步与已知边界

- 按验收清单逐项记录系统、包哈希、结果与证据。尚未执行项保持未执行。
- 性能释放后内存超出门槛，需要后续优化并在参考设备复测；GPU、冷/热启动分布与长期稳定性仍待测量。
- 完整标签、高级筛选、按日期选错题、复习卷生成与独立校验、手机传图仍为产品待办。固定模板 PDF 导出不等于复习卷功能完成，详见 [需求](docs/requirements.zh-CN.md)。
- Sandbox 每次调用独立 VM，要求可证明归属的新 `wsb` CLI；Windows 10 或缺少 CLI 时不标为就绪。开始前明确确认本机回退，执行中失败不在宿主重放，详见 [执行与权限](docs/execution-permissions.zh-CN.md)。
- 旧资料只读备份、恢复到新目录并仅升级副本，全部初始化成功才切换；失败重新打开原库。真实切库前退出旧版并保留完整备份。凭据不随备份导出，跨账户需重新配置。
- 发行二进制尚未配置代码签名；哈希只证明完整性。干净系统安装及分发审核尚待完成，见 [安装说明](docs/installation.zh-CN.md)。
- CI 的操作和复现以 [CI 文档](docs/ci.zh-CN.md)为准，服务端实际结果从 Actions 查看，本机通过不能替代 CI 通过。

本次文档维护：删除完成阶段计划、阶段记录、迁移历史和被最终报告取代的证据；保留当前操作说明、兼容规则、最新可用验证摘要与未通过事项。

本次清理验证：当前 11 份说明文档的本地链接及 3 份验证 JSON 检查通过；仓库边界、打包脚本 PowerShell 语法和 `git diff --check` 通过。本次未重新执行应用回归。

Git 签名配置（2026-10-18）：本仓库显式设置 `gpg.format=ssh`、`commit.gpgsign=true`，使用当前账户已有 ED25519 密钥，并配置本地公钥验签文件。独立测试提交对象签名与 `git verify-commit` 验证通过，未改变分支历史。GitHub 验证还需账户登记对应 Signing Key。此前 `b7c1ab5` 未签名，不因配置更新自动补签；后续不得关闭签名提交。

0.1.0 发布准备（2026-10-18）：本次仅更新签名约定与交接文档；仓库边界检查、Release 干运行及五项无效输入回归、git diff --check 通过。计划推送 v0.1.0 标签，由 CI 对标签提交重新检查、构建并发布 ZIP 与校验清单；实际结果以该标签的 Actions 与 Release 为准，现有人工验收待办继续保留。

0.1.0 发布完成（2026-10-18）：SSH 签名提交 d28742c962e89223317c3904153b739f226af555 已推送至 main，v0.1.0 标签指向该提交。GitHub Actions 39119056807 的检查与测试、离线包构建、Release 发布全部成功：https://github.com/TaiRuiQu/oh-my-study/actions/runs/39119056807 。正式 Release：https://github.com/TaiRuiQu/oh-my-study/releases/tag/v0.1.0 ，已上传 OhMyStudy-v0.1.0-win-x64.zip（149301546 字节；SHA-256：3a4bcdb83ec7cb273a4cfeee411d79bb195b60edbfff447984f32f6bbd17aadc）、SHA256SUMS.txt 和 build-manifest.json。该版本按普通 Release 发布；构建保留人工验收待完成状态，不能据此宣称人工验收或二进制签名已通过。下一步继续执行上述验收与产品待办。
