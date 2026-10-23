# 安装、升级与卸载

[返回首页](../README.zh-CN.md) · [使用指南](usage.zh-CN.md) · [English](installation.md)

界面支持简体中文、英语、日语和韩语，切换方法见[语言与排版](usage.zh-CN.md#语言与排版)。

## 选择下载文件

应用面向 Windows 10/11 x64。每种版本提供两种下载格式：

| 文件后缀 | 使用方式 |
| --- | --- |
| `-installer.msi` | 使用安装向导，添加开始菜单入口，可选择桌面快捷方式 |
| `-portable.zip` | 解压后直接运行，不添加系统安装项 |
| `-from-版本-patch.msp` | 可选补丁，仅更新指定基线版本、相同构建类型的 MSI 安装 |

根据文件名中的版本标识选择：

| 文件标识 | 适用情况 |
| --- | --- |
| `aot` | 无需另装 .NET；不确定时选择此项 |
| `aot-avx512` | 需要处理器和系统支持 x86-64-v4（AVX-512 F/BW/CD/DQ/VL）；不确定时选择 `aot` |
| `self-contained` | 自带 .NET 运行时，无需另装 .NET |
| `framework-dependent` | 需要先安装 Microsoft .NET 10 x64 Runtime，安装器不会自动下载 |

安装包包含本地排版、图片处理和计算所需组件，无需自行安装 Python、Node.js 或 TeX。AI 功能仍需要网络、供应商密钥和可用模型。

正式安装包不包含调试符号、Python 字节码缓存和依赖库测试套件，但保留计算所需的运行辅助模块。内置的 Windows 界面组件、PDF 工具和数值计算库仍会占用一定下载体积。

## 使用安装向导

1. 双击下载的 MSI。
2. 阅读许可，选择安装位置和是否创建桌面快捷方式。
3. 在 Windows 请求管理员确认后继续安装。
4. 完成后从开始菜单或桌面快捷方式打开 Oh My Study。

默认安装位置为 `%ProgramFiles%/OhMyStudy`。更改程序安装位置不会移动学习资料；资料库位置在应用设置中单独选择。

发行文件尚未代码签名。若系统提示无法验证发布者，请先确认下载来源，并与发布页面提供的 SHA-256 校验值核对。校验值用于确认文件完整性，不能代替发布者签名。

## 使用便携版

将 ZIP 完整解压到可访问的文件夹，再运行其中的 `OhMyStudy.App.exe`。请保留解压后的全部文件，不能只复制 EXE。

便携版的学习资料默认仍保存在用户资料目录。不要将便携版解压到 MSI 管理的安装目录。

## 升级与修复

先保存修改并关闭应用，再运行新版 MSI。升级保留学习资料，默认沿用原安装位置和桌面快捷方式选项。四种版本作为同一个应用安装，可在升级时切换，不并排安装；安装器会拒绝降级。

需要修复安装时，可重新运行原 MSI。更新便携版时，将新版完整解压到单独的文件夹，再运行新版应用。

发布页如有 MSP，先确认文件名中的 `from-版本` 和构建类型与已安装的原 MSI 一致。关闭应用后双击 MSP，批准 Windows 管理员权限请求；保留原 MSI 以便修复或撤销补丁。补丁可在控制面板“程序和功能 → 查看已安装的更新”中卸载。MSP 不适用于便携版、首次安装或构建类型切换，不匹配时使用新版完整 MSI。更改文件布局（包括首次清理旧安装中的 PDB）也必须先使用完整 MSI。

静默安装或应用补丁时，必须先以管理员身份打开 PowerShell。`/qn` 不会显示权限请求；普通权限可能导致 1925、2502 或 2503。不要通过修改 Windows Installer 或临时目录的权限来处理。安装失败时保留 `/L*v` 日志，先查看最终错误前出现的错误原因：

```powershell
msiexec /i "C:\Downloads\OhMyStudy-installer.msi" /qn /norestart /L*v "$env:TEMP\OhMyStudy-install.log"
msiexec /p "C:\Downloads\OhMyStudy-patch.msp" REINSTALL=ALL REINSTALLMODE=omus /qn /norestart /L*v "$env:TEMP\OhMyStudy-patch.log"
```

开发者可用 `./packaging/Build-MspPackage.ps1 -BaselineMsi <原始MSI> -UpdateMsi <新版MSI> -OutputFile <补丁.msp>` 生成补丁；也可给 `Publish-OfflinePackage.ps1` 或 `Invoke-CI.ps1 -Phase Build` 传入 `-BaselineMsi`。构建要求版本递增、类型相同且组件、文件和安装布局一致，否则拒绝生成并提示使用完整 MSI。原始 MSI 不会被修改。首次使用需用当前打包脚本生成的完整 MSI 作为基线。MSP 会省略未变化的依赖；实际大小取决于变化的文件，不能保证每次都很小。

手动触发 CI 时可填写 `baseline_tag`，下载相应发行版的同类型 MSI 并核对发布的 SHA-256，然后为四种构建生成补丁。发布清单和校验文件会包含补丁及其基线身份。未填写时仍只构建全量 MSI 和 ZIP。本地安装回归测试会请求 UAC；`./packaging/tests/Test-MsiInstaller.ps1 -BuildOnly` 仅检查补丁构建，不执行安装。

连续发布累积补丁时，继续使用同一个原始 MSI 作为 `baseline_tag`，这样已应用较早补丁的安装也能更新。不要把带有新产品标识的新版全量 MSI 当作同一补丁系列的新基线；用户实际安装新版全量 MSI 后，才改用该 MSI 对应的补丁系列。

如果安装过旧 EXE 版本，请先退出应用，通过 Windows 设置或旧版卸载入口卸载旧版，再安装 MSI。旧版卸载会保留学习资料；安装新版不等于已转换旧版资料，请先保留完整备份。

## 卸载与资料保留

在 Windows“已安装的应用”中找到 Oh My Study 并卸载，也可通过原 MSI 卸载。便携版关闭后可删除程序文件夹。

卸载保留学习资料、凭据和备份。默认资料库位于 `%LOCALAPPDATA%/OhMyStudy/Library`；如果改过位置，以应用“设置 → 资料库”中的“保存位置”为准。

如需删除学习资料，请先确认备份并关闭应用，再单独删除实际资料库文件夹。此操作会删除其中的试卷原件和已保存题目，无法通过重新安装找回。

## 首次打开

在“设置”中添加供应商、密钥和模型并保存，然后导入试卷。详细操作见[使用指南](usage.zh-CN.md)。

普通分支提交和拉取请求只执行回归检查，不再生成四套下载包。Provider 和 grading 测试以托管模式运行，避免反复进行耗时的 SDK Native AOT 编译；其余原生测试继续保留。标签发布和手动运行会执行完整 Native AOT 验证并构建四套安装包。同一分支的旧运行会被取消。
