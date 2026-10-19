# VelociTex 提交后的 CI 依赖锁修复

2026-10-19。失败运行：[39241021679](https://github.com/TaiRuiQu/oh-my-study/actions/runs/39241021679)，提交 `3989414`。失败发生在 `Restore locked solution`，后续 Lint、Checks、Test 和构建矩阵尚未执行。

## 原因与修复

文档探针和媒体测试支持非 AOT 本机验证，但普通 `packages.lock.json` 已由非 AOT 还原生成。原来的 Directory.Build.props 在项目声明 PublishAot 之前求值，因此在没有全局 PublishAot 参数的 Release 解决方案还原中仍选择普通锁，缺少 Microsoft.DotNet.ILCompiler，触发 NU1004。单独以全局 PublishAot=true 发布成功不能覆盖这个场景。

两个可执行项目现在在自身属性选定 PublishAot 后明确选择锁文件：AOT 使用 `packages.aot.lock.json`，非 AOT 使用 `packages.lock.json`。补充媒体测试 AOT 锁，不关闭 CI 的 `--locked-mode`。

Checks 增加这两个项目的非 AOT 调试还原和普通 Release 还原，再执行解决方案还原与构建，防止调试验证污染 CI 的依赖图。NuGet 缓存键覆盖所有 `packages*.lock.json`，包括 AOT 和其他发行模式。

## 本机验证

- 按失败作业原命令复现两个 NU1004 错误，修复后 `dotnet restore OhMyStudy.slnx -p:Configuration=Release --locked-mode` 通过。
- `./packaging/Invoke-CI.ps1 -Phase Lint` 通过。
- `./packaging/Invoke-CI.ps1 -Phase Checks` 通过：四种发行配置、App 调试/非 AOT 配置、两个可执行项目的双模式锁定还原、解决方案最终锁定还原、脚本和发布输入回归、整解决方案警告视为错误构建；0 个警告、0 个错误。
- 本机日志：`output/ci-velocitex-restore.log`、`output/ci-velocitex-lint.log`、`output/ci-velocitex-checks.log`。原始复现日志和格式检查日志保存在同一忽略的 output/ 下；工作区 CRLF 按既有 LF 规则整理，没有产品界面的语义改动。

远程完整 Test 和四项构建矩阵以修复提交对应的 GitHub Actions 运行结果为准，本记录不预先宣称远程通过。此修复不改变界面、公式排版或字体，沿用之前的实际截图证据。
