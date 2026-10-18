use oh_my_study_core::{
    ai::providers::{Capabilities, Protocol},
    domain::{grading::*, *},
    execution::{Capability, CapabilityState, ExecutionMode, ExecutionPolicy},
    jobs::{DiagnosticKind, Job, JobEvent, JobState},
    storage::{ImportSource, Storage},
};
use serde_json::json;
use std::{collections::HashSet, io::Cursor, path::PathBuf};

struct Fixture {
    path: PathBuf,
    storage: Storage,
}
impl Fixture {
    fn new() -> Self {
        let path = std::env::temp_dir().join(format!("study-grading-{}", uuid::Uuid::new_v4()));
        let storage = Storage::open(&path).unwrap();
        Self { path, storage }
    }
    async fn document(&self, mode: ImportMode) -> Document {
        let mut png = Cursor::new(vec![]);
        image::DynamicImage::new_rgb8(20, 30)
            .write_to(&mut png, image::ImageFormat::Png)
            .unwrap();
        self.storage
            .import_document(
                "自建整卷".into(),
                mode,
                vec![ImportSource {
                    name: "wrong.pdf".into(),
                    bytes: png.into_inner(),
                    role: "material".into(),
                }],
            )
            .await
            .unwrap()
    }
    fn job(&self, document: Document) -> Job {
        let id = uuid::Uuid::new_v4().to_string();
        let provider = Provider {
            id: uuid::Uuid::new_v4().to_string(),
            name: "test".into(),
            protocol: Protocol::Responses,
            base_url: "http://127.0.0.1:1/v1".into(),
            key_ref: None,
            has_key: false,
            models: vec![],
        };
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
        let job = Job {
            id,
            kind: DiagnosticKind::Grading,
            state: JobState::Running,
            created_at: 1,
            updated_at: 1,
            progress: 1.0,
            message: "test".into(),
            sequence: 1,
            execution_policy: ExecutionPolicy::freeze(
                Some(ExecutionMode::BypassPermission),
                &Capability {
                    state: CapabilityState::Unknown,
                    reason: "test".into(),
                },
                false,
            ),
            pending_permission: None,
            error: None,
            input: Some(GradingInput {
                document,
                provider,
                model,
                thinking: None,
                allow_unknown: false,
            }),
        };
        self.persist(&job);
        self.storage.begin_grading(&job).unwrap();
        job
    }
    fn persist(&self, job: &Job) {
        let event = JobEvent {
            id: format!("{}-{}", job.id, job.sequence),
            job_id: job.id.clone(),
            sequence: job.sequence,
            created_at: job.updated_at,
            kind: "test".into(),
            payload: serde_json::to_value(job).unwrap(),
        };
        self.storage
            .save_job_event(
                serde_json::to_value(job).unwrap(),
                serde_json::to_value(event).unwrap(),
            )
            .unwrap();
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.path);
    }
}
fn question(doc: &Document) -> Question {
    serde_json::from_value(json!({"draft_key":"q1","number":"1","subject":"数学","question_type":"填空","content":[{"kind":"text","text":"1+1=？"}],"options":[],"points":2.0,"standard_answer":"2","explanation":"整数相加","knowledge_points":["整数"],"sources":[{"page_id":doc.pages[0].id,"x":0.0,"y":0.0,"width":1.0,"height":1.0}],"uncertainties":[],"attempt":{"student_answer":"3","student_regions":[],"teacher":null,"reference_key":null,"ai_solution":"2","conflict":null,"grade":{"verdict":"incorrect","earned":0.0,"possible":2.0,"source":"ai_solution","reason":"算术错误"}},"parts":[]})).unwrap()
}
fn coverage(doc: &Document) -> Vec<Coverage> {
    vec![Coverage {
        page_id: doc.pages[0].id.clone(),
        status: "covered".into(),
        question_keys: vec!["q1".into()],
        reason: String::new(),
    }]
}

