use super::{permissions::AuthorizedCall, ExecutionMode};
use serde::{Deserialize, Serialize};
use std::{
    fs,
    path::{Path, PathBuf},
    sync::atomic::AtomicBool,
};

#[derive(Debug, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum HostStatus {
    Completed,
    Failed,
    TimedOut,
    Cancelled,
    OutputLimit,
}

#[derive(Debug, Serialize, Deserialize)]
pub struct HostResult {
    pub status: HostStatus,
    pub exit_code: Option<u32>,
    pub stdout: String,
    pub stderr: String,
}

pub struct HostExecutor {
    runtime: PathBuf,
}
impl HostExecutor {
    /// Runtime directory must come from application resources, never a model tool argument.
    pub fn new(runtime: PathBuf) -> Result<Self, String> {
        super::bridge::reject_reparse_components(&runtime)?;
        let runtime = fs::canonicalize(runtime).map_err(|e| e.to_string())?;
        for name in ["python.exe", "host_entry.py", "python313._pth"] {
            let path = runtime.join(name);
            super::bridge::reject_reparse_components(&path)?;
            if !path.is_file() {
                return Err(format!("private runtime missing {name}"));
            }
        }
        Ok(Self { runtime })
    }
    /// Blocking M0 primitive; call from a background worker, never the UI thread.
    /// An owned Job kills all descendants on timeout/cancel/output-limit/normal exit.
    pub fn execute(
        &self,
        authorized: AuthorizedCall,
        results: &Path,
        cancelled: &AtomicBool,
        output_limit: u64,
    ) -> Result<HostResult, String> {
        use std::sync::atomic::Ordering;
        if authorized.mode == ExecutionMode::Sandbox {
            return Err("sandbox authorization cannot execute on host".into());
        }
        if !authorized.is_live() || cancelled.load(Ordering::Acquire) {
            return Err("authorization revoked".into());
        }
        authorized.call().validate()?;
        if output_limit == 0 || output_limit > 64 * 1024 * 1024 {
            return Err("invalid output limit".into());
        }
        let parent = results.parent().ok_or("results need parent")?;
        super::bridge::reject_reparse_components(parent)?;
        fs::create_dir(results).map_err(|e| e.to_string())?;
        let results = fs::canonicalize(results).map_err(|e| e.to_string())?;
        let request = results.join("host-request.json");
        super::bridge::atomic_json(&request, authorized.call())?;
        #[cfg(windows)]
        {
            windows::execute(
                &self.runtime,
                &request,
                &results,
                &authorized,
                cancelled,
                output_limit,
            )
        }
        #[cfg(not(windows))]
        {
            let _ = (&self.runtime, &request, cancelled);
            Err("M0 HostExecutor supports Windows only".into())
        }
    }
}

