//! Local study application core, with an optional Tauri desktop host.
pub mod ai;
pub mod credentials;
#[cfg(feature = "desktop")]
pub mod desktop;
pub mod documents;
pub mod domain;
pub mod execution;
pub mod jobs;
pub mod storage;
