use super::*;
use std::{
    collections::BTreeMap,
    io::{Read, Write},
    net::TcpListener,
    thread,
};
use tokio_util::sync::CancellationToken;

fn fixture() -> Value {
    serde_json::from_str(include_str!(
        "../../../../tests/fixtures/protocols/round-trips.synthetic.json"
    ))
    .unwrap()
}
fn image() -> Image {
    Image{mime:"image/png".into(),base64:"iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZQAAAABJRU5ErkJggg==".into()}
}
fn tool() -> Tool {
    Tool {
        name: "read_image".into(),
        description: "Read a source page".into(),
        schema: json!({"type":"object","properties":{"page":{"type":"integer"}},"required":["page"]}),
    }
}
fn options() -> RequestOptions {
    RequestOptions {
        model: Model::manual("manual-model"),
        max_output_tokens: 4096,
        stream: false,
        thinking: None,
        allow_unknown_capabilities: true,
        extra: Map::new(),
    }
}

#[test]
fn responses_refusals_are_explicit_in_json_and_streams() {
    // SYNTHETIC refusal follows the documented native Responses content/event schemas.
    let raw = json!({"status":"completed","output":[{"type":"message","role":"assistant","content":[{"type":"refusal","refusal":"Synthetic refusal."}]}]});
    let error = responses::parse(raw.clone()).unwrap_err();
    assert_eq!(error.kind, ErrorKind::Refusal);
    assert!(!error.retryable);
    for event in [
        json!({"type":"response.refusal.delta","delta":"Synthetic refusal."}),
        json!({"type":"response.refusal.done","refusal":"Synthetic refusal."}),
    ] {
        let mut assembler = stream::Assembler::new(Protocol::Responses);
        assert_eq!(
            assembler.push(sse(event).as_bytes()).unwrap_err().kind,
            ErrorKind::Refusal
        );
    }
    let mut assembler = stream::Assembler::new(Protocol::Responses);
    assembler
        .push(sse(json!({"type":"response.completed","response":raw})).as_bytes())
        .unwrap();
    assert_eq!(assembler.finish().unwrap_err().kind, ErrorKind::Refusal);
}

#[test]
fn vision_checks_inspect_native_image_parts_instead_of_text_or_tool_arguments() {
    let mut o = options();
    o.model.capabilities.vision = Some(false);
    let text = "input_image image inlineData fileData";
    assert!(responses::request(&o, &responses::initial(text, &[]), &[]).is_ok());
    assert!(messages::request(&o, &messages::initial("image", &[]), &[]).is_ok());
    assert!(gemini::request(&o, &gemini::initial(text, &[]), &[]).is_ok());
    // JSON objects in function arguments are ordinary tool data, not native image content.
    assert!(responses::request(
        &o,
        &[json!({"type":"function_call","arguments":"{\"type\":\"input_image\"}"})],
        &[]
    )
    .is_ok());
    assert!(messages::request(
        &o,
        &[json!({"role":"assistant","content":[{"type":"tool_use","input":{"type":"image"}}]})],
        &[]
    )
    .is_ok());
    assert!(gemini::request(&o, &[json!({"role":"model","parts":[{"functionCall":{"args":{"inlineData":{"mimeType":"image/png"}}}}]})], &[]).is_ok());
    let images = vec![image()];
    assert!(responses::request(&o, &responses::initial(text, &images), &[]).is_err());
    assert!(messages::request(&o, &messages::initial(text, &images), &[]).is_err());
    assert!(gemini::request(&o, &gemini::initial(text, &images), &[]).is_err());
    assert!(responses::request(&o, &[json!({"type":"function_call_output","output":[{"type":"input_image","image_url":"data:image/png;base64,x"}]})], &[]).is_err());
    assert!(messages::request(&o, &[json!({"role":"user","content":[{"type":"tool_result","content":[{"type":"image","source":{}}]}]})], &[]).is_err());
    assert!(gemini::request(&o, &[json!({"role":"user","parts":[{"functionResponse":{"parts":[{"inlineData":{"mimeType":"image/png","data":"x"}}]}}]})], &[]).is_err());
    assert!(gemini::request(&o, &[json!({"role":"user","parts":[{"fileData":{"mimeType":"image/jpeg","fileUri":"https://example.invalid/image"}}]})], &[]).is_err());
    assert!(gemini::request(
        &o,
        &[json!({"role":"user","parts":[{"inlineData":{"mimeType":"audio/wav","data":"x"}}]})],
        &[]
    )
    .is_ok());
}
fn parse(protocol: Protocol, raw: Value) -> Turn {
    match protocol {
        Protocol::Responses => responses::parse(raw),
        Protocol::Messages => messages::parse(raw),
        Protocol::Gemini => gemini::parse(raw),
    }
    .unwrap()
}

