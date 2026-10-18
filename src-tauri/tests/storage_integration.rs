use image::{DynamicImage, ImageFormat};
use oh_my_study_core::{
    ai::providers::{Capabilities, Protocol},
    domain::{AppSettings, ImportMode, ModelConfig, PageEdit, Provider},
    storage::{ImportSource, Storage},
};
use serde_json::json;
use std::{io::Cursor, path::PathBuf};

struct Fixture(PathBuf);
impl Fixture {
    fn new() -> Self {
        let path =
            std::env::temp_dir().join(format!("oh-my-study-storage-{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&path).unwrap();
        Self(path)
    }
    fn open(&self) -> Storage {
        Storage::open(&self.0).unwrap()
    }
}
impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

fn source(format: ImageFormat, role: &str) -> ImportSource {
    let mut buffer = Cursor::new(Vec::new());
    DynamicImage::new_rgb8(40, 20)
        .write_to(&mut buffer, format)
        .unwrap();
    ImportSource {
        name: "../../a falsely named.PDF".into(),
        bytes: buffer.into_inner(),
        role: role.into(),
    }
}

#[tokio::test]
async fn migration_wal_settings_and_provider_survive_reopen() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    assert!(storage
        .verify_integrity()
        .unwrap()
        .contains("journal_mode=wal"));
    let id = uuid::Uuid::new_v4().to_string();
    let provider = Provider {
        id: id.clone(),
        name: "学生's provider; DROP TABLE providers;--".into(),
        protocol: Protocol::Responses,
        base_url: "https://example.test/v1".into(),
        key_ref: None,
        has_key: true,
        models: vec![ModelConfig {
            id: "manual-vision".into(),
            capabilities: Capabilities {
                vision: Some(true),
                ..Default::default()
            },
            price_input: Some(1.5),
            price_output: None,
            currency: "USD".into(),
        }],
    };
    storage.upsert_provider(provider).await.unwrap();
    let settings = AppSettings {
        default_model: Some("manual-vision".into()),
        execution_mode: "sandbox".into(),
        sandbox_networking: false,
    };
    storage.save_settings(settings).await.unwrap();
    drop(storage);
    let reopened = fixture.open();
    let providers = reopened.list_providers().await.unwrap();
    assert_eq!(providers.len(), 1);
    assert!(!providers[0].has_key);
    assert!(providers[0].name.contains("DROP TABLE"));
    assert_eq!(
        reopened.get_settings().await.unwrap().execution_mode,
        "sandbox"
    );
    let connection = rusqlite::Connection::open(fixture.0.join("study.sqlite3")).unwrap();
    connection
        .pragma_update(None, "foreign_keys", true)
        .unwrap();
    let version: i32 = connection
        .pragma_query_value(None, "user_version", |row| row.get(0))
        .unwrap();
    assert_eq!(version, 2);
    assert!(connection.execute("INSERT INTO source_pages(id,document_id,asset_id,page_index,rotation,preview_status,role) VALUES('bad','missing','missing',0,0,'ready','material')",[]).is_err());
    reopened.delete_provider(id).await.unwrap();
    assert!(reopened.list_providers().await.unwrap().is_empty());
}

#[tokio::test]
async fn magic_thumbnails_originals_roles_and_page_edits_are_persistent() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let first = source(ImageFormat::Png, "material");
    let original = first.bytes.clone();
    let document = storage
        .import_document(
            "整卷一".into(),
            ImportMode::TeacherGraded,
            vec![
                first,
                source(ImageFormat::Jpeg, "reference"),
                source(ImageFormat::WebP, "material"),
            ],
        )
        .await
        .unwrap();
    assert_eq!(document.pages.len(), 3);
    assert_eq!(document.pages[1].role, "reference");
    let data = storage
        .read_asset(document.pages[0].asset_id.clone(), false)
        .await
        .unwrap();
    assert_eq!(data.bytes, original);
    assert_eq!(data.mime, "image/png");
    let thumb = storage
        .read_asset(document.pages[0].asset_id.clone(), true)
        .await
        .unwrap();
    assert_eq!(image::guess_format(&thumb.bytes).unwrap(), ImageFormat::Png);
    assert!(storage
        .read_asset("../../secret".into(), false)
        .await
        .is_err());
    let edited = storage
        .update_pages(
            document.id.clone(),
            vec![
                PageEdit {
                    id: document.pages[2].id.clone(),
                    rotation: 90,
                },
                PageEdit {
                    id: document.pages[0].id.clone(),
                    rotation: 0,
                },
            ],
        )
        .await
        .unwrap();
    assert_eq!(edited.pages[0].id, document.pages[2].id);
    assert_eq!(edited.pages[0].rotation, 90);
    assert_eq!(edited.pages.len(), 2);
    assert!(storage
        .update_pages(
            document.id.clone(),
            vec![PageEdit {
                id: document.pages[1].id.clone(),
                rotation: 0
            }]
        )
        .await
        .is_err());
    assert!(storage
        .update_pages(
            document.id.clone(),
            vec![PageEdit {
                id: edited.pages[0].id.clone(),
                rotation: 17
            }]
        )
        .await
        .is_err());
    assert_eq!(
        storage
            .read_asset(document.pages[1].asset_id.clone(), false)
            .await
            .unwrap()
            .mime,
        "image/jpeg"
    );
    drop(storage);
    assert_eq!(
        fixture
            .open()
            .get_document(document.id)
            .await
            .unwrap()
            .pages
            .len(),
        2
    );
}

