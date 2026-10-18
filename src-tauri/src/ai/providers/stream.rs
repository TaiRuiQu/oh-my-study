//! Incremental UTF-8 safe SSE framing and native state reducers. EOF is not success.
use super::*;
use std::collections::BTreeMap;

const MAX_BUFFER: usize = 32 * 1024 * 1024;

#[derive(Default)]
pub struct SseDecoder {
    buffer: Vec<u8>,
    data: Vec<String>,
    event: Option<String>,
}

#[derive(Debug, Clone)]
pub struct SseEvent {
    pub event: Option<String>,
    pub data: String,
}

impl SseDecoder {
    pub fn push(&mut self, bytes: &[u8]) -> Result<Vec<SseEvent>, ProviderError> {
        self.buffer.extend_from_slice(bytes);
        if self.buffer.len() + self.data.iter().map(String::len).sum::<usize>() > MAX_BUFFER {
            return Err(ProviderError::protocol("SSE event exceeds limit"));
        }
        let mut events = Vec::new();
        while let Some(end) = self.buffer.iter().position(|b| *b == b'\n') {
            let mut line = self.buffer.drain(..=end).collect::<Vec<_>>();
            line.pop();
            if line.last() == Some(&b'\r') {
                line.pop();
            }
            let line = String::from_utf8(line)
                .map_err(|_| ProviderError::protocol("invalid UTF-8 SSE"))?;
            if line.is_empty() {
                if !self.data.is_empty() {
                    events.push(SseEvent {
                        event: self.event.take(),
                        data: self.data.join("\n"),
                    });
                    self.data.clear();
                } else {
                    self.event = None;
                }
            } else if !line.starts_with(':') {
                let (field, value) = line.split_once(':').unwrap_or((&line, ""));
                let value = value.strip_prefix(' ').unwrap_or(value);
                match field {
                    "data" => self.data.push(value.into()),
                    "event" => self.event = Some(value.into()),
                    _ => {}
                }
            }
        }
        Ok(events)
    }
    pub fn finish(&self) -> Result<(), ProviderError> {
        if !self.buffer.is_empty() || !self.data.is_empty() {
            Err(ProviderError::new(
                ErrorKind::Incomplete,
                "truncated SSE frame",
            ))
        } else {
            Ok(())
        }
    }
}

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
pub enum Progress {
    Text { text: String },
    Tool { name: String },
}

pub struct Assembler {
    protocol: Protocol,
    decoder: SseDecoder,
    raw: Value,
    blocks: BTreeMap<usize, Value>,
    arguments: BTreeMap<usize, String>,
    events: Vec<Value>,
    terminal: bool,
    bytes_seen: usize,
    started: bool,
    active_blocks: std::collections::BTreeSet<usize>,
}