#[test]
fn native_round_trips_preserve_opaque_context_and_tool_images() {
    for (protocol, key) in [
        (Protocol::Responses, "responses"),
        (Protocol::Messages, "messages"),
        (Protocol::Gemini, "gemini"),
    ] {
        let f = fixture();
        let turn = parse(protocol, f[key]["tool"].clone());
        assert_eq!(turn.calls.len(), 1);
        let results = vec![ToolResult {
            call: turn.calls[0].clone(),
            text: "page 1".into(),
            images: vec![image()],
            is_error: false,
        }];
        let mut history = match protocol {
            Protocol::Responses => responses::initial("识别整卷", &[image()]),
            Protocol::Messages => messages::initial("识别整卷", &[image()]),
            Protocol::Gemini => gemini::initial("识别整卷", &[image()]),
        };
        match protocol {
            Protocol::Responses => responses::continue_with(&mut history, &turn, &results),
            Protocol::Messages => messages::continue_with(&mut history, &turn, &results),
            Protocol::Gemini => gemini::continue_with(&mut history, &turn, &results),
        }
        .unwrap();
        let body = match protocol {
            Protocol::Responses => responses::request(&options(), &history, &[tool()]),
            Protocol::Messages => messages::request(&options(), &history, &[tool()]),
            Protocol::Gemini => gemini::request(&options(), &history, &[tool()]),
        }
        .unwrap();
        match protocol {
            Protocol::Responses => {
                assert_eq!(body["input"][1], f[key]["tool"]["output"][0]);
                assert_eq!(body["input"][3]["output"][1]["type"], "input_image");
                assert!(body.get("messages").is_none());
            }
            Protocol::Messages => {
                assert_eq!(body["messages"][1], turn.context);
                assert_eq!(
                    body["messages"][2]["content"][0]["content"][1]["type"],
                    "image"
                );
                assert!(body.get("input").is_none());
            }
            Protocol::Gemini => {
                assert_eq!(body["contents"][1], turn.context);
                assert_eq!(
                    body["contents"][2]["parts"][0]["functionResponse"]["parts"][0]["inlineData"]
                        ["mimeType"],
                    "image/png"
                );
                assert!(body.get("model").is_none());
            }
        }
        let completed = parse(protocol, f[key]["final"].clone());
        assert_eq!(completed.text, "完成。");
        assert!(completed.calls.is_empty());
        assert_eq!(turn.usage.input, Some(100));
        assert_eq!(turn.usage.output, Some(30));
        assert_eq!(turn.usage.total, Some(130));
    }
}

