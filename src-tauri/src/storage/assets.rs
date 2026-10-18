use super::{new_id, now, valid_id, Database, Result, Storage, StorageError};
use crate::domain::{Asset, Document, ImportMode};
use image::{ImageFormat, ImageReader, Limits};
use rusqlite::{params, OptionalExtension};
use std::{
    io::{Cursor, Write},
    path::{Component, Path, PathBuf},
};

pub const MAX_INPUT_PAGES: usize = 12;
pub const MAX_ASSET_BYTES: usize = 64 * 1024 * 1024;
pub const MAX_IMAGE_PIXELS: u64 = 40_000_000;
const MAX_BATCH_BYTES: usize = 256 * 1024 * 1024;

/// Only the native file-picker boundary constructs this. Not an IPC DTO.
pub struct ImportSource {
    pub name: String,
    pub bytes: Vec<u8>,
    pub role: String,
}

#[derive(Debug)]
pub struct AssetData {
    pub mime: String,
    pub bytes: Vec<u8>,
}

struct PreparedAsset {
    metadata: Asset,
    original_file: String,
    thumbnail_file: Option<String>,
    bytes: Vec<u8>,
    thumbnail: Option<Vec<u8>>,
    role: String,
}

impl Storage {
    pub async fn import_document(
        &self,
        name: String,
        mode: ImportMode,
        sources: Vec<ImportSource>,
    ) -> Result<Document> {
        if name.trim().is_empty() || name.len() > 512 {
            return Err(StorageError(
                "document name is required (maximum 512 bytes)".into(),
            ));
        }
        validate_batch(&sources)?;
        self.run(move |db| {
            let assets = sources
                .into_iter()
                .map(prepare)
                .collect::<Result<Vec<_>>>()?;
            let id = new_id();
            db.write_import(&id, Some((name, mode)), assets)?;
            db.document(&id)
        })
        .await
    }

    pub async fn append_document_sources(
        &self,
        document_id: String,
        sources: Vec<ImportSource>,
    ) -> Result<Document> {
        valid_id(&document_id)?;
        validate_batch(&sources)?;
        self.run(move |db| {
            let document = db.document(&document_id)?;
            if document.pages.len()+sources.len()>MAX_INPUT_PAGES { return Err(StorageError("a document supports at most 12 image/input entries; PDF page parsing is pending".into())); }
            let assets = sources.into_iter().map(prepare).collect::<Result<Vec<_>>>()?;
            db.write_import(&document_id,None,assets)?;
            db.document(&document_id)
        }).await
    }

    pub async fn get_asset(&self, asset_id: String) -> Result<Asset> {
        valid_id(&asset_id)?;
        self.run(move |db| db.asset(&asset_id)).await
    }

    pub async fn read_asset(&self, asset_id: String, thumbnail: bool) -> Result<AssetData> {
        valid_id(&asset_id)?;
        self.run(move |db| {
            let asset = db.asset(&asset_id)?;
            let (original, cached): (String, Option<String>) = db.connection.query_row(
                "SELECT original_file,thumbnail_file FROM assets WHERE id=?1",
                [asset_id],
                |row| Ok((row.get(0)?, row.get(1)?)),
            )?;
            let file = if thumbnail {
                cached.ok_or_else(|| {
                    StorageError("PDF page preview is pending; original remains available".into())
                })?
            } else {
                original
            };
            let path = safe_file(&db.asset_root, &file)?;
            if std::fs::metadata(&path)?.len() > MAX_ASSET_BYTES as u64 {
                return Err(StorageError("asset exceeds byte limit".into()));
            }
            let bytes = std::fs::read(path)?;
            if bytes.len() > MAX_ASSET_BYTES {
                return Err(StorageError("asset exceeds byte limit".into()));
            }
            Ok(AssetData {
                mime: if thumbnail {
                    "image/png".into()
                } else {
                    asset.mime
                },
                bytes,
            })
        })
        .await
    }
}

