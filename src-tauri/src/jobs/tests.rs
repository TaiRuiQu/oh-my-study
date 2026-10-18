use super::*;
use crate::execution::{Capability, CapabilityState, ExecutionMode};
use std::time::{Duration, Instant};

#[derive(Default)]
struct MemoryStore {
    data: Mutex<(HashMap<String, Job>, Vec<JobEvent>)>,
    fail_next: AtomicBool,
}
impl JobStore for MemoryStore {
    fn load_jobs(&self) -> Result<Vec<Job>, String> {
        Ok(self.data.lock().unwrap().0.values().cloned().collect())
    }
    fn commit(&self, job: &Job, event: &JobEvent) -> Result<(), String> {
        if self.fail_next.swap(false, Ordering::AcqRel) {
            return Err("injected atomic commit failure".into());
        }
        let mut data = self.data.lock().unwrap();
        if let Some(old) = data.0.get(&job.id) {
            assert_eq!(old.sequence + 1, job.sequence);
        }
        assert_eq!(event.sequence, job.sequence);
        assert_eq!(
            event.payload["state"],
            serde_json::to_value(job.state).unwrap()
        );
        data.0.insert(job.id.clone(), job.clone());
        data.1.push(event.clone());
        Ok(())
    }
    fn verify_integrity(&self) -> Result<String, String> {
        Ok("test store integrity".into())
    }
}

#[derive(Default)]
struct ControlledExecutor {
    calls: Mutex<Vec<(String, String)>>,
    gate: Mutex<bool>,
    wake: Condvar,
    block_first: AtomicBool,
}
impl ControlledExecutor {
    fn blocked() -> Arc<Self> {
        Arc::new(Self {
            block_first: AtomicBool::new(true),
            ..Self::default()
        })
    }
    fn release(&self) {
        *self.gate.lock().unwrap() = true;
        self.wake.notify_all();
    }
    fn count(&self) -> usize {
        self.calls.lock().unwrap().len()
    }
}
impl DiagnosticExecutor for ControlledExecutor {
    fn execute(
        &self,
        call: AuthorizedCall,
        _results: &Path,
        _cancelled: &AtomicBool,
    ) -> Result<String, String> {
        assert!(call.is_live());
        self.calls
            .lock()
            .unwrap()
            .push((call.call().job_id.clone(), call.call().call_id.clone()));
        if self.block_first.swap(false, Ordering::AcqRel) {
            let mut gate = self.gate.lock().unwrap();
            // Deliberately ignore cancellation to emulate a late provider/process
            // response. A bounded wait prevents a failed assertion hanging Drop.
            while !*gate {
                let (next, timeout) = self
                    .wake
                    .wait_timeout(gate, Duration::from_secs(3))
                    .unwrap();
                gate = next;
                if timeout.timed_out() {
                    break;
                }
            }
        }
        Ok("actual executor test result".into())
    }
}

fn config() -> JobConfig {
    let root = std::env::temp_dir().join(format!("study-jobs-{}", uuid::Uuid::new_v4()));
    JobConfig {
        private_runtime: root.join("private-runtime"),
        results_root: root.join("results"),
    }
}
fn policy(mode: ExecutionMode) -> ExecutionPolicy {
    ExecutionPolicy::freeze(
        Some(mode),
        &Capability {
            state: CapabilityState::Unknown,
            reason: "尚未验证 Sandbox 能力".into(),
        },
        false,
    )
}
fn wait_for(manager: &JobManager, id: &str, expected: JobState) -> Job {
    let deadline = Instant::now() + Duration::from_secs(6);
    loop {
        let job = manager.get(id).unwrap();
        if job.state == expected {
            return job;
        }
        assert!(
            Instant::now() < deadline,
            "wanted {expected:?}, got {:?}: {:?}",
            job.state,
            job.error
        );
        thread::sleep(Duration::from_millis(5));
    }
}
fn wait_calls(executor: &ControlledExecutor, count: usize) {
    let deadline = Instant::now() + Duration::from_secs(3);
    while executor.count() < count {
        assert!(Instant::now() < deadline, "executor did not start");
        thread::sleep(Duration::from_millis(5));
    }
}

