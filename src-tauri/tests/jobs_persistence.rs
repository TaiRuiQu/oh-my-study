use oh_my_study_core::{
    execution::{Capability, CapabilityState, ExecutionMode, ExecutionPolicy},
    jobs::{DiagnosticKind, JobConfig, JobManager, JobState, PermissionChoice},
    storage::Storage,
};
use std::{
    path::PathBuf,
    sync::Arc,
    thread,
    time::{Duration, Instant},
};

fn policy() -> ExecutionPolicy {
    ExecutionPolicy::freeze(
        Some(ExecutionMode::RequestPermission),
        &Capability {
            state: CapabilityState::Unknown,
            reason: "test capability".into(),
        },
        false,
    )
}
fn wait(manager: &JobManager, id: &str, status: JobState) {
    let deadline = Instant::now() + Duration::from_secs(15);
    loop {
        let job = manager.get(id).unwrap();
        if job.state == status {
            return;
        }
        assert!(
            Instant::now() < deadline,
            "wanted {status:?}, got {:?}: {:?}",
            job.state,
            job.error
        );
        thread::sleep(Duration::from_millis(10));
    }
}
fn workspace() -> PathBuf {
    PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .to_path_buf()
}

#[tokio::test]
async fn sqlite_job_snapshots_and_events_survive_manager_restart() {
    let root = std::env::temp_dir().join(format!("study-jobs-db-{}", uuid::Uuid::new_v4()));
    let storage = Storage::open(root.join("data")).unwrap();
    let manager = JobManager::new(
        Arc::new(storage.clone()),
        JobConfig {
            private_runtime: root.join("missing-python"),
            results_root: root.join("results"),
        },
    )
    .unwrap();
    let job = manager.submit(DiagnosticKind::Storage, policy()).unwrap();
    wait(&manager, &job.id, JobState::Completed);
    let completed = manager.get(&job.id).unwrap();
    assert!(completed.message.contains("integrity_check=ok"));
    let events = storage.list_job_events(job.id.clone()).await.unwrap();
    assert_eq!(events.len(), 3);
    assert_eq!(events.last().unwrap()["payload"]["state"], "COMPLETED");
    assert_eq!(events.last().unwrap()["sequence"], completed.sequence);
    manager.shutdown().unwrap();
    drop(manager);

    let restarted = JobManager::new(
        Arc::new(storage),
        JobConfig {
            private_runtime: root.join("missing-python"),
            results_root: root.join("results"),
        },
    )
    .unwrap();
    assert_eq!(restarted.get(&job.id).unwrap().state, JobState::Completed);
}

#[test]
#[cfg(windows)]
#[ignore = "requires runtime/staging/python; run cargo test --test jobs_persistence -- --ignored"]
fn private_python_diagnostic_executes_only_after_actual_permission_approval() {
    let root = workspace()
        .join("tmp")
        .join(format!("真实任务 自检-{}", uuid::Uuid::new_v4()));
    let storage = Storage::open(root.join("data")).unwrap();
    let manager = JobManager::new(
        Arc::new(storage),
        JobConfig {
            private_runtime: workspace().join("runtime/staging/python"),
            results_root: root.join("results"),
        },
    )
    .unwrap();
    let job = manager
        .submit(DiagnosticKind::Permission, policy())
        .unwrap();
    wait(&manager, &job.id, JobState::WaitingPermission);
    let review = manager.get(&job.id).unwrap().pending_permission.unwrap();
    assert!(review.call.content.contains("sys.flags.isolated"));
    assert!(!root.join("results").join(&job.id).join("step-1").exists());
    manager
        .decide(&job.id, &review.digest, PermissionChoice::AllowTask)
        .unwrap();
    wait(&manager, &job.id, JobState::Completed);
    let outputs = root.join("results").join(job.id);
    assert!(
        std::fs::read_to_string(outputs.join("step-1/check-1.stdout.log"))
            .unwrap()
            .contains("isolated")
    );
    assert!(
        std::fs::read_to_string(outputs.join("step-2/check-2.stdout.log"))
            .unwrap()
            .contains("numpy")
    );
}
