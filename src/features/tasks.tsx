import { useState } from 'react';
import {
  CheckCircle2,
  ChevronDown,
  ChevronRight,
  Circle,
  CircleX,
  Clock3,
  Database,
  LoaderCircle,
  Play,
  Shield,
  ShieldAlert,
  Square,
  Terminal,
} from 'lucide-react';
import type { Bootstrap, Job } from '../lib/api';
import { api, desktopAvailable } from '../lib/api';
import { executionLabels } from '../lib/labels';
import { Button, EmptyState, Modal, Notice } from '../components/ui';

const stateLabels: Record<Job['state'], string> = {
  QUEUED: '排队中',
  RUNNING: '运行中',
  WAITING_PERMISSION: '等待权限',
  COMPLETED: '已完成',
  FAILED: '失败',
  CANCELLED: '已取消',
  INTERRUPTED: '已中断',
};
const stateIcons = {
  QUEUED: Clock3,
  RUNNING: LoaderCircle,
  WAITING_PERMISSION: ShieldAlert,
  COMPLETED: CheckCircle2,
  FAILED: CircleX,
  CANCELLED: Square,
  INTERRUPTED: CircleX,
};
const active = (job: Job) => ['QUEUED', 'RUNNING', 'WAITING_PERMISSION'].includes(job.state);

export function TaskDrawer({
  open,
  onOpenChange,
  jobs,
  data,
  onChanged,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  jobs: Job[];
  data: Bootstrap | undefined;
  onChanged: () => Promise<void>;
}) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const pending = jobs.find((job) => job.state === 'WAITING_PERMISSION' && job.pending_permission);
  async function diagnose(kind: 'storage' | 'permission') {
    setBusy(true);
    setError('');
    try {
      await api.queueDiagnosticJob(kind);
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <Modal
        open={open}
        onOpenChange={onOpenChange}
        title="任务中心"
        description="查看本地任务的真实状态、执行方式与进度。"
        drawer
      >
        <div className="task-summary">
          <div>
            <span className="live-dot" />
            <strong>{jobs.filter(active).length}</strong>
            <span>项正在处理</span>
          </div>
          <span>{jobs.length} 项任务记录</span>
        </div>
        {error && <Notice kind="error">{error}</Notice>}
        <div className="task-list">
          {jobs.length ? (
            jobs.map((job) => <TaskItem key={job.id} job={job} onChanged={onChanged} />)
          ) : (
            <EmptyState title="当前没有任务">
              导入后的诊断和后续识别进度会在这里展示。任务状态由 Rust 队列管理。
            </EmptyState>
          )}
        </div>
        <div className="diagnostic-section">
          <h3>
            <Database size={16} />
            本地诊断
          </h3>
          <p>运行真实的存储检查，或使用私有 Python 检查当前执行方式。诊断不会调用模型。</p>
          <div className="diagnostic-actions">
            <Button
              variant="secondary"
              busy={busy}
              disabled={!desktopAvailable}
              onClick={() => void diagnose('storage')}
            >
              <Database size={14} />
              检查存储
            </Button>
            <Button
              variant="secondary"
              busy={busy}
              disabled={!desktopAvailable}
              onClick={() => void diagnose('permission')}
            >
              <Terminal size={14} />
              执行诊断
            </Button>
          </div>
          {data?.environment.policy.warning && (
            <Notice kind="warning">{data.environment.policy.warning}</Notice>
          )}
        </div>
      </Modal>
      {pending && <PermissionDialog job={pending} onChanged={onChanged} />}
    </>
  );
}

