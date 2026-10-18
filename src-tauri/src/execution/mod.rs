//! M0 execution policy and prototypes. No sandbox session is started implicitly.
pub mod bridge;
pub mod host;
pub mod permissions;
pub mod sandbox;

use serde::{Deserialize, Serialize};

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ExecutionMode {
    RequestPermission,
    Sandbox,
    BypassPermission,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum CapabilityState {
    Ready,
    Unsupported,
    NeedsEnable,
    NeedsReboot,
    VirtualizationUnavailable,
    PolicyBlocked,
    Busy,
    Unknown,
    StartFailed,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Capability {
    pub state: CapabilityState,
    pub reason: String,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ExecutorKind {
    Host,
    WindowsSandbox,
}

/// Freeze this snapshot when a job starts; settings changes apply to new jobs.
#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct ExecutionPolicy {
    pub preferred_mode: ExecutionMode,
    pub effective_mode: ExecutionMode,
    pub executor_kind: ExecutorKind,
    pub fallback_reason: Option<String>,
    pub warning: Option<String>,
    pub sandbox_networking: bool,
}

impl ExecutionPolicy {
    pub fn freeze(
        preferred: Option<ExecutionMode>,
        capability: &Capability,
        networking: bool,
    ) -> Self {
        let preferred = preferred.unwrap_or(ExecutionMode::Sandbox);
        let fallback =
            preferred == ExecutionMode::Sandbox && capability.state != CapabilityState::Ready;
        let effective = if fallback {
            ExecutionMode::BypassPermission
        } else {
            preferred
        };
        Self {
            preferred_mode: preferred, effective_mode: effective,
            executor_kind: if effective == ExecutionMode::Sandbox { ExecutorKind::WindowsSandbox } else { ExecutorKind::Host },
            fallback_reason: fallback.then(|| capability.reason.clone()),
            warning: fallback.then(|| format!("{}。本次代码与命令将在本机使用当前账户权限执行，可访问该账户的文件和网络。本机执行／未隔离。", capability.reason)),
            sandbox_networking: networking,
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn fallback_is_explicit_for_every_unavailable_state() {
        for state in [
            CapabilityState::Unsupported,
            CapabilityState::NeedsEnable,
            CapabilityState::NeedsReboot,
            CapabilityState::VirtualizationUnavailable,
            CapabilityState::PolicyBlocked,
            CapabilityState::Busy,
            CapabilityState::Unknown,
            CapabilityState::StartFailed,
        ] {
            let capability = Capability {
                state,
                reason: format!("{state:?}"),
            };
            let policy = ExecutionPolicy::freeze(None, &capability, false);
            assert_eq!(policy.effective_mode, ExecutionMode::BypassPermission);
            assert_eq!(policy.executor_kind, ExecutorKind::Host);
            assert!(policy.warning.unwrap().contains("未隔离"));
            assert_eq!(
                ExecutionPolicy::freeze(Some(ExecutionMode::RequestPermission), &capability, false)
                    .effective_mode,
                ExecutionMode::RequestPermission
            );
        }
    }
    #[test]
    fn ready_defaults_to_sandbox() {
        assert_eq!(
            ExecutionPolicy::freeze(
                None,
                &Capability {
                    state: CapabilityState::Ready,
                    reason: String::new()
                },
                false
            )
            .executor_kind,
            ExecutorKind::WindowsSandbox
        );
    }
}