#[test]
fn thinking_is_native_and_parameters_are_never_silently_deleted() {
    let mut o = options();
    o.thinking = Some(Thinking::Responses {
        effort: "medium".into(),
    });
    let input = responses::initial("x", &[]);
    assert_eq!(
        responses::request(&o, &input, &[]).unwrap()["reasoning"]["effort"],
        "medium"
    );
    assert!(messages::request(&o, &[], &[]).is_err());
    o.allow_unknown_capabilities = false;
    assert!(responses::request(&o, &input, &[]).is_err());
    o.allow_unknown_capabilities = true;
    o.extra.insert("reasoning".into(), json!({"effort":"low"}));
    assert!(responses::request(&o, &input, &[]).is_err());
    o.extra.clear();
    o.thinking = Some(Thinking::Adaptive {
        effort: "high".into(),
    });
    assert_eq!(
        messages::request(&o, &[], &[]).unwrap()["output_config"]["effort"],
        "high"
    );
    o.thinking = Some(Thinking::Budget { tokens: 2048 });
    assert_eq!(
        messages::request(&o, &[], &[]).unwrap()["thinking"]["budget_tokens"],
        2048
    );
    o.thinking = Some(Thinking::GeminiLevel {
        level: "HIGH".into(),
    });
    assert_eq!(
        gemini::request(&o, &[], &[]).unwrap()["generationConfig"]["thinkingConfig"]
            ["thinkingLevel"],
        "HIGH"
    );
    o.thinking = Some(Thinking::GeminiBudget { tokens: 1024 });
    o.extra
        .insert("generationConfig".into(), json!({"temperature":0.2}));
    assert_eq!(
        gemini::request(&o, &[], &[]).unwrap()["generationConfig"]["temperature"],
        0.2
    );
    o.extra
        .insert("generationConfig".into(), json!({"thinkingConfig":{}}));
    assert!(gemini::request(&o, &[], &[]).is_err());
    o = options();
    o.model.capabilities.vision = Some(false);
    assert!(responses::request(&o, &responses::initial("x", &[image()]), &[]).is_err());
}