fn validate_batch(sources: &[ImportSource]) -> Result<()> {
    if sources.is_empty() || sources.len() > MAX_INPUT_PAGES {
        return Err(StorageError(
            "select between 1 and 12 inputs; PDF page parsing is pending".into(),
        ));
    }
    if sources
        .iter()
        .map(|source| source.bytes.len())
        .sum::<usize>()
        > MAX_BATCH_BYTES
    {
        return Err(StorageError("input batch exceeds 256 MiB".into()));
    }
    for source in sources {
        if source.bytes.is_empty() || source.bytes.len() > MAX_ASSET_BYTES {
            return Err(StorageError(
                "each input must contain 1 byte to 64 MiB".into(),
            ));
        }
        if !["material", "reference"].contains(&source.role.as_str()) {
            return Err(StorageError("invalid source role".into()));
        }
    }
    Ok(())
}

fn prepare(source: ImportSource) -> Result<PreparedAsset> {
    // Names/extensions are display information, never type evidence or storage paths.
    let id = new_id();
    let format = if source.bytes.starts_with(b"%PDF-") {
        None
    } else {
        match image::guess_format(&source.bytes).map_err(|_| {
            StorageError("unsupported file magic; expected JPEG, PNG, WebP or PDF".into())
        })? {
            format @ (ImageFormat::Jpeg | ImageFormat::Png | ImageFormat::WebP) => Some(format),
            _ => {
                return Err(StorageError(
                    "unsupported file magic; expected JPEG, PNG, WebP or PDF".into(),
                ))
            }
        }
    };
    let (mime, extension, width, height, thumbnail) = if let Some(format) = format {
        let reader = ImageReader::with_format(Cursor::new(&source.bytes), format);
        let (width, height) = reader
            .into_dimensions()
            .map_err(|_| StorageError("invalid image header".into()))?;
        if width == 0
            || height == 0
            || u64::from(width) * u64::from(height) > MAX_IMAGE_PIXELS
            || width > 20_000
            || height > 20_000
        {
            return Err(StorageError(
                "image dimensions exceed 40 million pixels or 20,000 per side".into(),
            ));
        }
        let mut reader = ImageReader::with_format(Cursor::new(&source.bytes), format);
        let mut limits = Limits::default();
        limits.max_image_width = Some(20_000);
        limits.max_image_height = Some(20_000);
        limits.max_alloc = Some(256 * 1024 * 1024);
        reader.limits(limits);
        let decoded = reader.decode().map_err(|_| {
            StorageError("image decoding failed or allocation limit exceeded".into())
        })?;
        let mut thumbnail = Cursor::new(Vec::new());
        decoded
            .thumbnail(512, 512)
            .write_to(&mut thumbnail, ImageFormat::Png)
            .map_err(|_| StorageError("thumbnail generation failed".into()))?;
        let (mime, extension) = match format {
            ImageFormat::Jpeg => ("image/jpeg", "jpg"),
            ImageFormat::Png => ("image/png", "png"),
            _ => ("image/webp", "webp"),
        };
        (
            mime,
            extension,
            Some(width),
            Some(height),
            Some(thumbnail.into_inner()),
        )
    } else {
        ("application/pdf", "pdf", None, None, None)
    };
    Ok(PreparedAsset {
        original_file: format!("{id}.{extension}"),
        thumbnail_file: thumbnail.as_ref().map(|_| format!("{id}.thumb.png")),
        metadata: Asset {
            id,
            mime: mime.into(),
            byte_size: source.bytes.len() as u64,
            width,
            height,
        },
        bytes: source.bytes,
        thumbnail,
        role: source.role,
    })
}

