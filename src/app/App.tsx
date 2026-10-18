import { useEffect, useState } from 'react';
import { Channel } from '@tauri-apps/api/core';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { Link, NavLink, Navigate, Route, Routes, useLocation } from 'react-router-dom';
import {
  BookMarked,
  BookOpen,
  ChevronRight,
  CircleHelp,
  ClipboardList,
  FileStack,
  Layers3,
  LoaderCircle,
  PanelLeft,
  Settings2,
  ShieldCheck,
  ShieldAlert,
  Sparkles,
  Upload,
  WifiOff,
} from 'lucide-react';
import { api, desktopAvailable, desktopCall } from '../lib/api';
import type { Bootstrap, JobEvent } from '../lib/api';
import { executionLabels } from '../lib/labels';
import { Button, Notice } from '../components/ui';
import { ImportPage, DocumentPage, QuestionPage, PapersPage } from '../features/workspace';
import { SettingsPage } from '../features/settings';
import { TaskDrawer } from '../features/tasks';

const navigation = [
  { to: '/import', icon: Upload, label: '导入与批改', hint: '从一份练习开始' },
  { to: '/questions', icon: BookOpen, label: '我的题库', hint: '' },
  { to: '/wrong', icon: BookMarked, label: '错题整理', hint: '' },
  { to: '/papers', icon: ClipboardList, label: '复习卷', hint: '' },
];

