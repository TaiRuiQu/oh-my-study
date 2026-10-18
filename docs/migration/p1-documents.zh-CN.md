# P1 无浏览器公式、Typst 与 PDF 验证

日期：2026-10-18（Asia/Shanghai）。状态：**本机Release Native AOT文档探针及WinUI PDF预览通过；完整公式语义、真实材料、干净系统和实际打印尚未验收。** 本文与`migration-progress.md`共同作为交接依据。

## 实现与调用边界

新增 `src/OhMyStudy.Documents/`，目标为 `net10.0`、`IsAotCompatible=true`，没有业务工程引用，也没有第三方托管运行包。链路为：

```text
C# 有界 LaTeX/mhchem 子集解析 → 固定 Typst 模板 → 私有 typst.exe → PDF / SVG
                                                                ↓
                                             C# PDFium P/Invoke → BGRA32
```

新链路不调用Node.js、MathJax、Edge、WebView2或Rust SDK。Typst是计划允许的独立原生第三方排版引擎；PDFium只使用原生PDF API。旧排版工程已归档到`.reference/`，新准备脚本仅使用独立输出缓存或官方固定发布物，不读取旧工程。

公共 API：

| API | 用途与约定 |
| --- | --- |
| `LatexFormulaConverter.Convert(string)` | 返回 `FormulaConversion(IsSupported, TypstMath, Original, Diagnostic, ErrorOffset)`；只将成功结果中的内部数学表达式交给固定模板 |
| `TypstDocumentService(TypstToolchain)` | 创建时验证私有 Typst 可执行文件与固定字体的 SHA-256 |
| `RenderFormulaAsync(latex, outputDirectory, cancellationToken)` | 生成同源 PDF/SVG；返回路径及转换结果。超出 560pt 预览宽度时原文换行回退 |
| `ExportExamAsync(document, paper, edition, outputDirectory, cancellationToken)` | A3 横向双栏 / A4 纵向单栏，以及题目卷 / 答案册；返回 PDF、可信模板源文件、语法诊断 |
| `PdfiumRasterizer(libraryPath).Open(path or bytes)` | 校验私有 DLL；文档拥有 PDF 数据内存，调用者必须 `Dispose` |
| `PdfiumDocument.RenderPage(pageIndex, maximumDimension, quarterTurns, cancellationToken)` | 返回 `RasterizedPage(Width, Height, Stride, Bgra32, PageWidthPoints, PageHeightPoints)`，直接供原生位图使用；`SaveBmp` 仅作诊断 |
| `PdfiumDocument.ExtractText(pageIndex)` | 有上限的 PDF 文字提取；供可搜索预览及集成检查使用 |

调用者应在后台执行解码及栅格化，为每个文档任务分配独占工作目录。当前服务不提供跨请求缓存或并发写同一路径保证。UI 图像坐标、PDF 图像缓存与虚拟化由 P6 继续完成。

## 固定依赖

完整下载地址与 SHA-256 位于 `probes/native-documents.lock.json`；运行时再验证 Typst、字体及 PDFium DLL 哈希。

| 组件 | 固定版本 | 来源 / 许可 | 职责 |
| --- | --- | --- | --- |
| .NET SDK | 10.0.402；探针实际运行 .NET 10.0.13 AOT | 工程 `global.json`；MIT 等发行许可 | 编译 C# 与 AOT |
| Typst | 0.14.2，`b33de9de` | `typst/typst` 官方 Windows x64 release；Apache-2.0，归档带 LICENSE / NOTICE | 按需启动的原生排版进程，不是 Rust 业务后端 |
| Source Han Serif SC Regular | Adobe 2.003R | Adobe 官方固定 tag；OFL-1.1 | 中文、正文及中文数学条件 |
| New Computer Modern Math | 随固定 Typst 二进制内嵌的版本 | Typst NOTICE 所列许可 | 数学字体；PDF 实查嵌入字体名 `NewCMMath-Book` |
| PDFium | chromium/7881 Windows x64 | 沿用已有 `bblanchon/pdfium-binaries` 锁定归档；BSD 及其第三方 notices | PDF 解码、文字提取、原生栅格 |

