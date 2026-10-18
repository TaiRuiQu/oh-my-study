# GitHub CI 与版本发布

仓库：`TaiRuiQu/oh-my-study`。流程位于`.github/workflows/ci.yml`，使用Windows x64托管运行器和`global.json`锁定的.NET SDK。

## 触发与顺序

- 推送`main`、向`main`提交PR、手动运行：先lint、checks和test，全部成功后才构建离线应用并上传Actions artifacts。
- 推送任意新tag：执行同一检查和构建链，再通过四项构建矩阵自动创建对应 GitHub Release，附 4 个 portable ZIP、4 个独立 installer EXE、`SHA256SUMS.txt` 与 `build-manifest.json`。删除tag不会发布。
- `v1.2.3`或`1.2.3`形式发布为普通Release，其余tag（包括`v1.2.3-rc.1`）标为预发布。同名 Release 重跑或补充发布时，校验完整八件套后更新同名资产，保留其他文件和原发布说明；未完成的草稿可继续上传并发布。
- 检查或构建失败时不会上传正式应用artifact或创建Release；检查日志仍尽量保留14天，成功应用artifact保留30天。Release资产不随Actions artifact保留期过期。

常规作业只有仓库读取权限，只有tag发布作业拥有`contents: write`；使用内置`GITHUB_TOKEN`，不需要额外PAT或模型供应商密钥。Actions固定到经核对的官方提交。

## 检查内容

`packaging/Invoke-CI.ps1`的四阶段与workflow一致：

| 阶段 | 内容 |
| --- | --- |
| Lint | C#空格格式；固定版本/哈希的actionlint检查workflow |
| Checks | C#仓库边界、PowerShell语法、JSON/XML解析、Release发布输入干运行回归；整个解决方案警告视为错误构建 |
| Test | Domain、Storage、Provider、Execution、Grading、Media、Infrastructure、Documents、Installation的实际Native AOT回归（先发布Setup供真实CLI测试）；缺少必需套件或跳过私有计算会失败 |
| Build | 四项 App 构建矩阵；每项打包 portable ZIP 并将同一 ZIP 嵌入通用 Native AOT 安装器；逐文件校验、实际独立安装/校验/卸载，再上传 ZIP、EXE 与该项清单 |

测试项目是可执行兼容回归，必须运行发布的EXE，不能用一次空的`dotnet test`替代。宿主和私有计算测试使用合成任务；不访问用户资料、真实密钥和模型服务。

GitHub Windows运行器为服务器环境，CI不将原生桌面、打印和真实Windows10/11矩阵冒充已测；这些仍使用本机`-DesktopSmoke -WorkflowSmoke`和独立人工验收。工作流成功不等于所有目标系统和真实场景验收通过，构建清单会保留未验收状态。

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

推送版本 tag 时，发布脚本先校验包与tag/校验清单，再建立草稿，全部资产上传成功才公开发布。

## 四种分发构建

`packaging/Get-PublishProfile.ps1` 是发布参数的统一入口。矩阵四项各自在独立运行器运行，`fail-fast: false`；发布作业依赖整个矩阵成功，缺任一构建或缺 portable/installer 任一种格式均拒绝发布。

| Variant | App 参数 | 文件后缀 |
| --- | --- | --- |
| `aot` | `PublishAot=true`、`SelfContained=true`、`IlcInstructionSet=x86-64,-avx512`、向量上限 128 | `-aot-portable.zip` / `-aot-installer.exe` |
| `aot-avx512` | `PublishAot=true`、`SelfContained=true`、`IlcInstructionSet=x86-64-v4`、向量上限 512 | `-aot-avx512-portable.zip` / `-aot-avx512-installer.exe` |
| `self-contained` | `PublishAot=false`、`SelfContained=true`，不裁剪、不启用 ReadyToRun | `-self-contained-portable.zip` / `-self-contained-installer.exe` |
| `framework-dependent` | `PublishAot=false`、`SelfContained=false`，保留 apphost，不裁剪、不启用 ReadyToRun | `-framework-dependent-portable.zip` / `-framework-dependent-installer.exe` |

完整文件名为 `OhMyStudy-<tag>-win-x64-<variant>-<portable.zip|installer.exe>`。AVX-512 目标按 .NET 10 的 `x86-64-v4` 组选择，不使用构建机的 `native` 指令集；普通 AOT 明确排除 `avx512` 组。说明见 [.NET Native AOT 编译选项](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/nativeaot/docs/optimizing.md)及[指令集定义](https://github.com/dotnet/runtime/blob/v10.0.0/src/coreclr/tools/Common/JitInterface/ThunkGenerator/InstructionSetDesc.txt)。这里的指令集选择针对 App 的 .NET 原生编译，不重新编译附带的第三方工具。

Setup 与安装后的稳定启动器始终使用通用 Native AOT，避免依赖目标机 .NET，也避免在安装阶段要求 AVX-512。installer EXE 是携带 portable ZIP 资源的独立安装器，无需同目录 ZIP；解包到新建临时目录，拒绝越界/设备名/链接/重复路径，安装引擎继续校验每个文件，结束后清理临时目录。CI 实际运行下载用的 EXE，安装后再次校验，随后用 portable 内的小安装器卸载。

两种非 AOT App 使用 `packages.self-contained.lock.json` 和 `packages.framework-dependent.lock.json`，其余仍使用默认锁文件；restore 和 publish 必须传相同 profile 属性。可本机复现一项：

```powershell
./packaging/Invoke-CI.ps1 -Phase Build -Variant framework-dependent
```

每项清单记录源码提交、标签、variant、两个文件的名称/大小/哈希与未验收状态。发布脚本要求四份清单使用同一源码提交和标签，统一生成包含八个文件的 `SHA256SUMS.txt` 和 schemaVersion=2 的 `build-manifest.json`。回归检查覆盖缺失构建/格式、重复格式、混合提交、错误标签、路径越界、大小和哈希错误。

## 补充已有版本

手动运行 CI，输入 `release_tag`（例如 `v0.1.0`），从所选分支/提交重新执行全部检查与四项构建，再补充该版本的八个文件。不填此字段则只上传 Actions artifacts。保留已有版本标签及其他资产，不移动标签；新增二进制的实际源码提交明确写入 Release 说明、构建清单与包内源码。新版本正常推送新标签即可。

托管运行器不保证具有 AVX-512，CI 不执行 AVX-512 App，不能宣称该 CPU 上的桌面行为通过。各项均检查实际发布文件的运行时形态与完整性、运行通用安装器；GUI、AVX-512 实机及缺少 .NET 的干净系统验收仍需人工完成。
