# P6 原生图片、PDF 页与来源坐标验证

日期：2026-10-18。本记录仅覆盖本机合成媒体与独立缓存、坐标组件，不代表 P6 整体完成或 Windows 外部兼容矩阵通过。

## 实现

`NativeMediaProcessor` 实现 `ILibraryMediaProcessor`。PNG、JPEG、WebP 读取文件标识与尺寸后才解码，限制单文件 64 MiB、4000 万像素、单边 20000。原始字节由仓储保留，服务只返回尺寸、PNG 缩略图；PDF 由固定哈希 PDFium 逐页产生最长边 3600 的 PNG 与 512 缩略图，最多 12 页，包含原件与页图、缩略图的合计结果不超过 256 MiB。

`NativePageImages` 实现 `IGradingImageReader`：

```csharp
new NativeMediaProcessor(string? pdfiumPath = null, string? webpDecoderPath = null);
new NativePageImages(string? webpDecoderPath = null);
Task<GradingImageResult> ReadAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken ct);
Task<GradingImageResult> ReadForModelAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken ct = default);
Task<RasterizedPage> RasterForUiAsync(byte[] bytes, int maximumDimension, CancellationToken ct = default);
```

模型图片为 PNG/Base64。裁片先按未旋转原图的归一化区域计算 floor 起点、ceil 尺寸并夹紧，再顺时针旋转；不缩小裁片。整页在旋转后限定宽 2400、高 3600。0/90/180/270 以外角度拒绝。图片处理与 PDF 解码共享最多两个后台 MTA 工作槽，取消后不返回已处理结果。界面 raster 为 BGRA32 且按请求分辨率缩小，原件不落入预览临时文件。

`PageViewportTransform` 在未旋转像素、归一化来源与带缩放/偏移的视口之间双向转换，覆盖四个旋转方向；`CropBounds` 与旧整卷工具保持相同舍入和容差。`BoundedPageCache` 使用同步 LRU，按字节与条目双重限制；`BoundedRasterPageCache` 在共同预算内存储分辨率层级、复用满足请求的较大图像，并提供逐资料页失效与清空。

## WebP 自包含路径

本机 WIC 注册了 Microsoft Webp Decoder，但对 Google `cwebp 1.6.0 -lossless -exact` 从现有合成 4×3 PNG 编码、并经 Google `dwebp` 独立解码验证的 WebP，初始化仍返回 `0x88982F8B`。该失败在 JIT 与 Native AOT 均出现，不能用 PNG 成功替代 WebP 验收。

实现保留原生 WIC 优先路径，WebP 初始化失败后使用随产品分发的固定 Google `dwebp.exe`。该工具没有浏览器、Node、Python 或系统软件安装依赖。所有文件名通过 `ProcessStartInfo.ArgumentList` 传递，`UseShellExecute=false`、隐藏窗口，输入和输出位于单次新建的临时目录。先解析 VP8/VP8L/VP8X 容器尺寸并检查像素限制；每次启动前校验可执行文件 SHA-256；30 秒超时或用户取消时回收当前 `Process` 启动的进程树并等待退出，再清理本次已知输入、输出与空目录。不会枚举或结束其他进程。

输出文件监视与最终长度检查限制在 64 MiB；输出/错误流各限制 16384 字符，文本不记录。PNG 的文件标识与尺寸必须匹配 WebP 头。临时文件清理遇外部占用会保留该独立临时目录，不递归删除其他路径。

固定版本和许可证：`packaging/webp.lock.json`，准备命令 `./packaging/prepare-webp.ps1`。官方 `libwebp-1.6.0-windows-x64.zip` SHA-256 为 `48886f506b21f62e4661f0f4cbfca19800897c385128e8902542d29a950c93f1`；运行时 `dwebp.exe` 为 `17c1488bf84b7834e9aa908bb40afd0be2e55d57567d210e96336c56bb6ae993`。分发路径为 `native/webp/dwebp.exe`，同时携带对应 `COPYING`、`PATENTS`。官方包下载初次在沙箱中 TLS 失败，授权下载后文件、可执行文件与许可证均已计算真实 SHA-256；准备脚本在现有缓存上实跑输出版本 `1.6.0`。

## 验证命令与范围

```powershell
$env:DOTNET_CLI_HOME = Join-Path $PWD 'output/migration/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $PWD 'output/migration/nuget'
./packaging/prepare-webp.ps1
dotnet restore tests/OhMyStudy.Media.Tests/OhMyStudy.Media.Tests.csproj -p:Configuration=Release --locked-mode
dotnet publish tests/OhMyStudy.Media.Tests/OhMyStudy.Media.Tests.csproj --no-restore -c Release -p:TrimmerSingleWarn=false -o output/migration/media-aot
./output/migration/media-aot/OhMyStudy.Media.Tests.exe output/migration/media-aot-tests.json output/migration/toolchain/pdfium/pdfium.dll output/migration/toolchain/libwebp/libwebp-1.6.0-windows-x64/bin/dwebp.exe
```

独立控制台直接链接两个正式媒体服务源码，使用正式 Domain/Infrastructure/Documents，不依赖 App UI 或旧源码。测试包括真实合成 PNG/JPEG/WebP/PDF 解码、输出与原件保留、四方向逐像素和坐标往返、裁片次序、整页与裁片分辨率、header/页数/输入限制、取消、并行数 2、LRU 驱逐与分级/并发预算、WebP 固定 exe 篡改拒绝和容器大小限制。