fn sse(event: Value) -> String {
    format!("data: {event}\r\n\r\n")
}
#[test]
fn native_sse_survives_arbitrary_utf8_boundaries_and_preserves_signatures() {
    let f = fixture();
    let response_sse = format!(
        ": SYNTHETIC\r\n{}{}",
        sse(json!({"type":"response.output_text.delta","delta":"中文"})),
        sse(json!({"type":"response.completed","response":f["responses"]["tool"]}))
    );
    let gemini_sse = format!(
        ": SYNTHETIC\r\n{}{}",
        sse(f["gemini"]["tool"].clone()),
        sse(json!({"usageMetadata":f["gemini"]["tool"]["usageMetadata"]}))
    );
    for (protocol, key, data) in [
        (Protocol::Responses, "responses", response_sse.as_str()),
        (
            Protocol::Messages,
            "messages",
            include_str!("../../../../tests/fixtures/protocols/messages.synthetic.sse"),
        ),
        (Protocol::Gemini, "gemini", gemini_sse.as_str()),
    ] {
        let mut assembler = stream::Assembler::new(protocol);
        let mut visible = Vec::new();
        for byte in data.as_bytes() {
            visible.extend(assembler.push(&[*byte]).unwrap());
        }
        let turn = assembler.finish().unwrap();
        let expected = parse(protocol, f[key]["tool"].clone());
        assert_eq!(turn.context, expected.context);
        assert_eq!(turn.usage, expected.usage);
        assert_eq!(turn.calls[0].arguments, json!({"page":1}));
        assert!(!serde_json::to_string(&visible)
            .unwrap()
            .contains("SIGNATURE"));
        // A second complete turn in streaming mode yields the same public text as JSON.
        let final_stream = match protocol {
            Protocol::Responses => {
                sse(json!({"type":"response.completed","response":f[key]["final"]}))
            }
            Protocol::Messages => format!(
                "{}{}{}{}{}{}",
                sse(
                    json!({"type":"message_start","message":{"id":"synthetic_final","type":"message","role":"assistant","content":[],"usage":{"input_tokens":100,"output_tokens":1}}})
                ),
                sse(
                    json!({"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}})
                ),
                sse(
                    json!({"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"完成。"}})
                ),
                sse(json!({"type":"content_block_stop","index":0})),
                sse(
                    json!({"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":20}})
                ),
                sse(json!({"type":"message_stop"}))
            ),
            Protocol::Gemini => sse(f[key]["final"].clone()),
        };
        let mut final_assembler = stream::Assembler::new(protocol);
        final_assembler.push(final_stream.as_bytes()).unwrap();
        assert_eq!(final_assembler.finish().unwrap().text, "完成。");
    }
}

#[test]
fn invalid_messages_state_and_output_caps_are_rejected() {
    let start = sse(json!({"type":"message_start","message":{"content":[]}}));
    let block = sse(
        json!({"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"x","name":"x","input":{}}}),
    );
    let mut a = stream::Assembler::new(Protocol::Messages);
    assert!(a.push(block.as_bytes()).is_err());
    let mut a = stream::Assembler::new(Protocol::Messages);
    a.push(start.as_bytes()).unwrap();
    a.push(block.as_bytes()).unwrap();
    assert!(a.push(block.as_bytes()).is_err());
    let mut a = stream::Assembler::new(Protocol::Messages);
    a.push(start.as_bytes()).unwrap();
    a.push(block.as_bytes()).unwrap();
    assert!(a
        .push(sse(json!({"type":"message_stop"})).as_bytes())
        .is_err());
    let f = fixture();
    let mut response = f["responses"]["final"].clone();
    response["status"] = json!("incomplete");
    assert_eq!(
        responses::parse(response).unwrap_err().kind,
        ErrorKind::Incomplete
    );
    let mut message = f["messages"]["final"].clone();
    message["stop_reason"] = json!("max_tokens");
    assert_eq!(
        messages::parse(message).unwrap_err().kind,
        ErrorKind::Incomplete
    );
    let mut gemini = f["gemini"]["final"].clone();
    gemini["candidates"][0]["finishReason"] = json!("MAX_TOKENS");
    assert_eq!(
        gemini::parse(gemini).unwrap_err().kind,
        ErrorKind::Incomplete
    );
}

fn read_http(socket: &mut std::net::TcpStream) -> String {
    socket
        .set_read_timeout(Some(std::time::Duration::from_secs(5)))
        .unwrap();
    let mut bytes = Vec::new();
    loop {
        let mut chunk = [0; 4096];
        let n = socket.read(&mut chunk).unwrap();
        if n == 0 {
            break;
        }
        bytes.extend_from_slice(&chunk[..n]);
        if let Some(end) = bytes.windows(4).position(|w| w == b"\r\n\r\n") {
            let headers = String::from_utf8_lossy(&bytes[..end]);
            let length = headers
                .lines()
                .find_map(|l| {
                    l.to_ascii_lowercase()
                        .strip_prefix("content-length: ")
                        .and_then(|v| v.parse::<usize>().ok())
                })
                .unwrap_or(0);
            if bytes.len() >= end + 4 + length {
                break;
            }
        }
    }
    String::from_utf8(bytes).unwrap()
}

#[tokio::test]
async fn native_http_endpoints_headers_bodies_and_cursor_requests() {
    for (protocol, key, endpoint, auth, cursor_name) in [
        (
            Protocol::Responses,
            "responses",
            "responses",
            "authorization: bearer test-key",
            "after",
        ),
        (
            Protocol::Messages,
            "messages",
            "messages",
            "x-api-key: test-key",
            "after_id",
        ),
        (
            Protocol::Gemini,
            "gemini",
            "models/manual-model:generateContent",
            "x-goog-api-key: test-key",
            "pageToken",
        ),
    ] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let final_body = fixture()[key]["final"].to_string();
        let page = match protocol {
            Protocol::Gemini => {
                json!({"models":[{"name":"models/custom"}],"nextPageToken":"next token"})
            }
            _ => json!({"data":[{"id":"custom"}],"has_more":true,"last_id":"next token"}),
        }
        .to_string();
        let server = thread::spawn(move || {
            for (index, response) in [final_body, page].iter().enumerate() {
                let (mut socket, _) = listener.accept().unwrap();
                let request = read_http(&mut socket);
                let lower = request.to_ascii_lowercase();
                assert!(lower.contains(auth));
                if protocol == Protocol::Messages {
                    assert!(lower.contains("anthropic-version: 2023-06-01"));
                }
                if index == 0 {
                    assert!(request.starts_with(&format!("POST /v1/{endpoint} HTTP/1.1")));
                    let body: Value =
                        serde_json::from_str(request.split("\r\n\r\n").nth(1).unwrap()).unwrap();
                    assert!(body
                        .get(match protocol {
                            Protocol::Responses => "input",
                            Protocol::Messages => "messages",
                            Protocol::Gemini => "contents",
                        })
                        .is_some());
                } else {
                    assert!(request
                        .starts_with(&format!("GET /v1/models?{cursor_name}=next+token HTTP/1.1")));
                }
                write!(socket,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",response.len(),response).unwrap();
            }
        });
        let t =
            transport::Transport::new(protocol, &format!("http://{addr}/v1/"), &BTreeMap::new())
                .unwrap();
        let key = transport::Credentials::new("test-key".into()).unwrap();
        let token = CancellationToken::new();
        let body = match protocol {
            Protocol::Responses => {
                responses::request(&options(), &responses::initial("x", &[]), &[])
            }
            Protocol::Messages => messages::request(&options(), &messages::initial("x", &[]), &[]),
            Protocol::Gemini => gemini::request(&options(), &gemini::initial("x", &[]), &[]),
        }
        .unwrap();
        assert_eq!(
            t.call(&key, "manual-model", &body, false, &token, |_| {})
                .await
                .unwrap()
                .text,
            "完成。"
        );
        assert_eq!(
            t.list_models(&key, Some("next token"), &token)
                .await
                .unwrap()
                .next,
            Some("next token".into())
        );
        server.join().unwrap();
        if protocol == Protocol::Gemini {
            assert!(t
                .call(&key, "..", &body, false, &token, |_| {})
                .await
                .is_err());
        }
    }
}

#[tokio::test]
async fn http_errors_do_not_retry_or_disclose_echoed_secret() {
    for status in [401, 429, 503] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let _ = read_http(&mut socket);
            let body = json!({"error":{"message":"SYNTHETIC_SECRET_MUST_NOT_APPEAR"}}).to_string();
            write!(socket,"HTTP/1.1 {status} Rejected\r\nContent-Length: {}\r\nRetry-After: 7\r\nConnection: close\r\n\r\n{body}",body.len()).unwrap();
        });
        let t = transport::Transport::new(
            Protocol::Responses,
            &format!("http://{addr}/v1"),
            &BTreeMap::new(),
        )
        .unwrap();
        let key = transport::Credentials::new("test-key".into()).unwrap();
        let error = t
            .call(
                &key,
                "manual",
                &json!({}),
                false,
                &CancellationToken::new(),
                |_| {},
            )
            .await
            .unwrap_err();
        assert_eq!(error.status, Some(status));
        assert_eq!(error.retry_after, Some("7".into()));
        assert!(!format!("{error:?}").contains("SYNTHETIC_SECRET"));
        server.join().unwrap();
    }
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    drop(listener);
    let t = transport::Transport::new(
        Protocol::Responses,
        &format!("http://{addr}/v1"),
        &BTreeMap::new(),
    )
    .unwrap();
    let key = transport::Credentials::new("test-key".into()).unwrap();
    assert_eq!(
        t.call(
            &key,
            "manual",
            &json!({}),
            false,
            &CancellationToken::new(),
            |_| {}
        )
        .await
        .unwrap_err()
        .kind,
        ErrorKind::Network
    );
}

#[tokio::test]
async fn model_list_http_status_survives_non_json_error_bodies() {
    for protocol in [Protocol::Responses, Protocol::Messages, Protocol::Gemini] {
        for (status, content_type, body, expected) in [
            (401, "text/plain", "", ErrorKind::Authentication),
            (
                429,
                "text/plain",
                "synthetic rate limit",
                ErrorKind::RateLimit,
            ),
            (
                503,
                "text/html",
                "<html>synthetic gateway error</html>",
                ErrorKind::Server,
            ),
        ] {
            let listener = TcpListener::bind("127.0.0.1:0").unwrap();
            let address = listener.local_addr().unwrap();
            let server = thread::spawn(move || {
                let (mut socket, _) = listener.accept().unwrap();
                let request = read_http(&mut socket);
                assert!(request.starts_with("GET /v1/models HTTP/1.1"));
                write!(socket, "HTTP/1.1 {status} Rejected\r\nContent-Type: {content_type}\r\nContent-Length: {}\r\nRetry-After: 11\r\nConnection: close\r\n\r\n{body}", body.len()).unwrap();
            });
            let transport = transport::Transport::new(
                protocol,
                &format!("http://{address}/v1/"),
                &BTreeMap::new(),
            )
            .unwrap();
            let credentials = transport::Credentials::new("synthetic-test-key".into()).unwrap();
            let error = transport
                .list_models(&credentials, None, &CancellationToken::new())
                .await
                .unwrap_err();
            assert_eq!(error.kind, expected);
            assert_eq!(error.status, Some(status));
            assert_eq!(error.retry_after.as_deref(), Some("11"));
            assert_eq!(error.retryable, status != 401);
            server.join().unwrap();
        }
    }
}

#[tokio::test]
async fn cancellation_aborts_an_inflight_body() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let token = CancellationToken::new();
    let server_token = token.clone();
    let server = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        let _ = read_http(&mut socket);
        write!(socket, "HTTP/1.1 200 OK\r\nContent-Length: 10000\r\n\r\n{{").unwrap();
        server_token.cancel();
        socket
            .set_read_timeout(Some(std::time::Duration::from_secs(2)))
            .unwrap();
        let mut b = [0];
        let _ = socket.read(&mut b);
    });
    let t = transport::Transport::new(
        Protocol::Responses,
        &format!("http://{addr}/v1"),
        &BTreeMap::new(),
    )
    .unwrap();
    let key = transport::Credentials::new("test-key".into()).unwrap();
    assert_eq!(
        t.call(&key, "manual", &json!({}), false, &token, |_| {})
            .await
            .unwrap_err()
            .kind,
        ErrorKind::Cancelled
    );
    server.join().unwrap();
}

