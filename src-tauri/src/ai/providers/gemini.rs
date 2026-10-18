use super::*;

pub fn initial(text: &str, images: &[Image]) -> Vec<Value> {
    let mut parts = vec![json!({"text":text})];
    parts.extend(images.iter().map(|i| initial_image(i, Protocol::Gemini)));
    vec![json!({"role":"user","parts":parts})]
}

pub fn request(
    options: &RequestOptions,
    contents: &[Value],
    tools: &[Tool],
) -> Result<Value, ProviderError> {
    options.validate(
        Protocol::Gemini,
        contents.iter().any(|content| {
            content["parts"].as_array().is_some_and(|parts| {
                parts.iter().any(|part| {
                    image_part(part)
                        || part
                            .pointer("/functionResponse/parts")
                            .and_then(Value::as_array)
                            .is_some_and(|parts| parts.iter().any(image_part))
                })
            })
        }),
        !tools.is_empty(),
    )?;
    let mut body = json!({"contents":contents,"generationConfig":{"maxOutputTokens":options.max_output_tokens,"candidateCount":1},"tools":[{"functionDeclarations":tools.iter().map(|t|json!({"name":t.name,"description":t.description,"parametersJsonSchema":t.schema})).collect::<Vec<_>>()}]});
    if tools.is_empty() {
        body.as_object_mut().unwrap().remove("tools");
    }
    match &options.thinking {
        Some(Thinking::GeminiLevel { level }) => {
            body["generationConfig"]["thinkingConfig"] = json!({"thinkingLevel":level})
        }
        Some(Thinking::GeminiBudget { tokens }) => {
            body["generationConfig"]["thinkingConfig"] = json!({"thinkingBudget":tokens})
        }
        _ => {}
    }
    // generationConfig needs deliberate merging, never replacing the managed thinking limit.
    let mut extra = options.extra.clone();
    if let Some(config) = extra.remove("generationConfig") {
        let map = config
            .as_object()
            .ok_or_else(|| ProviderError::invalid("generationConfig must be an object"))?;
        extend(
            &mut body["generationConfig"],
            map,
            &["thinkingConfig", "maxOutputTokens", "candidateCount"],
        )?;
    }
    extend(&mut body, &extra, &["tools"])?;
    Ok(body)
}

fn image_part(part: &Value) -> bool {
    ["inlineData", "fileData"].iter().any(|key| {
        part[*key]["mimeType"]
            .as_str()
            .is_some_and(|mime| mime.starts_with("image/"))
    })
}

pub fn parse(raw: Value) -> Result<Turn, ProviderError> {
    if !raw["error"].is_null() {
        return Err(classify_error(
            raw.pointer("/error/code")
                .and_then(Value::as_u64)
                .unwrap_or(400) as u16,
            &raw,
            None,
        ));
    }
    let candidates = required_array(&raw, "candidates")?;
    if candidates.len() != 1 {
        return Err(ProviderError::protocol(
            "expected exactly one Gemini candidate",
        ));
    }
    let candidate = &candidates[0];
    if candidate["finishReason"] != "STOP" {
        return Err(ProviderError::new(
            ErrorKind::Incomplete,
            "Gemini result blocked or incomplete",
        ));
    }
    let content = &candidate["content"];
    let parts = required_array(content, "parts")?;
    let mut calls = Vec::new();
    let mut text = String::new();
    for part in parts {
        if let Some(call) = part.get("functionCall") {
            calls.push(ToolCall {
                id: call["id"].as_str().map(str::to_owned),
                name: call["name"]
                    .as_str()
                    .ok_or_else(|| ProviderError::protocol("missing function name"))?
                    .into(),
                arguments: json_arguments(call.get("args").unwrap_or(&json!({})))?,
            });
        } else if part["thought"] != true {
            if let Some(s) = part["text"].as_str() {
                text.push_str(s);
            }
        }
    }
    let u = &raw["usageMetadata"];
    let input = count(u, "promptTokenCount");
    let read = count(u, "cachedContentTokenCount");
    let candidates = count(u, "candidatesTokenCount");
    let reasoning = count(u, "thoughtsTokenCount");
    // Gemini candidatesTokenCount excludes thoughtsTokenCount; normalized output includes both once.
    let output = match (candidates, reasoning) {
        (Some(a), b) => Some(a + b.unwrap_or(0)),
        (None, Some(b)) => Some(b),
        _ => None,
    };
    let usage = Usage {
        input,
        input_uncached: input.map(|i| i.saturating_sub(read.unwrap_or(0))),
        cache_read: read,
        cache_write: None,
        output,
        reasoning,
        total: count(u, "totalTokenCount"),
        raw: u.clone(),
    };
    Ok(Turn {
        protocol: Protocol::Gemini,
        text,
        calls,
        context: content.clone(),
        usage,
        stop_reason: Some("STOP".into()),
        raw,
    })
}

pub fn continue_with(
    contents: &mut Vec<Value>,
    turn: &Turn,
    results: &[ToolResult],
) -> Result<(), ProviderError> {
    if turn.protocol != Protocol::Gemini {
        return Err(ProviderError::invalid("wrong turn protocol"));
    }
    responses::validate_results(turn, results)?;
    let mut parts = Vec::new();
    for result in results {
        let mut response = json!({"name":result.call.name,"response":if result.is_error{json!({"error":result.text})}else{json!({"result":result.text})}});
        if let Some(id) = &result.call.id {
            response["id"] = json!(id);
        }
        if !result.images.is_empty() {
            response["parts"] = Value::Array(
                result
                    .images
                    .iter()
                    .map(|i| initial_image(i, Protocol::Gemini))
                    .collect(),
            );
        }
        parts.push(json!({"functionResponse":response}));
    }
    contents.push(turn.context.clone());
    contents.push(json!({"role":"user","parts":parts}));
    Ok(())
}
