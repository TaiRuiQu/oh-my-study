//! Treat guest JSON, filenames and artifacts as untrusted data, never instructions.
use super::permissions::{valid_id, ExecutionCall};
use serde::{Deserialize, Serialize};
use std::{
    collections::HashSet,
    fs::{self, File, OpenOptions},
    io::{Read, Write},
    path::{Component, Path, PathBuf},
};

pub fn reject_reparse_components(path: &Path) -> Result<(), String> {
    let mut current = PathBuf::new();
    for part in path.components() {
        if matches!(part, Component::ParentDir) {
            return Err("parent path component".into());
        }
        current.push(part);
        if matches!(part, Component::Prefix(_) | Component::RootDir) {
            continue;
        }
        let metadata = fs::symlink_metadata(&current).map_err(|e| e.to_string())?;
        if metadata.file_type().is_symlink() {
            return Err("symlink forbidden".into());
        }
        #[cfg(windows)]
        {
            use std::os::windows::fs::MetadataExt;
            if metadata.file_attributes() & 0x400 != 0 {
                return Err("reparse point forbidden".into());
            }
        }
    }
    Ok(())
}

pub fn atomic_json(path: &Path, value: &impl Serialize) -> Result<(), String> {
    let temporary = path.with_extension("pending");
    let mut file = OpenOptions::new()
        .create_new(true)
        .write(true)
        .open(&temporary)
        .map_err(|e| e.to_string())?;
    file.write_all(&serde_json::to_vec(value).map_err(|e| e.to_string())?)
        .map_err(|e| e.to_string())?;
    file.sync_all().map_err(|e| e.to_string())?;
    fs::rename(temporary, path).map_err(|e| e.to_string())
}

pub fn publish_request(directory: &Path, call: &ExecutionCall) -> Result<(), String> {
    call.validate()?;
    reject_reparse_components(directory)?;
    if directory.join(format!("{}.json", call.call_id)).exists() {
        return Err("call already published".into());
    }
    atomic_json(&directory.join(format!("{}.json", call.call_id)), call)
}

#[derive(Debug, Deserialize, Serialize)]
#[serde(deny_unknown_fields)]
pub struct GuestResponse {
    pub protocol_version: u32,
    pub job_id: String,
    pub call_id: String,
    pub status: String,
    pub exit_code: Option<u32>,
    pub artifacts: Vec<String>,
}

pub struct ArtifactRegistry {
    job_id: String,
    call_id: String,
    allowed: HashSet<String>,
    max_file_bytes: u64,
}
impl ArtifactRegistry {
    pub fn new(
        job: &str,
        call: &str,
        allowed: impl IntoIterator<Item = String>,
        max_file_bytes: u64,
    ) -> Result<Self, String> {
        if !valid_id(job) || !valid_id(call) {
            return Err("invalid identity".into());
        }
        let allowed: HashSet<_> = allowed.into_iter().collect();
        if max_file_bytes == 0
            || max_file_bytes > 64 * 1024 * 1024
            || allowed.len() > 32
            || allowed.iter().any(|n| !flat_filename(n))
        {
            return Err("invalid artifact registration".into());
        }
        Ok(Self {
            job_id: job.into(),
            call_id: call.into(),
            allowed,
            max_file_bytes,
        })
    }
    pub fn read_response(&self, results: &Path) -> Result<GuestResponse, String> {
        let data = read_flat(results, &format!("{}.response.json", self.call_id), 65_536)?;
        let response: GuestResponse = serde_json::from_slice(&data).map_err(|e| e.to_string())?;
        if response.protocol_version != 1
            || response.job_id != self.job_id
            || response.call_id != self.call_id
            || !["completed", "failed", "cancelled", "timed_out"]
                .contains(&response.status.as_str())
            || response.artifacts.len() > self.allowed.len()
        {
            return Err("response identity, status or artifact count mismatch".into());
        }
        let mut unique = HashSet::new();
        for name in &response.artifacts {
            if !self.allowed.contains(name) || !unique.insert(name) {
                return Err("unregistered or duplicate artifact".into());
            }
        }
        Ok(response)
    }
    /// Validate bytes, copy into a fresh trusted file, THEN let the asset service register it.
    /// SVG is deliberately rejected until a real SVG sanitizer exists.
    pub fn copy_image(
        &self,
        results: &Path,
        name: &str,
        destination: &Path,
    ) -> Result<u64, String> {
        if !self.allowed.contains(name) {
            return Err("unregistered artifact".into());
        }
        let data = read_flat(results, name, self.max_file_bytes)?;
        validate_image(name, &data)?;
        let parent = destination.parent().ok_or("missing destination parent")?;
        reject_reparse_components(parent)?;
        let mut target = OpenOptions::new()
            .write(true)
            .create_new(true)
            .open(destination)
            .map_err(|e| e.to_string())?;
        target.write_all(&data).map_err(|e| e.to_string())?;
        target.sync_all().map_err(|e| e.to_string())?;
        Ok(data.len() as u64)
    }
}

