//! Durable, serial local diagnostics. This is not the M2 model/agent pipeline.
use crate::domain::grading::GradingInput;
use crate::execution::{
    host::{HostExecutor, HostStatus},
    permissions::{
        AuthorizedCall, ExecutionCall, PermissionBroker, PermissionDecision, PermissionState,
        ToolKind,
    },
    ExecutionPolicy, ExecutorKind,
};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::{
    collections::{HashMap, VecDeque},
    path::{Path, PathBuf},
    sync::{
        atomic::{AtomicBool, Ordering},
        Arc, Condvar, Mutex,
    },
    thread::{self, JoinHandle},
    time::{SystemTime, UNIX_EPOCH},
};
use tokio::sync::broadcast;
use tokio_util::sync::CancellationToken;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "SCREAMING_SNAKE_CASE")]
pub enum JobState {
    Queued,
    Running,
    WaitingPermission,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}
impl JobState {
    pub fn is_terminal(self) -> bool {
        matches!(
            self,
            Self::Completed | Self::Failed | Self::Cancelled | Self::Interrupted
        )
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum DiagnosticKind {
    Storage,
    Permission,
    Grading,
    References,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum PermissionChoice {
    AllowOnce,
    AllowTask,
    Deny,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct PermissionReview {
    pub call: ExecutionCall,
    pub digest: String,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Job {
    pub id: String,
    pub kind: DiagnosticKind,
    pub state: JobState,
    pub created_at: u64,
    pub updated_at: u64,
    pub progress: f64,
    pub message: String,
    pub sequence: u64,
    pub execution_policy: ExecutionPolicy,
    pub pending_permission: Option<PermissionReview>,
    pub error: Option<String>,
    #[serde(default)]
    pub input: Option<GradingInput>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct JobEvent {
    pub id: String,
    pub job_id: String,
    pub sequence: u64,
    pub created_at: u64,
    pub kind: String,
    /// Full snapshot: channel consumers can recover after reconnect via list_jobs.
    pub payload: Value,
}

/// A commit must atomically store the snapshot AND event, or change neither.
/// This synchronous interface belongs on a background thread (including commands).
pub trait JobStore: Send + Sync + 'static {
    fn load_jobs(&self) -> Result<Vec<Job>, String>;
    fn commit(&self, job: &Job, event: &JobEvent) -> Result<(), String>;
    fn verify_integrity(&self) -> Result<String, String>;
}

pub struct JobConfig {
    pub private_runtime: PathBuf,
    pub results_root: PathBuf,
}

/// Injectable executor used to verify late-result and process cancellation races.
pub trait DiagnosticExecutor: Send + Sync + 'static {
    fn execute(
        &self,
        call: AuthorizedCall,
        results: &Path,
        cancelled: &AtomicBool,
    ) -> Result<String, String>;
}
struct PrivatePython {
    runtime: PathBuf,
}
impl DiagnosticExecutor for PrivatePython {
    fn execute(
        &self,
        call: AuthorizedCall,
        results: &Path,
        cancelled: &AtomicBool,
    ) -> Result<String, String> {
        let result = HostExecutor::new(self.runtime.clone())?
            .execute(call, results, cancelled, 1_048_576)?;
        if result.status != HostStatus::Completed {
            return Err(format!(
                "本地执行失败 ({:?}, exit {:?}): {}",
                result.status, result.exit_code, result.stderr
            ));
        }
        Ok(result.stdout)
    }
}

struct Control {
    cancelled: Arc<AtomicBool>,
    cancel_token: CancellationToken,
    authorized: Option<AuthorizedCall>,
}
struct State {
    jobs: HashMap<String, Job>,
    queue: VecDeque<String>,
    controls: HashMap<String, Control>,
    broker: PermissionBroker,
    stopped: bool,
    fatal_error: Option<String>,
}
struct Inner {
    state: Mutex<State>,
    wake: Condvar,
    store: Arc<dyn JobStore>,
    events: broadcast::Sender<JobEvent>,
    executor: Arc<dyn DiagnosticExecutor>,
    results_root: PathBuf,
    business: Option<Arc<dyn BusinessRunner>>,
}

pub trait BusinessRunner: Send + Sync + 'static {
    fn run(&self, job: &Job, context: &JobContext<'_>) -> Result<(), String>;
}
pub struct JobContext<'a> {
    inner: &'a Inner,
    id: String,
}
impl JobContext<'_> {
    pub fn cancellation(&self) -> Result<CancellationToken, String> {
        let state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
        state
            .controls
            .get(&self.id)
            .map(|c| c.cancel_token.clone())
            .ok_or("任务已取消".into())
    }
    pub fn progress(&self, progress: f64, message: String) -> Result<(), String> {
        if self
            .inner
            .update(&self.id, JobState::Running, progress, message, None)?
        {
            Ok(())
        } else {
            Err("任务已取消或终止".into())
        }
    }
    /// Serialize every result write against cancellation and terminal state commits.
    pub fn commit<T>(&self, operation: impl FnOnce() -> Result<T, String>) -> Result<T, String> {
        let state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
        healthy(&state)?;
        if state.jobs[&self.id].state.is_terminal() {
            return Err("任务已终止，拒绝迟到写入".into());
        }
        operation()
    }
    pub fn execute(
        &self,
        content: String,
        tool: ToolKind,
        call_id: String,
    ) -> Result<String, String> {
        let job = self.inner.state.lock().map_err(|_| "任务锁已损坏")?.jobs[&self.id].clone();
        if job.execution_policy.executor_kind == ExecutorKind::WindowsSandbox {
            return Err("Windows Sandbox 执行器尚未接入；不会在宿主重放".into());
        }
        let job_dir = self.inner.results_root.join(&self.id);
        create_checked_directory(&job_dir)?;
        let call = ExecutionCall {
            protocol_version: 1,
            job_id: self.id.clone(),
            call_id: call_id.clone(),
            tool,
            content,
            arguments: vec![],
            working_directory: job_dir.to_string_lossy().into(),
            timeout_ms: 60_000,
        };
        let Some((authorized, cancelled)) = self.inner.authorize(call)? else {
            return Err("执行已取消或拒绝".into());
        };
        self.inner
            .executor
            .execute(authorized, &job_dir.join(call_id), &cancelled)
    }
}

pub struct JobManager {
    inner: Arc<Inner>,
    worker: Mutex<Option<JoinHandle<()>>>,
}
fn now() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_millis() as u64
}

impl JobManager {
    pub fn with_runner(
        store: Arc<dyn JobStore>,
        config: JobConfig,
        runner: Arc<dyn BusinessRunner>,
    ) -> Result<Self, String> {
        let executor = Arc::new(PrivatePython {
            runtime: config.private_runtime.clone(),
        });
        Self::with_business(store, config, executor, Some(runner))
    }
    pub fn new(store: Arc<dyn JobStore>, config: JobConfig) -> Result<Self, String> {
        let executor = Arc::new(PrivatePython {
            runtime: config.private_runtime.clone(),
        });
        Self::with_executor(store, config, executor)
    }

