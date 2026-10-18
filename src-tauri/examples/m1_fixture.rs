//! Development-only fixture for a fresh, isolated desktop acceptance profile.
//! Does not read user material, configure credentials, or call a model.
use oh_my_study_core::{
    domain::ImportMode,
    storage::{ImportSource, Storage},
};
use std::{io::Cursor, path::PathBuf};

#[tokio::main]
async fn main() -> Result<(), Box<dyn std::error::Error>> {
    let workspace = PathBuf::from(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .to_path_buf();
    let directory = workspace.join("output/m1-desktop-acceptance");
    let storage = Storage::open(&directory)?;
    if storage.list_documents().await?.is_empty() {
        let image = image::RgbaImage::from_fn(1000, 1400, |x, y| {
            if x > 90 && x < 910 && y > 100 && y < 1300 {
                if y % 120 < 5 {
                    image::Rgba([115, 108, 181, 255])
                } else {
                    image::Rgba([255, 255, 255, 255])
                }
            } else {
                image::Rgba([239, 237, 249, 255])
            }
        });
        let mut bytes = Cursor::new(Vec::new());
        image::DynamicImage::ImageRgba8(image).write_to(&mut bytes, image::ImageFormat::Png)?;
        let image = bytes.into_inner();
        let mut sources = vec![
            ImportSource {
                name: "自建材料.png".into(),
                bytes: image.clone(),
                role: "material".into(),
            },
            ImportSource {
                name: "自建参考答案.png".into(),
                bytes: image,
                role: "reference".into(),
            },
        ];
        let pdf = workspace.join("probes/typesetting/output/a3-questions.pdf");
        if pdf.is_file() {
            sources.push(ImportSource {
                name: "M0 自建排版 PDF.pdf".into(),
                bytes: std::fs::read(pdf)?,
                role: "material".into(),
            });
        }
        storage
            .import_document(
                "M1 自建验收材料（非用户样卷）".into(),
                ImportMode::TeacherGraded,
                sources,
            )
            .await?;
    }
    println!(
        "{}",
        serde_json::json!({"data_directory":directory,"documents":storage.list_documents().await?})
    );
    Ok(())
}
