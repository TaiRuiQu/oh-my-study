use super::permissions::valid_id;
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
};

fn xml(value: &str) -> String {
    value
        .replace('&', "&amp;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
        .replace('"', "&quot;")
        .replace('\'', "&apos;")
}

#[derive(Debug)]
pub struct SandboxMappings {
    runtime: PathBuf,
    inputs: PathBuf,
    requests: PathBuf,
    results: PathBuf,
}

fn checked_directory(path: &Path) -> Result<PathBuf, String> {
    if !path.is_absolute() || !path.is_dir() {
        return Err("mapping must be an existing absolute directory".into());
    }
    super::bridge::reject_reparse_components(path)?;
    fs::canonicalize(path).map_err(|e| e.to_string())
}

impl SandboxMappings {
    /// All task mappings must be immediate subdirectories of a dedicated task root.
    /// This API intentionally cannot map an arbitrary user-selected Results directory.
    pub fn for_task(runtime: &Path, tasks_root: &Path, job_id: &str) -> Result<Self, String> {
        if !valid_id(job_id) {
            return Err("invalid job id".into());
        }
        let runtime = checked_directory(runtime)?;
        let task = checked_directory(&tasks_root.join(job_id))?;
        let tasks_root = checked_directory(tasks_root)?;
        if task.parent() != Some(tasks_root.as_path()) {
            return Err("task outside tasks root".into());
        }
        let result = Self {
            runtime,
            inputs: checked_directory(&task.join("inputs"))?,
            requests: checked_directory(&task.join("requests"))?,
            results: checked_directory(&task.join("results"))?,
        };
        let paths = [
            &result.runtime,
            &result.inputs,
            &result.requests,
            &result.results,
        ];
        for (i, a) in paths.iter().enumerate() {
            for b in paths.iter().skip(i + 1) {
                if a.starts_with(b) || b.starts_with(a) {
                    return Err("mapped directories overlap".into());
                }
            }
        }
        Ok(result)
    }
    pub fn to_wsb(
        &self,
        job_id: &str,
        session_nonce: &str,
        networking: bool,
    ) -> Result<String, String> {
        if !valid_id(job_id) || !valid_id(session_nonce) {
            return Err("invalid session identity".into());
        }
        let mappings = [(&self.runtime, "Runtime", true), (&self.inputs, "Inputs", true),
            (&self.requests, "Requests", true), (&self.results, "Results", false)]
            .into_iter().map(|(host, guest, readonly)| {
                let path = host.to_str().ok_or("non-Unicode mapping path")?;
                if path.starts_with(r"\\?\UNC\") || path.starts_with(r"\\") && !path.starts_with(r"\\?\") { return Err("UNC mapping not validated in M0"); }
                let path = path.strip_prefix(r"\\?\").unwrap_or(path);
                Ok(format!("<MappedFolder><HostFolder>{}</HostFolder><SandboxFolder>C:\\OMS\\{guest}</SandboxFolder><ReadOnly>{readonly}</ReadOnly></MappedFolder>", xml(path)))
            }).collect::<Result<Vec<_>, _>>()?.join("");
        let command = format!(
            r#"C:\OMS\Runtime\python.exe -I C:\OMS\Runtime\guest_runner.py --job-id {job_id} --nonce {session_nonce} --requests C:\OMS\Requests --results C:\OMS\Results --inputs C:\OMS\Inputs"#
        );
        Ok(format!("<Configuration><VGpu>Disable</VGpu><Networking>{}</Networking><ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>2048</MemoryInMB><MappedFolders>{mappings}</MappedFolders><LogonCommand><Command>{}</Command></LogonCommand></Configuration>", if networking { "Enable" } else { "Disable" }, xml(&command)))
    }
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum SandboxLifecycle {
    NotStarted,
    WaitingHandshake,
    Ready,
    Interrupted,
}

/// Bookkeeping only: never infers OS ownership from a PID/name or guest JSON.
/// OS session launch/stop stays disabled until the Win10/11 ownership matrix passes.
pub struct SandboxSession {
    job_id: String,
    nonce: String,
    pub state: SandboxLifecycle,
    in_flight: Option<String>,
}
impl SandboxSession {
    pub fn new(job_id: String, nonce: String) -> Result<Self, String> {
        if !valid_id(&job_id) || !valid_id(&nonce) {
            return Err("invalid session identity".into());
        }
        Ok(Self {
            job_id,
            nonce,
            state: SandboxLifecycle::NotStarted,
            in_flight: None,
        })
    }
    pub fn awaiting_handshake(&mut self) -> Result<(), String> {
        if self.state != SandboxLifecycle::NotStarted {
            return Err("session already used".into());
        }
        self.state = SandboxLifecycle::WaitingHandshake;
        Ok(())
    }
    pub fn handshake(&mut self, version: u32, job: &str, nonce: &str) -> Result<(), String> {
        if self.state != SandboxLifecycle::WaitingHandshake
            || version != 1
            || job != self.job_id
            || nonce != self.nonce
        {
            return Err("unexpected handshake; does not confer OS session ownership".into());
        }
        self.state = SandboxLifecycle::Ready;
        Ok(())
    }
    pub fn begin_call(&mut self, call: &str) -> Result<(), String> {
        if self.state != SandboxLifecycle::Ready || self.in_flight.is_some() || !valid_id(call) {
            return Err("session not ready".into());
        }
        self.in_flight = Some(call.into());
        Ok(())
    }
    /// The returned call is indeterminate and must never be replayed on the host automatically.
    pub fn disconnected(&mut self) -> Option<String> {
        self.state = SandboxLifecycle::Interrupted;
        self.in_flight.take()
    }
    pub fn complete(&mut self, call: &str) -> Result<(), String> {
        if self.in_flight.as_deref() != Some(call) || self.state != SandboxLifecycle::Ready {
            return Err("unexpected call response".into());
        }
        self.in_flight = None;
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn mapping_is_minimal_and_escaped() {
        let m = SandboxMappings {
            runtime: r"C:\app & runtime".into(),
            inputs: r"C:\任务\inputs".into(),
            requests: r"C:\任务\requests".into(),
            results: r"C:\任务\results".into(),
        };
        let wsb = m.to_wsb("job", "nonce", false).unwrap();
        assert!(wsb.contains("app &amp; runtime"));
        assert_eq!(wsb.matches("<MappedFolder>").count(), 4);
        assert_eq!(wsb.matches("<ReadOnly>true").count(), 3);
        assert_eq!(wsb.matches("<ReadOnly>false").count(), 1);
        assert!(wsb.contains("<Networking>Disable"));
        assert!(wsb.contains("<MemoryInMB>2048"));
        assert!(m.to_wsb("job\"injection", "nonce", false).is_err());
    }
    #[test]
    fn loss_never_replays() {
        let mut s = SandboxSession::new("job".into(), "nonce".into()).unwrap();
        s.awaiting_handshake().unwrap();
        assert!(s.handshake(1, "other", "nonce").is_err());
        s.handshake(1, "job", "nonce").unwrap();
        s.begin_call("a").unwrap();
        assert_eq!(s.disconnected().as_deref(), Some("a"));
        assert!(s.begin_call("a").is_err());
        assert!(s.complete("a").is_err());
    }
}