    pub fn with_executor(
        store: Arc<dyn JobStore>,
        config: JobConfig,
        executor: Arc<dyn DiagnosticExecutor>,
    ) -> Result<Self, String> {
        Self::with_business(store, config, executor, None)
    }
    pub fn with_business(
        store: Arc<dyn JobStore>,
        config: JobConfig,
        executor: Arc<dyn DiagnosticExecutor>,
        business: Option<Arc<dyn BusinessRunner>>,
    ) -> Result<Self, String> {
        if !config.results_root.is_absolute() || !config.private_runtime.is_absolute() {
            return Err("任务运行时和输出目录必须是应用提供的绝对路径".into());
        }
        create_checked_directory(&config.results_root)?;
        let mut jobs = HashMap::new();
        // Never replay uncertain work or stale permission grants after a restart.
        for mut job in store.load_jobs()? {
            if !job.state.is_terminal() {
                job.state = JobState::Interrupted;
                job.pending_permission = None;
                job.error = Some("应用上次退出前任务未完成，未自动重放任何代码或命令".into());
                job.message = "任务因应用重启而中断".into();
                job.updated_at = now();
                job.sequence += 1;
                store.commit(&job, &event(&job, "interrupted"))?;
            }
            if jobs.insert(job.id.clone(), job).is_some() {
                return Err("重复的持久化任务 ID".into());
            }
        }
        let (events, _) = broadcast::channel(256);
        let inner = Arc::new(Inner {
            state: Mutex::new(State {
                jobs,
                queue: VecDeque::new(),
                controls: HashMap::new(),
                broker: PermissionBroker::default(),
                stopped: false,
                fatal_error: None,
            }),
            wake: Condvar::new(),
            store,
            events,
            executor,
            results_root: config.results_root,
            business,
        });
        let background = inner.clone();
        let worker = thread::Builder::new()
            .name("study-job-queue".into())
            .spawn(move || background.run())
            .map_err(|e| e.to_string())?;
        Ok(Self {
            inner,
            worker: Mutex::new(Some(worker)),
        })
    }