2026-10-18 实际发布并直接运行结果：**26 项通过，0 失败**；发布 0 警告、0 错误。报告 `output/migration/media-aot-tests.json` 记录 `native_aot=true`、`coreclr_loaded=false`。其中包含额外的 VP8X canvas/实际编码尺寸一致性和动画明确拒绝测试，避免只信外层尺寸而绕过像素限制。当前动画 WebP 会提示另存静态图片，不能宣称支持动画。

该运行没有测量全进程峰值内存或 UI 帧时间，没有覆盖真实资料、所有 WebP 变体/ICC、恶意解码器漏洞、异常系统关机、外部 Windows 10/11 矩阵、物理打印或正式离线安装。


## 复杂题目预览与导出（2026-10-18追加）

`ExamSegment`完整表达文字、公式、二维表格或已解码PNG/JPEG；`ExamPart`递归表达小问、选项、答案及解析。A3横向双栏与A4纵向共享固定字体、公式转换和回退规则。题干与小问正文允许跨页；图像保持比例并限制高度，宽表按原列号分组并重复行号，所有单元格均保留。图片字节只写入本次导出根目录中按SHA-256生成的文件名，不接受题目提供的文件路径或SVG。

应用从资料库读取图像来源页，复用原生区域裁剪和页面旋转。PNG/JPEG/WebP原件及PDF产生的页图都先经过原生解码，导出接收PNG裁片。缺失来源或无效图片会使整个导出失败，不产生声称完整的缺图文件。含手写标记的题图会在题目卷导出前明确要求更换干净题图；不能自动推断或擦除未知笔迹。

题目卷模板只读取正文、图表、选项和小问；答案与解析仅在答案册分支读取，`StudentAttempt`从不进入模板。答案册保留正文与小问，方便对照。公式不受支持或超过解析长度时保留完整可换行原文，保存后提示检查，不再阻止其他内容导出或截断8192字符后的内容。

`DocumentText`统一识别`$...$`、`$$...$$`、`\(...\)`、`\[...\]`，用于正文、选项、表格和答案中的公式。原生阅读控件使用相同固定中文字体、11pt对应的逻辑字号及同源公式栅格；主题变化只改变公式前景色。转换器补充cases/aligned、组合数、重音、上下附注和常用几何/集合符号。不支持任意TeX宏与包，未知语法仍保留原文。

资源限制：1–500题，正文单段32768字符、全卷1000000字符；小问最多32层、单节点100个、全卷10000个；单表最多2000行、100列、50000单元格；图像最多512个、单个64MiB、总计128MiB，20000单边与4000万像素。边界被明确拒绝，不能据此声称处理无限输入。所有输入文字均转义，表格和小问标签同样计入预算。

新增`ComplexDocumentProbe`覆盖519个宽表/不等长行单元格、12层小问、图像与标题、超长题干、9000字符公式原文以及四份A3/A4题目/答案PDF的实际提取文字和隔离。`probes/verify-documents.py`独立检查实际纸张、字体嵌入、图片对象、逐字页面边界并用Poppler渲染。2026-10-18最终Release Native AOT实跑通过（13.96秒开发探针总耗时），0构建警告、0错误，x64 PE的CLR目录RVA/大小均为0，`DynamicCodeSupported=false`。旧26项媒体AOT结果不替代本轮复杂导出验证。

外部验收按用户2026-10-18指示交由用户完成：真实资料、干净Windows系统、DPI/辅助功能人工检查、打印对话框及纸张实打。本次不把这些未执行项写为通过。


### 本轮实际产物与独立检查

产物与报告：`output/migration/documents-complete-aot/verification.json`及`verification-pdf.json`。基础A3题目/答案各2页，A4题目4页/答案3页；`complex/`内A3题目/答案各6页，A4题目/答案各11页，共8份45页。四份复杂PDF均有实际嵌入图像对象，全部字体均有嵌入流，未发现替换字符、交互注释或逐字页面越界。

所有45页经Poppler渲染；已逐一查看8份全页联系图，未观察到裁切、重叠、表格缺格或长原文丢失。合成图像为明确标识的蓝色测试图，不能据此宣称真实样卷图像识别正确。常用几何符号、重音、cases/aligned等新增公式也通过固定引擎实际编译。首次实跑发现并修复了无定界符矩阵的`#none`命名空间问题，最终重跑通过。

应用导出准备同样执行累计图片字节/数量预算并复用重复裁片；未保存修改会先提示保存，避免把界面中的新答案误认为已导出。应用适配还需要主代理串行发布与桌面回归，本节文档探针不能替代它。

重跑：

```powershell
dotnet publish probes/OhMyStudy.Documents.Probe/OhMyStudy.Documents.Probe.csproj -c Release -r win-x64 --no-restore -p:TrimmerSingleWarn=false -o output/migration/publish/documents-complete
./output/migration/publish/documents-complete/OhMyStudy.Documents.Probe.exe $PWD (Join-Path $PWD 'output/migration/documents-complete-aot')
# 开发QA工具，使用已有pypdf/pdfplumber/Pillow与Poppler；不进入产品运行/安装依赖。
python probes/verify-documents.py output/migration/documents-complete-aot --pdftoppm '<pdftoppm.exe的绝对路径>'
```