impl Database {
    fn asset(&self, id: &str) -> Result<Asset> {
        self.connection
            .query_row(
                "SELECT id,mime,byte_size,width,height FROM assets WHERE id=?1",
                [id],
                |row| {
                    Ok(Asset {
                        id: row.get(0)?,
                        mime: row.get(1)?,
                        byte_size: row.get(2)?,
                        width: row.get(3)?,
                        height: row.get(4)?,
                    })
                },
            )
            .optional()?
            .ok_or_else(|| StorageError("asset not found".into()))
    }

    fn write_import(
        &mut self,
        document_id: &str,
        new_document: Option<(String, ImportMode)>,
        assets: Vec<PreparedAsset>,
    ) -> Result<()> {
        let mut created = Vec::new();
        let result = (|| {
            for asset in &assets {
                let path = self.asset_root.join(&asset.original_file);
                write_new(&self.asset_root, &path, &asset.bytes)?;
                created.push(path);
                if let (Some(filename), Some(bytes)) = (&asset.thumbnail_file, &asset.thumbnail) {
                    let path = self.asset_root.join(filename);
                    write_new(&self.asset_root, &path, bytes)?;
                    created.push(path);
                }
            }
            let transaction = self.connection.transaction()?;
            if let Some((name, mode)) = new_document {
                transaction.execute("INSERT INTO documents(id,name,mode,created_at,pending_pdf_pages) VALUES(?1,?2,?3,?4,0)",params![document_id,name,serde_json::to_string(&mode)?,now()])?;
            }
            let start: u32 = transaction.query_row(
                "SELECT COUNT(*) FROM source_pages WHERE document_id=?1",
                [document_id],
                |row| row.get(0),
            )?;
            for (index, asset) in assets.iter().enumerate() {
                let metadata = &asset.metadata;
                transaction.execute("INSERT INTO assets(id,mime,byte_size,width,height,original_file,thumbnail_file) VALUES(?1,?2,?3,?4,?5,?6,?7)",params![metadata.id,metadata.mime,metadata.byte_size,metadata.width,metadata.height,asset.original_file,asset.thumbnail_file])?;
                transaction.execute("INSERT INTO source_pages(id,document_id,asset_id,page_index,rotation,preview_status,role) VALUES(?1,?2,?3,?4,0,?5,?6)",params![new_id(),document_id,metadata.id,start+index as u32,if metadata.mime=="application/pdf" {"pending_pdf"} else {"ready"},asset.role])?;
            }
            transaction.execute("UPDATE documents SET pending_pdf_pages=EXISTS(SELECT 1 FROM source_pages WHERE document_id=?1 AND preview_status='pending_pdf') WHERE id=?1",[document_id])?;
            transaction.commit()?;
            Ok(())
        })();
        if result.is_err() {
            for path in created {
                let _ = std::fs::remove_file(path);
            }
        }
        result
    }
}

fn write_new(root: &Path, path: &Path, bytes: &[u8]) -> Result<()> {
    super::reject_link(root)?;
    if path
        .parent()
        .ok_or_else(|| StorageError("invalid asset path".into()))?
        .canonicalize()?
        != root
    {
        return Err(StorageError(
            "asset path escapes application directory".into(),
        ));
    }
    let mut file = std::fs::OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(path)?;
    if let Err(error) = file.write_all(bytes).and_then(|_| file.sync_all()) {
        let _ = std::fs::remove_file(path);
        return Err(error.into());
    }
    Ok(())
}

fn safe_file(root: &Path, name: &str) -> Result<PathBuf> {
    let mut components = Path::new(name).components();
    if !matches!(components.next(), Some(Component::Normal(_)))
        || components.next().is_some()
        || name.contains(['/', '\\', ':'])
    {
        return Err(StorageError("invalid managed asset reference".into()));
    }
    super::reject_link(root)?;
    super::reject_link(&root.join(name))?;
    let path = root.join(name).canonicalize()?;
    if path.parent() != Some(root) {
        return Err(StorageError(
            "asset reference escapes application directory".into(),
        ));
    }
    Ok(path)
}
