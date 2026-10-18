# M0 正式试卷排版验证

验证日期：2026-10-18（Asia/Shanghai）。状态：**本地公式 SVG → 固定 Typst 模板 → PDF 路径已实跑；WebView2 集成与纸张实际打印仍待验收。** 本探针是独立 npm 工程，不初始化应用前端，也不向最终应用加入 Node 运行时。

## 已实现的验证路径

`probes/typesetting/sample.mjs` 包含九道原创样题、十二个长小问和七个不同公式，覆盖分式、三阶矩阵、上下标、求和、中文公式、带中文反应条件与离子电荷的 mhchem、选项、表格、结构图及分页留白。

1. 本机无头 Edge 加载本地 MathJax 3.2.2 与 mhchem。浏览器请求限定到本探针的 loopback 地址，不从 CDN 加载排版资源。固定 `em=16, ex=8`，保存 SVG 宽高及 `vertical-align` 深度。
2. 使用 `fontCache: none` 生成独立公式 SVG。MathJax 的中文回退 `<text>` 需要额外处理：以固定思源宋体字形转为 `<path>`，检查字形存在；否则中文公式仍依赖宿主字体，不能称为自包含。
3. 转换尺寸为 11pt 正文字号对应的 pt 单位；Typst 使用 `box(baseline: depthPt * 1pt)` 恢复基线。SVG 元素白名单拒绝脚本、事件、样式、字体文本、外部图片及外部引用。TeX 拒绝链接、HTML 命令和运行期加载包。
4. `pipeline.mjs` 为题目卷与答案册分别创建字段白名单 JSON；题目卷不传入答案、解析或学生作答。固定 `template.typ` 只使用 `text()` 呈现正文，不使用 `eval()` 或执行题目中提供的 Typst 源码。样题中的 `#panic("UNTRUSTED_TYPST")` 实际被打印为文字，编译未执行它。
5. 同一模板生成 A3 横向双栏和 A4 纵向单栏。页边距为左右 17mm、上 18mm、下 17mm；A3 栏距 12mm；正文 11pt。表格与结构图不拆开，小问携带题号及续题提示，留白随小问移动。答案册每题题干、答案和解析保持在一起，已修复一次跨栏孤行。
6. 直接用生成的 PDF 做 Poppler 页面渲染；没有另排 HTML 预览。本阶段验证的是这些最终 PDF 的渲染，尚无应用内 PDFium 预览。

固定开发依赖见 `probes/typesetting/package-lock.json`：MathJax 3.2.2、Typst Node native wrapper 0.7.0（生成文件标识底层 Typst 0.14.2）、opentype.js 1.3.4、Playwright Core 1.58.2。验证使用 Edge 155.0.4283.45。`@xmldom/xmldom` 显式覆盖为 0.9.12，避免间接使用其有已知问题的 0.9.10。MathJax 3 为本探针锁定版本；迁移当前 MathJax 4 或换用 Rust 原生集成时须重跑这些样本，不把本结果直接外推为新版本通过。

字体固定为 Adobe Source Han Serif 2.003R 的 `SourceHanSerifSC-Regular.otf`，24,543,332 bytes，SHA-256 为 `78aa7a328fd974df2d688c8a9fd74a33d8334dfa84ab24d9d11efb2ffc464117`。`setup-font.mjs` 下载后校验哈希；OFL 许可保存在 `assets/OFL.txt`；字体仅放在探针目录，不安装到系统。字体和开发依赖未提交，须首次准备。

## 实跑结果

| 输出（均位于 `probes/typesetting/output/`） | 尺寸 | 页数 | 文件字节 |
| --- | --- | --- | --- |
| `a3-questions.pdf` | 420 × 297 mm | 2 | 106,977 |
| `a3-answers.pdf` | 420 × 297 mm | 1 | 103,831 |
| `a4-questions.pdf` | 210 × 297 mm | 3 | 108,400 |
| `a4-answers.pdf` | 210 × 297 mm | 2 | 106,455 |