    pub fn submit(&self, kind: DiagnosticKind, policy: ExecutionPolicy) -> Result<Job, String> {
        self.submit_input(kind, policy, None)
    }
    pub fn submit_input(
        &self,
        kind: DiagnosticKind,
        policy: ExecutionPolicy,
        input: Option<GradingInput>,
    ) -> Result<Job, String> {
        if matches!(kind, DiagnosticKind::Grading | DiagnosticKind::References)
            && (input.is_none() || self.inner.business.is_none())
        {
            return Err("整卷任务未配置".into());
        }
        let mut state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
        healthy(&state)?;
        if let Some(input) = &input {
            if state.jobs.values().any(|job| {
                !job.state.is_terminal()
                    && job
                        .input
                        .as_ref()
                        .is_some_and(|old| old.document.id == input.document.id)
            }) {
                return Err("该材料已有未完成整卷任务".into());
            }
        }
        let id = loop {
            let id = uuid::Uuid::new_v4().to_string();
            if !state.jobs.contains_key(&id) {
                break id;
            }
        };
        let created_at = now();
        let job = Job {
            id: id.clone(),
            kind,
            state: JobState::Queued,
            created_at,
            updated_at: created_at,
            progress: 0.0,
            message: if input.is_some() {
                "整卷模型任务已入队".into()
            } else {
                "本地诊断已入队；不会调用模型供应商".into()
            },
            sequence: 1,
            execution_policy: policy.clone(),
            pending_permission: None,
            error: None,
            input,
        };
        self.inner.persist(&mut state, &job, "queued")?;
        state.broker.register_job(&id, policy)?;
        state.controls.insert(
            id.clone(),
            Control {
                cancelled: Arc::new(AtomicBool::new(false)),
                cancel_token: CancellationToken::new(),
                authorized: None,
            },
        );
        state.queue.push_back(id);
        self.inner.wake.notify_all();
        Ok(job)
    }

    pub fn list(&self) -> Result<Vec<Job>, String> {
        let state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
        if let Some(error) = &state.fatal_error {
            return Err(error.clone());
        }
        let mut jobs: Vec<_> = state.jobs.values().cloned().collect();
        jobs.sort_by(|a, b| {
            b.created_at
                .cmp(&a.created_at)
                .then_with(|| b.id.cmp(&a.id))
        });
        Ok(jobs)
    }
    pub fn get(&self, id: &str) -> Result<Job, String> {
        let state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
        if let Some(error) = &state.fatal_error {
            return Err(error.clone());
        }
        state
            .jobs
            .get(id)
            .cloned()
            .ok_or_else(|| "找不到任务".into())
    }
    pub fn subscribe(&self) -> broadcast::Receiver<JobEvent> {
        self.inner.events.subscribe()
    }

    pub fn cancel(&self, id: &str) -> Result<Job, String> {
        let mut state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
        let mut job = state.jobs.get(id).cloned().ok_or("找不到任务")?;
        if job.state.is_terminal() {
            return Ok(job);
        }
        // Revoke first: even a failed DB commit must never allow further execution.
        if let Some(control) = state.controls.get_mut(id) {
            control.cancelled.store(true, Ordering::Release);
            control.cancel_token.cancel();
            control.authorized = None;
        }
        state.broker.close(id, PermissionState::Cancelled);
        job.state = JobState::Cancelled;
        job.pending_permission = None;
        job.message = "任务已取消；正在结束本应用拥有的执行进程".into();
        bump(&mut job);
        let result = self
            .inner
            .persist(&mut state, &job, "cancelled")
            .map(|_| job);
        // A running executor retains its own cancellation Arc. Queued entries
        // may be skipped without ever visiting the worker cleanup path.
        state.controls.remove(id);
        self.inner.wake.notify_all();
        result
    }

