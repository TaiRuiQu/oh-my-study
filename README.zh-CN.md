# Oh My Study

[English](README.md) · **中文**

面向学生的 Windows 学习应用，用于导入试卷、识别与批改题目、整理题库和错题，并导出复习用的 PDF。

## 主要功能

- 导入 PDF、PNG、JPEG 和 WebP，自动识别试卷类型，或指定空白、待批改、已有教师批改模式。
- 为空白卷生成标准答案与解法；之后可上传作答文件或手动录入，再次识别以批改并分析错因。
- 对照原件查看题目，确认已核查的识别疑点，编辑题干、答案、解析、评分和备注。
- 在题库和错题中整理已保存的题目，查看识别任务进度与预估费用。
- 将题目或答案与解析分别导出为 A4、A3 横向 PDF。
- 在本地保存试卷原件和学习资料，按需更改资料库位置。
- 可选择简体中文、英语、日语或韩语界面，文档排版仍以中文和英文为主。

## 开始使用

1. 下载 Windows 10/11 x64 发布版本。每个源码标签 `vX` 都有独立的 `vX-aot`、`vX-self-contained` 和 `vX-framework-dependent` 发布；不确定时选择 `vX-aot`。
2. 运行 `Setup.exe` 进行当前用户安装，或完整解压便携 ZIP 后运行 `OhMyStudy.App.exe`。
3. 打开“设置”，添加模型供应商、密钥和模型并保存。
4. 点击“导入试卷”，选择 PDF 或图片、试卷类型和模型，再点击“导入并识别”。

AI 识别与批改需要联网，会将材料发送给所选供应商，并可能产生费用。软件包附带本地排版和计算所需组件；安装与查看已保存资料不需要调用模型。AI 结果可能有误，请对照原件和参考答案核对。

## 使用文档

- [维基](docs/wiki/Home-zh-CN.md)：[快速开始](docs/wiki/Quick-Start-zh-CN.md)、[功能详解](docs/wiki/Feature-Guide-zh-CN.md)与 [Tech for nerds：技术内幕](docs/wiki/Tech-for-nerds-zh-CN.md)。
- [安装、升级与卸载](docs/installation.zh-CN.md)：下载选择、安装步骤和资料保留。
- [使用指南](docs/usage.zh-CN.md)：模型设置、试卷导入、题目整理、PDF 导出和常见问题。
- [v0.2.0 发行说明](docs/releases/v0.2.0.zh-CN.md)：试卷重命名、Velopack 安装与增量更新。

## 许可证

本项目采用 [GPL-3.0-or-later](LICENSE)。分发组件保留各自的许可证与声明。
