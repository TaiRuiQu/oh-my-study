# VelociTex NuGet 接入与公式渲染验收

2026-10-19。本轮接续工作区已有的原生公式接入代码，保留其他未提交改动。没有提交、发布、制作 MSI 或访问真实资料、密钥、模型。最终程序为 `output/velocitex-native-app/OhMyStudy.App.exe`；可提交的源码、包来源、构建与截图哈希索引见相邻 JSON。旧的 Typst 显示验收记录保留历史事实，不代表当前渲染路径。

## 接入范围

- 三个直接依赖统一锁定为 nuget.org 的 `VelociTex.Core`、`VelociTex.Rendering.Direct2D`、`VelociTex.WinUI` **1.0.0-rc.2**。包缓存的来源元数据均为 `https://api.nuget.org/v3/index.json`。移除相邻 VelociTex 工程的本地包源，更新 AOT、自带运行时和依赖运行时锁文件。
- 正文、预览、选项、表格、小问和批改反馈统一编译原始 TeX。行内公式由宿主 DirectWrite 段落管理基线，独立公式使用 MathView；渲染资源留在创建线程，编译队列、公式缓存和摘要高度缓存有界。删除显示开关、Typst SVG 显示分支及 LaTeX → Typst 转换器；退役实现的本机备份位于忽略的 `.reference/retired-typst-formulas-2026-10-19/`，正式构建不引用它。
- 打印/PDF 公式由 VelociTex 导出自包含 SVG 字形路径，Typst 只处理正文、分页、表格和图像放置，不再接收数学语法。行内保持基线，独立公式居中；超宽或不支持的内容保留可换行原文。公式资源使用内容哈希命名，后续导出不会覆盖旧源文档引用的不同公式。
- 用户明确同意将 KaTeX 字体例外扩展到单个打印公式，已同步 AGENTS.md 与设计原则。界面正文仍为非衬线字体，打印正文仍为 NSimSun。原始字体字节与名称不变，普通构建、publish 和打包都保留 VelociTex MIT、KaTeX MIT、OFL / RFN 声明；包检查增加许可证存在要求。

## 实际界面检查

最终 Native AOT EXE 使用隔离资料库运行 `--math-smoke`（16 张）与 `--preview-smoke`（17 张），报告位于 `output/velocitex-native-review/math.txt`、`preview.txt`。截图覆盖 1440×960、900×850、720×960、460×960 窗口；PNG 为实际客户区尺寸，不含系统外边框。

`RenderTargetBitmap` 会遗漏原生 Composition 子视觉，因此公式验收改为由应用等待渲染稳定、临时置顶自己的窗口后自动捕获客户区；恢复置顶状态，不使用 Computer Use 验收布局。早期一组被其他窗口遮挡的截图没有作为最终证据。最终 33 张窗口 PNG 均逐张检查，联系表在 `output/velocitex-native-review/sheets/`；另查看原尺寸正文、错误、窄窗、矩阵和选项图，检查字形、基线、分母/根号边缘、截断、省略、行高、对齐、相邻答案/备注区域和主题对比。

范围包括空库、单题、多题、普通文字、四类分隔符、分式、根式、向量、矩阵、分段函数、含中文公式、表格、小问、长正文、可横向滚动的长独立公式、无效/未闭合输入、19 类摘要、500 题列表顶部/中部/底部及快速复用。修复了超宽摘要截断数学单元、摘要多余空白、缺少省略号和失败摘要暴露原始命令的问题；不支持的公式在详情保留原文。

原尺寸复查进一步发现 MathView 的分数 DIP 尺寸与整数表面像素不一致，造成字形轻微重采样。宿主在加载和根缩放改变时将公式表面尺寸对齐到实际像素，再调用公开 Rebuild API；已重新生成并检查最终截图，独立公式恢复清晰。

原生功能检查通过：段落与单式只读 UIA 原文、真实 Ctrl+C、菜单复制单式、实际 DirectWrite 文字/公式共同基线、100/125/150/200% 表面重建、取消、长公式滚动至末端、MathView 像素边界和实时深浅主题、反馈同源路径、1/10/50/100 公式页和有界缓存。表面重建不等于物理显示器 DPI 切换验证。

## PDF 与工程验证

- Release Native AOT 编译与运行通过，无编译、裁剪或 AOT 警告。日志：`output/velocitex-aot-restore.log`、`velocitex-aot-publish.log`。
- 原生文档探针通过，`DynamicCodeSupported=false`。报告：`output/velocitex-native-documents-review/verification.json`；原生构建日志：`output/velocitex-native-documents-publish.log`。
- 8 类直接公式 SVG（含中文、矩阵、分段函数和旧转换器不支持的 cancel/boxed）、错误诊断、预取消、分隔符/转义与新命令识别通过。打印题干、选项、表格、答案、解析、小问均实际使用 VelociTex。普通英文下划线标识符继续作为正文，避免被误识别成数学。
- 12 份 A3/A4 PDF 共 46 页实际渲染并检查：4 份公式打印示例用 Poppler 生成 PNG；普通及复杂文档的全部页面用 PDFium 生成 BMP。检查行内基线、独立居中、字号、页边距、重复表头、分页、长原文回退、519 个表格单元格和 12 层小问；所有答案/作答隔离与全文标记检查通过。渲染及联系表保存在 `output/velocitex-native-documents-review/`。修复了初版导出行内公式意外换行、独立公式未居中和导出资源命名冲突后重新生成并检查。
- 既有 Native AOT 完整工作流 **48 项通过**，报告：`output/velocitex-native-review/workflow.json`。媒体检查 **26 项通过**，报告：`output/migration/media-tests.json`。
- `git diff --check`、仓库布局检查、两份打包脚本语法检查通过；Impeccable 对公式控件机械扫描返回空列表。机械检查不替代截图。所有三种发行模式已更新官方包锁文件；未制作或验收完整安装包。

## 边界

当前公开包不支持的命令会返回诊断并保留原文，包括现有化学样例中的 `\ce`，没有转用其他公式引擎。PDF 公式是矢量路径，不能按文字搜索或复制；正文仍可提取。不声明支持任意 TeX 宏或第三方包。

未验证物理打印、真实多显示器 DPI 切换、系统高对比切换、读屏用户体验、干净系统安装和完整 MSI/离线发行包；没有将本机截图及自动检查描述为这些场景的验收结果。Native AOT 应用和所有本机证据位于忽略的 output/，未作为发行物发布。
