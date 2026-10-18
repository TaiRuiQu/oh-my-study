pub mod gemini;
pub mod messages;
pub mod responses;
pub mod stream;
pub mod transport;

use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum Protocol {
    Responses,
    Messages,
    Gemini,
}

#[derive(Debug, Clone, Default, Serialize, Deserialize)]
pub struct Capabilities {
    pub vision: Option<bool>,
    pub tools: Option<bool>,
    /// Exact native configurations established by API metadata or explicit manual settings.
    pub thinking: Vec<Thinking>,
    pub messages_budget: Option<bool>,
    pub source: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Thinking {
    Responses { effort: String },
    Adaptive { effort: String },
    Budget { tokens: u64 },
    GeminiLevel { level: String },
    GeminiBudget { tokens: i64 },
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Model {
    pub id: String,
    pub capabilities: Capabilities,
    /// Preserve API metadata; names alone never establish capabilities.
    pub raw: Value,
}

impl Model {
    pub fn manual(id: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            capabilities: Capabilities::default(),
            raw: Value::Null,
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Image {
    pub mime: String,
    pub base64: String,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Tool {
    pub name: String,
    pub description: String,
    pub schema: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolCall {
    pub id: Option<String>,
    pub name: String,
    pub arguments: Value,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ToolResult {
    pub call: ToolCall,
    pub text: String,
    pub images: Vec<Image>,
    pub is_error: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct RequestOptions {
    pub model: Model,
    pub max_output_tokens: u64,
    pub stream: bool,
    pub thinking: Option<Thinking>,
    /// Explicit active testing of a manually configured/unknown model.
    pub allow_unknown_capabilities: bool,
    /// Native protocol parameters; collisions with managed fields are errors, never dropped.
    pub extra: Map<String, Value>,
}

impl RequestOptions {
    pub fn validate(
        &self,
        protocol: Protocol,
        vision: bool,
        tools: bool,
    ) -> Result<(), ProviderError> {
        if self.model.id.trim().is_empty() || self.max_output_tokens == 0 {
            return Err(ProviderError::invalid(
                "model and positive token limit are required",
            ));
        }
        for (needed, capability, name) in [
            (vision, self.model.capabilities.vision, "vision"),
            (tools, self.model.capabilities.tools, "tools"),
        ] {
            if needed
                && (capability == Some(false)
                    || (capability.is_none() && !self.allow_unknown_capabilities))
            {
                return Err(ProviderError::invalid(&format!("{name} is unsupported or unknown; configure capabilities or explicitly test this model")));
            }
        }
        if let Some(thinking) = &self.thinking {
            let correct_protocol = matches!(
                (protocol, thinking),
                (Protocol::Responses, Thinking::Responses { .. })
                    | (
                        Protocol::Messages,
                        Thinking::Adaptive { .. } | Thinking::Budget { .. }
                    )
                    | (
                        Protocol::Gemini,
                        Thinking::GeminiLevel { .. } | Thinking::GeminiBudget { .. }
                    )
            );
            let metadata_budget = matches!(thinking, Thinking::Budget { .. })
                && self.model.capabilities.messages_budget == Some(true);
            if !correct_protocol
                || (!metadata_budget
                    && !self.model.capabilities.thinking.contains(thinking)
                    && !self.allow_unknown_capabilities)
            {
                return Err(ProviderError::invalid(
                    "thinking configuration is unsupported or unknown",
                ));
            }
            if matches!(thinking, Thinking::Budget { tokens } if *tokens < 1024 || *tokens >= self.max_output_tokens)
            {
                return Err(ProviderError::invalid(
                    "Messages thinking budget must be >=1024 and below max_tokens",
                ));
            }
            if matches!(thinking, Thinking::GeminiBudget { tokens } if *tokens < -1) {
                return Err(ProviderError::invalid(
                    "Gemini thinking budget must be -1 or nonnegative",
                ));
            }
        }
        Ok(())
    }
}

pub(crate) fn extend(
    body: &mut Value,
    extra: &Map<String, Value>,
    reserved: &[&str],
) -> Result<(), ProviderError> {
    for (key, value) in extra {
        if reserved.contains(&key.as_str()) || body.get(key).is_some() {
            return Err(ProviderError::invalid(&format!(
                "native parameter collision: {key}"
            )));
        }
        body[key] = value.clone();
    }
    Ok(())
}

#[derive(Debug, Clone, Default, PartialEq, Serialize, Deserialize)]
pub struct Usage {
    /// Total prompt including cached reads/writes, when the provider supplies enough data.
    pub input: Option<u64>,
    pub input_uncached: Option<u64>,
    pub cache_read: Option<u64>,
    pub cache_write: Option<u64>,
    /// All output, including reasoning exactly once.
    pub output: Option<u64>,
    pub reasoning: Option<u64>,
    pub total: Option<u64>,
    pub raw: Value,
}

pub(crate) fn count(v: &Value, key: &str) -> Option<u64> {
    v.get(key).and_then(Value::as_u64)
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Turn {
    pub protocol: Protocol,
    pub text: String,
    pub calls: Vec<ToolCall>,
    /// Original provider items/content, including opaque reasoning/signature values.
    pub context: Value,
    pub usage: Usage,
    pub stop_reason: Option<String>,
    pub raw: Value,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum ErrorKind {
    Refusal,
    Authentication,
    Quota,
    RateLimit,
    UnsupportedParameter,
    InvalidRequest,
    Server,
    Network,
    Cancelled,
    Protocol,
    Incomplete,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ProviderError {
    pub kind: ErrorKind,
    pub status: Option<u16>,
    /// Sanitized local description. Raw provider text can echo credentials/input and is not logged.
    pub message: String,
    pub retryable: bool,
    pub retry_after: Option<String>,
}

impl std::fmt::Display for ProviderError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "{:?}: {}", self.kind, self.message)
    }
}
impl std::error::Error for ProviderError {}
impl ProviderError {
    pub fn new(kind: ErrorKind, message: &str) -> Self {
        Self {
            kind,
            status: None,
            message: message.into(),
            retryable: matches!(
                kind,
                ErrorKind::RateLimit | ErrorKind::Server | ErrorKind::Network
            ),
            retry_after: None,
        }
    }
    pub fn invalid(message: &str) -> Self {
        Self::new(ErrorKind::InvalidRequest, message)
    }
    pub fn protocol(message: &str) -> Self {
        Self::new(ErrorKind::Protocol, message)
    }
}

pub fn classify_error(status: u16, body: &Value, retry_after: Option<String>) -> ProviderError {
    let code = body
        .pointer("/error/code")
        .or_else(|| body.pointer("/error/type"))
        .and_then(Value::as_str)
        .unwrap_or("");
    let message = body
        .pointer("/error/message")
        .and_then(Value::as_str)
        .unwrap_or("")
        .to_ascii_lowercase();
    let kind = if matches!(
        code,
        "insufficient_quota" | "billing_error" | "credit_balance_too_low"
    ) || message.contains("credit balance")
    {
        ErrorKind::Quota
    } else if status == 401 || status == 403 {
        ErrorKind::Authentication
    } else if status == 429 {
        ErrorKind::RateLimit
    } else if code.contains("unsupported")
        || ((status == 400 || status == 422)
            && (message.contains("unsupported")
                || message.contains("not supported")
                || message.contains("unknown parameter")))
    {
        ErrorKind::UnsupportedParameter
    } else if status >= 500 || matches!(code, "overloaded_error" | "api_error") {
        ErrorKind::Server
    } else {
        ErrorKind::InvalidRequest
    };
    let mut error = ProviderError::new(
        kind,
        "provider rejected request; review model, parameters and account settings",
    );
    error.status = Some(status);
    error.retry_after = retry_after;
    error
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ModelPage {
    pub models: Vec<Model>,
    pub next: Option<String>,
    pub raw: Value,
}

pub fn parse_model_page(protocol: Protocol, raw: Value) -> Result<ModelPage, ProviderError> {
    let key = if protocol == Protocol::Gemini {
        "models"
    } else {
        "data"
    };
    let entries = raw[key]
        .as_array()
        .ok_or_else(|| ProviderError::protocol("missing model list"))?;
    let mut models = Vec::new();
    for item in entries {
        let id = item[if protocol == Protocol::Gemini {
            "name"
        } else {
            "id"
        }]
        .as_str()
        .ok_or_else(|| ProviderError::protocol("missing model ID"))?;
        let mut capabilities = Capabilities::default();
        if protocol == Protocol::Messages {
            capabilities.vision = item
                .pointer("/capabilities/image_input/supported")
                .and_then(Value::as_bool);
            capabilities.tools = item
                .pointer("/capabilities/tool_use/supported")
                .and_then(Value::as_bool);
            capabilities.messages_budget = item
                .pointer("/capabilities/thinking/types/enabled/supported")
                .and_then(Value::as_bool);
            if item.pointer("/capabilities/thinking/types/adaptive/supported")
                == Some(&Value::Bool(true))
            {
                if let Some(efforts) = item
                    .pointer("/capabilities/effort")
                    .and_then(Value::as_object)
                {
                    for (effort, metadata) in efforts {
                        if metadata.get("supported") == Some(&Value::Bool(true)) {
                            capabilities.thinking.push(Thinking::Adaptive {
                                effort: effort.clone(),
                            });
                        }
                    }
                }
            }
            if !item["capabilities"].is_null() {
                capabilities.source = Some("provider_metadata".into());
            }
        }
        models.push(Model {
            id: id.into(),
            capabilities,
            raw: item.clone(),
        });
    }
    let next = match protocol {
        Protocol::Gemini => raw["nextPageToken"]
            .as_str()
            .filter(|s| !s.is_empty())
            .map(str::to_owned),
        // Official OpenAI /models currently has no documented cursor. Accept explicit
        // compatible-gateway has_more only; do not invent pagination for the official API.
        _ if raw["has_more"] == true => Some(
            raw["last_id"]
                .as_str()
                .ok_or_else(|| ProviderError::protocol("has_more without last_id"))?
                .to_owned(),
        ),
        _ => None,
    };
    Ok(ModelPage { models, next, raw })
}

pub(crate) fn text_blocks(items: &[Value], kind: &str, field: &str) -> String {
    items
        .iter()
        .filter(|b| b["type"] == kind)
        .filter_map(|b| b[field].as_str())
        .collect::<Vec<_>>()
        .join("")
}

pub(crate) fn required_array<'a>(
    raw: &'a Value,
    key: &str,
) -> Result<&'a Vec<Value>, ProviderError> {
    raw[key]
        .as_array()
        .ok_or_else(|| ProviderError::protocol(&format!("missing {key}")))
}

pub(crate) fn json_arguments(value: &Value) -> Result<Value, ProviderError> {
    let parsed = if let Some(s) = value.as_str() {
        serde_json::from_str(s)
            .map_err(|_| ProviderError::protocol("invalid tool argument JSON"))?
    } else {
        value.clone()
    };
    if !parsed.is_object() {
        return Err(ProviderError::protocol(
            "tool arguments must be a JSON object",
        ));
    }
    Ok(parsed)
}

pub(crate) fn initial_image(image: &Image, protocol: Protocol) -> Value {
    match protocol {
        Protocol::Responses => {
            json!({"type":"input_image","image_url":format!("data:{};base64,{}",image.mime,image.base64)})
        }
        Protocol::Messages => {
            json!({"type":"image","source":{"type":"base64","media_type":image.mime,"data":image.base64}})
        }
        Protocol::Gemini => json!({"inlineData":{"mimeType":image.mime,"data":image.base64}}),
    }
}

#[cfg(test)]
mod tests;
