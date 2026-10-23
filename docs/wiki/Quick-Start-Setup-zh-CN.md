# 1. 安装并准备首个模型

[首页](Home-zh-CN.md) · [快速开始](Quick-Start-zh-CN.md) · [English](Quick-Start-Setup.md)

![数据边界：资料库保留原件与记录，供应商接收图片与上下文，可选代码遵循独立执行策略。](images/data-boundaries.png)

## 完成本页后你会得到什么

可运行的应用、已保存的供应商与模型，以及你了解的执行策略。下一步导入小试卷时，可以明确知道材料会发给谁。

## 安装完整软件包

从项目 Releases 下载。普通 Windows 10/11 x64 电脑可先选择不含 AVX512 的 AOT 版；AVX512 版要求兼容硬件。其他版本有不同运行时要求，请参阅安装指南，不要仅按文件大小选择。

MSI 按向导安装后启动。便携 ZIP 必须全部解压，再运行 OhMyStudy.App.exe；只取 EXE 会缺少原生文档和计算组件。安装和打开已存资料本身不调用模型。

## 建立连接

1. 打开设置，添加供应商。
2. 填写容易识别的名称，例如服务名称。
3. 按端点协议选择 Responses 或 Messages。
4. 填写服务地址和密钥并保存。
5. 导入模型，或手动添加精确 ID。
6. 核实图片、工具能力并保存模型。

显示名称供你识别，模型 ID 才会发送给服务。密钥应填入密钥字段，不放进名称或地址；应用通过 Windows 凭据保存。

## 选择计算代码的运行方式

首次任务前打开设置 → 执行。当前默认直接使用账户权限执行代码；每次询问可先查看 Python/PowerShell，沙箱则逐次使用 AppContainer 隔离。任何模式下供应商请求仍需联网。

## 准备检查

对已存模型测试连接。这是可能计费的小请求，不是试卷准确率测试。失败时先查协议、地址、密钥、ID、额度和网络。首次使用清晰的一至两页材料，便于完整核对。

实现与参考: [installation.md](../installation.md) · [MainWindow.Models.cs](../../src/OhMyStudy.App/MainWindow.Models.cs)

---

[章节目录](Quick-Start-zh-CN.md) · [下一章](Quick-Start-First-Exam-zh-CN.md)
