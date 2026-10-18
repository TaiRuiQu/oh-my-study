//! Persisted application data. Secrets and local filesystem paths are never DTO fields.
use crate::ai::providers::{Capabilities, Protocol};
use serde::{Deserialize, Serialize};
pub mod grading;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Provider {
    pub id: String,
    pub name: String,
    pub protocol: Protocol,
    pub base_url: String,
    pub key_ref: Option<String>,
    #[serde(default, skip_deserializing)]
    pub has_key: bool,
    pub models: Vec<ModelConfig>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelConfig {
    pub id: String,
    pub capabilities: Capabilities,
    pub price_input: Option<f64>,
    pub price_output: Option<f64>,
    pub currency: String,
}

#[derive(Debug, Clone, Copy, Serialize, Deserialize, PartialEq, Eq)]
#[serde(rename_all = "snake_case")]
pub enum ImportMode {
    TeacherGraded,
    AiGrading,
    Blank,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Document {
    pub id: String,
    pub name: String,
    pub mode: ImportMode,
    pub created_at: String,
    pub pages: Vec<SourcePage>,
    /// PDF originals are registered, but their page count/preview is not yet parsed.
    pub pending_pdf_pages: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SourcePage {
    pub id: String,
    pub asset_id: String,
    pub original_asset_id: Option<String>,
    pub pdf_page_index: Option<u32>,
    pub page_index: u32,
    pub rotation: u16,
    pub width: Option<u32>,
    pub height: Option<u32>,
    pub preview_status: String,
    pub role: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Asset {
    pub id: String,
    pub mime: String,
    pub byte_size: u64,
    pub width: Option<u32>,
    pub height: Option<u32>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct PageEdit {
    pub id: String,
    pub rotation: u16,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AppSettings {
    pub default_model: Option<String>,
    pub execution_mode: String,
    pub sandbox_networking: bool,
}
impl Default for AppSettings {
    fn default() -> Self {
        Self {
            default_model: None,
            execution_mode: "bypass_permission".into(),
            sandbox_networking: false,
        }
    }
}