#[cfg(windows)]
mod windows {
    use super::*;
    use std::{
        ffi::OsStr,
        io::Read,
        mem::{size_of, zeroed},
        os::windows::ffi::OsStrExt,
        ptr::null,
        sync::atomic::Ordering,
        time::Instant,
    };
    use windows_sys::Win32::{
        Foundation::{CloseHandle, HANDLE, WAIT_OBJECT_0, WAIT_TIMEOUT},
        System::{
            JobObjects::{
                AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation,
                SetInformationJobObject, JOBOBJECT_EXTENDED_LIMIT_INFORMATION,
                JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
            Threading::{
                CreateProcessW, GetExitCodeProcess, ResumeThread, TerminateProcess,
                WaitForSingleObject, CREATE_NO_WINDOW, CREATE_SUSPENDED,
                CREATE_UNICODE_ENVIRONMENT, PROCESS_INFORMATION, STARTUPINFOW,
            },
        },
    };

    struct Handle(HANDLE);
    impl Drop for Handle {
        fn drop(&mut self) {
            unsafe {
                CloseHandle(self.0);
            }
        }
    }
    fn error() -> String {
        std::io::Error::last_os_error().to_string()
    }
    fn wide(value: &OsStr) -> Result<Vec<u16>, String> {
        let mut value: Vec<_> = value.encode_wide().collect();
        if value.contains(&0) {
            return Err("NUL in process parameter".into());
        }
        value.push(0);
        Ok(value)
    }
    /// Implements CommandLineToArgvW quoting including trailing backslashes.
    fn quote(value: &str) -> String {
        let mut out = String::from("\"");
        let mut slashes = 0;
        for c in value.chars() {
            if c == '\\' {
                slashes += 1;
                continue;
            }
            if c == '"' {
                out.extend(std::iter::repeat_n('\\', slashes * 2 + 1));
            } else {
                out.extend(std::iter::repeat_n('\\', slashes));
            }
            slashes = 0;
            out.push(c);
        }
        out.extend(std::iter::repeat_n('\\', slashes * 2));
        out.push('"');
        out
    }
    fn file_len(path: &Path) -> u64 {
        fs::metadata(path).map(|v| v.len()).unwrap_or(0)
    }
    fn bounded_log(path: &Path, limit: u64) -> String {
        let mut bytes = Vec::new();
        if let Ok(file) = fs::File::open(path) {
            let _ = file.take(limit).read_to_end(&mut bytes);
        }
        String::from_utf8_lossy(&bytes).into_owned()
    }

    pub(super) fn execute(
        runtime: &Path,
        request: &Path,
        results: &Path,
        authorized: &AuthorizedCall,
        cancelled: &AtomicBool,
        limit: u64,
    ) -> Result<HostResult, String> {
        let python = runtime.join("python.exe");
        let entry = runtime.join("host_entry.py");
        let command = [
            python.as_path(),
            Path::new("-I"),
            entry.as_path(),
            request,
            results,
        ]
        .iter()
        .map(|p| p.to_str().ok_or("non-Unicode process argument").map(quote))
        .collect::<Result<Vec<_>, _>>()?
        .join(" ");
        let mut command = wide(OsStr::new(&command))?;
        let application = wide(python.as_os_str())?;
        let cwd = wide(OsStr::new(&authorized.call().working_directory))?;
        let system_root = std::env::var("SystemRoot").map_err(|_| "SystemRoot unavailable")?;
        // Allowlist only. No inherited API keys, proxy credentials, Python paths, DB handles.
        let mut environment = vec![
            format!("SystemRoot={system_root}"),
            format!("WINDIR={system_root}"),
            format!("TEMP={}", results.display()),
            format!("TMP={}", results.display()),
            format!("PATH={};{system_root}\\System32", runtime.display()),
            "MPLBACKEND=Agg".into(),
            "OMP_NUM_THREADS=2".into(),
            "OPENBLAS_NUM_THREADS=2".into(),
            "PYTHONIOENCODING=utf-8".into(),
        ];
        environment.sort_by_key(|v| v.to_ascii_lowercase());
        let mut environment: Vec<u16> = environment.join("\0").encode_utf16().collect();
        environment.extend([0, 0]);
        unsafe {
            let job = CreateJobObjectW(null(), null());
            if job.is_null() {
                return Err(error());
            }
            let job = Handle(job);
            let mut limits: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = zeroed();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            if SetInformationJobObject(
                job.0,
                JobObjectExtendedLimitInformation,
                &limits as *const _ as _,
                size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            ) == 0
            {
                return Err(error());
            }
            let mut startup: STARTUPINFOW = zeroed();
            startup.cb = size_of::<STARTUPINFOW>() as u32;
            let mut process: PROCESS_INFORMATION = zeroed();
            if CreateProcessW(
                application.as_ptr(),
                command.as_mut_ptr(),
                null(),
                null(),
                0,
                CREATE_SUSPENDED | CREATE_NO_WINDOW | CREATE_UNICODE_ENVIRONMENT,
                environment.as_ptr() as _,
                cwd.as_ptr(),
                &startup,
                &mut process,
            ) == 0
            {
                return Err(error());
            }
            let process_handle = Handle(process.hProcess);
            let thread = Handle(process.hThread);
            if AssignProcessToJobObject(job.0, process_handle.0) == 0 {
                let reason = error();
                TerminateProcess(process_handle.0, 1);
                WaitForSingleObject(process_handle.0, 5000);
                return Err(format!("cannot own process tree: {reason}"));
            }
            if !authorized.is_live() || cancelled.load(Ordering::Acquire) {
                return Err("authorization revoked before start".into());
            }
            if ResumeThread(thread.0) == u32::MAX {
                return Err(error());
            }
            drop(thread);
            let started = Instant::now();
            let stdout = results.join(format!("{}.stdout.log", authorized.call().call_id));
            let stderr = results.join(format!("{}.stderr.log", authorized.call().call_id));
            let status = loop {
                if !authorized.is_live() || cancelled.load(Ordering::Acquire) {
                    break HostStatus::Cancelled;
                }
                if started.elapsed().as_millis() >= authorized.call().timeout_ms as u128 {
                    break HostStatus::TimedOut;
                }
                if file_len(&stdout).saturating_add(file_len(&stderr)) > limit {
                    break HostStatus::OutputLimit;
                }
                match WaitForSingleObject(process_handle.0, 50) {
                    WAIT_OBJECT_0 => break HostStatus::Completed,
                    WAIT_TIMEOUT => {}
                    _ => return Err(error()),
                }
            };
            // Closing this exact owned Job also cleans up children after a successful root exit.
            drop(job);
            if WaitForSingleObject(process_handle.0, 5000) != WAIT_OBJECT_0 {
                return Err("owned process did not exit after job close".into());
            }
            let mut code = 0;
            if GetExitCodeProcess(process_handle.0, &mut code) == 0 {
                return Err(error());
            }
            let status = if status == HostStatus::Completed
                && file_len(&stdout).saturating_add(file_len(&stderr)) > limit
            {
                HostStatus::OutputLimit
            } else if status == HostStatus::Completed && code != 0 {
                HostStatus::Failed
            } else {
                status
            };
            Ok(HostResult {
                status,
                exit_code: Some(code),
                stdout: bounded_log(&stdout, limit),
                stderr: bounded_log(&stderr, limit),
            })
        }
    }
    #[cfg(test)]
    mod tests {
        use super::*;
        #[test]
        fn argv_quoting_preserves_space_quote_and_trailing_slash() {
            assert_eq!(quote("a b"), "\"a b\"");
            assert_eq!(quote("a\"b"), "\"a\\\"b\"");
            assert_eq!(quote("a\\"), "\"a\\\\\"");
        }
    }
}