#[tokio::test]
async fn rejected_batch_and_malformed_pdf_are_atomic() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let invalid = ImportSource {
        name: "fake.jpg".into(),
        bytes: b"not a real image".to_vec(),
        role: "material".into(),
    };
    assert!(storage
        .import_document(
            "invalid".into(),
            ImportMode::Blank,
            vec![source(ImageFormat::Png, "material"), invalid]
        )
        .await
        .is_err());
    assert!(storage.list_documents().await.unwrap().is_empty());
    assert_eq!(
        std::fs::read_dir(fixture.0.join("assets")).unwrap().count(),
        0
    );
    let too_many = (0..13)
        .map(|_| source(ImageFormat::Png, "material"))
        .collect();
    assert!(storage
        .import_document("13 pages".into(), ImportMode::Blank, too_many)
        .await
        .is_err());
    let pdf = ImportSource {
        name: "pdf".into(),
        bytes: b"%PDF-1.7\noriginal-unparsed-by-M1\n%%EOF".to_vec(),
        role: "material".into(),
    };
    assert!(storage
        .import_document("PDF".into(), ImportMode::AiGrading, vec![pdf])
        .await
        .is_err());
    assert!(storage.list_documents().await.unwrap().is_empty());
    assert_eq!(
        std::fs::read_dir(fixture.0.join("assets")).unwrap().count(),
        0
    );
}

#[tokio::test]
async fn job_snapshot_and_event_commit_together_without_reused_sequences() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let id = uuid::Uuid::new_v4().to_string();
    let snapshot = json!({"id":id,"kind":"diagnostic","state":"running","created_at":123,"updated_at":124,"progress":35.0,"message":"checking","sequence":1,"execution_policy":{"mode":"sandbox"}});
    let event = json!({"id":"event-1","job_id":id,"sequence":1,"created_at":124,"kind":"progress","payload":snapshot});
    storage
        .save_job_event(snapshot.clone(), event.clone())
        .unwrap();
    assert!(storage.save_job_event(snapshot.clone(), event).is_err());
    let mut changed = snapshot.clone();
    changed["state"] = json!("completed");
    changed["sequence"] = json!(2);
    // Mismatched sequence cannot replace the persisted snapshot.
    let bad_event = json!({"job_id":id,"sequence":3,"kind":"completed"});
    assert!(storage.save_job_event(changed.clone(), bad_event).is_err());
    // This passes JSON validation but violates the immutable event ID constraint,
    // so the snapshot UPDATE must roll back with the event INSERT.
    let duplicate_id = json!({"id":"event-1","job_id":id,"sequence":2,"kind":"completed"});
    assert!(storage.save_job_event(changed, duplicate_id).is_err());
    assert_eq!(
        storage.load_job_snapshots().unwrap(),
        vec![snapshot.clone()]
    );
    assert_eq!(storage.list_job_events(id).await.unwrap().len(), 1);
    drop(storage);
    assert_eq!(fixture.open().load_job_snapshots().unwrap(), vec![snapshot]);
}

#[tokio::test]
async fn managed_asset_database_path_cannot_escape_root() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let document = storage
        .import_document(
            "test".into(),
            ImportMode::Blank,
            vec![source(ImageFormat::Png, "material")],
        )
        .await
        .unwrap();
    let id = &document.pages[0].asset_id;
    let connection = rusqlite::Connection::open(fixture.0.join("study.sqlite3")).unwrap();
    connection
        .execute(
            "UPDATE assets SET original_file='../study.sqlite3' WHERE id=?1",
            [id],
        )
        .unwrap();
    assert!(storage
        .read_asset(id.clone(), false)
        .await
        .unwrap_err()
        .to_string()
        .contains("reference"));
}