#[tokio::test]
async fn save_idempotence_is_scoped_to_document_and_cancel_blocks_late_writes() {
    let f = Fixture::new();
    let d = f.document(ImportMode::AiGrading).await;
    let mut job = f.job(d.clone());
    let q = question(&d);
    let first = f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .unwrap();
    let again = f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .unwrap();
    assert_eq!(first.id, again.id);
    assert_eq!(f.storage.list_questions().await.unwrap().len(), 1);
    assert!(f
        .storage
        .finish_grading(
            job.id.clone(),
            vec![],
            HashSet::new(),
            false,
            HashSet::from(["q1".into()])
        )
        .is_err());
    assert!(f
        .storage
        .finish_grading(
            job.id.clone(),
            coverage(&d),
            HashSet::new(),
            false,
            HashSet::from(["q1".into()])
        )
        .is_err());
    f.storage
        .finish_grading(
            job.id.clone(),
            coverage(&d),
            HashSet::from([d.pages[0].id.clone()]),
            false,
            HashSet::from(["q1".into()]),
        )
        .unwrap();
    job.state = JobState::Cancelled;
    job.sequence += 1;
    f.persist(&job);
    assert!(f.storage.save_grading_question(job.id, q).is_err());
    let second_doc = f.document(ImportMode::AiGrading).await;
    let second = f.job(second_doc.clone());
    let other = f
        .storage
        .save_grading_question(second.id, question(&second_doc))
        .unwrap();
    assert_ne!(other.id, first.id);
    let reopened = Storage::open(&f.path).unwrap();
    assert_eq!(reopened.list_questions().await.unwrap().len(), 2);
}

#[tokio::test]
async fn teacher_evidence_and_manual_grades_survive_regrading() {
    let f = Fixture::new();
    let d = f.document(ImportMode::TeacherGraded).await;
    let mut job = f.job(d.clone());
    let mut q = question(&d);
    q.attempt.as_mut().unwrap().grade.source = GradeSource::Teacher;
    q.attempt.as_mut().unwrap().teacher = Some(TeacherMark {
        raw: "× -2".into(),
        regions: q.sources.clone(),
        grade: Some(q.attempt.as_ref().unwrap().grade.clone()),
    });
    let mut part = q.clone();
    part.draft_key = "q1.a".into();
    part.number = "1(a)".into();
    q.parts.push(part);
    let saved = f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .unwrap();
    let mut changed = q.clone();
    let mut omitted = q.clone();
    omitted.parts.clear();
    assert!(f
        .storage
        .save_grading_question(job.id.clone(), omitted)
        .is_err());
    changed.attempt.as_mut().unwrap().teacher = None;
    changed.attempt.as_mut().unwrap().grade = Grade {
        verdict: Verdict::Correct,
        earned: Some(2.0),
        possible: Some(2.0),
        source: GradeSource::AiSolution,
        reason: "尝试覆盖".into(),
    };
    changed.parts[0].attempt.as_mut().unwrap().grade =
        changed.attempt.as_ref().unwrap().grade.clone();
    let protected = f
        .storage
        .save_grading_question(job.id.clone(), changed.clone())
        .unwrap();
    assert_eq!(
        protected.question.attempt.unwrap().grade.source,
        GradeSource::Teacher
    );
    assert!(f
        .storage
        .edit_question(saved.id.clone(), changed.clone())
        .await
        .is_err());
    job.state = JobState::Completed;
    job.sequence += 1;
    f.persist(&job);
    let edited = f
        .storage
        .edit_question(saved.id.clone(), changed.clone())
        .await
        .unwrap();
    assert_eq!(
        edited.question.attempt.unwrap().grade.source,
        GradeSource::Manual
    );
    let new_job = f.job(d);
    let protected = f
        .storage
        .save_grading_question(new_job.id, changed)
        .unwrap();
    assert_eq!(
        protected.question.attempt.unwrap().grade.source,
        GradeSource::Manual
    );
}

#[tokio::test]
async fn score_parts_blank_and_source_rules_reject_inconsistent_results() {
    let f = Fixture::new();
    let d = f.document(ImportMode::AiGrading).await;
    let job = f.job(d.clone());
    let mut q = question(&d);
    q.attempt.as_mut().unwrap().grade.earned = Some(5.0);
    assert!(f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .is_err());
    q = question(&d);
    q.attempt.as_mut().unwrap().grade.verdict = Verdict::Uncertain;
    assert!(f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .is_err());
    q = question(&d);
    q.sources[0].page_id = uuid::Uuid::new_v4().to_string();
    assert!(f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .is_err());
    q = question(&d);
    let mut part = q.clone();
    part.draft_key = "q1.a".into();
    part.points = Some(1.0);
    part.attempt.as_mut().unwrap().grade.possible = Some(1.0);
    q.parts.push(part);
    assert!(f.storage.save_grading_question(job.id, q).is_err());
    let blank = f.document(ImportMode::Blank).await;
    let job = f.job(blank.clone());
    let mut q = question(&blank);
    assert!(f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .is_err());
    q.attempt = None;
    f.storage.save_grading_question(job.id, q).unwrap();
}

