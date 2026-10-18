//! Explicit opt-in tests requiring the assembled Windows private runtime.
//! Run with: cargo test --test host_execution -- --ignored
#![cfg(windows)]

use oh_my_study_core::execution::{
    host::{HostExecutor, HostStatus},
    permissions::{AuthorizedCall, ExecutionCall, PermissionBroker, PermissionState, ToolKind},
    Capability, CapabilityState, ExecutionMode, ExecutionPolicy,
};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::atomic::{AtomicBool, Ordering},
    thread,
    time::{Duration, Instant, SystemTime, UNIX_EPOCH},
};

fn authorize(
    root: &Path,
    content: &str,
    timeout_ms: u64,
    mode: ExecutionMode,
) -> (PermissionBroker, AuthorizedCall) {
    authorize_tool(root, content, timeout_ms, mode, ToolKind::Python, vec![])
}

fn authorize_tool(
    root: &Path,
    content: &str,
    timeout_ms: u64,
    mode: ExecutionMode,
    tool: ToolKind,
    arguments: Vec<String>,
) -> (PermissionBroker, AuthorizedCall) {
    let mut broker = PermissionBroker::default();
    broker
        .register_job(
            "host-test",
            ExecutionPolicy::freeze(
                Some(mode),
                &Capability {
                    state: CapabilityState::Ready,
                    reason: "Synthetic policy for a local executor test".into(),
                },
                false,
            ),
        )
        .unwrap();
    let call = broker
        .request(ExecutionCall {
            protocol_version: 1,
            job_id: "host-test".into(),
            call_id: "call".into(),
            tool,
            content: content.into(),
            arguments,
            working_directory: root.to_str().unwrap().into(),
            timeout_ms,
        })
        .unwrap()
        .unwrap();
    (broker, call)
}

fn python_string(path: &Path) -> String {
    serde_json::to_string(path.to_str().unwrap()).unwrap()
}

#[test]
#[ignore = "requires a built private CPython runtime; opt in using scripts/check-m0.ps1 -Runtime"]
fn private_host_execution_lifecycle() {
    let workspace = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .to_path_buf();
    let executor = HostExecutor::new(workspace.join("runtime/staging/python")).unwrap();
    let root = workspace.join("tmp").join(format!(
        "宿主 执行-{}-{}",
        std::process::id(),
        SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos()
    ));
    fs::create_dir_all(&root).unwrap();
    let running = AtomicBool::new(false);

    // A synthetic value, never a user's credential. The child must not inherit it.
    std::env::set_var("OMS_M0_SYNTHETIC_CREDENTIAL", "synthetic-must-not-inherit");
    let (_, call) = authorize(
        &root,
        "import os; assert 'OMS_M0_SYNTHETIC_CREDENTIAL' not in os.environ; print('中文数学 ✓')",
        5000,
        ExecutionMode::BypassPermission,
    );
    let result = executor.execute(call, &root.join("normal"), &running, 4096);
    std::env::remove_var("OMS_M0_SYNTHETIC_CREDENTIAL");
    let result = result.unwrap();
    assert_eq!(result.status, HostStatus::Completed, "{}", result.stderr);
    assert!(result.stdout.contains("中文数学 ✓"), "{}", result.stdout);

    let (_, call) = authorize_tool(
        &root,
        "Write-Output '中文命令'; $args | ConvertTo-Json -Compress",
        5000,
        ExecutionMode::BypassPermission,
        ToolKind::Shell,
        vec!["a b".into(), "$(throw 'must remain data'); x".into()],
    );
    let result = executor
        .execute(call, &root.join("shell"), &running, 4096)
        .unwrap();
    assert_eq!(result.status, HostStatus::Completed, "{}", result.stderr);
    assert!(result.stdout.contains("中文命令"));
    let arguments: Vec<String> = serde_json::from_str(
        result
            .stdout
            .lines()
            .find(|line| line.starts_with('['))
            .unwrap(),
    )
    .unwrap();
    assert_eq!(arguments, ["a b", "$(throw 'must remain data'); x"]);

    let (_, call) = authorize(
        &root,
        "raise RuntimeError('中文异常')",
        5000,
        ExecutionMode::BypassPermission,
    );
    let result = executor
        .execute(call, &root.join("failed"), &running, 4096)
        .unwrap();
    assert_eq!(result.status, HostStatus::Failed);
    assert!(result.stderr.contains("中文异常"));

    let (_, call) = authorize(
        &root,
        "import time; time.sleep(60)",
        250,
        ExecutionMode::BypassPermission,
    );
    let start = Instant::now();
    let result = executor
        .execute(call, &root.join("timeout"), &running, 4096)
        .unwrap();
    assert_eq!(result.status, HostStatus::TimedOut);
    assert!(start.elapsed() < Duration::from_secs(8));

    let (_, call) = authorize(
        &root,
        "print('x' * 4096)",
        5000,
        ExecutionMode::BypassPermission,
    );
    let result = executor
        .execute(call, &root.join("output-limit"), &running, 128)
        .unwrap();
    assert_eq!(result.status, HostStatus::OutputLimit);
    assert!(result.stdout.len() <= 128);

    let started = root.join("started.marker");
    let (_, call) = authorize(
        &root,
        &format!(
            "from pathlib import Path; import time; Path({}).touch(); time.sleep(60)",
            python_string(&started)
        ),
        5000,
        ExecutionMode::BypassPermission,
    );
    thread::scope(|scope| {
        scope.spawn(|| {
            let deadline = Instant::now() + Duration::from_secs(2);
            while !started.exists() && Instant::now() < deadline {
                thread::sleep(Duration::from_millis(20));
            }
            running.store(true, Ordering::Release);
        });
        let result = executor
            .execute(call, &root.join("cancelled"), &running, 4096)
            .unwrap();
        assert_eq!(result.status, HostStatus::Cancelled);
    });
    assert!(
        started.exists(),
        "test must cancel after the call actually started"
    );
    running.store(false, Ordering::Release);

    let late_write = root.join("late-child.txt");
    let child = format!(
        "from pathlib import Path; import time; time.sleep(0.6); Path({}).write_text('leaked')",
        python_string(&late_write)
    );
    let (_, call) = authorize(
        &root,
        &format!(
            "import subprocess,sys; subprocess.Popen([sys.executable,'-I','-c',{}]); print('root completed')",
            serde_json::to_string(&child).unwrap()
        ),
        5000,
        ExecutionMode::BypassPermission,
    );
    let result = executor
        .execute(call, &root.join("descendant"), &running, 4096)
        .unwrap();
    assert_eq!(result.status, HostStatus::Completed, "{}", result.stderr);
    thread::sleep(Duration::from_millis(900));
    assert!(
        !late_write.exists(),
        "owned descendant survived normal root exit"
    );

    let (mut broker, call) = authorize(
        &root,
        "raise Exception('must not run')",
        5000,
        ExecutionMode::BypassPermission,
    );
    broker.close("host-test", PermissionState::Cancelled);
    assert!(executor
        .execute(call, &root.join("revoked"), &running, 4096)
        .is_err());
    assert!(!root.join("revoked").exists());

    let (_, call) = authorize(
        &root,
        "raise Exception('must not replay')",
        5000,
        ExecutionMode::Sandbox,
    );
    assert!(executor
        .execute(call, &root.join("no-host-replay"), &running, 4096)
        .is_err());
    assert!(!root.join("no-host-replay").exists());
    eprintln!("Retained local host execution evidence: {}", root.display());
}