#[tokio::test]
async fn chunked_http_streams_use_native_streaming_endpoints() {
    for (protocol, key, endpoint) in [
        (Protocol::Responses, "responses", "responses"),
        (Protocol::Messages, "messages", "messages"),
        (
            Protocol::Gemini,
            "gemini",
            "models/manual-model:streamGenerateContent?alt=sse",
        ),
    ] {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let f = fixture();
        let data = match protocol {
            Protocol::Responses => format!(
                "{}{}",
                sse(json!({"type":"response.output_text.delta","delta":"完成。"})),
                sse(json!({"type":"response.completed","response":f[key]["final"]}))
            ),
            Protocol::Messages => format!(
                "{}{}{}{}{}{}",
                sse(
                    json!({"type":"message_start","message":{"id":"synthetic_final","type":"message","role":"assistant","content":[],"usage":{"input_tokens":100,"output_tokens":1}}})
                ),
                sse(
                    json!({"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}})
                ),
                sse(
                    json!({"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"完成。"}})
                ),
                sse(json!({"type":"content_block_stop","index":0})),
                sse(
                    json!({"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":20}})
                ),
                sse(json!({"type":"message_stop"}))
            ),
            Protocol::Gemini => sse(f[key]["final"].clone()),
        };
        let server = thread::spawn(move || {
            let (mut socket, _) = listener.accept().unwrap();
            let request = read_http(&mut socket);
            assert!(request.starts_with(&format!("POST /v1/{endpoint} HTTP/1.1")));
            let body: Value =
                serde_json::from_str(request.split("\r\n\r\n").nth(1).unwrap()).unwrap();
            if protocol != Protocol::Gemini {
                assert_eq!(body["stream"], true);
            }
            write!(socket,"HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n").unwrap();
            for chunk in data.as_bytes().chunks(7) {
                write!(socket, "{:X}\r\n", chunk.len()).unwrap();
                socket.write_all(chunk).unwrap();
                socket.write_all(b"\r\n").unwrap();
            }
            socket.write_all(b"0\r\n\r\n").unwrap();
        });
        let mut o = options();
        o.stream = true;
        let body = match protocol {
            Protocol::Responses => responses::request(&o, &responses::initial("x", &[]), &[]),
            Protocol::Messages => messages::request(&o, &messages::initial("x", &[]), &[]),
            Protocol::Gemini => gemini::request(&o, &gemini::initial("x", &[]), &[]),
        }
        .unwrap();
        let t = transport::Transport::new(protocol, &format!("http://{addr}/v1"), &BTreeMap::new())
            .unwrap();
        let key = transport::Credentials::new("synthetic-test-key".into()).unwrap();
        let mut visible = String::new();
        let turn = t
            .call(
                &key,
                "manual-model",
                &body,
                true,
                &CancellationToken::new(),
                |p| {
                    if let stream::Progress::Text { text } = p {
                        visible.push_str(&text)
                    }
                },
            )
            .await
            .unwrap();
        assert_eq!(turn.text, "完成。");
        assert_eq!(visible, "完成。");
        server.join().unwrap();
    }
}

#[test]
fn truncated_streams_and_invalid_tool_results_cannot_be_completed() {
    for p in [Protocol::Responses, Protocol::Messages, Protocol::Gemini] {
        let mut a = stream::Assembler::new(p);
        a.push(b"data: {\"type\":\"ping\"}\n\n").unwrap();
        assert!(a.finish().is_err());
    }
    let mut a = stream::Assembler::new(Protocol::Responses);
    a.push(b"data: {").unwrap();
    assert!(a.finish().is_err());
    let turn = parse(Protocol::Responses, fixture()["responses"]["tool"].clone());
    let mut history = Vec::new();
    assert!(responses::continue_with(&mut history, &turn, &[]).is_err());
    assert!(history.is_empty());
}

#[test]
fn model_pagination_and_error_classes() {
    assert_eq!(parse_model_page(Protocol::Messages,json!({"data":[{"id":"manual","capabilities":{"image_input":{"supported":true},"tool_use":{"supported":true}}}],"has_more":true,"last_id":"manual"})).unwrap().next,Some("manual".into()));
    assert_eq!(
        parse_model_page(
            Protocol::Gemini,
            json!({"models":[{"name":"models/manual"}],"nextPageToken":"two"})
        )
        .unwrap()
        .next,
        Some("two".into())
    );
    assert!(
        parse_model_page(Protocol::Responses, json!({"data":[{"id":"arbitrary"}]}))
            .unwrap()
            .models[0]
            .capabilities
            .tools
            .is_none()
    );
    assert!(parse_model_page(Protocol::Messages, json!({"data":[],"has_more":true})).is_err());
    for (status, body, kind, retry) in [
        (401, json!({}), ErrorKind::Authentication, false),
        (403, json!({}), ErrorKind::Authentication, false),
        (429, json!({}), ErrorKind::RateLimit, true),
        (
            429,
            json!({"error":{"code":"insufficient_quota"}}),
            ErrorKind::Quota,
            false,
        ),
        (
            400,
            json!({"error":{"message":"parameter not supported"}}),
            ErrorKind::UnsupportedParameter,
            false,
        ),
        (503, json!({}), ErrorKind::Server, true),
    ] {
        let e = classify_error(status, &body, Some("5".into()));
        assert_eq!(e.kind, kind);
        assert_eq!(e.retryable, retry);
        assert_eq!(e.retry_after, Some("5".into()));
    }
}

#[tokio::test]
async fn real_http_transport_works_against_local_synthetic_server_and_cancels() {
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let addr = listener.local_addr().unwrap();
    let response = fixture()["responses"]["final"].to_string();
    let server = thread::spawn(move || {
        let (mut socket, _) = listener.accept().unwrap();
        socket
            .set_read_timeout(Some(std::time::Duration::from_secs(5)))
            .unwrap();
        let mut buffer = [0; 8192];
        let n = socket.read(&mut buffer).unwrap();
        let request = String::from_utf8_lossy(&buffer[..n]);
        assert!(request.starts_with("POST /v1/responses"));
        assert!(request
            .to_ascii_lowercase()
            .contains("authorization: bearer synthetic-test-key"));
        write!(socket,"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",response.len(),response).unwrap();
    });
    let t = transport::Transport::new(
        Protocol::Responses,
        &format!("http://{addr}/v1/"),
        &BTreeMap::new(),
    )
    .unwrap();
    let key = transport::Credentials::new("synthetic-test-key".into()).unwrap();
    let cancel = CancellationToken::new();
    let body = responses::request(&options(), &responses::initial("x", &[]), &[]).unwrap();
    assert_eq!(
        t.call(&key, "manual-model", &body, false, &cancel, |_| {})
            .await
            .unwrap()
            .text,
        "完成。"
    );
    server.join().unwrap();
    cancel.cancel();
    assert_eq!(
        t.call(&key, "manual-model", &body, false, &cancel, |_| {})
            .await
            .unwrap_err()
            .kind,
        ErrorKind::Cancelled
    );
    assert!(transport::Transport::new(
        Protocol::Responses,
        "http://example.com/v1",
        &BTreeMap::new()
    )
    .is_err());
}