#[test]
fn cancel_is_a_late_write_barrier_and_global_slot_waits_for_executor_exit() {
    let store = Arc::new(MemoryStore::default());
    let executor = ControlledExecutor::blocked();
    let manager = JobManager::with_executor(store.clone(), config(), executor.clone()).unwrap();
    let first = manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::BypassPermission),
        )
        .unwrap();
    wait_calls(&executor, 1);
    let second = manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::BypassPermission),
        )
        .unwrap();
    let cancelled = manager.cancel(&first.id).unwrap();
    assert_eq!(cancelled.state, JobState::Cancelled);
    assert_eq!(manager.get(&second.id).unwrap().state, JobState::Queued);
    assert_eq!(
        executor.count(),
        1,
        "second job must not overlap a cancelling executor"
    );
    executor.release();
    wait_for(&manager, &second.id, JobState::Completed);
    let final_first = manager.get(&first.id).unwrap();
    assert_eq!(final_first.state, JobState::Cancelled);
    assert_eq!(final_first.sequence, cancelled.sequence);
    assert_eq!(
        executor.count(),
        3,
        "cancelled first job must not run its second call"
    );
    let events = &store.data.lock().unwrap().1;
    assert!(!events
        .iter()
        .any(|event| event.job_id == first.id && event.sequence > cancelled.sequence));
}

#[test]
fn reviewed_digest_is_required_allow_once_waits_again_and_denial_stops_job() {
    let store = Arc::new(MemoryStore::default());
    let executor = Arc::new(ControlledExecutor::default());
    let manager = JobManager::with_executor(store, config(), executor.clone()).unwrap();
    let job = manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::RequestPermission),
        )
        .unwrap();
    let first = wait_for(&manager, &job.id, JobState::WaitingPermission)
        .pending_permission
        .unwrap();
    assert_eq!(executor.count(), 0);
    assert!(first.call.content.contains("sys.flags.isolated"));
    assert_eq!(first.digest, first.call.digest());
    assert!(manager
        .decide(&job.id, "altered", PermissionChoice::AllowOnce)
        .is_err());
    manager
        .decide(&job.id, &first.digest, PermissionChoice::AllowOnce)
        .unwrap();
    let second = wait_for(&manager, &job.id, JobState::WaitingPermission)
        .pending_permission
        .unwrap();
    assert_ne!(first.digest, second.digest);
    assert_eq!(executor.count(), 1);
    assert!(manager
        .decide(&job.id, &first.digest, PermissionChoice::AllowOnce)
        .is_err());
    let denied = manager
        .decide(&job.id, &second.digest, PermissionChoice::Deny)
        .unwrap();
    assert_eq!(denied.state, JobState::Failed);
    assert!(denied.pending_permission.is_none());
    assert!(manager
        .decide(&job.id, &second.digest, PermissionChoice::AllowTask)
        .is_err());
    assert_eq!(executor.count(), 1);
}

#[test]
fn task_grant_covers_only_one_job_and_freezes_execution_environment() {
    let executor = Arc::new(ControlledExecutor::default());
    let manager =
        JobManager::with_executor(Arc::new(MemoryStore::default()), config(), executor.clone())
            .unwrap();
    let first = manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::RequestPermission),
        )
        .unwrap();
    let pending = wait_for(&manager, &first.id, JobState::WaitingPermission)
        .pending_permission
        .unwrap();
    manager
        .decide(&first.id, &pending.digest, PermissionChoice::AllowTask)
        .unwrap();
    let done = wait_for(&manager, &first.id, JobState::Completed);
    assert_eq!(
        done.execution_policy.effective_mode,
        ExecutionMode::RequestPermission
    );
    assert_eq!(executor.count(), 2);
    let second = manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::RequestPermission),
        )
        .unwrap();
    wait_for(&manager, &second.id, JobState::WaitingPermission);
    assert_eq!(executor.count(), 2);
    manager.cancel(&second.id).unwrap();
    let fallback = policy(ExecutionMode::Sandbox);
    assert_eq!(fallback.effective_mode, ExecutionMode::BypassPermission);
    assert!(fallback.warning.as_deref().unwrap().contains("未隔离"));
}

#[test]
fn restarting_interrupts_all_unfinished_states_without_resuming_permissions() {
    let store = Arc::new(MemoryStore::default());
    {
        let mut data = store.data.lock().unwrap();
        for (index, state) in [
            JobState::Queued,
            JobState::Running,
            JobState::WaitingPermission,
            JobState::Completed,
        ]
        .into_iter()
        .enumerate()
        {
            let job = Job {
                input: None,
                id: uuid::Uuid::new_v4().to_string(),
                kind: DiagnosticKind::Permission,
                state,
                created_at: index as u64,
                updated_at: 0,
                progress: 10.0,
                message: "previous process".into(),
                sequence: 5,
                execution_policy: policy(ExecutionMode::RequestPermission),
                pending_permission: None,
                error: None,
            };
            data.0.insert(job.id.clone(), job);
        }
    }
    let executor = Arc::new(ControlledExecutor::default());
    let manager = JobManager::with_executor(store.clone(), config(), executor.clone()).unwrap();
    let jobs = manager.list().unwrap();
    assert_eq!(
        jobs.iter()
            .filter(|j| j.state == JobState::Interrupted)
            .count(),
        3
    );
    assert_eq!(
        jobs.iter()
            .filter(|j| j.state == JobState::Completed)
            .count(),
        1
    );
    assert!(jobs
        .iter()
        .filter(|j| j.state == JobState::Interrupted)
        .all(|j| j.sequence == 6 && j.pending_permission.is_none()));
    assert_eq!(executor.count(), 0);
    assert_eq!(store.data.lock().unwrap().1.len(), 3);
}

