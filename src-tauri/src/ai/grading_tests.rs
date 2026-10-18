use super::*;
use crate::{
    domain::*,
    execution::{Capability, CapabilityState, ExecutionMode, ExecutionPolicy},
    jobs::{JobConfig, JobManager, JobState},
    storage::ImportSource,
};
use std::{
    io::{Read, Write},
    net::TcpListener,
    sync::Arc,
    thread,
};

struct TestRunner(GradingRunner);
impl BusinessRunner for TestRunner {
    fn run(&self, job: &Job, ctx: &JobContext<'_>) -> Result<(), String> {
        tokio::runtime::Builder::new_current_thread()
            .enable_all()
            .build()
            .unwrap()
            .block_on(self.0.run_async(
                job,
                ctx,
                &Credentials::new("synthetic-loopback-key".into()).unwrap(),
            ))
    }
}
fn read_request(socket: &mut std::net::TcpStream) -> Value {
    socket.set_nonblocking(false).unwrap();
    socket
        .set_read_timeout(Some(Duration::from_secs(5)))
        .unwrap();
    let mut bytes = vec![];
    let mut buf = [0; 8192];
    let offset = loop {
        let n = socket.read(&mut buf).unwrap();
        assert!(n > 0);
        bytes.extend_from_slice(&buf[..n]);
        if let Some(i) = bytes.windows(4).position(|v| v == b"\r\n\r\n") {
            break i + 4;
        }
    };
    let headers = String::from_utf8_lossy(&bytes[..offset]);
    let len = headers
        .lines()
        .find_map(|l| {
            l.to_ascii_lowercase()
                .strip_prefix("content-length: ")
                .map(str::to_owned)
        })
        .unwrap()
        .parse::<usize>()
        .unwrap();
    while bytes.len() < offset + len {
        let n = socket.read(&mut buf).unwrap();
        assert!(n > 0);
        bytes.extend_from_slice(&buf[..n]);
    }
    serde_json::from_slice(&bytes[offset..offset + len]).unwrap()
}
fn respond(socket: &mut std::net::TcpStream, protocol: Protocol, calls: Vec<(&str, Value)>) {
    let body = match protocol {
        Protocol::Responses => {
            let raw = json!({"type":"response.completed","response":{"status":"completed","output":calls.iter().enumerate().map(|(i,(name,args))|json!({"type":"function_call","call_id":format!("c{i}"),"name":name,"arguments":args.to_string()})).collect::<Vec<_>>(),"usage":{"input_tokens":100,"output_tokens":10}}});
            format!("event: response.completed\ndata: {raw}\n\n")
        }
        Protocol::Messages => {
            let mut s = format!(
                "event: message_start\ndata: {}\n\n",
                json!({"type":"message_start","message":{"type":"message","role":"assistant","content":[],"usage":{"input_tokens":100}}})
            );
            for (i, (name, args)) in calls.iter().enumerate() {
                s+=&format!("event: content_block_start\ndata: {}\n\nevent: content_block_stop\ndata: {}\n\n",json!({"type":"content_block_start","index":i,"content_block":{"type":"tool_use","id":format!("c{i}"),"name":name,"input":args}}),json!({"type":"content_block_stop","index":i}));
            }
            s += &format!(
                "event: message_delta\ndata: {}\n\nevent: message_stop\ndata: {}\n\n",
                json!({"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":10}}),
                json!({"type":"message_stop"})
            );
            s
        }
        Protocol::Gemini => format!(
            "data: {}\n\n",
            json!({"candidates":[{"content":{"role":"model","parts":calls.iter().map(|(name,args)|json!({"functionCall":{"name":name,"args":args},"thoughtSignature":"opaque-test-signature"})).collect::<Vec<_>>()},"finishReason":"STOP"}],"usageMetadata":{"promptTokenCount":100,"candidatesTokenCount":10}})
        ),
    };
    socket.write_all(format!("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",body.len()).as_bytes()).unwrap();
}
#[test]
fn three_native_whole_paper_loops_save_once_reject_missing_coverage_and_finish() {
    for protocol in [Protocol::Responses, Protocol::Messages, Protocol::Gemini] {
        let root = std::env::temp_dir().join(format!("study-agent-{}", uuid::Uuid::new_v4()));
        let storage = Storage::open(&root).unwrap();
        let runtime = tokio::runtime::Runtime::new().unwrap();
        let mut bytes = Cursor::new(vec![]);
        image::DynamicImage::new_rgb8(30, 40)
            .write_to(&mut bytes, image::ImageFormat::Png)
            .unwrap();
        let document = runtime
            .block_on(storage.import_document(
                "合成样卷".into(),
                ImportMode::Blank,
                vec![ImportSource {
                    name: "test".into(),
                    bytes: bytes.into_inner(),
                    role: "material".into(),
                }],
            ))
            .unwrap();
        let page = document.pages[0].id.clone();
        let question = json!({"draft_key":"q1","number":"1","subject":"数学","question_type":"填空","content":[{"kind":"text","text":"1+1=?"}],"options":[],"points":2,"standard_answer":"2","explanation":"相加","knowledge_points":["整数"],"sources":[{"page_id":page,"x":0,"y":0,"width":1,"height":1}],"uncertainties":[],"attempt":null,"parts":[]});
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let address = listener.local_addr().unwrap();
        listener.set_nonblocking(true).unwrap();
        let server = thread::spawn(move || {
            for ordinal in 0..4 {
                let deadline = Instant::now() + Duration::from_secs(8);
                let mut socket = loop {
                    match listener.accept() {
                        Ok((s, _)) => break s,
                        Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                            assert!(
                                Instant::now() < deadline,
                                "agent failed to request round {ordinal}"
                            );
                            thread::sleep(Duration::from_millis(5));
                        }
                        Err(e) => panic!("{e}"),
                    }
                };
                let body = read_request(&mut socket);
                assert!(body.to_string().contains("save_question"));
                if protocol == Protocol::Gemini {
                    assert!(body["tools"][0]["functionDeclarations"][0]
                        .get("parametersJsonSchema")
                        .is_some());
                }
                let calls = match ordinal {
                    0 => vec![("read_page", json!({"page_id":page}))],
                    1 => {
                        assert!(
                            body.to_string().contains("image/png"),
                            "real tool result image must round-trip"
                        );
                        vec![
                            ("save_question", question.clone()),
                            ("save_question", question.clone()),
                            ("finish", json!({"coverage":[]})),
                        ]
                    }
                    2 => {
                        assert!(
                            body.to_string().contains("error"),
                            "coverage error must return to model for correction"
                        );
                        vec![(
                            "finish",
                            json!({"coverage":[{"page_id":page,"status":"covered","question_keys":["q1"],"reason":""}]}),
                        )]
                    }
                    _ => unreachable!(),
                };
                respond(&mut socket, protocol, calls);
                if ordinal == 2 {
                    break;
                }
            }
        });
        let model = ModelConfig {
            id: "synthetic-vision".into(),
            capabilities: Capabilities {
                vision: Some(true),
                tools: Some(true),
                ..Default::default()
            },
            price_input: Some(2.0),
            price_output: Some(4.0),
            currency: "USD".into(),
        };
        let input = GradingInput {
            document: document.clone(),
            provider: Provider {
                id: uuid::Uuid::new_v4().to_string(),
                name: "synthetic".into(),
                protocol,
                base_url: format!("http://{address}/v1/"),
                key_ref: None,
                has_key: false,
                models: vec![model.clone()],
            },
            model,
            thinking: None,
            allow_unknown: false,
        };
        let grading = GradingRunner {
            storage: storage.clone(),
        };
        let mut viewed = HashSet::new();
        let mut images = vec![];
        assert!(runtime
            .block_on(grading.read_page(
                &input,
                ReadPage {
                    page_id: input.document.pages[0].id.clone(),
                    crop: None,
                    rotation: Some(17)
                },
                &mut viewed,
                &mut images
            ))
            .is_err());
        assert!(
            viewed.is_empty(),
            "failed image reads must not satisfy coverage"
        );
        let runner = Arc::new(TestRunner(grading));
        let manager = JobManager::with_runner(
            Arc::new(storage.clone()),
            JobConfig {
                private_runtime: root.join("unused"),
                results_root: root.join("jobs"),
            },
            runner,
        )
        .unwrap();
        let policy = ExecutionPolicy::freeze(
            Some(ExecutionMode::BypassPermission),
            &Capability {
                state: CapabilityState::Unknown,
                reason: "test".into(),
            },
            false,
        );
        let job = manager
            .submit_input(DiagnosticKind::Grading, policy, Some(input))
            .unwrap();
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            let job = manager.get(&job.id).unwrap();
            if job.state.is_terminal() {
                assert_eq!(job.state, JobState::Completed, "{:?}", job.error);
                break;
            }
            assert!(Instant::now() < deadline);
            thread::sleep(Duration::from_millis(5));
        }
        server.join().unwrap();
        let result = runtime
            .block_on(storage.grading_result(document.id))
            .unwrap();
        assert_eq!(result.questions.len(), 1);
        assert_eq!(result.coverage.len(), 1);
        assert_eq!(result.model_calls.len(), 3);
        assert!(result.model_calls[0]["estimated_cost"].as_f64().unwrap() > 0.0);
        manager.shutdown().unwrap();
        drop(manager);
        drop(storage);
        let _ = std::fs::remove_dir_all(root);
    }
}

