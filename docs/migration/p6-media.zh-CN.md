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
