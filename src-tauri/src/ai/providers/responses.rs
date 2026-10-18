use super::*;

pub fn initial(text: &str, images: &[Image]) -> Vec<Value> {
    let mut content = vec![json!({"type":"input_text","text":text})];
    content.extend(images.iter().map(|i| initial_image(i, Protocol::Responses)));
    vec![json!({"role":"user","content":content})]
}

pub fn request(
    options: &RequestOptions,
    input: &[Value],
    tools: &[Tool],
) -> Result<Value, ProviderError> {
    options.validate(
        Protocol::Responses,
        input.iter().any(|item| {
            let blocks = if item["type"] == "function_call_output" {
                item["output"].as_array()
            } else {
                item["content"].as_array()
            };
            blocks.is_some_and(|blocks| blocks.iter().any(|block| block["type"] == "input_image"))
        }),
        !tools.is_empty(),
    )?;
    let mut body = json!({"model":options.model.id,"input":input,"max_output_tokens":options.max_output_tokens,"stream":options.stream,"store":false,"include":["reasoning.encrypted_content"],"tools":tools.iter().map(|t|json!({"type":"function","name":t.name,"description":t.description,"parameters":t.schema,"strict":false})).collect::<Vec<_>>()});
    if let Some(Thinking::Responses { effort }) = &options.thinking {
        body["reasoning"] = json!({"effort":effort});
    }
    extend(
        &mut body,
        &options.extra,
        &["reasoning", "previous_response_id", "conversation"],
    )?;
    Ok(body)
}

pub fn parse(raw: Value) -> Result<Turn, ProviderError> {
    if raw["status"] == "failed" || !raw["error"].is_null() {
        return Err(classify_error(400, &raw, None));
    }
    if raw["status"] != "completed" {
        return Err(ProviderError::new(
            ErrorKind::Incomplete,
            "Responses result did not complete",
        ));
    }
    let output = required_array(&raw, "output")?;
    let mut calls = Vec::new();
    let mut text = String::new();
    for item in output {
        if item["type"] == "function_call" {
            calls.push(ToolCall {
                id: Some(
                    item["call_id"]
                        .as_str()
                        .ok_or_else(|| ProviderError::protocol("missing call_id"))?
                        .into(),
                ),
                name: item["name"]
                    .as_str()
                    .ok_or_else(|| ProviderError::protocol("missing tool name"))?
                    .into(),
                arguments: json_arguments(&item["arguments"])?,
            });
        } else if item["type"] == "message" {
            if required_array(item, "content")?
                .iter()
                .any(|block| block["type"] == "refusal")
            {
                return Err(ProviderError::new(
                    ErrorKind::Refusal,
                    "provider declined the request",
                ));
            }
            text.push_str(&text_blocks(
                required_array(item, "content")?,
                "output_text",
                "text",
            ));
        }
    }
    let u = &raw["usage"];
    let input = count(u, "input_tokens");
    let read = u
        .pointer("/input_tokens_details/cached_tokens")
        .and_then(Value::as_u64);
    let usage = Usage {
        input,
        input_uncached: input.zip(read).and_then(|(a, b)| a.checked_sub(b)),
        cache_read: read,
        cache_write: None,
        output: count(u, "output_tokens"),
        reasoning: u
            .pointer("/output_tokens_details/reasoning_tokens")
            .and_then(Value::as_u64),
        total: count(u, "total_tokens"),
        raw: u.clone(),
    };
    Ok(Turn {
        protocol: Protocol::Responses,
        text,
        calls,
        context: Value::Array(output.clone()),
        usage,
        stop_reason: raw["status"].as_str().map(str::to_owned),
        raw,
    })
}

pub fn continue_with(
    input: &mut Vec<Value>,
    turn: &Turn,
    results: &[ToolResult],
) -> Result<(), ProviderError> {
    if turn.protocol != Protocol::Responses {
        return Err(ProviderError::invalid("wrong turn protocol"));
    }
    let mut appended = turn
        .context
        .as_array()
        .ok_or_else(|| ProviderError::protocol("invalid native context"))?
        .clone();
    validate_results(turn, results)?;
    for result in results {
        let text = if result.is_error {
            format!("Tool execution failed: {}", result.text)
        } else {
            result.text.clone()
        };
        let mut output = vec![json!({"type":"input_text","text":text})];
        output.extend(
            result
                .images
                .iter()
                .map(|i| initial_image(i, Protocol::Responses)),
        );
        appended
            .push(json!({"type":"function_call_output","call_id":result.call.id,"output":output}));
    }
    input.extend(appended);
    Ok(())
}

pub(crate) fn validate_results(turn: &Turn, results: &[ToolResult]) -> Result<(), ProviderError> {
    if results.len() != turn.calls.len() {
        return Err(ProviderError::invalid(
            "return exactly one result for each tool call",
        ));
    }
    let mut matched = vec![false; turn.calls.len()];
    for result in results {
        let index = turn
            .calls
            .iter()
            .enumerate()
            .position(|(i, c)| {
                !matched[i]
                    && c.id == result.call.id
                    && c.name == result.call.name
                    && c.arguments == result.call.arguments
            })
            .ok_or_else(|| ProviderError::invalid("tool result does not match a pending call"))?;
        matched[index] = true;
    }
    Ok(())
}