impl Assembler {
    pub fn new(protocol: Protocol) -> Self {
        Self {
            protocol,
            decoder: SseDecoder::default(),
            raw: json!({}),
            blocks: BTreeMap::new(),
            arguments: BTreeMap::new(),
            events: Vec::new(),
            terminal: false,
            bytes_seen: 0,
            started: false,
            active_blocks: Default::default(),
        }
    }
    pub fn push(&mut self, bytes: &[u8]) -> Result<Vec<Progress>, ProviderError> {
        self.bytes_seen += bytes.len();
        if self.bytes_seen > MAX_BUFFER {
            return Err(ProviderError::protocol("stream exceeds limit"));
        }
        let mut progress = Vec::new();
        for frame in self.decoder.push(bytes)? {
            if frame.data == "[DONE]" {
                continue;
            }
            let event: Value = serde_json::from_str(&frame.data)
                .map_err(|_| ProviderError::protocol("invalid SSE JSON"))?;
            let kind = event["type"]
                .as_str()
                .or(frame.event.as_deref())
                .unwrap_or("")
                .to_owned();
            if kind == "error" || kind == "response.failed" {
                return Err(classify_error(400, &event, None));
            }
            if self.terminal {
                return Err(ProviderError::protocol(
                    "event received after terminal response",
                ));
            }
            match self.protocol {
                Protocol::Responses => match kind.as_str() {
                    "response.refusal.delta" | "response.refusal.done" => {
                        return Err(ProviderError::new(
                            ErrorKind::Refusal,
                            "provider declined the request",
                        ));
                    }
                    "response.output_text.delta" => {
                        if let Some(t) = event["delta"].as_str() {
                            progress.push(Progress::Text { text: t.into() });
                        }
                    }
                    "response.output_item.added" => {
                        if event.pointer("/item/type") == Some(&json!("function_call")) {
                            progress.push(Progress::Tool {
                                name: event
                                    .pointer("/item/name")
                                    .and_then(Value::as_str)
                                    .unwrap_or("")
                                    .into(),
                            });
                        }
                    }
                    "response.completed" => {
                        self.raw = event["response"].clone();
                        self.terminal = true;
                    }
                    "response.incomplete" => {
                        return Err(ProviderError::new(
                            ErrorKind::Incomplete,
                            "Responses stream incomplete",
                        ))
                    }
                    _ => {}
                },
                Protocol::Messages => match kind.as_str() {
                    "message_start" => {
                        if self.started {
                            return Err(ProviderError::protocol("duplicate message_start"));
                        }
                        self.started = true;
                        self.raw = event["message"].clone();
                    }
                    "content_block_start" => {
                        if !self.started {
                            return Err(ProviderError::protocol("content before message_start"));
                        }
                        let i = index(&event)?;
                        let block = event["content_block"].clone();
                        if self.blocks.contains_key(&i) {
                            return Err(ProviderError::protocol("duplicate content block index"));
                        }
                        self.active_blocks.insert(i);
                        if block["type"] == "tool_use" {
                            progress.push(Progress::Tool {
                                name: block["name"].as_str().unwrap_or("").into(),
                            });
                        }
                        self.blocks.insert(i, block);
                    }
                    "content_block_delta" => {
                        let i = index(&event)?;
                        let delta = &event["delta"];
                        if !self.active_blocks.contains(&i) {
                            return Err(ProviderError::protocol(
                                "delta outside active content block",
                            ));
                        }
                        let block = self.blocks.get_mut(&i).ok_or_else(|| {
                            ProviderError::protocol("delta before content block start")
                        })?;
                        match delta["type"].as_str().unwrap_or("") {
                            "text_delta" => {
                                append(block, "text", &delta["text"])?;
                                if let Some(t) = delta["text"].as_str() {
                                    progress.push(Progress::Text { text: t.into() });
                                }
                            }
                            "thinking_delta" => append(block, "thinking", &delta["thinking"])?,
                            "signature_delta" => append(block, "signature", &delta["signature"])?,
                            "input_json_delta" => {
                                self.arguments.entry(i).or_default().push_str(
                                    delta["partial_json"].as_str().ok_or_else(|| {
                                        ProviderError::protocol("missing partial_json")
                                    })?,
                                );
                            }
                            _ => {}
                        }
                    }
                    "content_block_stop" => {
                        let i = index(&event)?;
                        if !self.active_blocks.remove(&i) {
                            return Err(ProviderError::protocol("stop outside active block"));
                        }
                        if let Some(s) = self.arguments.remove(&i) {
                            if !s.is_empty() {
                                let args = serde_json::from_str(&s).map_err(|_| {
                                    ProviderError::protocol("truncated tool arguments")
                                })?;
                                self.blocks.get_mut(&i).ok_or_else(|| {
                                    ProviderError::protocol("stop before start")
                                })?["input"] = args;
                            }
                        }
                    }
                    "message_delta" => {
                        if !self.started {
                            return Err(ProviderError::protocol("delta before message_start"));
                        }
                        if let Some(map) = event["delta"].as_object() {
                            for (k, v) in map {
                                self.raw[k] = v.clone();
                            }
                        }
                        if let Some(map) = event["usage"].as_object() {
                            if !self.raw["usage"].is_object() {
                                self.raw["usage"] = json!({});
                            }
                            for (k, v) in map {
                                self.raw["usage"][k] = v.clone();
                            }
                        }
                    }
                    "message_stop" => {
                        if !self.started
                            || !self.arguments.is_empty()
                            || !self.active_blocks.is_empty()
                        {
                            return Err(ProviderError::protocol(
                                "unclosed or missing message/content block",
                            ));
                        }
                        self.raw["content"] = Value::Array(self.blocks.values().cloned().collect());
                        self.terminal = true;
                    }
                    _ => {}
                },
                Protocol::Gemini => {
                    if !event["error"].is_null() {
                        return Err(classify_error(
                            event
                                .pointer("/error/code")
                                .and_then(Value::as_u64)
                                .unwrap_or(400) as u16,
                            &event,
                            None,
                        ));
                    }
                    if let Some(candidates) = event["candidates"].as_array() {
                        if candidates.len() != 1 {
                            return Err(ProviderError::protocol(
                                "expected one streaming Gemini candidate",
                            ));
                        }
                        let candidate = &candidates[0];
                        if !self.raw["candidates"].is_array() {
                            self.raw["candidates"] =
                                json!([{"content":{"role":"model","parts":[]}}]);
                        }
                        if let Some(parts) = candidate
                            .pointer("/content/parts")
                            .and_then(Value::as_array)
                        {
                            for part in parts {
                                if part["thought"] != true {
                                    if let Some(t) = part["text"].as_str() {
                                        progress.push(Progress::Text { text: t.into() });
                                    }
                                }
                                if let Some(t) =
                                    part.pointer("/functionCall/name").and_then(Value::as_str)
                                {
                                    progress.push(Progress::Tool { name: t.into() });
                                }
                                self.raw["candidates"][0]["content"]["parts"]
                                    .as_array_mut()
                                    .unwrap()
                                    .push(part.clone());
                            }
                        }
                        if let Some(reason) = candidate.get("finishReason") {
                            self.raw["candidates"][0]["finishReason"] = reason.clone();
                        }
                    }
                    if let Some(u) = event.get("usageMetadata") {
                        self.raw["usageMetadata"] = u.clone();
                    }
                    // Gemini can send usage after finishReason. EOF is checked against STOP in finish().
                }
            }
            self.events.push(event);
        }
        Ok(progress)
    }
    pub fn finish(mut self) -> Result<Turn, ProviderError> {
        self.decoder.finish()?;
        if self.protocol != Protocol::Gemini && !self.terminal {
            return Err(ProviderError::new(
                ErrorKind::Incomplete,
                "stream ended before terminal event",
            ));
        }
        self.raw["_stream_events"] = Value::Array(self.events);
        match self.protocol {
            Protocol::Responses => responses::parse(self.raw),
            Protocol::Messages => messages::parse(self.raw),
            Protocol::Gemini => gemini::parse(self.raw),
        }
    }
}
fn index(event: &Value) -> Result<usize, ProviderError> {
    event["index"]
        .as_u64()
        .map(|v| v as usize)
        .ok_or_else(|| ProviderError::protocol("missing block index"))
}
fn append(block: &mut Value, key: &str, delta: &Value) -> Result<(), ProviderError> {
    let mut text = block[key].as_str().unwrap_or("").to_owned();
    text.push_str(
        delta
            .as_str()
            .ok_or_else(|| ProviderError::protocol("invalid text delta"))?,
    );
    block[key] = json!(text);
    Ok(())
}
