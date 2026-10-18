//! PDFium only loads the application's explicit library, never a system DLL.
use pdfium_render::prelude::*;
use std::{
    io::Cursor,
    path::{Path, PathBuf},
    sync::Mutex,
};

// pdfium-render 0.9 owns one process-wide binding. Keep its owner alive and
// serialize complete document operations, including initialization/destruction.
static PDFIUM: Mutex<Option<(PathBuf, Pdfium)>> = Mutex::new(None);

pub fn render_pdf(bytes: &[u8], library: &Path, maximum: usize) -> Result<Vec<Vec<u8>>, String> {
    crate::execution::bridge::reject_reparse_components(library)
        .map_err(|_| "PDFium 未就绪，请运行 scripts/prepare-pdfium.ps1".to_string())?;
    let library = library.canonicalize().map_err(|_| "PDFium 路径无效")?;
    let mut engine = PDFIUM.lock().map_err(|_| "PDFium 工作锁已损坏")?;
    if engine.is_none() {
        let pdfium = Pdfium::new(Pdfium::bind_to_library(&library).map_err(|error| {
            format!("无法加载应用 PDFium 7881；请检查运行时版本和架构：{error}")
        })?);
        *engine = Some((library.clone(), pdfium));
    }
    let (loaded, pdfium) = engine.as_ref().unwrap();
    if loaded != &library {
        return Err("PDFium 已绑定其他运行时路径，拒绝替换".into());
    }
    let document = pdfium
        .load_pdf_from_byte_slice(bytes, None)
        .map_err(|_| "PDF 损坏、需要密码或格式不受支持；没有导入部分页面".to_string())?;
    let count = document.pages().len() as usize;
    if count == 0 || count > maximum {
        return Err(format!(
            "材料和参考答案合计最多 12 页，PDF 含 {count} 页，请分批处理"
        ));
    }
    let mut images = Vec::with_capacity(count);
    let mut total = 0;
    for page in document.pages().iter() {
        let config = PdfRenderConfig::new()
            .set_target_width(2400)
            .set_maximum_height(3600);
        let image = page
            .render_with_config(&config)
            .and_then(|bitmap| bitmap.as_image())
            .map_err(|_| "PDF 页面渲染失败；整批未导入".to_string())?;
        let mut output = Cursor::new(Vec::new());
        image
            .write_to(&mut output, image::ImageFormat::Png)
            .map_err(|_| "PDF 页面编码失败".to_string())?;
        let bytes = output.into_inner();
        total += bytes.len();
        if total > 256 * 1024 * 1024 {
            return Err("PDF 页面缓存超过 256 MiB".into());
        }
        images.push(bytes);
    }
    Ok(images)
}