fn validate_image(name: &str, data: &[u8]) -> Result<(), String> {
    let extension = Path::new(name)
        .extension()
        .and_then(|v| v.to_str())
        .unwrap_or("")
        .to_ascii_lowercase();
    let valid = match extension.as_str() {
        "png" => data.starts_with(b"\x89PNG\r\n\x1a\n"),
        "jpg" | "jpeg" => data.starts_with(&[0xff, 0xd8, 0xff]) && data.ends_with(&[0xff, 0xd9]),
        _ => false,
    };
    if !valid {
        return Err("unsupported or mismatched image type; SVG requires sanitization".into());
    }
    let format = if extension == "png" {
        image::ImageFormat::Png
    } else {
        image::ImageFormat::Jpeg
    };
    let mut reader = image::ImageReader::with_format(std::io::Cursor::new(&data), format);
    let mut limits = image::Limits::default();
    limits.max_image_width = Some(8192);
    limits.max_image_height = Some(8192);
    limits.max_alloc = Some(128 * 1024 * 1024);
    reader.limits(limits);
    let decoded = reader.decode().map_err(|e| format!("invalid image: {e}"))?;
    if u64::from(decoded.width()) * u64::from(decoded.height()) > 32_000_000 {
        return Err("artifact pixel limit exceeded".into());
    }
    Ok(())
}

pub fn flat_filename(value: &str) -> bool {
    !value.is_empty()
        && value.len() <= 180
        && !value.starts_with('.')
        && !value.ends_with('.')
        && value
            .bytes()
            .all(|c| c.is_ascii_alphanumeric() || matches!(c, b'-' | b'_' | b'.'))
}

fn read_flat(root: &Path, name: &str, max: u64) -> Result<Vec<u8>, String> {
    if !flat_filename(name) {
        return Err("invalid flat artifact filename".into());
    }
    reject_reparse_components(root)?;
    let root = fs::canonicalize(root).map_err(|e| e.to_string())?;
    let path = root.join(name);
    reject_reparse_components(&path)?;
    let mut options = OpenOptions::new();
    options.read(true);
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        // Open the link itself; deny concurrent writes/deletes while reading.
        options.custom_flags(0x00200000).share_mode(1);
    }
    let file: File = options.open(&path).map_err(|e| e.to_string())?;
    let metadata = file.metadata().map_err(|e| e.to_string())?;
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        if metadata.file_attributes() & 0x400 != 0 {
            return Err("reparse point forbidden".into());
        }
    }
    if !metadata.is_file() || metadata.len() > max {
        return Err("non-file or oversized result".into());
    }
    if fs::canonicalize(path).map_err(|e| e.to_string())?.parent() != Some(root.as_path()) {
        return Err("artifact escaped Results".into());
    }
    let mut bytes = Vec::new();
    file.take(max + 1)
        .read_to_end(&mut bytes)
        .map_err(|e| e.to_string())?;
    if bytes.len() as u64 > max {
        return Err("result grew beyond limit".into());
    }
    Ok(bytes)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn rejects_guest_paths_and_duplicate_or_foreign_outputs() {
        for name in [
            "../secret.png",
            "C:\\secret.png",
            "a/b.png",
            "a:stream",
            ".hidden",
            "image.svg/../../x",
        ] {
            assert!(ArtifactRegistry::new("job", "call", [name.into()], 100).is_err());
        }
        assert!(ArtifactRegistry::new("job", "call", ["image.png".into()], 100).is_ok());
    }
    #[test]
    fn rejects_truncated_png_and_unregistered_or_foreign_response() {
        assert!(validate_image("image.png", b"\x89PNG\r\n\x1a\n").is_err());
        assert!(validate_image("image.svg", b"<svg/>").is_err());
        let root = Path::new(env!("CARGO_MANIFEST_DIR"))
            .parent()
            .unwrap()
            .join("tmp/bridge-test")
            .join(format!("{}", std::process::id()));
        fs::create_dir_all(&root).unwrap();
        let registry = ArtifactRegistry::new("job", "call", ["image.png".into()], 1024).unwrap();
        fs::write(root.join("image.png"), b"\x89PNG\r\n\x1a\n").unwrap();
        assert!(registry
            .copy_image(&root, "image.png", &root.join("saved.png"))
            .is_err());
        let response = GuestResponse {
            protocol_version: 1,
            job_id: "other-job".into(),
            call_id: "call".into(),
            status: "completed".into(),
            exit_code: Some(0),
            artifacts: vec!["image.png".into()],
        };
        fs::write(
            root.join("call.response.json"),
            serde_json::to_vec(&response).unwrap(),
        )
        .unwrap();
        assert!(registry.read_response(&root).is_err());
        fs::remove_file(root.join("image.png")).unwrap();
        fs::remove_file(root.join("call.response.json")).unwrap();
        fs::remove_dir(root).unwrap();
    }
}
