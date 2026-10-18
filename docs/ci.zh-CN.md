# GitHub CI 与版本发布

仓库：`TaiRuiQu/oh-my-study`。流程位于`.github/workflows/ci.yml`，使用Windows x64托管运行器和`global.json`锁定的.NET SDK。

## 触发与顺序

- 推送`main`、向`main`提交PR、手动运行：先lint、checks和test，全部成功后才构建离线应用并上传Actions artifacts。
- 推送任意新tag：执行同一检查和构建链，再自动创建对应GitHub Release，附ZIP、`SHA256SUMS.txt`与`build-manifest.json`。删除tag不会发布。
- `v1.2.3`或`1.2.3`形式发布为普通Release，其余tag（包括`v1.2.3-rc.1`）标为预发布。已发布的同名Release在重跑时保留；未完成的草稿可继续上传并发布。
- 检查或构建失败时不会上传正式应用artifact或创建Release；检查日志仍尽量保留14天，成功应用artifact保留30天。Release资产不随Actions artifact保留期过期。

常规作业只有仓库读取权限，只有tag发布作业拥有`contents: write`；使用内置`GITHUB_TOKEN`，不需要额外PAT或模型供应商密钥。Actions固定到经核对的官方提交。

## 检查内容

`packaging/Invoke-CI.ps1`的四阶段与workflow一致：

| 阶段 | 内容 |
| --- | --- |
| Lint | C#空格格式；固定版本/哈希的actionlint检查workflow |
| Checks | C#仓库边界、PowerShell语法、JSON/XML解析、Release发布输入干运行回归；整个解决方案警告视为错误构建 |
| Test | Domain、Storage、Provider、Execution、Grading、Media、Infrastructure、Documents的实际Native AOT可执行回归；缺少必需套件或跳过私有计算测试会失败 |
| Build | Native AOT/WinUI自包含发布、完整离线依赖/许可证/源码打包、包哈希验证，整理ZIP和发布清单 |

测试项目是可执行兼容回归，必须运行发布的EXE，不能用一次空的`dotnet test`替代。宿主和私有计算测试使用合成任务；不访问用户资料、真实密钥和模型服务。

GitHub Windows运行器为服务器环境，CI不将原生桌面、打印和真实Windows10/11矩阵冒充已测；这些仍使用本机`-DesktopSmoke -WorkflowSmoke`和独立人工验收。工作流成功不等于迁移阶段全部关闭，构建清单会保留未验收状态。

## 本机复现

```powershell
# 首次准备所需工具/计算依赖。
./probes/prepare-documents.ps1
./packaging/prepare-webp.ps1
./packaging/prepare-runtime.ps1

$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
dotnet restore OhMyStudy.slnx -p:Configuration=Release --locked-mode
./packaging/Invoke-CI.ps1 -Phase Lint
./packaging/Invoke-CI.ps1 -Phase Checks
./packaging/Invoke-CI.ps1 -Phase Test
./packaging/Invoke-CI.ps1 -Phase Build
```

各阶段按顺序执行，避免同时构建/还原同一工程图。CI缓存仅存锁定的NuGet和原生依赖，二进制发布使用全新目录；离线包附完整源码快照（包括CI脚本）。

发布时由维护者选择版本号，例如：

```powershell
git tag v0.1.0
git push origin v0.1.0
```

本次配置不会自行创建版本tag或Release。后续实际推送tag时，发布脚本先校验包与tag/校验清单，再建立草稿，全部资产上传成功才公开发布。
