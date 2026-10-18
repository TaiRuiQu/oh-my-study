//! Self-authored UI fixture; seeded results are explicitly synthetic, not model evidence.
use oh_my_study_core::{
    ai::providers::{Capabilities, Protocol},
    domain::{grading::*, *},
    execution::{Capability, CapabilityState, ExecutionMode, ExecutionPolicy},
    jobs::{DiagnosticKind, Job, JobEvent, JobState},
    storage::{ImportSource, Storage},
};
use serde_json::json;
use std::{collections::HashSet, path::PathBuf};

fn pdf(reference: bool) -> Vec<u8> {
    let text = if reference {
        "M2 SELF-AUTHORED REFERENCE / Question 1: Answer = 1"
    } else {
        "M2 SELF-AUTHORED MATH PAPER / Question 1: 1/2 + 1/2 = ? / Student: 2 / Teacher: -2"
    };
    let lines = text
        .split(" / ")
        .map(|line| format!("({line}) Tj 0 -26 Td"))
        .collect::<Vec<_>>()
        .join("\n");
    let stream =
        format!("BT /F1 16 Tf 40 780 Td\n{lines}\nET\n0.2 0.4 0.7 RG 40 600 480 100 re S\n");
    let objects=["<< /Type /Catalog /Pages 2 0 R >>".to_string(),"<< /Type /Pages /Count 1 /Kids [3 0 R] >>".into(),"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>".into(),format!("<< /Length {} >>\nstream\n{stream}endstream",stream.len()),"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>".into()];
    let mut bytes = b"%PDF-1.7\n".to_vec();
    let mut offsets = vec![0];
    for (i, obj) in objects.iter().enumerate() {
        offsets.push(bytes.len());
        bytes.extend_from_slice(format!("{} 0 obj\n{obj}\nendobj\n", i + 1).as_bytes());
    }
    let xref = bytes.len();
    bytes.extend_from_slice(format!("xref\n0 {}\n0000000000 65535 f \n", offsets.len()).as_bytes());
    for offset in offsets.iter().skip(1) {
        bytes.extend_from_slice(format!("{offset:010} 00000 n \n").as_bytes());
    }
    bytes.extend_from_slice(
        format!(
            "trailer\n<< /Size {} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF",
            offsets.len()
        )
        .as_bytes(),
    );
    bytes
}
fn persist(storage: &Storage, job: &Job) -> Result<(), String> {
    let event = JobEvent {
        id: format!("{}-{}", job.id, job.sequence),
        job_id: job.id.clone(),
        sequence: job.sequence,
        created_at: job.updated_at,
        kind: "synthetic_fixture".into(),
        payload: serde_json::to_value(job).unwrap(),
    };
    storage.save_job_event(
        serde_json::to_value(job).unwrap(),
        serde_json::to_value(event).unwrap(),
    )
}
#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let workspace = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .to_path_buf();
    let fixture_root = workspace.join("output/m2-desktop-acceptance");
    let directory = fixture_root.join(uuid::Uuid::new_v4().to_string());
    let storage = Storage::open(&directory)?;
    let model = ModelConfig {
        id: "m2-synthetic-no-provider-call".into(),
        capabilities: Capabilities {
            vision: Some(true),
            tools: Some(true),
            ..Default::default()
        },
        price_input: None,
        price_output: None,
        currency: "USD".into(),
    };
    let provider = Provider {
        id: "00000000-0000-4000-8000-000000000002".into(),
        name: "M2 合成验收（未配置密钥）".into(),
        protocol: Protocol::Responses,
        base_url: "http://127.0.0.1:1/v1/".into(),
        key_ref: None,
        has_key: false,
        models: vec![model.clone()],
    };
    storage.upsert_provider(provider.clone()).await?;
    for (name, mode) in [
        ("M2 自建教师批改数学卷", ImportMode::TeacherGraded),
        ("M2 自建 AI 批改数学卷", ImportMode::AiGrading),
        ("M2 自建空白数学卷", ImportMode::Blank),
    ] {
        if storage
            .list_documents()
            .await?
            .iter()
            .any(|d| d.name == name)
        {
            continue;
        }
        let document = storage
            .import_document(
                name.into(),
                mode,
                vec![
                    ImportSource {
                        name: "falsely-named.png".into(),
                        bytes: pdf(false),
                        role: "material".into(),
                    },
                    ImportSource {
                        name: "reference.pdf".into(),
                        bytes: pdf(true),
                        role: "reference".into(),
                    },
                ],
            )
            .await?;
        let mut job = Job {
            id: uuid::Uuid::new_v4().to_string(),
            kind: DiagnosticKind::Grading,
            state: JobState::Running,
            created_at: chrono::Utc::now().timestamp_millis() as u64,
            updated_at: chrono::Utc::now().timestamp_millis() as u64,
            progress: 0.0,
            message: "合成界面验收数据，不是模型结果".into(),
            sequence: 1,
            execution_policy: ExecutionPolicy::freeze(
                Some(ExecutionMode::BypassPermission),
                &Capability {
                    state: CapabilityState::Unknown,
                    reason: "self-authored UI fixture".into(),
                },
                false,
            ),
            pending_permission: None,
            error: None,
            input: Some(GradingInput {
                document: document.clone(),
                provider: provider.clone(),
                model: model.clone(),
                thinking: None,
                allow_unknown: false,
            }),
        };
        persist(&storage, &job)?;
        storage.begin_grading(&job)?;
        let source = Region {
            page_id: document.pages[0].id.clone(),
            x: 0.05,
            y: 0.05,
            width: 0.9,
            height: 0.3,
        };
        storage.save_grading_references(
            job.id.clone(),
            vec![ReferenceAnswer {
                key: "r1".into(),
                number: "1".into(),
                answer: "1".into(),
                explanation: "同分母分数相加".into(),
                sources: vec![Region {
                    page_id: document.pages[1].id.clone(),
                    x: 0.05,
                    y: 0.05,
                    width: 0.9,
                    height: 0.3,
                }],
                matched_draft_key: Some("q1".into()),
                uncertain_reason: None,
            }],
        )?;
        let grade = Grade {
            verdict: Verdict::Incorrect,
            earned: Some(0.0),
            possible: Some(2.0),
            source: if mode == ImportMode::TeacherGraded {
                GradeSource::Teacher
            } else {
                GradeSource::AiReference
            },
            reason: "合成验收：分数相加结果不正确".into(),
        };
        let question = Question {
            draft_key: "q1".into(),
            number: "1".into(),
            subject: "数学".into(),
            question_type: "计算".into(),
            content: vec![
                Content::Text {
                    text: "计算下列分式（自建界面验收题，非模型识别）".into(),
                },
                Content::Formula {
                    latex: r"\frac{1}{2}+\frac{1}{2}".into(),
                },
            ],
            options: vec![],
            points: Some(2.0),
            standard_answer: "1".into(),
            explanation: "分母保持不变，分子相加。".into(),
            knowledge_points: vec!["分数运算".into()],
            sources: vec![source.clone()],
            uncertainties: vec![],
            attempt: if mode == ImportMode::Blank {
                None
            } else {
                Some(Attempt {
                    student_answer: "2".into(),
                    student_regions: vec![source.clone()],
                    teacher: if mode == ImportMode::TeacherGraded {
                        Some(TeacherMark {
                            raw: "-2".into(),
                            regions: vec![source],
                            grade: Some(grade.clone()),
                        })
                    } else {
                        None
                    },
                    reference_key: Some("r1".into()),
                    ai_solution: "1".into(),
                    conflict: None,
                    grade,
                })
            },
            parts: vec![],
        };
        storage.save_grading_question(job.id.clone(), question)?;
        storage.finish_grading(
            job.id.clone(),
            document
                .pages
                .iter()
                .map(|p| Coverage {
                    page_id: p.id.clone(),
                    status: "covered".into(),
                    question_keys: if p.role == "material" {
                        vec!["q1".into()]
                    } else {
                        vec![]
                    },
                    reason: "合成界面验收".into(),
                })
                .collect(),
            document.pages.iter().map(|p| p.id.clone()).collect(),
            false,
            HashSet::from(["q1".into()]),
        )?;
        job.state = JobState::Completed;
        job.sequence += 1;
        job.progress = 100.0;
        persist(&storage, &job)?;
    }
    storage
        .save_settings(AppSettings {
            default_model: Some(format!("{}:{}", provider.id, model.id)),
            ..Default::default()
        })
        .await?;
    std::fs::write(
        fixture_root.join("latest.json"),
        serde_json::to_vec_pretty(&json!({"data_directory":directory,"synthetic":true}))?,
    )?;
    println!(
        "{}",
        json!({"data_directory":directory,"synthetic":true,"documents":storage.list_documents().await?})
    );
    Ok(())
}