#[test]
fn recursive_tool_schema_keeps_definitions_and_reference_task_has_no_execution_tools() {
    let grading = tools(false);
    let question = &grading
        .iter()
        .find(|t| t.name == "save_question")
        .unwrap()
        .schema;
    assert!(question["definitions"]["Question"].is_object());
    assert!(question["properties"]["parts"]["items"]["$ref"].is_string());
    assert!(!tools(true)
        .iter()
        .any(|t| matches!(t.name.as_str(), "save_question" | "python" | "shell")));
}

#[test]
fn cancelling_inflight_model_request_releases_queue_without_late_results() {
    let root = std::env::temp_dir().join(format!("study-model-cancel-{}", uuid::Uuid::new_v4()));
    let storage = Storage::open(&root).unwrap();
    let runtime = tokio::runtime::Runtime::new().unwrap();
    let mut bytes = Cursor::new(vec![]);
    image::DynamicImage::new_rgb8(4, 4)
        .write_to(&mut bytes, image::ImageFormat::Png)
        .unwrap();
    let document = runtime
        .block_on(storage.import_document(
            "取消测试".into(),
            ImportMode::Blank,
            vec![ImportSource {
                name: "test".into(),
                bytes: bytes.into_inner(),
                role: "material".into(),
            }],
        ))
        .unwrap();
    let listener = TcpListener::bind("127.0.0.1:0").unwrap();
    let address = listener.local_addr().unwrap();
    listener.set_nonblocking(true).unwrap();
    let (received_tx, received_rx) = std::sync::mpsc::channel();
    let (release_tx, release_rx) = std::sync::mpsc::channel();
    let server = thread::spawn(move || {
        let deadline = Instant::now() + Duration::from_secs(6);
        let mut socket = loop {
            match listener.accept() {
                Ok((s, _)) => break s,
                Err(e) if e.kind() == std::io::ErrorKind::WouldBlock => {
                    assert!(Instant::now() < deadline);
                    thread::sleep(Duration::from_millis(5));
                }
                Err(e) => panic!("{e}"),
            }
        };
        read_request(&mut socket);
        received_tx.send(()).unwrap();
        let _ = release_rx.recv_timeout(Duration::from_secs(8));
        let _ = socket.write_all(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\n{}");
    });
    let model = ModelConfig {
        id: "synthetic".into(),
        capabilities: Capabilities {
            vision: Some(true),
            tools: Some(true),
            ..Default::default()
        },
        price_input: None,
        price_output: None,
        currency: "USD".into(),
    };
    let input = GradingInput {
        document: document.clone(),
        provider: Provider {
            id: uuid::Uuid::new_v4().to_string(),
            name: "synthetic".into(),
            protocol: Protocol::Responses,
            base_url: format!("http://{address}/v1/"),
            key_ref: None,
            has_key: false,
            models: vec![],
        },
        model,
        thinking: None,
        allow_unknown: false,
    };
    let manager = JobManager::with_runner(
        Arc::new(storage.clone()),
        JobConfig {
            private_runtime: root.join("unused"),
            results_root: root.join("jobs"),
        },
        Arc::new(TestRunner(GradingRunner {
            storage: storage.clone(),
        })),
    )
    .unwrap();
    let policy = ExecutionPolicy::freeze(
        Some(ExecutionMode::BypassPermission),
        &Capability {
            state: CapabilityState::Unknown,
            reason: "test".into(),
        },
        false,
    );
    let job = manager
        .submit_input(DiagnosticKind::Grading, policy.clone(), Some(input))
        .unwrap();
    received_rx.recv_timeout(Duration::from_secs(6)).unwrap();
    manager.cancel(&job.id).unwrap();
    let next = manager.submit(DiagnosticKind::Storage, policy).unwrap();
    let deadline = Instant::now() + Duration::from_secs(4);
    while manager.get(&next.id).unwrap().state != JobState::Completed {
        assert!(
            Instant::now() < deadline,
            "cancelled HTTP request retained the global queue slot"
        );
        thread::sleep(Duration::from_millis(5));
    }
    assert_eq!(manager.get(&job.id).unwrap().state, JobState::Cancelled);
    let result = runtime
        .block_on(storage.grading_result(document.id))
        .unwrap();
    assert!(result.questions.is_empty());
    assert!(result.model_calls.is_empty());
    release_tx.send(()).unwrap();
    server.join().unwrap();
    manager.shutdown().unwrap();
    drop(manager);
    drop(storage);
    let _ = std::fs::remove_dir_all(root);
}
