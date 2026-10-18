# M1 桌面界面与前端交接

本文记录 M1 提交 `308b33e` 时的界面与验证边界。2026-10-18 后续已按新增需求迁移为 Fluent Design 2／Fluent UI React v9，并接入 M2 结果页面；当前能力以 [M2 文档](m2-grading.zh-CN.md) 为准。

前端使用 React、TypeScript、Vite、HashRouter、TanStack Query、Tailwind CSS、Lucide 和 Radix Dialog。静态资源由 Tauri 加载；浏览器开发模式明确显示“界面预览”，不读取本机文件、不生成示例数据库，也不展示虚构模型或题目。

## 页面与当前能力

- 导入与批改：本机文件选择、页序调整、三种材料模式、参考答案附件与材料命名。Rust 保存后才显示“本地已保存”。超过 12 份输入会明确拒绝这次追加，不静默截断。
- 材料详情：按资产 ID 读取原件；图片可以旋转、排序和移除，修改经 Rust 原子保存。删除页面保留资产原件。参考答案也可在详情中后补，只追加原件，不自动批改或产生费用。PDF 通过本地 Blob iframe 显示；页面明确说明 M1 没有 PDFium、PDF 页数尚未解析，WebView2 可能无法显示内置 PDF。不能据此视为 M2 的逐页 PDF 导入已完成。
- 题库、错题和复习卷：显示真实空态和对应后续阶段。没有将演示题目、分数、生成进度或 PDF 冒充业务结果。
- 供应商：Responses、Messages、Gemini 原生协议，供应商根地址、模型 ID、图片／工具能力的支持／不支持／未知、原生思考参数和百万 tokens 输入／输出价格。未知能力不会按模型名猜测。价格为用户配置，不代表已查证供应商报价。
- 权限：请求权限、沙箱运行、绕过权限；显示 Rust 检测出的实际执行环境。沙箱检测未就绪时禁用新的沙箱选择，保留旧配置的实际回退警告。设置只影响新任务。
- 任务中心：实时 Tauri Channel 通知并辅以 2.5 秒列表轮询、真实状态、进度、取消、诊断入口与绑定 digest 的逐次／本任务／拒绝权限对话框。等待权限时可以显式取消；关闭权限窗也会取消任务，不会冒充拒绝或批准。诊断来自 Rust 队列；不是模拟 AI 批改。

## IPC 与数据边界

契约集中在 `src/lib/api.ts`，命令参数统一 snake_case。SQLite、资产路径、供应商请求、凭据存储、任务队列和进程管理全部经 Rust IPC。前端没有 localStorage 或 IndexedDB 持久化，没有通用文件或 shell API。

密钥使用不受控 password 输入和 DOM ref。在保存事件内读取后立即清空，再直接调用 Rust；不经过 React state、TanStack mutation 参数、查询缓存或日志。服务器返回仅包含 `has_key` 与非敏感凭据引用。密钥保存失败时不会保留原输入，需要重新输入。

默认模型字段 `settings.default_model` 编码为 `${provider.id}:${model.id}`，仅在首个冒号处分开，避免不同供应商同名模型歧义。价格未知保存为 null；能力未知也保存为 null。

原件字节只在读取期间短暂存在，随后转换为 Blob URL；切换原件或卸载组件会 revoke URL，不放入持久化或 TanStack 查询缓存。

## 本地开发与验证

```powershell
npm ci
npm run typecheck
npm run lint
npm run build
npm run dev
npm run desktop:dev
npm run desktop:build
```

Vite 固定监听 `127.0.0.1:1420`，桌面构建 target 为 Chrome 105。界面适配普通笔记本与 Windows 缩放后的窄视口；宽度缩小时导入区和详情改为单栏，侧栏可折叠。最小 CSS 视口宽度为 760 px，支持减少动画偏好和键盘焦点。Radix 管理模态层、焦点和可访问性标题。

M1 不包含真实供应商调用验收、PDFium 逐页解析、题目数据库查询、整卷 Agent、组卷打印。这些边界会在界面中明确显示，不能把静态页面预览视为完成后续里程碑。