#[test]
fn shutdown_cancels_waiting_and_queued_jobs_and_revokes_approvals() {
    let executor = Arc::new(ControlledExecutor::default());
    let manager =
        JobManager::with_executor(Arc::new(MemoryStore::default()), config(), executor.clone())
            .unwrap();
    let first = manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::RequestPermission),
        )
        .unwrap();
    let pending = wait_for(&manager, &first.id, JobState::WaitingPermission)
        .pending_permission
        .unwrap();
    let second = manager
        .submit(
            DiagnosticKind::Storage,
            policy(ExecutionMode::RequestPermission),
        )
        .unwrap();
    manager.shutdown().unwrap();
    assert_eq!(manager.get(&first.id).unwrap().state, JobState::Cancelled);
    assert_eq!(manager.get(&second.id).unwrap().state, JobState::Cancelled);
    assert!(manager
        .decide(&first.id, &pending.digest, PermissionChoice::AllowOnce)
        .is_err());
    assert!(manager
        .submit(
            DiagnosticKind::Storage,
            policy(ExecutionMode::RequestPermission)
        )
        .is_err());
    assert_eq!(executor.count(), 0);
}

#[test]
fn channel_events_are_persisted_before_broadcast_and_storage_check_reads_own_write() {
    let store = Arc::new(MemoryStore::default());
    let manager = JobManager::with_executor(
        store.clone(),
        config(),
        Arc::new(ControlledExecutor::default()),
    )
    .unwrap();
    let mut channel = manager.subscribe();
    let job = manager
        .submit(
            DiagnosticKind::Storage,
            policy(ExecutionMode::RequestPermission),
        )
        .unwrap();
    wait_for(&manager, &job.id, JobState::Completed);
    let mut sequences = Vec::new();
    while let Ok(event) = channel.try_recv() {
        assert!(store
            .data
            .lock()
            .unwrap()
            .1
            .iter()
            .any(|saved| saved.id == event.id));
        sequences.push(event.sequence);
    }
    assert_eq!(sequences, vec![1, 2, 3]);
}

#[test]
fn failed_persistence_stops_queue_before_any_unaudited_execution() {
    let store = Arc::new(MemoryStore::default());
    let executor = Arc::new(ControlledExecutor::default());
    let manager = JobManager::with_executor(store.clone(), config(), executor.clone()).unwrap();
    store.fail_next.store(true, Ordering::Release);
    assert!(manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::BypassPermission)
        )
        .is_err());
    assert!(manager.list().is_err());
    assert_eq!(executor.count(), 0);
    assert!(store.data.lock().unwrap().0.is_empty());
}

#[test]
fn sandbox_executor_is_not_faked_by_host_fallback() {
    let executor = Arc::new(ControlledExecutor::default());
    let manager =
        JobManager::with_executor(Arc::new(MemoryStore::default()), config(), executor.clone())
            .unwrap();
    let policy = ExecutionPolicy::freeze(
        Some(ExecutionMode::Sandbox),
        &Capability {
            state: CapabilityState::Ready,
            reason: "test capability".into(),
        },
        false,
    );
    let job = manager.submit(DiagnosticKind::Permission, policy).unwrap();
    let failed = wait_for(&manager, &job.id, JobState::Failed);
    assert!(failed.error.unwrap().contains("未在宿主重放"));
    assert_eq!(executor.count(), 0);
}

#[test]
fn missing_private_runtime_reports_failure_instead_of_completing() {
    let manager = JobManager::new(Arc::new(MemoryStore::default()), config()).unwrap();
    let job = manager
        .submit(
            DiagnosticKind::Permission,
            policy(ExecutionMode::BypassPermission),
        )
        .unwrap();
    let failed = wait_for(&manager, &job.id, JobState::Failed);
    assert!(failed.error.is_some());
    assert!(failed.progress < 100.0);
}
