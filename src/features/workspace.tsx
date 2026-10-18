import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import {
  ArrowDown,
  ArrowLeft,
  ArrowRight,
  ArrowUp,
  BookOpen,
  Check,
  ChevronRight,
  ClipboardCheck,
  FileCheck2,
  FileImage,
  FilePlus2,
  FileText,
  FolderOpen,
  ImagePlus,
  Layers,
  ListChecks,
  LoaderCircle,
  Plus,
  RotateCw,
  ScanLine,
  Search,
  ShieldCheck,
  Sparkles,
  Trash2,
  UploadCloud,
} from 'lucide-react';
import type { PageProps } from '../app/App';
import { api, desktopAvailable } from '../lib/api';
import type { ImportMode, SourceDocument, SourcePage } from '../lib/api';
import { Button, EmptyState, Notice, SectionTitle } from '../components/ui';

const modeLabels: Record<ImportMode, string> = {
  teacher_graded: '已有教师批改',
  ai_grading: '待 AI 批改',
  blank: '空白试卷',
};
const modes: { value: ImportMode; title: string; hint: string; icon: typeof FileText }[] = [
  {
    value: 'teacher_graded',
    title: '已有教师批改',
    hint: '保留老师的批改，整理错题',
    icon: ClipboardCheck,
  },
  { value: 'ai_grading', title: '待 AI 批改', hint: '识别学生作答，辅助检查', icon: Sparkles },
  { value: 'blank', title: '空白试卷', hint: '整理题目与答案，不生成作答', icon: FileText },
];
const filename = (path: string) => path.split(/[\\/]/).pop() || path;
const dateLabel = (value: string) =>
  new Date(value).toLocaleDateString('zh-CN', { month: 'long', day: 'numeric' });