export function App() {
  const [drawerOpen, setDrawerOpen] = useState(false);
  const [compact, setCompact] = useState(false);
  const location = useLocation();
  const queryClient = useQueryClient();
  const bootstrap = useQuery({
    queryKey: ['bootstrap'],
    queryFn: api.bootstrap,
    enabled: desktopAvailable,
  });
  const jobs = useQuery({
    queryKey: ['jobs'],
    queryFn: api.listJobs,
    enabled: desktopAvailable && !!bootstrap.data,
    refetchInterval: 2500,
  });

  useEffect(() => {
    if (!desktopAvailable) return;
    const channel = new Channel<JobEvent>();
    channel.onmessage = () => {
      void queryClient.invalidateQueries({ queryKey: ['jobs'] });
    };
    // Polling remains active if IPC subscriptions are unavailable or interrupted.
    void desktopCall('subscribe_jobs', { channel }).catch(() => undefined);
  }, [queryClient]);

  const activeJobs = (jobs.data ?? bootstrap.data?.jobs ?? []).filter((job) =>
    ['QUEUED', 'RUNNING', 'WAITING_PERMISSION'].includes(job.state),
  );
  const current = location.pathname.startsWith('/documents')
    ? '材料详情'
    : (navigation.find((item) => location.pathname.startsWith(item.to))?.label ?? '设置');
  const data = bootstrap.data;
  const offline = !desktopAvailable;

  async function reload() {
    await queryClient.invalidateQueries({ queryKey: ['bootstrap'] });
    await queryClient.invalidateQueries({ queryKey: ['jobs'] });
  }

  return (
    <div className={`app-shell ${compact ? 'compact-nav' : ''}`}>
      <aside className="sidebar">
        <NavLink to="/import" className="brand">
          <span className="brand-mark">
            <Layers3 size={23} />
          </span>
          <span>
            <strong>
              Oh My Study<span className="brand-dot">.</span>
            </strong>
            <small>让练习有迹可循</small>
          </span>
        </NavLink>
        <div className="workspace-label">
          我的学习空间<span>本地</span>
        </div>
        <nav aria-label="主导航">
          {navigation.map(({ to, icon: Icon, label, hint }) => (
            <NavLink
              key={to}
              to={to}
              className={({ isActive }) => `nav-item ${isActive ? 'active' : ''}`}
              title={label}
            >
              <Icon size={19} strokeWidth={1.75} />
              <span>
                {label}
                {hint && <small>{hint}</small>}
              </span>
              {to === '/import' && <ChevronRight size={14} className="nav-chevron" />}
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-bottom">
          <div className="local-note">
            <span className="local-note-icon">
              <ShieldCheck size={19} />
            </span>
            <strong>学习资料，留在本机</strong>
            <p>
              原始材料和学习记录
              <br />
              保存在你的电脑上
            </p>
          </div>
          <NavLink
            to="/settings"
            className={({ isActive }) => `nav-item settings-nav ${isActive ? 'active' : ''}`}
            title="设置"
          >
            <Settings2 size={19} />
            <span>设置</span>
          </NavLink>
          <div className="sidebar-footer">
            <span className="avatar">学</span>
            <span>
              <strong>本地学习空间</strong>
              <small>M1 · 应用基础</small>
            </span>
            <CircleHelp size={16} aria-label="应用基础版本" />
          </div>
        </div>
      </aside>
      <div className="workspace-shell">
        <header className="topbar">
          <div className="breadcrumb">
            <button className="icon-button" title="折叠侧栏" onClick={() => setCompact(!compact)}>
              <PanelLeft size={18} />
            </button>
            <span>学习工作台</span>
            <ChevronRight size={13} />
            <strong>{current}</strong>
          </div>
          <div className="topbar-actions">
            <span className="local-status">
              <i />
              本地空间
            </span>
            <Button variant="secondary" onClick={() => setDrawerOpen(true)}>
              <FileStack size={16} />
              任务
              {activeJobs.length > 0 && <span className="count-badge">{activeJobs.length}</span>}
            </Button>
          </div>
        </header>
        <main>
          {offline && (
            <div className="browser-banner">
              <WifiOff size={16} />
              <span>浏览器界面预览 · 文件、设置和任务需要在桌面应用中使用</span>
            </div>
          )}
          {data && (
            <div className={`execution-banner ${data.environment.policy.warning ? 'warning' : ''}`}>
              {data.environment.policy.warning ? (
                <ShieldAlert size={14} />
              ) : (
                <ShieldCheck size={14} />
              )}
              <strong>{executionLabels[data.environment.policy.effective_mode]}</strong>
              <span>{data.environment.policy.warning ?? '实际执行环境：Windows Sandbox'}</span>
              <Link to="/settings">
                权限设置
                <ChevronRight size={12} />
              </Link>
            </div>
          )}
          {bootstrap.isPending && !offline && (
            <div className="loading-panel">
              <LoaderCircle className="spin" />
              正在打开本地学习空间…
            </div>
          )}
          {bootstrap.error && (
            <div className="page">
              <Notice kind="error">
                本地空间未能打开：{bootstrap.error.message}
                <button className="text-button" onClick={() => void reload()}>
                  重试
                </button>
              </Notice>
            </div>
          )}
          {(offline || data) && (
            <Routes>
              <Route path="/import" element={<ImportPage data={data} onChanged={reload} />} />
              <Route
                path="/documents/:id"
                element={<DocumentPage data={data} onChanged={reload} />}
              />
              <Route path="/questions" element={<QuestionPage wrong={false} />} />
              <Route path="/wrong" element={<QuestionPage wrong />} />
              <Route path="/papers" element={<PapersPage data={data} />} />
              <Route path="/settings" element={<SettingsPage data={data} onChanged={reload} />} />
              <Route path="*" element={<Navigate to="/import" replace />} />
            </Routes>
          )}
        </main>
        <footer className="workspace-footer">
          <span>
            <Sparkles size={13} />
            每一次整理，都是进步的一部分
          </span>
          <span>
            <span className="footer-dot" />
            {data ? '本地数据库已连接' : offline ? '桌面界面预览' : '正在连接本地空间'}
          </span>
        </footer>
      </div>
      <TaskDrawer
        open={drawerOpen}
        onOpenChange={setDrawerOpen}
        jobs={jobs.data ?? data?.jobs ?? []}
        data={data}
        onChanged={reload}
      />
    </div>
  );
}

export type PageProps = { data: Bootstrap | undefined; onChanged: () => Promise<void> };