#[tokio::test]
async fn reference_matching_is_separate_and_ai_reference_requires_a_match() {
    let f = Fixture::new();
    let mut d = f.document(ImportMode::AiGrading).await;
    let mut png = Cursor::new(vec![]);
    image::DynamicImage::new_rgb8(10, 10)
        .write_to(&mut png, image::ImageFormat::Png)
        .unwrap();
    d = f
        .storage
        .append_document_sources(
            d.id,
            vec![ImportSource {
                name: "answer".into(),
                bytes: png.into_inner(),
                role: "reference".into(),
            }],
        )
        .await
        .unwrap();
    let job = f.job(d.clone());
    let mut q = question(&d);
    q.attempt.as_mut().unwrap().grade.source = GradeSource::AiReference;
    assert!(f
        .storage
        .save_grading_question(job.id.clone(), q.clone())
        .is_err());
    let reference = ReferenceAnswer {
        key: "r1".into(),
        number: "1".into(),
        answer: "2".into(),
        explanation: String::new(),
        sources: vec![Region {
            page_id: d.pages[1].id.clone(),
            x: 0.0,
            y: 0.0,
            width: 1.0,
            height: 1.0,
        }],
        matched_draft_key: Some("q1".into()),
        uncertain_reason: None,
    };
    f.storage
        .save_grading_references(job.id.clone(), vec![reference])
        .unwrap();
    assert!(f.storage.list_questions().await.unwrap().is_empty());
    q.attempt.as_mut().unwrap().reference_key = Some("r1".into());
    f.storage.save_grading_question(job.id, q).unwrap();
}

fn pdf(count: usize) -> Vec<u8> {
    let mut objects = vec![
        "<< /Type /Catalog /Pages 2 0 R >>".to_string(),
        format!(
            "<< /Type /Pages /Count {count} /Kids [{}] >>",
            (0..count)
                .map(|i| format!("{} 0 R", 3 + i))
                .collect::<Vec<_>>()
                .join(" ")
        ),
    ];
    for _ in 0..count {
        objects
            .push("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 120] /Resources <<>> >>".into());
    }
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

#[tokio::test]
#[ignore = "requires scripts/prepare-pdfium.ps1; real PDFium rendering"]
async fn pdfium_page_count_original_mapping_limits_and_legacy_upgrade() {
    let f = Fixture::new();
    let bytes = pdf(3);
    let doc = f
        .storage
        .import_document(
            "三页 PDF".into(),
            ImportMode::Blank,
            vec![ImportSource {
                name: "fake.png".into(),
                bytes: bytes.clone(),
                role: "material".into(),
            }],
        )
        .await
        .unwrap();
    assert_eq!(doc.pages.len(), 3);
    assert!(!doc.pending_pdf_pages);
    let original = doc.pages[0].original_asset_id.clone().unwrap();
    assert_eq!(
        f.storage
            .read_asset(original.clone(), false)
            .await
            .unwrap()
            .bytes,
        bytes
    );
    for (i, page) in doc.pages.iter().enumerate() {
        assert_eq!(page.pdf_page_index, Some(i as u32));
        assert_eq!(page.original_asset_id.as_ref(), Some(&original));
        assert_eq!(page.width, Some(2400));
        assert_eq!(
            f.storage
                .read_asset(page.asset_id.clone(), true)
                .await
                .unwrap()
                .mime,
            "image/png"
        );
    }
    let prior = f.storage.list_documents().await.unwrap().len();
    assert!(f
        .storage
        .import_document(
            "13页".into(),
            ImportMode::Blank,
            vec![ImportSource {
                name: "13".into(),
                bytes: pdf(13),
                role: "material".into()
            }]
        )
        .await
        .is_err());
    assert_eq!(f.storage.list_documents().await.unwrap().len(), prior);
    let connection = rusqlite::Connection::open(f.path.join("study.sqlite3")).unwrap();
    connection.execute("UPDATE source_pages SET asset_id=?1,preview_status='pending_pdf',original_asset_id=NULL,pdf_page_index=NULL WHERE id=?2",rusqlite::params![original,doc.pages[0].id]).unwrap();
    connection
        .execute(
            "DELETE FROM source_pages WHERE document_id=?1 AND id!=?2",
            rusqlite::params![doc.id, doc.pages[0].id],
        )
        .unwrap();
    connection
        .execute(
            "UPDATE documents SET pending_pdf_pages=1 WHERE id=?1",
            [&doc.id],
        )
        .unwrap();
    let upgraded = f.storage.parse_pending_pdfs(doc.id).await.unwrap();
    assert_eq!(upgraded.pages.len(), 3);
    assert_eq!(upgraded.pages[0].id, doc.pages[0].id);
}
