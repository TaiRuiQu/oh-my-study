use super::*;

pub fn initial(text: &str, images: &[Image]) -> Vec<Value> {
    let mut content = vec![json!({"type":"text","text":text})];
    content.extend(images.iter().map(|i| initial_image(i, Protocol::Messages)));
    vec![json!({"role":"user","content":content})]
}

pub fn request(
    options: &RequestOptions,
    messages: &[Value],
    tools: &[Tool],
) -> Result<Value, ProviderError> {
    options.validate(
        Protocol::Messages,
        messages.iter().any(|message| {
            message["content"].as_array().is_some_and(|blocks| {
                blocks.iter().any(|block| {
                    block["type"] == "image"
                        || (block["type"] == "tool_result"
                            && block["content"].as_array().is_some_and(|content| {
                                content.iter().any(|part| part["type"] == "image")
                            }))
                })
            })
        }),
        !tools.is_empty(),
    )?;
    let mut body = json!({"model":options.model.id,"messages":messages,"max_tokens":options.max_output_tokens,"stream":options.stream,"tools":tools.iter().map(|t|json!({"name":t.name,"description":t.description,"input_schema":t.schema})).collect::<Vec<_>>()});
    match &options.thinking {
        Some(Thinking::Adaptive { effort }) => {
            body["thinking"] = json!({"type":"adaptive"});
            body["output_config"] = json!({"effort":effort});
        }
        Some(Thinking::Budget { tokens }) => {
            body["thinking"] = json!({"type":"enabled","budget_tokens":tokens})
        }
        _ => {}
    }
    extend(&mut body, &options.extra, &["thinking", "output_config"])?;
    Ok(body)
}

pub fn parse(raw: Value) -> Result<Turn, ProviderError> {
    if raw["type"] == "error" {
        return Err(classify_error(400, &raw, None));
    }
    let content = required_array(&raw, "content")?;
    let reason = raw["stop_reason"]
        .as_str()
        .ok_or_else(|| ProviderError::new(ErrorKind::Incomplete, "missing Messages stop_reason"))?;
    if !matches!(reason, "end_turn" | "tool_use" | "stop_sequence") {
        return Err(ProviderError::new(
            ErrorKind::Incomplete,
            "Messages stopped without completing turn",
        ));
    }
    let mut calls = Vec::new();
    for block in content.iter().filter(|v| v["type"] == "tool_use") {
        calls.push(ToolCall {
            id: Some(
                block["id"]
                    .as_str()
                    .ok_or_else(|| ProviderError::protocol("missing tool id"))?
                    .into(),
            ),
            name: block["name"]
                .as_str()
                .ok_or_else(|| ProviderError::protocol("missing tool name"))?
                .into(),
            arguments: json_arguments(&block["input"])?,
        });
    }
    let u = &raw["usage"];
    let uncached = count(u, "input_tokens");
    let read = count(u, "cache_read_input_tokens");
    let write = count(u, "cache_creation_input_tokens");
    // Missing cache fields in Messages represent no cache use; keep the original absence in raw.
    let input = uncached.map(|v| v + read.unwrap_or(0) + write.unwrap_or(0));
    let output = count(u, "output_tokens");
    let usage = Usage {
        input,
        input_uncached: uncached,
        cache_read: read,
        cache_write: write,
        output,
        reasoning: None,
        total: input.zip(output).map(|(a, b)| a + b),
        raw: u.clone(),
    };
    Ok(Turn {
        protocol: Protocol::Messages,
        text: text_blocks(content, "text", "text"),
        calls,
        context: json!({"role":"assistant","content":content}),
        usage,
        stop_reason: Some(reason.into()),
        raw,
    })
}

pub fn continue_with(
    messages: &mut Vec<Value>,
    turn: &Turn,
    results: &[ToolResult],
) -> Result<(), ProviderError> {
    if turn.protocol != Protocol::Messages {
        return Err(ProviderError::invalid("wrong turn protocol"));
    }
    responses::validate_results(turn, results)?;
    let mut content = Vec::new();
    for result in results {
        let mut blocks = vec![json!({"type":"text","text":result.text})];
        blocks.extend(
            result
                .images
                .iter()
                .map(|i| initial_image(i, Protocol::Messages)),
        );
        content.push(json!({"type":"tool_result","tool_use_id":result.call.id,"content":blocks,"is_error":result.is_error}));
    }
    messages.push(turn.context.clone());
    messages.push(json!({"role":"user","content":content}));
    Ok(())
}
