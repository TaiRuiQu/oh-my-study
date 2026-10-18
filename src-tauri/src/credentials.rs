//! Windows Credential Manager boundary. Secrets are never serialized or logged.
//! Only opaque references belong in SQLite; calculation processes get no vault.

const PREFIX: &str = "OhMyStudy/provider/";

pub struct Secret(String);

impl Secret {
    pub fn expose(&self) -> &str {
        &self.0
    }
}

fn validate_reference(reference: &str) -> Result<(), String> {
    let suffix = reference
        .strip_prefix(PREFIX)
        .ok_or("invalid credential reference")?;
    uuid::Uuid::parse_str(suffix).map_err(|_| "invalid credential reference".to_string())?;
    Ok(())
}

pub fn new_reference() -> String {
    format!("{PREFIX}{}", uuid::Uuid::new_v4())
}

pub fn save(reference: &str, secret: &str) -> Result<(), String> {
    validate_reference(reference)?;
    if secret.is_empty() || secret.len() > 2560 || secret.contains('\0') {
        return Err("密钥为空或超过 Windows 凭据长度限制".into());
    }
    platform::save(reference, secret)
}

pub fn read(reference: &str) -> Result<Option<Secret>, String> {
    validate_reference(reference)?;
    platform::read(reference)
}

pub fn delete(reference: &str) -> Result<(), String> {
    validate_reference(reference)?;
    platform::delete(reference)
}

#[cfg(windows)]
mod platform {
    use super::Secret;
    use std::ptr::null_mut;
    use windows_sys::Win32::{
        Foundation::ERROR_NOT_FOUND,
        Security::Credentials::{
            CredDeleteW, CredFree, CredReadW, CredWriteW, CREDENTIALW, CRED_PERSIST_LOCAL_MACHINE,
            CRED_TYPE_GENERIC,
        },
    };

    fn wide(value: &str) -> Vec<u16> {
        value.encode_utf16().chain([0]).collect()
    }

    fn error() -> String {
        // No target name, secret or provider response enters this error.
        format!(
            "Windows 凭据存储不可用（{}）",
            std::io::Error::last_os_error().raw_os_error().unwrap_or(0)
        )
    }

    pub fn save(reference: &str, secret: &str) -> Result<(), String> {
        let mut target = wide(reference);
        let mut user = wide("api-key");
        let credential = CREDENTIALW {
            Type: CRED_TYPE_GENERIC,
            TargetName: target.as_mut_ptr(),
            CredentialBlobSize: secret.len() as u32,
            CredentialBlob: secret.as_ptr() as *mut u8,
            Persist: CRED_PERSIST_LOCAL_MACHINE,
            UserName: user.as_mut_ptr(),
            ..unsafe { std::mem::zeroed() }
        };
        if unsafe { CredWriteW(&credential, 0) } == 0 {
            return Err(error());
        }
        Ok(())
    }

    pub fn read(reference: &str) -> Result<Option<Secret>, String> {
        let target = wide(reference);
        let mut credential: *mut CREDENTIALW = null_mut();
        if unsafe { CredReadW(target.as_ptr(), CRED_TYPE_GENERIC, 0, &mut credential) } == 0 {
            if std::io::Error::last_os_error().raw_os_error() == Some(ERROR_NOT_FOUND as i32) {
                return Ok(None);
            }
            return Err(error());
        }
        // A successful CredRead allocates this buffer; free it on every path.
        let data = unsafe {
            let record = &*credential;
            if record.CredentialBlobSize == 0 {
                Vec::new()
            } else {
                std::slice::from_raw_parts(
                    record.CredentialBlob,
                    record.CredentialBlobSize as usize,
                )
                .to_vec()
            }
        };
        unsafe {
            CredFree(credential as _);
        }
        String::from_utf8(data)
            .map(|value| Some(Secret(value)))
            .map_err(|_| "凭据格式损坏，请重新配置".into())
    }

    pub fn delete(reference: &str) -> Result<(), String> {
        let target = wide(reference);
        if unsafe { CredDeleteW(target.as_ptr(), CRED_TYPE_GENERIC, 0) } == 0
            && std::io::Error::last_os_error().raw_os_error() != Some(ERROR_NOT_FOUND as i32)
        {
            return Err(error());
        }
        Ok(())
    }
}

#[cfg(not(windows))]
mod platform {
    use super::Secret;
    pub fn save(_: &str, _: &str) -> Result<(), String> {
        Err("当前仅支持 Windows 系统凭据存储".into())
    }
    pub fn read(_: &str) -> Result<Option<Secret>, String> {
        Err("当前仅支持 Windows 系统凭据存储".into())
    }
    pub fn delete(_: &str) -> Result<(), String> {
        Err("当前仅支持 Windows 系统凭据存储".into())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn refuses_foreign_or_malformed_references() {
        assert!(read("another-application/credential").is_err());
        assert!(save("OhMyStudy/provider/not-a-uuid", "synthetic").is_err());
        assert!(save(&new_reference(), "").is_err());
    }

    #[cfg(windows)]
    #[test]
    #[ignore = "writes and deletes one unique synthetic Windows credential"]
    fn windows_credential_roundtrip() {
        let reference = new_reference();
        assert!(read(&reference).unwrap().is_none());
        save(&reference, "m1-synthetic-only").unwrap();
        let secret = read(&reference).unwrap().unwrap();
        delete(&reference).unwrap();
        assert_eq!(secret.expose(), "m1-synthetic-only");
        assert!(read(&reference).unwrap().is_none());
    }
}