function TaskItem({ job, onChanged }: { job: Job; onChanged: () => Promise<void> }) {
  const [expanded, setExpanded] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const Icon = stateIcons[job.state] ?? Circle;
  async function cancel() {
    setBusy(true);
    setError('');
    try {
      await api.cancelJob(job.id);
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <article className={`task-item task-${job.state.toLowerCase()}`}>
      <div className="task-title">
        <span className="task-state-icon">
          <Icon size={18} className={job.state === 'RUNNING' ? 'spin' : ''} />
        </span>
        <div>
          <h3>
            {job.kind === 'storage'
              ? '本地存储诊断'
              : job.kind === 'permission'
                ? '执行环境诊断'
                : job.kind}
          </h3>
          <span>
            {stateLabels[job.state]} ·{' '}
            {new Date(job.created_at).toLocaleString('zh-CN', {
              month: 'numeric',
              day: 'numeric',
              hour: '2-digit',
              minute: '2-digit',
            })}
          </span>
        </div>
        <button
          className="icon-button"
          aria-label="展开任务详情"
          aria-expanded={expanded}
          onClick={() => setExpanded(!expanded)}
        >
          {expanded ? <ChevronDown size={16} /> : <ChevronRight size={16} />}
        </button>
      </div>
      <p className="task-message">{job.message}</p>
      <div
        className="progress-track"
        aria-label="任务进度"
        role="progressbar"
        aria-valuenow={job.progress}
        aria-valuemin={0}
        aria-valuemax={100}
      >
        <span style={{ width: `${Math.min(100, Math.max(0, job.progress))}%` }} />
      </div>
      <div className="task-meta">
        <span>
          <Shield size={12} />
          {executionLabels[job.execution_policy.effective_mode]} ·{' '}
          {job.execution_policy.executor_kind === 'host' ? '本机' : '沙箱'}
        </span>
        {active(job) && (
          <Button variant="ghost" busy={busy} onClick={() => void cancel()}>
            <Square size={11} />
            取消
          </Button>
        )}
      </div>
      {error && <Notice kind="error">{error}</Notice>}
      {expanded && (
        <div className="task-details">
          <code>{job.id}</code>
          {job.execution_policy.warning && (
            <Notice kind="warning">{job.execution_policy.warning}</Notice>
          )}
          {job.error && <Notice kind="error">{job.error}</Notice>}
          <p>
            实际执行方式：{executionLabels[job.execution_policy.effective_mode]}
            。运行中任务保留启动时的权限设置。
          </p>
        </div>
      )}
    </article>
  );
}

function PermissionDialog({ job, onChanged }: { job: Job; onChanged: () => Promise<void> }) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const request = job.pending_permission!;
  async function cancel() {
    if (busy) return;
    setBusy(true);
    setError('');
    try {
      await api.cancelJob(job.id);
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  async function resolve(decision: 'allow_once' | 'allow_task' | 'deny') {
    setBusy(true);
    setError('');
    try {
      await api.resolvePermission(job.id, request.digest, decision);
      await onChanged();
    } catch (cause) {
      setError((cause as Error).message);
    } finally {
      setBusy(false);
    }
  }
  return (
    <Modal
      open
      onOpenChange={(isOpen) => {
        if (!isOpen) void cancel();
      }}
      closeLabel="取消任务并关闭"
      title="任务请求执行权限"
      description="审阅内容后决定。等待期间任务已暂停；关闭此窗口将取消任务。"
    >
      <div className="permission-body">
        <Notice kind="warning">
          本次将在本机使用当前账户权限执行，能够访问该账户的文件和网络。允许本任务后，后续调用不再逐次询问。
        </Notice>
        <dl className="permission-meta">
          <div>
            <dt>工具</dt>
            <dd>{request.call.tool}</dd>
          </div>
          <div>
            <dt>工作目录</dt>
            <dd>
              <code>{request.call.working_directory}</code>
            </dd>
          </div>
          <div>
            <dt>超时</dt>
            <dd>{request.call.timeout_ms / 1000} 秒</dd>
          </div>
        </dl>
        <pre className="permission-code">
          <code>{request.call.content}</code>
        </pre>
        {request.call.arguments.length > 0 && (
          <pre className="permission-code">
            <code>{JSON.stringify(request.call.arguments, null, 2)}</code>
          </pre>
        )}
        <p className="permission-digest">
          审核绑定摘要 <code>{request.digest}</code>
        </p>
        {error && <Notice kind="error">{error}</Notice>}
      </div>
      <div className="permission-actions">
        <Button variant="secondary" disabled={busy} onClick={() => void cancel()}>
          <Square size={12} />
          取消任务
        </Button>
        <Button variant="danger" disabled={busy} onClick={() => void resolve('deny')}>
          拒绝
        </Button>
        <span />
        <Button variant="secondary" busy={busy} onClick={() => void resolve('allow_task')}>
          允许本任务
        </Button>
        <Button busy={busy} onClick={() => void resolve('allow_once')}>
          <Play size={14} />
          仅允许这次
        </Button>
      </div>
    </Modal>
  );
}
