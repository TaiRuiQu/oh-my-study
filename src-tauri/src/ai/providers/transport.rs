use super::*;
use futures_util::StreamExt;
use reqwest::{
    header::{HeaderMap, HeaderName, HeaderValue},
    Client, Method, Url,
};
use std::{collections::BTreeMap, time::Duration};
use tokio_util::sync::CancellationToken;

/// Does not implement Debug/Serialize so a key cannot accidentally enter traces or IPC.
pub struct Credentials {
    key: String,
}
impl Credentials {
    pub fn new(key: String) -> Result<Self, ProviderError> {
        if key.trim().is_empty() {
            return Err(ProviderError::invalid("missing provider key"));
        }
        Ok(Self { key })
    }
}

pub struct Transport {
    protocol: Protocol,
    base: Url,
    client: Client,
    headers: HeaderMap,
}

impl Transport {
    /// Base includes version (e.g. https://api.openai.com/v1/). HTTP only for local test servers.
    pub fn new(
        protocol: Protocol,
        base: &str,
        headers: &BTreeMap<String, String>,
    ) -> Result<Self, ProviderError> {
        let mut base =
            Url::parse(base).map_err(|_| ProviderError::invalid("invalid provider URL"))?;
        let local = matches!(base.host_str(), Some("localhost" | "127.0.0.1" | "[::1]"));
        if (base.scheme() != "https" && !(base.scheme() == "http" && local))
            || !base.username().is_empty()
            || base.password().is_some()
            || base.query().is_some()
            || base.fragment().is_some()
        {
            return Err(ProviderError::invalid(
                "provider URL must be HTTPS without embedded credentials, query or fragment",
            ));
        }
        if !base.path().ends_with('/') {
            base.set_path(&format!("{}/", base.path()));
        }
        let mut custom = HeaderMap::new();
        for (name, value) in headers {
            if matches!(
                name.to_ascii_lowercase().as_str(),
                "authorization" | "x-api-key" | "x-goog-api-key" | "host"
            ) {
                return Err(ProviderError::invalid(
                    "credentials must use private credential argument",
                ));
            }
            let name = HeaderName::from_bytes(name.as_bytes())
                .map_err(|_| ProviderError::invalid("invalid header name"))?;
            let mut value = HeaderValue::from_str(value)
                .map_err(|_| ProviderError::invalid("invalid header value"))?;
            value.set_sensitive(true);
            custom.insert(name, value);
        }
        let client = Client::builder()
            .timeout(Duration::from_secs(180))
            .redirect(reqwest::redirect::Policy::none())
            .build()
            .map_err(|_| ProviderError::new(ErrorKind::Network, "cannot build HTTP client"))?;
        Ok(Self {
            protocol,
            base,
            client,
            headers: custom,
        })
    }
    fn headers(&self, credentials: &Credentials) -> Result<HeaderMap, ProviderError> {
        let mut headers = self.headers.clone();
        let (name, value) = match self.protocol {
            Protocol::Responses => ("authorization", format!("Bearer {}", credentials.key)),
            Protocol::Messages => ("x-api-key", credentials.key.clone()),
            Protocol::Gemini => ("x-goog-api-key", credentials.key.clone()),
        };
        let mut value = HeaderValue::from_str(&value)
            .map_err(|_| ProviderError::invalid("invalid credential"))?;
        value.set_sensitive(true);
        headers.insert(name, value);
        if self.protocol == Protocol::Messages && !headers.contains_key("anthropic-version") {
            headers.insert("anthropic-version", HeaderValue::from_static("2023-06-01"));
        }
        Ok(headers)
    }
    fn endpoint(&self, model: &str, stream: bool) -> Result<Url, ProviderError> {
        let mut url = self.base.clone();
        match self.protocol {
            Protocol::Responses => {
                url = url
                    .join("responses")
                    .map_err(|_| ProviderError::invalid("invalid endpoint"))?;
            }
            Protocol::Messages => {
                url = url
                    .join("messages")
                    .map_err(|_| ProviderError::invalid("invalid endpoint"))?;
            }
            Protocol::Gemini => {
                let id = model.strip_prefix("models/").unwrap_or(model);
                if id.is_empty()
                    || id == "."
                    || id == ".."
                    || !id
                        .chars()
                        .all(|c| c.is_ascii_alphanumeric() || matches!(c, '-' | '_' | '.'))
                {
                    return Err(ProviderError::invalid(
                        "Gemini model must be a plain model ID or models/ID",
                    ));
                }
                url.path_segments_mut()
                    .map_err(|_| ProviderError::invalid("invalid base URL"))?
                    .pop_if_empty()
                    .push("models")
                    .push(&format!(
                        "{id}:{}",
                        if stream {
                            "streamGenerateContent"
                        } else {
                            "generateContent"
                        }
                    ));
                if stream {
                    url.query_pairs_mut().append_pair("alt", "sse");
                }
            }
        }
        Ok(url)
    }
    /// One network attempt only. Caller may apply bounded retries for explicit retryable errors.
    /// Cancellation drops the in-flight request/body; it never silently replays a tool operation.
    pub async fn call(
        &self,
        credentials: &Credentials,
        model: &str,
        body: &Value,
        stream: bool,
        cancel: &CancellationToken,
        mut progress: impl FnMut(stream::Progress),
    ) -> Result<Turn, ProviderError> {
        let url = self.endpoint(model, stream)?;
        let operation = async {
            let response = self
                .client
                .request(Method::POST, url)
                .headers(self.headers(credentials)?)
                .json(body)
                .send()
                .await
                .map_err(network)?;
            let status = response.status().as_u16();
            let retry = response
                .headers()
                .get("retry-after")
                .and_then(|v| v.to_str().ok())
                .map(str::to_owned);
            if !(200..300).contains(&status) {
                let bytes = bounded_body(response).await?;
                let raw = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
                return Err(classify_error(status, &raw, retry));
            }
            if stream {
                let mut assembler = stream::Assembler::new(self.protocol);
                let mut chunks = response.bytes_stream();
                while let Some(chunk) = chunks.next().await {
                    for event in assembler.push(&chunk.map_err(network)?)? {
                        progress(event);
                    }
                }
                assembler.finish()
            } else {
                let bytes = bounded_body(response).await?;
                let raw = serde_json::from_slice(&bytes)
                    .map_err(|_| ProviderError::protocol("invalid response JSON"))?;
                match self.protocol {
                    Protocol::Responses => responses::parse(raw),
                    Protocol::Messages => messages::parse(raw),
                    Protocol::Gemini => gemini::parse(raw),
                }
            }
        };
        tokio::select! {biased;_ = cancel.cancelled()=>Err(ProviderError::new(ErrorKind::Cancelled,"provider call cancelled")),result=operation=>result}
    }
    pub async fn list_models(
        &self,
        credentials: &Credentials,
        cursor: Option<&str>,
        cancel: &CancellationToken,
    ) -> Result<ModelPage, ProviderError> {
        let mut url = self
            .base
            .join("models")
            .map_err(|_| ProviderError::invalid("invalid models URL"))?;
        if let Some(cursor) = cursor {
            let key = match self.protocol {
                Protocol::Responses => "after",
                Protocol::Messages => "after_id",
                Protocol::Gemini => "pageToken",
            };
            url.query_pairs_mut().append_pair(key, cursor);
        }
        let operation = async {
            let response = self
                .client
                .get(url)
                .headers(self.headers(credentials)?)
                .send()
                .await
                .map_err(network)?;
            let status = response.status().as_u16();
            let retry = response
                .headers()
                .get("retry-after")
                .and_then(|v| v.to_str().ok())
                .map(str::to_owned);
            let bytes = bounded_body(response).await?;
            if !(200..300).contains(&status) {
                let raw = serde_json::from_slice(&bytes).unwrap_or(Value::Null);
                return Err(classify_error(status, &raw, retry));
            }
            let raw: Value = serde_json::from_slice(&bytes)
                .map_err(|_| ProviderError::protocol("invalid model list JSON"))?;
            parse_model_page(self.protocol, raw)
        };
        tokio::select! {biased;_=cancel.cancelled()=>Err(ProviderError::new(ErrorKind::Cancelled,"model listing cancelled")),result=operation=>result}
    }
}
fn network(_: reqwest::Error) -> ProviderError {
    ProviderError::new(ErrorKind::Network, "provider connection failed")
}
async fn bounded_body(response: reqwest::Response) -> Result<Vec<u8>, ProviderError> {
    let mut bytes = Vec::new();
    let mut stream = response.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(network)?;
        if bytes.len() + chunk.len() > 32 * 1024 * 1024 {
            return Err(ProviderError::protocol("provider response exceeds limit"));
        }
        bytes.extend_from_slice(&chunk);
    }
    Ok(bytes)
}
