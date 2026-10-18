//! A single SQLite owner thread keeps blocking database/image work off the async runtime.
mod assets;
mod grading;
mod jobs;
mod records;

use rusqlite::Connection;
use std::{
    fmt,
    path::{Path, PathBuf},
    sync::mpsc,
    thread,
};
use tokio::sync::oneshot;

pub use assets::{AssetData, ImportSource, MAX_ASSET_BYTES, MAX_IMAGE_PIXELS, MAX_INPUT_PAGES};

#[derive(Debug, Clone)]
pub struct StorageError(pub String);
impl fmt::Display for StorageError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.0)
    }
}
impl std::error::Error for StorageError {}
impl From<rusqlite::Error> for StorageError {
    fn from(value: rusqlite::Error) -> Self {
        Self(format!("database operation failed: {value}"))
    }
}
impl From<std::io::Error> for StorageError {
    fn from(value: std::io::Error) -> Self {
        Self(format!("asset I/O failed: {}", value.kind()))
    }
}
impl From<serde_json::Error> for StorageError {
    fn from(_: serde_json::Error) -> Self {
        Self("stored data is invalid".into())
    }
}
pub type Result<T> = std::result::Result<T, StorageError>;
type Work = Box<dyn FnOnce(&mut Database) + Send>;
struct Database {
    connection: Connection,
    asset_root: PathBuf,
    pdfium_library: PathBuf,
}

#[derive(Clone)]
pub struct Storage {
    sender: mpsc::Sender<Work>,
}

impl Storage {
    /// Call during application setup, not from an async IPC handler.
    pub fn open(data_dir: impl AsRef<Path>) -> Result<Self> {
        Self::open_with_pdfium(
            data_dir,
            PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("resources/pdfium/bin/pdfium.dll"),
        )
    }

    pub fn open_with_pdfium(data_dir: impl AsRef<Path>, pdfium_library: PathBuf) -> Result<Self> {
        std::fs::create_dir_all(data_dir.as_ref())?;
        reject_link(data_dir.as_ref())?;
        let data_dir = data_dir.as_ref().canonicalize()?;
        let (sender, receiver) = mpsc::channel::<Work>();
        let (ready_sender, ready_receiver) = mpsc::sync_channel(1);
        thread::Builder::new()
            .name("study-database".into())
            .spawn(move || {
                let result = Database::open(&data_dir, pdfium_library);
                match result {
                    Ok(mut database) => {
                        let _ = ready_sender.send(Ok(()));
                        while let Ok(work) = receiver.recv() {
                            work(&mut database);
                        }
                    }
                    Err(error) => {
                        let _ = ready_sender.send(Err(error));
                    }
                }
            })?;
        ready_receiver
            .recv()
            .map_err(|_| StorageError("database worker did not start".into()))??;
        Ok(Self { sender })
    }

    async fn run<T, F>(&self, operation: F) -> Result<T>
    where
        T: Send + 'static,
        F: FnOnce(&mut Database) -> Result<T> + Send + 'static,
    {
        let (sender, receiver) = oneshot::channel();
        self.sender
            .send(Box::new(move |database| {
                let _ = sender.send(operation(database));
            }))
            .map_err(|_| StorageError("database worker unavailable".into()))?;
        receiver
            .await
            .map_err(|_| StorageError("database worker stopped".into()))?
    }

    fn run_blocking<T, F>(&self, operation: F) -> std::result::Result<T, String>
    where
        T: Send + 'static,
        F: FnOnce(&mut Database) -> Result<T> + Send + 'static,
    {
        let (sender, receiver) = mpsc::sync_channel(1);
        self.sender
            .send(Box::new(move |database| {
                let _ = sender.send(operation(database));
            }))
            .map_err(|_| "database worker unavailable".to_string())?;
        receiver
            .recv()
            .map_err(|_| "database worker stopped".to_string())?
            .map_err(|error| error.to_string())
    }
}

impl Database {
    fn open(data_dir: &Path, pdfium_library: PathBuf) -> Result<Self> {
        let mut connection = Connection::open(data_dir.join("study.sqlite3"))?;
        connection.busy_timeout(std::time::Duration::from_secs(5))?;
        connection.execute_batch(
            "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;",
        )?;
        let version: i64 = connection.pragma_query_value(None, "user_version", |row| row.get(0))?;
        if version > 2 {
            return Err(StorageError(
                "database version is newer than this application".into(),
            ));
        }
        if version == 0 {
            let transaction = connection.transaction()?;
            transaction.execute_batch(include_str!("../../migrations/001_initial.sql"))?;
            transaction.pragma_update(None, "user_version", 1)?;
            transaction.commit()?;
        }
        if version < 2 {
            let transaction = connection.transaction()?;
            transaction.execute_batch(include_str!("../../migrations/002_grading.sql"))?;
            transaction.pragma_update(None, "user_version", 2)?;
            transaction.commit()?;
        }
        let asset_root = data_dir.join("assets");
        std::fs::create_dir_all(&asset_root)?;
        reject_link(&asset_root)?;
        let asset_root = asset_root.canonicalize()?;
        if !asset_root.starts_with(data_dir) {
            return Err(StorageError(
                "asset directory escapes application data".into(),
            ));
        }
        Ok(Self {
            connection,
            asset_root,
            pdfium_library,
        })
    }
}

pub(crate) fn new_id() -> String {
    uuid::Uuid::new_v4().to_string()
}
pub(crate) fn now() -> String {
    chrono::Utc::now().to_rfc3339()
}
pub(crate) fn valid_id(id: &str) -> Result<()> {
    if uuid::Uuid::parse_str(id).is_err() {
        return Err(StorageError("invalid identifier".into()));
    }
    Ok(())
}

pub(super) fn reject_link(path: &Path) -> Result<()> {
    let metadata = std::fs::symlink_metadata(path)?;
    #[cfg(windows)]
    let reparse = {
        use std::os::windows::fs::MetadataExt;
        metadata.file_attributes() & 0x400 != 0
    };
    #[cfg(not(windows))]
    let reparse = metadata.file_type().is_symlink();
    if metadata.file_type().is_symlink() || reparse {
        return Err(StorageError(
            "managed asset path cannot be a symlink or reparse point".into(),
        ));
    }
    Ok(())
}