    pub fn decide(&self, id: &str, digest: &str, choice: PermissionChoice) -> Result<Job, String> {
        let mut state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
        healthy(&state)?;
        let mut job = state.jobs.get(id).cloned().ok_or("找不到任务")?;
        if job.state != JobState::WaitingPermission {
            return Err("任务未等待权限，决定已失效".into());
        }
        let decision = match choice {
            PermissionChoice::AllowOnce => PermissionDecision::AllowOnce,
            PermissionChoice::AllowTask => PermissionDecision::AllowTask,
            PermissionChoice::Deny => PermissionDecision::Deny,
        };
        let authorized = state.broker.decide(id, digest, decision)?;
        job.pending_permission = None;
        let kind = if choice == PermissionChoice::Deny {
            job.state = JobState::Failed;
            job.error = Some("用户拒绝执行；任务已终止，不会改写调用后重试".into());
            job.message = "已拒绝代码执行".into();
            if let Some(control) = state.controls.get_mut(id) {
                control.cancelled.store(true, Ordering::Release);
            }
            "permission_denied"
        } else {
            job.state = JobState::Running;
            job.message = "权限已批准，继续本地诊断".into();
            state
                .controls
                .get_mut(id)
                .ok_or("任务控制器缺失")?
                .authorized = authorized;
            "permission_allowed"
        };
        bump(&mut job);
        self.inner.persist(&mut state, &job, kind)?;
        self.inner.wake.notify_all();
        Ok(job)
    }