#[tokio::test]
async fn refuses_future_database_versions_and_embedded_provider_credentials() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let provider = Provider {
        id: uuid::Uuid::new_v4().to_string(),
        name: "invalid".into(),
        protocol: Protocol::Gemini,
        base_url: "https://example.test?key=secret".into(),
        key_ref: None,
        has_key: false,
        models: vec![],
    };
    assert!(storage.upsert_provider(provider).await.is_err());
    let connection = rusqlite::Connection::open(fixture.0.join("study.sqlite3")).unwrap();
    connection.pragma_update(None, "user_version", 99).unwrap();
    assert!(Storage::open(&fixture.0).is_err());
}

#[tokio::test]
async fn rejects_remote_http_plaintext_refs_and_accepts_only_credential_refs() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let mut provider = Provider {
        id: uuid::Uuid::new_v4().to_string(),
        name: "local".into(),
        protocol: Protocol::Messages,
        base_url: "http://remote.example.test/v1".into(),
        key_ref: None,
        has_key: false,
        models: vec![],
    };
    assert!(storage.upsert_provider(provider.clone()).await.is_err());
    provider.base_url = "http://127.0.0.1:8888/v1".into();
    provider.key_ref = Some("non-sk-format-secret".into());
    assert!(storage.upsert_provider(provider.clone()).await.is_err());
    provider.key_ref = Some(format!("OhMyStudy/provider/{}", uuid::Uuid::new_v4()));
    storage.upsert_provider(provider).await.unwrap();
    assert!(storage.list_providers().await.unwrap()[0]
        .key_ref
        .as_ref()
        .unwrap()
        .starts_with("OhMyStudy/provider/"));
    let mut ipv6 = storage.list_providers().await.unwrap().remove(0);
    ipv6.base_url = "http://[::1]:8888/v1".into();
    storage.upsert_provider(ipv6).await.unwrap();
}

#[tokio::test]
async fn sqlite_import_failure_removes_files_and_rolls_back_document() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let connection = rusqlite::Connection::open(fixture.0.join("study.sqlite3")).unwrap();
    connection.execute_batch("CREATE TRIGGER fail_page BEFORE INSERT ON source_pages BEGIN SELECT RAISE(ABORT,'test failure'); END;").unwrap();
    assert!(storage
        .import_document(
            "transaction".into(),
            ImportMode::Blank,
            vec![source(ImageFormat::Png, "material")]
        )
        .await
        .is_err());
    assert!(storage.list_documents().await.unwrap().is_empty());
    let assets: i64 = connection
        .query_row("SELECT COUNT(*) FROM assets", [], |row| row.get(0))
        .unwrap();
    assert_eq!(assets, 0);
    assert_eq!(
        std::fs::read_dir(fixture.0.join("assets")).unwrap().count(),
        0
    );
}

#[tokio::test]
async fn oversized_pixel_header_is_rejected_before_allocation() {
    let fixture = Fixture::new();
    let storage = fixture.open();
    let mut oversized = source(ImageFormat::Png, "material");
    oversized.bytes[16..20].copy_from_slice(&10000u32.to_be_bytes());
    oversized.bytes[20..24].copy_from_slice(&10000u32.to_be_bytes());
    let mut crc = 0xffffffffu32;
    for byte in &oversized.bytes[12..29] {
        crc ^= u32::from(*byte);
        for _ in 0..8 {
            crc = (crc >> 1) ^ if crc & 1 == 1 { 0xedb88320 } else { 0 };
        }
    }
    oversized.bytes[29..33].copy_from_slice(&(!crc).to_be_bytes());
    let error = storage
        .import_document("large".into(), ImportMode::Blank, vec![oversized])
        .await
        .unwrap_err();
    assert!(error.to_string().contains("dimensions"), "{error}");
    assert!(storage.list_documents().await.unwrap().is_empty());
}

#[cfg(windows)]
#[test]
fn asset_directory_junction_is_rejected_even_if_target_is_inside_data_root() {
    let fixture = Fixture::new();
    let target = fixture.0.join("inner");
    std::fs::create_dir(&target).unwrap();
    let link = fixture.0.join("assets");
    let output = std::process::Command::new("cmd")
        .args(["/C", "mklink", "/J"])
        .arg(&link)
        .arg(&target)
        .output()
        .unwrap();
    assert!(
        output.status.success(),
        "{}",
        String::from_utf8_lossy(&output.stderr)
    );
    let error = match Storage::open(&fixture.0) {
        Err(error) => error,
        Ok(_) => panic!("junction should not be accepted"),
    };
    assert!(error.to_string().contains("reparse"), "{error}");
}