Typst 0.14.2 与旧探针的底层引擎版本一致，用于比较迁移行为。它是明确锁定的版本，不代表“最新版本”。后续升级需重跑原始样本、检查许可证和发布兼容性。

所有工具、字体、许可证与输出位于被忽略的 `output/migration/`。准备脚本不会安装字体或修改系统；可执行文件约 45.09 MiB，字体约 23.41 MiB，PDFium 及完整 notices 还需单独打包。最终离线安装包大小和部署身份仍属于 P9 验证范围。

## 语法、数据及资源边界

转换器接受明确子集：分式、根式、数学上下标、有限常用符号 / 函数、文字、matrix / pmatrix / bmatrix / vmatrix，以及 `\ce{...}` 的元素、系数、下标、离子电荷、括号、沉淀、反应箭头与至多两行普通文字条件。指定的分式、三阶矩阵、求和、中文速度式、点燃反应、硫酸钡离子反应、氢氧化铁和可逆反应已实际排版。化学元素使用直立数学字母，避免相邻字符串产生单词间距。

它**不是完整 TeX 或 mhchem 兼容库**：宏展开、包加载、任意环境、嵌套矩阵、复杂有机结构、通用化学键、部分状态与同位素标注等不在已验证范围。未知命令、非法分组、缺参或超限输入返回诊断与原文；不会静默删除内容。输入最多 8192 字符、32 层递归、4096 个语法单元，矩阵最多 16 × 16。解析上限仍为8192字符，超限回退完整保留原文；导出单段文字上限32768字符。需用真实既有题库建立语法覆盖统计后，才能关闭公式兼容风险。

生成的 Typst 只包含固定函数与解析器白名单。P6新增图片仅允许受限PNG/JPEG字节，由服务生成资产路径；不接受题目提供的路径或SVG。文字通过严格字符串转义传给 `text`；不接受来自题目的 Typst 标识符、代码、路径、SVG 或 `eval`。题目卷分支根本不读取答案 / 解析 / 小问答案字段，所有输出均不读取 `StudentAttempt`。实际 PDF 中 `#panic("UNTRUSTED_TYPST")` 作为文字出现；公式 `\text{#panic("UNTRUSTED_FORMULA")}` 同样打印为文字。

超出栏宽的公式显示边框提示与换行原式，保留语义供人工读取；当前没有完成长公式的数学自动断行。P6已将题干和小问正文改为可跨页块，图片按比例限制最大高度，留白限制0–100mm。正文、答案、选项及小问标签均计入单段32768、全文1000000字符限制；审查发现的标签漏计已修复，并加入真实AOT探针拒绝超长标签的回归。P6已增加完整表格、图像、递归小问与长段落样本及独立逐字页边界检查；最新实现与实际验证结果见`p6-media.zh-CN.md`，本节下方原始P1页数是历史证据。

Typst 使用私有字体目录与 `--ignore-system-fonts`，每次最多 2 个工作线程、30 秒超时；取消或异常后回收进程树，诊断输出最多保留 64 KiB，成功前先写临时文件再替换目标。可信模板没有包或插件入口，额外清除 `TYPST_*` 环境覆盖。此服务不等同于通用不可信代码沙箱。

PDFium 调用按公共 API 要求在进程级锁内串行执行；文档最多 512 MiB，栅格最多 4000 万像素，最长边最多 8192。原生模块保留到进程结束；各文档、页、文字页、位图独立释放。当前取消会在栅格化前后检查，**不能中断已经进入的同步 PDFium 绘制**；P6 应验证复杂原件并考虑渐进式绘制。该锁与大文件读取都不能放在 UI 线程。

## 实跑证据

环境：Windows build 26200 x64；.NET SDK 10.0.402；.NET 10.0.13；2026-10-18 本机执行。

`dotnet publish -c Release -r win-x64` 完成原生编译，生成 `output/migration/publish/documents/OhMyStudy.Documents.Probe.exe`，目录仅包含 exe 与调试符号，没有托管运行 DLL。直接执行发布 exe，`RuntimeFeature.IsDynamicCodeSupported=false`，含逐项符号编译的完整探针约 11.20 秒；这是多次原生排版调用的本机开发探针总耗时，不是产品启动、冷启动或性能预算证据。构建没有 AOT / 裁剪警告。