`npm test` 五项通过：危险 TeX、非自包含 SVG、嵌套作答／答案泄漏、题号与小问对应、固定模板边界。`verify.py` 针对真实 PDF 校验尺寸误差小于 0.05mm、字体实际嵌入、文本字符位于页面内、无替换字符、无交互注释、九题答案与十二小问答案全部存在。题目卷和答案册均无 `STUDENT_PRIVATE`；题目卷无 `ANSWER_ONLY` / `SUBANSWER`。这些标记刻意保留在原创探针数据中，方便检测，并非产品文案。

已逐页查看全部八页 PNG。分式、矩阵、中文公式和反应条件可读；基线与行高未发现明显错位；A4 第一页栏末表格、A3 第一页跨栏结构图、长小问续页和答案对应未见裁切、重叠或丢失。PNG 检查不能替代真实纸张上的 100% 打印验收。

最后一次资源专用运行用 `--hold-seconds 10` 保留浏览器阶段及排版后缓存阶段。Node + Edge 进程树采样 65 次、窗口 28,022ms，观测到最大私有内存 774,356,992 bytes（738.48 MiB），无采样 warning；四次 PDF 编译分别 29 / 20 / 20 / 20ms。这是**开发工具进程树采样窗口内的观测最大值**，包含 Edge 和缓存保留期；不能写成产品内存、严格的 PDF 瞬时峰值或 Rust 集成基线。资源原始输出位于 `output/measurements/typesetting-probe/resources.json`，总体口径见 [M0 资源记录](m0-resources.zh-CN.md)。本机该探针 `node_modules` 文件合计约 102.61 MiB，单独字体约 23.41 MiB；这些不是离线安装包大小。

## 重跑

在项目根目录的 PowerShell 中执行，所有 npm 依赖和锁文件均在探针目录内：

```powershell
Push-Location probes/typesetting
npm ci
npm run setup-font
npm test
npm run probe
# 使用已有 pypdf、pdfplumber 的开发 Python，并确保 Poppler pdftoppm 在 PATH。
python verify.py
Pop-Location
```

当前 Codex 工作区提供包含上述 Python 包及 Poppler 的 bundled runtime；可用 `load_workspace_dependencies` 获取位置。本机实跑使用其 Python，而非向系统 Python 安装依赖。`npm run probe` 需要本机 Edge；缺少 Edge 会明确失败，不切到未报告的模拟渲染。受限命令沙箱中 Edge 曾启动即退出，使用正常用户执行环境后成功；没有修改系统设置或安装浏览器。

`output/metrics.json` 记录公式宽高／基线、引擎与编译耗时；`output/verification.json` 记录真实 PDF 检查；`output/*.png` 为渲染页面。需要资源采样时运行 `node probes/typesetting/run.mjs --hold-seconds 10`；`output/phase.json` 给出进程 PID、阶段和 Node 自身内存。参数仅增加两个等待窗口，不改变 PDF 内容。等待后的缓存存活内存不代表生成时峰值。生成物被 `.gitignore` 排除，接手者可依上述命令重建。

## 后续验收边界

- M1 / M4：在真实 Tauri WebView2 中复用相同 MathJax 设置和自包含 SVG，验证 IPC 大小限制、字体轮廓和 pt／基线数据一致；再通过 Rust Typst / typst-pdf 集成生成同一模板结果。本探针通过 Node native wrapper 调用真实 Typst，不代表 Rust 集成已经完成。
- M4：A3 100% 实际打印与 A4 打印，检查字号、留白、打印机非打印区域、页码、双面和系统打印对话框。当前未发生物理打印。
- 本探针只接受自建受控结构数据，没有生产级任意 SVG 导入、全 TeX 语法支持或无限长表格能力。超长单个小问／图表必须预检后分段或给出明确错误；当前 `breakable: false` 不适合高于整页的块，不能用静默缩图解决。
- 继续增加原题图笔迹标记、竖排或超宽公式、混合中英文、更多 mhchem 和复杂结构式样本。当前长小问覆盖跨栏／跨页，但不是所有真实试卷的版面保证。
- 这里的题目、答案和解析用于版面验证，没有真实模型调用，也不作为生成题语义正确率或学科答案准确率验收。

相关机制参考 [MathJax SVG 官方说明](https://docs.mathjax.org/en/v3.2/output/svg.html)、[Typst box / baseline](https://typst.app/docs/reference/layout/box/)、[Source Han Serif 字体来源](https://github.com/adobe-fonts/source-han-serif/tree/2.003R)。
