use super::{ExecutionMode, ExecutionPolicy};
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::collections::{HashMap, HashSet};
use std::sync::{
    atomic::{AtomicBool, Ordering},
    Arc,
};

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ToolKind {
    Python,
    Shell,
}

/// All reviewable fields participate in the permission digest, including timeout.
#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(deny_unknown_fields)]
pub struct ExecutionCall {
    pub protocol_version: u32,
    pub job_id: String,
    pub call_id: String,
    pub tool: ToolKind,
    pub content: String,
    pub arguments: Vec<String>,
    pub working_directory: String,
    pub timeout_ms: u64,
}
impl ExecutionCall {
    pub fn digest(&self) -> String {
        format!(
            "{:x}",
            Sha256::digest(serde_json::to_vec(self).expect("serializable call"))
        )
    }
    pub fn validate(&self) -> Result<(), String> {
        if self.protocol_version != 1 || !valid_id(&self.job_id) || !valid_id(&self.call_id) {
            return Err("invalid protocol version or call identity".into());
        }
        if self.timeout_ms == 0 || self.timeout_ms > 3_600_000 || self.content.len() > 1_048_576 {
            return Err("invalid timeout or oversized code".into());
        }
        if self.arguments.len() > 128
            || self.arguments.iter().map(String::len).sum::<usize>() > 65_536
        {
            return Err("argument count or size limit exceeded".into());
        }
        if !std::path::Path::new(&self.working_directory).is_absolute() {
            return Err("working directory must be absolute".into());
        }
        Ok(())
    }
}
pub fn valid_id(s: &str) -> bool {
    !s.is_empty()
        && s.len() <= 128
        && s.bytes()
            .all(|c| c.is_ascii_alphanumeric() || c == b'-' || c == b'_')
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PermissionDecision {
    AllowOnce,
    AllowTask,
    Deny,
    Cancel,
}
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum PermissionState {
    WaitingPermission,
    Allowed,
    Denied,
    Cancelled,
}

/// Constructed only by the broker after approval; execution consumes this token.
#[derive(Debug)]
pub struct AuthorizedCall {
    call: ExecutionCall,
    pub(crate) mode: ExecutionMode,
    live: Arc<AtomicBool>,
}
impl AuthorizedCall {
    pub fn call(&self) -> &ExecutionCall {
        &self.call
    }
    pub fn is_live(&self) -> bool {
        self.live.load(Ordering::Acquire)
    }
}

#[derive(Default)]
pub struct PermissionBroker {
    jobs: HashMap<String, ExecutionPolicy>,
    task_grants: HashSet<String>,
    waiting: HashMap<String, ExecutionCall>,
    stopped: HashMap<String, PermissionState>,
    used_calls: HashSet<(String, String)>,
    live: HashMap<String, Arc<AtomicBool>>,
}

impl PermissionBroker {
    pub fn register_job(&mut self, job: &str, policy: ExecutionPolicy) -> Result<(), String> {
        if !valid_id(job) || self.jobs.contains_key(job) || self.stopped.contains_key(job) {
            return Err("invalid or previously registered job".into());
        }
        self.live
            .insert(job.into(), Arc::new(AtomicBool::new(true)));
        self.jobs.insert(job.into(), policy);
        Ok(())
    }
    /// One pending call pauses the entire job; callers must not keep requesting model work.
    pub fn request(&mut self, call: ExecutionCall) -> Result<Option<AuthorizedCall>, String> {
        call.validate()?;
        if self.stopped.contains_key(&call.job_id) {
            return Err("job denied, cancelled or completed".into());
        }
        if self.waiting.contains_key(&call.job_id) {
            return Err("WAITING_PERMISSION: pause job".into());
        }
        let policy = self.jobs.get(&call.job_id).ok_or("unregistered job")?;
        if !self
            .used_calls
            .insert((call.job_id.clone(), call.call_id.clone()))
        {
            return Err("call id already submitted; do not replay".into());
        }
        if policy.effective_mode != ExecutionMode::RequestPermission
            || self.task_grants.contains(&call.job_id)
        {
            let live = self.live[&call.job_id].clone();
            return Ok(Some(AuthorizedCall {
                call,
                mode: policy.effective_mode,
                live,
            }));
        }
        self.waiting.insert(call.job_id.clone(), call);
        Ok(None)
    }
    pub fn pending(&self, job: &str) -> Option<&ExecutionCall> {
        self.waiting.get(job)
    }
    pub fn decide(
        &mut self,
        job: &str,
        expected_digest: &str,
        decision: PermissionDecision,
    ) -> Result<Option<AuthorizedCall>, String> {
        let call = self.waiting.get(job).ok_or("no pending request")?;
        if call.digest() != expected_digest {
            return Err("reviewed content changed".into());
        }
        let call = self.waiting.remove(job).expect("checked pending");
        match decision {
            PermissionDecision::AllowOnce | PermissionDecision::AllowTask => {
                if decision == PermissionDecision::AllowTask {
                    self.task_grants.insert(job.into());
                }
                let live = self.live[job].clone();
                Ok(Some(AuthorizedCall {
                    call,
                    mode: ExecutionMode::RequestPermission,
                    live,
                }))
            }
            PermissionDecision::Deny => {
                self.close(job, PermissionState::Denied);
                Ok(None)
            }
            PermissionDecision::Cancel => {
                self.close(job, PermissionState::Cancelled);
                Ok(None)
            }
        }
    }
    pub fn close(&mut self, job: &str, state: PermissionState) {
        self.waiting.remove(job);
        self.task_grants.remove(job);
        self.jobs.remove(job);
        if let Some(live) = self.live.remove(job) {
            live.store(false, Ordering::Release);
        }
        self.stopped.insert(job.into(), state);
    }
    pub fn state(&self, job: &str) -> Option<PermissionState> {
        self.stopped.get(job).copied().or_else(|| {
            self.waiting
                .contains_key(job)
                .then_some(PermissionState::WaitingPermission)
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::execution::{Capability, CapabilityState};
    fn call(job: &str, id: &str) -> ExecutionCall {
        ExecutionCall {
            protocol_version: 1,
            job_id: job.into(),
            call_id: id.into(),
            tool: ToolKind::Python,
            content: "print('你好')".into(),
            arguments: vec![],
            working_directory: std::env::temp_dir().to_string_lossy().into(),
            timeout_ms: 1000,
        }
    }
    fn broker() -> PermissionBroker {
        let mut b = PermissionBroker::default();
        for job in ["one", "two"] {
            b.register_job(
                job,
                ExecutionPolicy::freeze(
                    Some(ExecutionMode::RequestPermission),
                    &Capability {
                        state: CapabilityState::Unknown,
                        reason: "not tested".into(),
                    },
                    false,
                ),
            )
            .unwrap();
        }
        b
    }
    #[test]
    fn once_is_bound_and_consumed_and_cannot_cross_jobs() {
        let mut b = broker();
        let c = call("one", "a");
        let digest = c.digest();
        assert!(b.request(c.clone()).unwrap().is_none());
        assert!(b
            .decide("one", "changed", PermissionDecision::AllowOnce)
            .is_err());
        assert_eq!(
            b.decide("one", &digest, PermissionDecision::AllowOnce)
                .unwrap()
                .unwrap()
                .call(),
            &c
        );
        assert!(b
            .decide("one", &digest, PermissionDecision::AllowOnce)
            .is_err());
        assert!(b.request(c).is_err());
        assert!(b.request(call("one", "b")).unwrap().is_none());
        assert!(b.request(call("two", "a")).unwrap().is_none());
    }
    #[test]
    fn task_grant_ends_and_denial_cannot_be_reworded() {
        let mut b = broker();
        let c = call("one", "a");
        let digest = c.digest();
        b.request(c).unwrap();
        b.decide("one", &digest, PermissionDecision::AllowTask)
            .unwrap();
        assert!(b.request(call("one", "b")).unwrap().is_some());
        b.close("one", PermissionState::Cancelled);
        assert!(b.request(call("one", "c")).is_err());
        let c = call("two", "a");
        let digest = c.digest();
        b.request(c).unwrap();
        b.decide("two", &digest, PermissionDecision::Deny).unwrap();
        assert!(b.request(call("two", "reworded")).is_err());
    }
    #[test]
    fn every_review_field_changes_digest() {
        let c = call("one", "a");
        let mut changes = vec![c.clone(); 6];
        changes[0].job_id = "two".into();
        changes[1].tool = ToolKind::Shell;
        changes[2].content.push(' ');
        changes[3].arguments.push("a".into());
        changes[4].working_directory.push('x');
        changes[5].timeout_ms += 1;
        for changed in changes {
            assert_ne!(c.digest(), changed.digest());
        }
    }
    #[test]
    fn cancel_revokes_issued_but_unexecuted_token() {
        let mut b = broker();
        let c = call("one", "a");
        let digest = c.digest();
        b.request(c).unwrap();
        let token = b
            .decide("one", &digest, PermissionDecision::AllowOnce)
            .unwrap()
            .unwrap();
        assert!(token.is_live());
        b.close("one", PermissionState::Cancelled);
        assert!(!token.is_live());
    }
}