`output/migration/documents-aot/verification.json` 包含原生探针结果；`verification-pdf.json` 是独立 pypdf / pdfplumber 检查；最终 PDF、PDFium BMP、Poppler PNG、公式 PDF/SVG 与模板源文件保留在同目录。

| 输出 | 实际尺寸 | 页数 | 字节 |
| --- | --- | --- | --- |
| `a3-questions.pdf` | 420 × 297mm | 2 | 62,212 |
| `a3-answers.pdf` | 420 × 297mm | 1 | 64,447 |
| `a4-questions.pdf` | 210 × 297mm | 3 | 62,871 |
| `a4-answers.pdf` | 210 × 297mm | 2 | 65,193 |

通过项：

- 9 个代表性数学 / 化学 / 长公式样本解析；11 个非法、危险、深嵌套、括号不匹配及超长输入明确回退。额外 69 个白名单符号、定界符、根式和字体形式逐项实际编译。
- 9 个代表性公式、1 个含代码字样的文字公式与 1 个错误公式实际输出 PDF / SVG；公式 SVG 未包含 `<text>` 或 `<script>`，字形为自包含路径。
- PDFium 实际载入、渲染、提取文字、旋转及预取消；全部四份 PDF 纸张误差小于 0.05mm。
- 题目卷实际 PDF 与源文件均无答案 / 小问答案标记；全部 PDF 无学生作答标记；答案册的 10 个答案和 12 个小问答案全部存在。
- 独立检查字体实际嵌入，无交互注释，无替换字符或 NUL，逐字几何位于页面内。
- 查看全部八页的最终 Poppler 渲染和关键公式近图。已修复一次题干 sticky 链产生的大段空白和一次化学元素单词间距；最终没有观察到裁切、重叠或缺字。长公式明确使用原式回退。

尚未通过：完整LaTeX/mhchem语法兼容、真实试卷语义与复杂版面、12页高分辨率材料资源曲线、Win10/Win11干净离线机、DPI/读屏、原生打印对话框与纸张实打。WinUI现已集成PDFium后台栅格与内存位图，3页合成PDF的预览/翻页由桌面AOT探针实际验证；这不代表完整P6阅读器或P1系统矩阵已通过，也不代表M4复习卷完成。

## 重跑与接手

项目根目录执行；不需要运行旧 npm 或 Cargo 工程：

```powershell
./probes/prepare-documents.ps1
# 在正常用户会话执行官方HTTPS下载；不需要开发Python、Node或Rust。

$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
dotnet restore probes/OhMyStudy.Documents.Probe/OhMyStudy.Documents.Probe.csproj -p:Configuration=Release --locked-mode
dotnet publish probes/OhMyStudy.Documents.Probe/OhMyStudy.Documents.Probe.csproj -c Release -r win-x64 --no-restore -o output/migration/publish/documents
./output/migration/publish/documents/OhMyStudy.Documents.Probe.exe $PWD (Join-Path $PWD 'output/migration/documents-aot')

# 独立开发 QA 使用已具备 pypdf/pdfplumber 的 Python 和 Poppler；不属于产品运行依赖：
# 本机历史独立QA脚本已归档，不是新工程依赖：
# python .reference/verification/verify-native-documents.py output/migration/documents-aot --pdftoppm 'C:/.../pdftoppm.exe'
```

下一位维护者先复跑实际AOT产物和已接通的WinUI位图预览；工作区选择/缩放/来源坐标由Presentation维护。继续补齐图像格式、分级缓存和真实12页场景；覆盖更多TeX时先增加样本、明确回退，再扩展解析器，不引入隐式浏览器补偿。真实资料覆盖和系统矩阵通过前保留P1门槛。

官方参考：

- [Typst 固定版本发布](https://github.com/typst/typst/releases/tag/v0.14.2)
- [Typst math](https://typst.app/docs/reference/math/)、[attach](https://typst.app/docs/reference/math/attach/)、[matrix](https://typst.app/docs/reference/math/mat/)
- [Adobe Source Han Serif 固定版本](https://github.com/adobe-fonts/source-han-serif/tree/2.003R)
- [PDFium public API / 线程约束](https://pdfium.googlesource.com/pdfium/+/refs/heads/main/public/fpdfview.h)