    /// Call from the application ExitRequested hook. Cancels and joins the worker;
    /// it cannot release the global queue slot until its owned process has exited.
    pub fn shutdown(&self) -> Result<(), String> {
        let ids = {
            let mut state = self.inner.state.lock().map_err(|_| "任务锁已损坏")?;
            state.stopped = true;
            state
                .jobs
                .values()
                .filter(|j| !j.state.is_terminal())
                .map(|j| j.id.clone())
                .collect::<Vec<_>>()
        };
        let mut failure = None;
        for id in ids {
            if let Err(error) = self.cancel(&id) {
                failure = Some(error);
            }
        }
        self.inner.wake.notify_all();
        if let Some(worker) = self
            .worker
            .lock()
            .map_err(|_| "worker lock poisoned")?
            .take()
        {
            if worker.join().is_err() {
                failure = Some("任务线程异常退出".into());
            }
        }
        failure.map_or(Ok(()), Err)
    }
}

impl JobStore for crate::storage::Storage {
    fn load_jobs(&self) -> Result<Vec<Job>, String> {
        self.load_job_snapshots()?
            .into_iter()
            .map(|value| {
                serde_json::from_value(value).map_err(|e| format!("任务快照格式无效：{e}"))
            })
            .collect()
    }
    fn commit(&self, job: &Job, event: &JobEvent) -> Result<(), String> {
        self.save_job_event(
            serde_json::to_value(job).map_err(|e| e.to_string())?,
            serde_json::to_value(event).map_err(|e| e.to_string())?,
        )
    }
    fn verify_integrity(&self) -> Result<String, String> {
        crate::storage::Storage::verify_integrity(self)
    }
}
impl Drop for JobManager {
    fn drop(&mut self) {
        let _ = self.shutdown();
    }
}

fn healthy(state: &State) -> Result<(), String> {
    if let Some(error) = &state.fatal_error {
        return Err(error.clone());
    }
    if state.stopped {
        return Err("应用正在退出，不能启动或批准任务".into());
    }
    Ok(())
}
fn create_checked_directory(path: &Path) -> Result<(), String> {
    let mut missing = Vec::new();
    let mut current = path;
    while !current.exists() {
        missing.push(current.to_path_buf());
        current = current.parent().ok_or("输出目录没有存在的父目录")?;
    }
    crate::execution::bridge::reject_reparse_components(current)?;
    for directory in missing.into_iter().rev() {
        std::fs::create_dir(&directory).map_err(|e| e.to_string())?;
        crate::execution::bridge::reject_reparse_components(&directory)?;
    }
    crate::execution::bridge::reject_reparse_components(path)
}
fn bump(job: &mut Job) {
    job.sequence += 1;
    job.updated_at = now();
}
fn event(job: &Job, kind: &str) -> JobEvent {
    JobEvent {
        id: format!("{}-{}", job.id, job.sequence),
        job_id: job.id.clone(),
        sequence: job.sequence,
        created_at: job.updated_at,
        kind: kind.into(),
        payload: serde_json::to_value(job).expect("serializable job"),
    }
}

impl Inner {
    /// The state mutex is also the late-write barrier. Cancel and result commits
    /// serialize through it; terminal snapshots can never be overwritten by workers.
    fn persist(&self, state: &mut State, job: &Job, kind: &str) -> Result<(), String> {
        let event = event(job, kind);
        if let Err(error) = self.store.commit(job, &event) {
            let error = format!("任务事件持久化失败，执行队列已停止：{error}");
            state.fatal_error = Some(error.clone());
            state.stopped = true;
            for control in state.controls.values() {
                control.cancelled.store(true, Ordering::Release);
                control.cancel_token.cancel();
            }
            self.wake.notify_all();
            return Err(error);
        }
        state.jobs.insert(job.id.clone(), job.clone());
        let _ = self.events.send(event);
        Ok(())
    }
    fn update(
        &self,
        id: &str,
        status: JobState,
        progress: f64,
        message: String,
        error: Option<String>,
    ) -> Result<bool, String> {
        let mut state = self.state.lock().map_err(|_| "任务锁已损坏")?;
        let mut job = state.jobs.get(id).cloned().ok_or("找不到任务")?;
        if job.state.is_terminal() || state.fatal_error.is_some() {
            return Ok(false);
        }
        job.state = status;
        job.progress = progress;
        job.message = message;
        job.error = error;
        if status.is_terminal() {
            job.pending_permission = None;
            state.broker.close(id, PermissionState::Cancelled);
        }
        bump(&mut job);
        self.persist(
            &mut state,
            &job,
            match status {
                JobState::Completed => "completed",
                JobState::Failed => "failed",
                _ => "progress",
            },
        )?;
        Ok(true)
    }
    fn run(&self) {
        loop {
            let next = {
                let mut state = self.state.lock().expect("job state lock");
                loop {
                    if state.stopped {
                        return;
                    }
                    if let Some(id) = state.queue.pop_front() {
                        if state.jobs[&id].state == JobState::Queued {
                            break id;
                        }
                    } else {
                        state = self.wake.wait(state).expect("job wake lock");
                    }
                }
            };
            let result = self.execute_job(&next);
            if let Err(error) = result {
                let _ = self.update(
                    &next,
                    JobState::Failed,
                    0.0,
                    "本地诊断未通过".into(),
                    Some(error),
                );
            }
            let mut state = self.state.lock().expect("job state lock");
            state.controls.remove(&next);
        }
    }
    fn execute_job(&self, id: &str) -> Result<(), String> {
        if !self.update(id, JobState::Running, 0.0, "任务开始".into(), None)? {
            return Ok(());
        }
        let job = self.state.lock().map_err(|_| "任务锁已损坏")?.jobs[id].clone();
        match job.kind {
            DiagnosticKind::Grading | DiagnosticKind::References => {
                self.business.as_ref().ok_or("整卷任务未配置")?.run(
                    &job,
                    &JobContext {
                        inner: self,
                        id: id.into(),
                    },
                )?;
                self.update(
                    id,
                    JobState::Completed,
                    100.0,
                    "整卷处理完成，覆盖检查与结果已保存".into(),
                    None,
                )?;
            }
            DiagnosticKind::Storage => {
                let report = self.store.verify_integrity()?;
                // Read after the RUNNING commit to verify read/write visibility.
                let persisted = self
                    .store
                    .load_jobs()?
                    .into_iter()
                    .find(|j| j.id == id)
                    .ok_or("诊断任务写入后无法读回")?;
                if persisted.sequence < job.sequence {
                    return Err("任务快照读取滞后".into());
                }
                self.update(
                    id,
                    JobState::Completed,
                    100.0,
                    format!("SQLite 自检通过：{report}；任务和事件已持久化"),
                    None,
                )?;
            }
            DiagnosticKind::Permission => {
                if job.execution_policy.executor_kind == ExecutorKind::WindowsSandbox {
                    return Err(
                        "M1 尚未接入 Windows Sandbox 执行器，未执行代码，也未在宿主重放".into(),
                    );
                }
                let job_dir = self.results_root.join(id);
                crate::execution::bridge::reject_reparse_components(&self.results_root)?;
                std::fs::create_dir(&job_dir).map_err(|e| e.to_string())?;
                crate::execution::bridge::reject_reparse_components(&job_dir)?;
                for (index, content) in [PYTHON_RUNTIME_CHECK, PYTHON_PACKAGE_CHECK]
                    .iter()
                    .enumerate()
                {
                    let call = ExecutionCall {
                        protocol_version: 1,
                        job_id: id.into(),
                        call_id: format!("check-{}", index + 1),
                        tool: ToolKind::Python,
                        content: (*content).into(),
                        arguments: vec![],
                        working_directory: job_dir.to_string_lossy().into(),
                        timeout_ms: 30_000,
                    };
                    let Some((authorized, cancelled)) = self.authorize(call)? else {
                        return Ok(());
                    };
                    let output = self.executor.execute(
                        authorized,
                        &job_dir.join(format!("step-{}", index + 1)),
                        &cancelled,
                    )?;
                    if !self.update(
                        id,
                        JobState::Running,
                        (index + 1) as f64 * 45.0,
                        format!("私有 Python 自检 {}/2：{}", index + 1, output.trim()),
                        None,
                    )? {
                        return Ok(());
                    }
                }
                self.update(
                    id,
                    JobState::Completed,
                    100.0,
                    "私有 Python 与 NumPy / SymPy / Pillow / Matplotlib 自检通过".into(),
                    None,
                )?;
            }
        }
        Ok(())
    }
    fn authorize(
        &self,
        call: ExecutionCall,
    ) -> Result<Option<(AuthorizedCall, Arc<AtomicBool>)>, String> {
        let mut state = self.state.lock().map_err(|_| "任务锁已损坏")?;
        let id = call.job_id.clone();
        if state.stopped || state.jobs[&id].state.is_terminal() {
            return Ok(None);
        }
        let cancelled = state.controls[&id].cancelled.clone();
        if let Some(authorized) = state.broker.request(call.clone())? {
            return Ok(Some((authorized, cancelled)));
        }
        let mut job = state.jobs[&id].clone();
        job.state = JobState::WaitingPermission;
        job.pending_permission = Some(PermissionReview {
            digest: call.digest(),
            call,
        });
        job.message = "等待审阅完整代码、参数、工作目录和超时".into();
        bump(&mut job);
        self.persist(&mut state, &job, "permission_requested")?;
        loop {
            if state.stopped || state.jobs[&id].state.is_terminal() {
                return Ok(None);
            }
            if let Some(authorized) = state
                .controls
                .get_mut(&id)
                .and_then(|c| c.authorized.take())
            {
                return Ok(Some((authorized, cancelled)));
            }
            state = self.wake.wait(state).map_err(|_| "任务等待锁已损坏")?;
        }
    }
}

const PYTHON_RUNTIME_CHECK: &str = "import json, sys\nassert sys.version_info[:2] == (3, 13), sys.version\nassert sys.flags.isolated == 1\nprint(json.dumps({'python': sys.version.split()[0], 'isolated': bool(sys.flags.isolated)}, ensure_ascii=False))\n";
const PYTHON_PACKAGE_CHECK: &str = "import json\nimport numpy as np\nimport sympy as sp\nfrom PIL import Image\nimport matplotlib\nassert int(np.arange(10).sum()) == 45\nx = sp.Symbol('x')\nassert sp.diff(x**3, x) == 3*x**2\nassert Image.new('RGB', (2, 2)).size == (2, 2)\nassert matplotlib.get_backend().lower() == 'agg'\nprint(json.dumps({'numpy': np.__version__, 'sympy': sp.__version__, 'matplotlib': matplotlib.__version__, 'pillow': Image.__version__}, ensure_ascii=False))\n";

#[cfg(test)]
mod tests;