export function ImportPage({ data, onChanged }: PageProps) {
  const navigate = useNavigate();
  const [paths, setPaths] = useState<string[]>([]);
  const [references, setReferences] = useState<string[]>([]);
  const [mode, setMode] = useState<ImportMode>('teacher_graded');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [selecting, setSelecting] = useState(false);

  async function choose(reference = false) {
    setError('');
    setSelecting(true);
    try {
      const chosen = await api.selectFiles();
      if (paths.length + references.length + chosen.length > 12) {
        setError('本次材料与参考答案合计最多选择 12 份文件，请分批导入。本次选择尚未添加。');
        return;
      }
      if (reference) setReferences((items) => [...items, ...chosen]);
      else setPaths((items) => [...items, ...chosen]);
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setSelecting(false);
    }
  }

  async function save() {
    setBusy(true);
    setError('');
    try {
      const document = await api.importFiles(
        paths,
        name.trim() || filename(paths[0]).replace(/\.[^.]+$/, ''),
        mode,
        references,
      );
      await onChanged();
      navigate(`/documents/${document.id}`);
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function move(index: number, offset: number) {
    const next = [...paths];
    [next[index], next[index + offset]] = [next[index + offset], next[index]];
    setPaths(next);
  }

  return (
    <div className="page import-page">
      <div className="page-heading">
        <div>
          <span className="eyebrow">从一份练习开始</span>
          <h1>
            导入与批改<span className="heading-dot">.</span>
          </h1>
          <p>留住每一次练习，让知识点和错题慢慢清晰。</p>
        </div>
        <span className="outline-badge">
          <ShieldCheck size={14} />
          本地保存 · 原件保留
        </span>
      </div>
      <div className="workflow-strip">
        <span className="workflow-step current">
          <i>1</i>导入材料
        </span>
        <span className="workflow-line" />
        <span className="workflow-step">
          <i>2</i>识别与批改<small>M2</small>
        </span>
        <span className="workflow-line" />
        <span className="workflow-step">
          <i>3</i>整理到题库<small>M2</small>
        </span>
      </div>
      {error && <Notice kind="error">{error}</Notice>}
      <div className="import-grid">
        <section className="panel material-panel">
          <div className="panel-heading">
            <h2>
              <span className="section-number">01</span>添加练习材料
            </h2>
            <span>图片 / PDF</span>
          </div>
          {paths.length === 0 ? (
            <div className="upload-zone">
              <div className="paper-illustration">
                <div className="paper-back" />
                <div className="paper-front">
                  <span className="paper-fold" />
                  <FileText size={34} strokeWidth={1.25} />
                  <i />
                  <i />
                  <i />
                </div>
                <span className="upload-symbol">
                  <Plus size={20} />
                </span>
                <span className="illustration-dot one" />
                <span className="illustration-dot two" />
              </div>
              <h3>一份试卷，一次完整的整理</h3>
              <p>选择本机照片、扫描件或 PDF 原件</p>
              <Button onClick={() => void choose()} disabled={!desktopAvailable} busy={selecting}>
                <FolderOpen size={16} />
                选择文件
              </Button>
              <small>支持 JPG、PNG、WebP、PDF · 最多 12 份输入 · PDF 页数待解析</small>
            </div>
          ) : (
            <div className="staged-files">
              <div className="file-list-heading">
                <strong>已选择 {paths.length} 份文件</strong>
                <Button
                  variant="ghost"
                  disabled={selecting || paths.length >= 12}
                  onClick={() => void choose()}
                >
                  <Plus size={14} />
                  继续添加
                </Button>
              </div>
              {paths.map((path, index) => (
                <div className="staged-file" key={`${path}-${index}`}>
                  <span className="file-order">{index + 1}</span>
                  <FileImage size={21} />
                  <span className="file-label" title={path}>
                    {filename(path)}
                  </span>
                  <div className="row-actions">
                    <button
                      className="icon-button"
                      aria-label={`上移 ${filename(path)}`}
                      disabled={index === 0}
                      onClick={() => move(index, -1)}
                    >
                      <ArrowUp size={14} />
                    </button>
                    <button
                      className="icon-button"
                      aria-label={`下移 ${filename(path)}`}
                      disabled={index === paths.length - 1}
                      onClick={() => move(index, 1)}
                    >
                      <ArrowDown size={14} />
                    </button>
                    <button
                      className="icon-button"
                      aria-label={`移除 ${filename(path)}`}
                      onClick={() => setPaths(paths.filter((_, i) => i !== index))}
                    >
                      <Trash2 size={14} />
                    </button>
                  </div>
                </div>
              ))}
              <p className="field-hint">文件顺序决定材料页序；导入后可继续旋转、排序和移除。</p>
            </div>
          )}
          <div className="material-footnote">
            <FileCheck2 size={16} />
            <span>原始文件会完整保留，整理不会改动你的原件</span>
          </div>
        </section>
        <section className="panel mode-panel">
          <div className="panel-heading">
            <h2>
              <span className="section-number">02</span>选择材料类型
            </h2>
          </div>
          <div className="mode-options" role="radiogroup" aria-label="材料类型">
            {modes.map(({ value, title, hint, icon: Icon }) => (
              <button
                key={value}
                className={`mode-option ${mode === value ? 'selected' : ''}`}
                role="radio"
                aria-checked={mode === value}
                onClick={() => setMode(value)}
              >
                <span className="mode-icon">
                  <Icon size={20} strokeWidth={1.6} />
                </span>
                <span>
                  <strong>{title}</strong>
                  <small>{hint}</small>
                </span>
                <span className="radio-mark">
                  {mode === value && <Check size={10} strokeWidth={3} />}
                </span>
              </button>
            ))}
          </div>
          <div className="reference-box">
            <div>
              <FilePlus2 size={17} />
              <strong>附加参考答案</strong>
              <span>可选</span>
            </div>
            <p>答案文件和学生作答分开保存</p>
            <Button
              variant="secondary"
              disabled={!desktopAvailable || selecting}
              onClick={() => void choose(true)}
            >
              <Plus size={14} />
              {references.length ? `已选 ${references.length} 份，继续添加` : '添加答案文件'}
            </Button>
            {references.map((path, index) => (
              <div className="reference-file" key={`${path}-${index}`}>
                <span>{filename(path)}</span>
                <button
                  className="icon-button"
                  aria-label={`移除参考答案 ${filename(path)}`}
                  onClick={() => setReferences(references.filter((_, i) => i !== index))}
                >
                  <Trash2 size={13} />
                </button>
              </div>
            ))}
          </div>
        </section>
      </div>
      <section className="panel import-action">
        <label className="name-field">
          <span>材料名称</span>
          <input
            value={name}
            onChange={(event) => setName(event.target.value)}
            placeholder="例如：数学 · 第三单元练习"
            maxLength={200}
          />
        </label>
        <div>
          <span className="action-caption">当前阶段保存材料与原件</span>
          <Button
            busy={busy}
            disabled={!desktopAvailable || !paths.length}
            onClick={() => void save()}
          >
            <UploadCloud size={16} />
            保存到本地
            <ArrowRight size={16} />
          </Button>
        </div>
      </section>
      <div className="scope-note">
        <span className="mini-stage">M1</span>
        <p>
          材料导入、原件管理和模型设置已开放。整卷识别与 AI 批改将在 M2
          接入，当前保存不会调用模型或产生模型费用。
        </p>
        <Link to="/settings">
          配置模型
          <ChevronRight size={14} />
        </Link>
      </div>
      <section className="recent-section">
        <SectionTitle title="最近导入">
          <span className="subtle">{data?.documents.length ?? 0} 份材料</span>
        </SectionTitle>
        {data?.documents.length ? (
          <div className="document-list">
            {data.documents.slice(0, 5).map((document) => (
              <DocumentRow key={document.id} document={document} />
            ))}
          </div>
        ) : (
          <div className="recent-empty">
            <span>
              <Layers size={21} strokeWidth={1.4} />
            </span>
            <div>
              <strong>你的学习材料，从这里开始积累</strong>
              <p>导入后，原件和整理进度会保存在这里。</p>
            </div>
          </div>
        )}
      </section>
    </div>
  );
}

function DocumentRow({ document }: { document: SourceDocument }) {
  return (
    <Link className="document-row" to={`/documents/${document.id}`}>
      <span className="document-icon">
        <FileText size={21} />
      </span>
      <div>
        <strong>{document.name}</strong>
        <small>
          {modeLabels[document.mode]} ·{' '}
          {document.pages.filter((p) => p.role !== 'reference').length} 个原件
          {document.pending_pdf_pages ? ' · PDF 待解析' : ''}
        </small>
      </div>
      <span className="subtle">{dateLabel(document.created_at)}</span>
      <span className="status-tag">已保存</span>
      <ChevronRight size={16} />
    </Link>
  );
}

function AssetPreview({ page }: { page: SourcePage }) {
  const [asset, setAsset] = useState<{ url: string; mime: string } | null>(null);
  const [error, setError] = useState('');
  useEffect(() => {
    let disposed = false;
    let url: string | null = null;
    void api
      .readAsset(page.asset_id)
      .then((result) => {
        if (disposed) return;
        url = URL.createObjectURL(new Blob([new Uint8Array(result.bytes)], { type: result.mime }));
        setAsset({ url, mime: result.mime });
      })
      .catch((cause: Error) => {
        if (!disposed) setError(cause.message);
      });
    return () => {
      disposed = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [page.asset_id]);
  if (error) return <Notice kind="error">原件读取失败：{error}</Notice>;
  if (!asset)
    return (
      <div className="preview-loading">
        <LoaderCircle className="spin" size={22} />
        读取本地原件…
      </div>
    );
  if (asset.mime === 'application/pdf')
    return (
      <div className="pdf-preview">
        <Notice>
          此处显示 PDF 原件。M1 尚未集成 PDFium，页数和逐页旋转待 M2 解析；若下方无法显示，是当前
          WebView2 未提供 PDF 查看器。原件仍已保存在本地。
        </Notice>
        <iframe src={asset.url} title="PDF 原件预览" />
      </div>
    );
  const width = page.width ?? 1;
  const height = page.height ?? 1;
  const swapped = page.rotation % 180 !== 0;
  const transform = page.rotation === 90 ? `translate(${height} 0) rotate(90)`
    : page.rotation === 180 ? `translate(${width} ${height}) rotate(180)`
    : page.rotation === 270 ? `translate(0 ${width}) rotate(270)` : undefined;
  return (
    <div className="image-preview">
      <svg role="img" aria-label="导入材料原件" viewBox={`0 0 ${swapped ? height : width} ${swapped ? width : height}`} preserveAspectRatio="xMidYMid meet">
        <image href={asset.url} width={width} height={height} transform={transform} />
      </svg>
    </div>
  );
}

export function DocumentPage({ data, onChanged }: PageProps) {
  const { id } = useParams();
  const document = data?.documents.find((item) => item.id === id);
  const [selected, setSelected] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  if (!document)
    return (
      <div className="page">
        <Link to="/import" className="back-link">
          <ArrowLeft size={15} />
          返回导入
        </Link>
        <EmptyState title="未找到这份材料">请从最近导入中选择已保存的材料。</EmptyState>
      </div>
    );
  const page = document.pages.find((item) => item.id === selected) ?? document.pages[0];
  async function edit(action: 'rotate' | 'up' | 'down' | 'delete') {
    if (!document || !page) return;
    setBusy(true);
    setError('');
    const next = document.pages.map((item) => ({ id: item.id, rotation: item.rotation }));
    const index = next.findIndex((item) => item.id === page.id);
    if (action === 'rotate') next[index].rotation = (next[index].rotation + 90) % 360;
    if (action === 'delete') next.splice(index, 1);
    if (action === 'up' || action === 'down') {
      const other = index + (action === 'up' ? -1 : 1);
      if (other >= 0 && other < next.length)
        [next[index], next[other]] = [next[other], next[index]];
    }
    try {
      await api.updatePages(document.id, next);
      setSelected(
        action === 'delete' ? (next[Math.min(index, next.length - 1)]?.id ?? '') : page.id,
      );
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function appendReferences() {
    if (!document) return;
    setBusy(true);
    setError('');
    try {
      const paths = await api.selectFiles();
      if (paths.length === 0) return;
      if (document.pages.length + paths.length > 12) {
        setError('材料与参考答案合计最多 12 份文件。本次选择尚未添加，请分批导入。');
        return;
      }
      await api.appendReferences(document.id, paths);
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <div className="page detail-page">
      <Link to="/import" className="back-link">
        <ArrowLeft size={15} />
        返回导入与批改
      </Link>
      <div className="page-heading">
        <div>
          <span className="eyebrow">已保存的材料</span>
          <h1>{document.name}</h1>
          <p>
            {modeLabels[document.mode]} · {dateLabel(document.created_at)} · {document.pages.length}{' '}
            个原件
          </p>
        </div>
        <span className="status-tag">
          <Check size={13} />
          本地已保存
        </span>
      </div>
      {error && <Notice kind="error">{error}</Notice>}
      <div className="detail-grid">
        <section className="panel source-panel">
          <div className="panel-heading">
            <h2>原始材料</h2>
            <span>
              {page ? `${document.pages.indexOf(page) + 1} / ${document.pages.length}` : '无页面'}
            </span>
          </div>
          <div className="page-tabs">
            {document.pages.map((item, index) => (
              <button
                key={item.id}
                onClick={() => setSelected(item.id)}
                className={`page-tab ${item.id === page?.id ? 'active' : ''}`}
              >
                <FileImage size={14} />
                {item.role === 'reference' ? '答案' : '材料'} {index + 1}
                {item.preview_status === 'pending_pdf' && <small>PDF</small>}
              </button>
            ))}
          </div>
          {page ? (
            <>
              <div className="preview-toolbar">
                <span>
                  {page.width && page.height
                    ? `${page.width} × ${page.height}`
                    : 'PDF 原件 · 待解析'}
                </span>
                <div className="row-actions">
                  <button
                    className="icon-button"
                    disabled={busy || document.pages.indexOf(page) === 0}
                    onClick={() => void edit('up')}
                    title="上移"
                  >
                    <ArrowUp size={15} />
                  </button>
                  <button
                    className="icon-button"
                    disabled={busy || document.pages.indexOf(page) === document.pages.length - 1}
                    onClick={() => void edit('down')}
                    title="下移"
                  >
                    <ArrowDown size={15} />
                  </button>
                  <button
                    className="icon-button"
                    disabled={busy || page.preview_status === 'pending_pdf'}
                    onClick={() => void edit('rotate')}
                    title="顺时针旋转"
                  >
                    <RotateCw size={15} />
                  </button>
                  <button
                    className="icon-button"
                    disabled={busy}
                    onClick={() => void edit('delete')}
                    title="从材料中移除（原件保留）"
                  >
                    <Trash2 size={15} />
                  </button>
                </div>
              </div>
              <AssetPreview key={page.id} page={page} />
            </>
          ) : (
            <EmptyState title="材料中暂无页面">
              被移除的原件仍由资产库保留。可重新导入一份材料。
            </EmptyState>
          )}
        </section>
        <section className="panel grading-panel">
          <div className="panel-heading">
            <h2>
              <ScanLine size={17} />
              题目与批改
            </h2>
            <span className="mini-stage">M2</span>
          </div>
          <EmptyState title="原件已就位，识别尚未开始">
            整卷识别与批改将在下一阶段接入。当前没有生成题目、得分或 AI 判断。
          </EmptyState>
          <div className="detail-next">
            <h3>接下来可以做什么</h3>
            <p>
              <Check size={14} />
              检查材料顺序和图片方向
            </p>
            <p>
              <Check size={14} />
              查看教师批改与参考答案原件
            </p>
            <p>
              <Check size={14} />
              先配置供应商和模型能力
            </p>
            <Link to="/settings" className="text-link">
              打开模型设置
              <ArrowRight size={14} />
            </Link>
            <div className="append-reference">
              <Button
                variant="secondary"
                busy={busy}
                disabled={!desktopAvailable || document.pages.length >= 12}
                onClick={() => void appendReferences()}
              >
                <FilePlus2 size={14} />
                后补参考答案
              </Button>
              <p>只保存附件，不会自动批改或产生模型费用。</p>
            </div>
          </div>
        </section>
      </div>
    </div>
  );
}

export function QuestionPage({ wrong }: { wrong: boolean }) {
  return (
    <div className="page">
      <div className="page-heading">
        <div>
          <span className="eyebrow">{wrong ? '从错误中，找到下一步' : '把学过的题，留在身边'}</span>
          <h1>
            {wrong ? '错题整理' : '我的题库'}
            <span className="heading-dot">.</span>
          </h1>
          <p>
            {wrong
              ? '错误、部分正确和未作答的题目，将在这里汇集。'
              : '每份材料里的题目，都有自己的来源和整理记录。'}
          </p>
        </div>
        <span className="outline-badge">{wrong ? 'M3 · 错题视图' : 'M2 · 识别后入库'}</span>
      </div>
      <section className="panel">
        <div className="filter-bar">
          <span className="search-placeholder">
            <Search size={16} />
            识别入库后，可搜索题目内容
          </span>
          <span className="subtle">0 道题目</span>
        </div>
        <EmptyState title={wrong ? '还没有可展示的错题' : '还没有识别入库的题目'}>
          {wrong
            ? 'M1 只保存材料原件；完成 M2 批改闭环后，M3 将提供错题筛选与整理。'
            : '你可以先导入材料。M2 接入整卷识别后，题目、答案与解析会在这里保存。'}
        </EmptyState>
        <div className="empty-action">
          <Link className="button button-primary" to="/import">
            <ImagePlus size={16} />
            导入一份材料
            <ArrowRight size={15} />
          </Link>
        </div>
      </section>
      <div className="quiet-info">
        <BookOpen size={17} />
        <p>原始材料始终保留，题目会关联到对应页面。当前没有使用演示题目填充题库。</p>
      </div>
    </div>
  );
}

export function PapersPage({ data }: { data: PageProps['data'] }) {
  const pdfDocuments = data?.documents.filter((document) => document.pending_pdf_pages) ?? [];
  return (
    <div className="page">
      <div className="page-heading">
        <div>
          <span className="eyebrow">温故，然后向前</span>
          <h1>
            复习卷<span className="heading-dot">.</span>
          </h1>
          <p>把需要巩固的内容，整理成一份适合自己的练习。</p>
        </div>
        <span className="outline-badge">M4 · 组卷与打印</span>
      </div>
      <div className="paper-types">
        <section className="panel paper-type">
          <span className="mode-icon">
            <Layers size={23} />
          </span>
          <h2>原题复现</h2>
          <p>保留题目内容，去除作答与答案，重新检查理解。</p>
          <span className="subtle">等待题库闭环</span>
        </section>
        <section className="panel paper-type">
          <span className="mode-icon">
            <Sparkles size={23} />
          </span>
          <h2>AI 巩固练习</h2>
          <p>围绕错题知识点生成新题，再独立解题核验。</p>
          <span className="subtle">等待 M4 实现</span>
        </section>
      </div>
      <section className="panel">
        <div className="panel-heading">
          <h2>
            <ListChecks size={18} />
            已生成的复习卷
          </h2>
          <span className="subtle">0 份</span>
        </div>
        <EmptyState title="复习卷尚未生成">
          正式组卷、A3 / A4 排版与答案册将在 M4 实现。预览与导出将使用同一份 PDF。
        </EmptyState>
      </section>
      {pdfDocuments.length > 0 && (
        <section className="recent-section">
          <SectionTitle title="已导入的 PDF 原件" />
          <div className="document-list">
            {pdfDocuments.map((document) => (
              <DocumentRow key={document.id} document={document} />
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
