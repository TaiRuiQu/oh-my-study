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
    original_asset_id: Option<String>,
    pdf_page_index: Option<u32>,
    original_only: bool,
    metadata: Asset,
    original_file: String,
    thumbnail_file: Option<String>,
    bytes: Vec<u8>,
    thumbnail: Option<Vec<u8>>,
    role: String,
}

impl Storage {
    /// Upgrade M1 pending PDF entries atomically, keeping the first source page ID.
    pub async fn parse_pending_pdfs(&self, document_id: String) -> Result<Document> {
        valid_id(&document_id)?;
        self.run(move|db|{
            db.document_idle(&document_id)?;
            let document=db.document(&document_id)?;
            if !document.pending_pdf_pages {return Ok(document);}
            let mut prepared=Vec::new();
            let mut order=Vec::new();
            let mut rotations=Vec::new();
            let mut total_bytes=document.pages.iter().try_fold(0usize,|total,page|db.asset(&page.asset_id).map(|asset|total+asset.byte_size as usize))?;
            for page in &document.pages {
                if page.preview_status!="pending_pdf" {order.push(page.id.clone());rotations.push(page.rotation);continue;}
                let file:String=db.connection.query_row("SELECT original_file FROM assets WHERE id=?1",[&page.asset_id],|r|r.get(0))?;
                let path=safe_file(&db.asset_root,&file)?;
                if std::fs::metadata(&path)?.len()>MAX_ASSET_BYTES as u64 {return Err(StorageError("PDF 原件超出限制".into()));}
                let images=crate::documents::render_pdf(&std::fs::read(path)?,&db.pdfium_library,MAX_INPUT_PAGES.saturating_sub(order.len())).map_err(StorageError)?;
                for (index,bytes) in images.into_iter().enumerate() {
                    let mut image=prepare(ImportSource{name:String::new(),bytes,role:page.role.clone()})?;
                    total_bytes+=image.bytes.len()+image.thumbnail.as_ref().map_or(0,Vec::len);
                    if total_bytes>MAX_BATCH_BYTES {return Err(StorageError("原件和页面缓存合计超过 256 MiB；旧材料未修改".into()));}
                    image.original_asset_id=Some(page.asset_id.clone());image.pdf_page_index=Some(index as u32);
                    let id=if index==0 {page.id.clone()} else {new_id()};
                    order.push(id.clone());rotations.push(page.rotation);prepared.push((id,image));
                }
            }
            if order.len()>MAX_INPUT_PAGES {return Err(StorageError("PDF 解析后超过 12 页，请重新分批导入".into()));}
            let mut created=Vec::new();
            let result=(||->Result<()> {
                for (_,asset) in &prepared {
                    let path=db.asset_root.join(&asset.original_file);write_new(&db.asset_root,&path,&asset.bytes)?;created.push(path);
                    if let (Some(name),Some(bytes))=(&asset.thumbnail_file,&asset.thumbnail) {let path=db.asset_root.join(name);write_new(&db.asset_root,&path,bytes)?;created.push(path);}
                }
                let tx=db.connection.transaction()?;
                tx.execute("DELETE FROM source_pages WHERE document_id=?1 AND preview_status='pending_pdf'",[&document_id])?;
                tx.execute("UPDATE source_pages SET page_index=page_index+1000 WHERE document_id=?1",[&document_id])?;
                for (id,asset) in &prepared {
                    let m=&asset.metadata;
                    tx.execute("INSERT INTO assets(id,mime,byte_size,width,height,original_file,thumbnail_file) VALUES(?1,?2,?3,?4,?5,?6,?7)",params![m.id,m.mime,m.byte_size,m.width,m.height,asset.original_file,asset.thumbnail_file])?;
                    let index=order.iter().position(|p|p==id).unwrap();
                    tx.execute("INSERT INTO source_pages(id,document_id,asset_id,page_index,rotation,preview_status,role,original_asset_id,pdf_page_index) VALUES(?1,?2,?3,?4,?5,'ready',?6,?7,?8)",params![id,document_id,m.id,index,rotations[index],asset.role,asset.original_asset_id,asset.pdf_page_index])?;
                }
                for (index,id) in order.iter().enumerate() {tx.execute("UPDATE source_pages SET page_index=?1 WHERE id=?2",params![index,id])?;}
                tx.execute("UPDATE documents SET pending_pdf_pages=0 WHERE id=?1",[&document_id])?;
                tx.commit()?;Ok(())
            })();
            if result.is_err() {for path in created {let _=std::fs::remove_file(path);}}
            result?;db.document(&document_id)
        }).await
    }
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
            let assets = prepare_inputs(sources, &db.pdfium_library, MAX_INPUT_PAGES)?;
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
            db.document_idle(&document_id)?;
            let document = db.document(&document_id)?;
            if document.pages.len()+sources.len()>MAX_INPUT_PAGES { return Err(StorageError("a document supports at most 12 image/input entries; PDF page parsing is pending".into())); }
            let assets = prepare_inputs(sources, &db.pdfium_library, MAX_INPUT_PAGES-document.pages.len())?;
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
        original_asset_id: None,
        pdf_page_index: None,
        original_only: false,
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

fn prepare_inputs(
    sources: Vec<ImportSource>,
    library: &Path,
    maximum: usize,
) -> Result<Vec<PreparedAsset>> {
    let mut result = Vec::new();
    let mut count = 0;
    for source in sources {
        let mut original = prepare(source)?;
        if original.metadata.mime == "application/pdf" {
            let pages = crate::documents::render_pdf(
                &original.bytes,
                library,
                maximum.saturating_sub(count),
            )
            .map_err(StorageError)?;
            let original_id = original.metadata.id.clone();
            let role = original.role.clone();
            original.original_only = true;
            result.push(original);
            for (index, bytes) in pages.into_iter().enumerate() {
                let mut page = prepare(ImportSource {
                    name: String::new(),
                    bytes,
                    role: role.clone(),
                })?;
                page.original_asset_id = Some(original_id.clone());
                page.pdf_page_index = Some(index as u32);
                result.push(page);
                count += 1;
            }
        } else {
            result.push(original);
            count += 1;
        }
        if count > maximum {
            return Err(StorageError(
                "材料和参考答案合计最多 12 页；整批未导入".into(),
            ));
        }
        if result
            .iter()
            .map(|a| a.bytes.len() + a.thumbnail.as_ref().map_or(0, Vec::len))
            .sum::<usize>()
            > MAX_BATCH_BYTES
        {
            return Err(StorageError(
                "原件和页面缓存合计超过 256 MiB；整批未导入".into(),
            ));
        }
    }
    Ok(result)
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
            let mut ordinal = start;
            for asset in &assets {
                let metadata = &asset.metadata;
                transaction.execute("INSERT INTO assets(id,mime,byte_size,width,height,original_file,thumbnail_file) VALUES(?1,?2,?3,?4,?5,?6,?7)",params![metadata.id,metadata.mime,metadata.byte_size,metadata.width,metadata.height,asset.original_file,asset.thumbnail_file])?;
                if !asset.original_only {
                    transaction.execute("INSERT INTO source_pages(id,document_id,asset_id,page_index,rotation,preview_status,role,original_asset_id,pdf_page_index) VALUES(?1,?2,?3,?4,0,'ready',?5,?6,?7)",params![new_id(),document_id,metadata.id,ordinal,asset.role,asset.original_asset_id,asset.pdf_page_index])?;
                    ordinal += 1;
                }
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
