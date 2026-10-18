use super::{valid_id, Database, Result, Storage, StorageError};
use crate::domain::*;
use rusqlite::{params, OptionalExtension};
use std::collections::HashSet;

impl Storage {
    pub async fn list_providers(&self) -> Result<Vec<Provider>> {
        self.run(|db| {
            let mut statement = db
                .connection
                .prepare("SELECT config_json FROM providers ORDER BY id")?;
            let rows = statement.query_map([], |row| row.get::<_, String>(0))?;
            rows.map(|row| Ok(serde_json::from_str(&row?)?)).collect()
        })
        .await
    }

    pub async fn upsert_provider(&self, mut provider: Provider) -> Result<Provider> {
        validate_provider(&provider)?;
        provider.has_key = false;
        self.run(move |db| {
            let json = serde_json::to_string(&provider)?;
            db.connection.execute("INSERT INTO providers(id,config_json) VALUES(?1,?2) ON CONFLICT(id) DO UPDATE SET config_json=excluded.config_json", params![provider.id, json])?;
            Ok(provider)
        }).await
    }

    pub async fn delete_provider(&self, id: String) -> Result<()> {
        valid_id(&id)?;
        self.run(move |db| {
            db.connection
                .execute("DELETE FROM providers WHERE id=?1", [id])?;
            Ok(())
        })
        .await
    }

    pub async fn get_settings(&self) -> Result<AppSettings> {
        self.run(|db| {
            let json: Option<String> = db
                .connection
                .query_row("SELECT settings_json FROM settings WHERE id=1", [], |row| {
                    row.get(0)
                })
                .optional()?;
            match json {
                Some(json) => Ok(serde_json::from_str(&json)?),
                None => Ok(AppSettings::default()),
            }
        })
        .await
    }

    pub async fn save_settings(&self, settings: AppSettings) -> Result<AppSettings> {
        if !["request_permission", "sandbox", "bypass_permission"]
            .contains(&settings.execution_mode.as_str())
            || settings
                .default_model
                .as_ref()
                .is_some_and(|v| v.len() > 1024)
        {
            return Err(StorageError("invalid application settings".into()));
        }
        self.run(move |db| {
            db.connection.execute("INSERT INTO settings(id,settings_json) VALUES(1,?1) ON CONFLICT(id) DO UPDATE SET settings_json=excluded.settings_json", [serde_json::to_string(&settings)?])?;
            Ok(settings)
        }).await
    }

    pub async fn list_documents(&self) -> Result<Vec<Document>> {
        self.run(|db| {
            let ids: Vec<String> = {
                let mut query = db
                    .connection
                    .prepare("SELECT id FROM documents ORDER BY created_at DESC,id")?;
                let rows = query.query_map([], |row| row.get(0))?;
                rows.collect::<std::result::Result<Vec<_>, _>>()?
            };
            ids.into_iter().map(|id| db.document(&id)).collect()
        })
        .await
    }

    pub async fn get_document(&self, id: String) -> Result<Document> {
        valid_id(&id)?;
        self.run(move |db| db.document(&id)).await
    }

    /// Replace page order/rotations atomically; omitted pages are removed from the document.
    /// Originals remain available by asset ID. A foreign page can never be attached here.
    pub async fn update_pages(
        &self,
        document_id: String,
        pages: Vec<PageEdit>,
    ) -> Result<Document> {
        valid_id(&document_id)?;
        let mut unique = HashSet::new();
        for page in &pages {
            valid_id(&page.id)?;
            if ![0, 90, 180, 270].contains(&page.rotation) || !unique.insert(page.id.clone()) {
                return Err(StorageError("invalid rotation or duplicate page".into()));
            }
        }
        self.run(move |db| {
            let document = db.document(&document_id)?;
            if pages.iter().any(|edit| !document.pages.iter().any(|page| page.id == edit.id)) {
                return Err(StorageError("page does not belong to this document".into()));
            }
            let transaction = db.connection.transaction()?;
            // Lift ordinals temporarily to avoid uniqueness collisions during reorder.
            transaction.execute("UPDATE source_pages SET page_index=page_index+1000 WHERE document_id=?1", [&document_id])?;
            for old in &document.pages {
                if !unique.contains(&old.id) { transaction.execute("DELETE FROM source_pages WHERE id=?1 AND document_id=?2", params![old.id,document_id])?; }
            }
            for (index, page) in pages.iter().enumerate() {
                transaction.execute("UPDATE source_pages SET page_index=?1,rotation=?2 WHERE id=?3 AND document_id=?4", params![index as u32,page.rotation,page.id,document_id])?;
            }
            transaction.execute("UPDATE documents SET pending_pdf_pages=EXISTS(SELECT 1 FROM source_pages WHERE document_id=?1 AND preview_status='pending_pdf') WHERE id=?1", [&document_id])?;
            transaction.commit()?;
            db.document(&document_id)
        }).await
    }
}

impl Database {
    pub(super) fn document(&self, id: &str) -> Result<Document> {
        let mut document = self
            .connection
            .query_row(
                "SELECT id,name,mode,created_at,pending_pdf_pages FROM documents WHERE id=?1",
                [id],
                |row| {
                    Ok((
                        row.get::<_, String>(0)?,
                        row.get::<_, String>(1)?,
                        row.get::<_, String>(2)?,
                        row.get::<_, String>(3)?,
                        row.get::<_, bool>(4)?,
                    ))
                },
            )
            .optional()?
            .ok_or_else(|| StorageError("document not found".into()))?;
        let mut statement = self.connection.prepare("SELECT p.id,p.asset_id,p.page_index,p.rotation,a.width,a.height,p.preview_status,p.role FROM source_pages p JOIN assets a ON a.id=p.asset_id WHERE p.document_id=?1 ORDER BY p.page_index")?;
        let pages = statement
            .query_map([id], |row| {
                Ok(SourcePage {
                    id: row.get(0)?,
                    asset_id: row.get(1)?,
                    page_index: row.get(2)?,
                    rotation: row.get(3)?,
                    width: row.get(4)?,
                    height: row.get(5)?,
                    preview_status: row.get(6)?,
                    role: row.get(7)?,
                })
            })?
            .collect::<std::result::Result<Vec<_>, _>>()?;
        Ok(Document {
            id: document.0,
            name: document.1,
            mode: serde_json::from_str(&document.2)?,
            created_at: std::mem::take(&mut document.3),
            pending_pdf_pages: document.4,
            pages,
        })
    }
}

fn validate_provider(provider: &Provider) -> Result<()> {
    valid_id(&provider.id)?;
    let url = reqwest::Url::parse(&provider.base_url)
        .map_err(|_| StorageError("invalid provider base URL".into()))?;
    if !["https", "http"].contains(&url.scheme())
        || url.host_str().is_none()
        || !url.username().is_empty()
        || url.password().is_some()
        || url.query().is_some()
        || url.fragment().is_some()
    {
        return Err(StorageError(
            "provider base URL must use HTTP(S), without credentials, query or fragment".into(),
        ));
    }
    let loopback = matches!(url.host_str(), Some("localhost" | "127.0.0.1" | "[::1]"));
    if url.scheme() == "http" && !loopback {
        return Err(StorageError(
            "HTTP is only allowed for loopback providers".into(),
        ));
    }
    if provider.name.trim().is_empty() || provider.name.len() > 200 || provider.models.len() > 500 {
        return Err(StorageError("invalid provider name or model count".into()));
    }
    if provider.key_ref.as_ref().is_some_and(|value| {
        value
            .strip_prefix("OhMyStudy/provider/")
            .is_none_or(|id| uuid::Uuid::parse_str(id).is_err())
    }) {
        return Err(StorageError("invalid credential reference".into()));
    }
    let mut ids = HashSet::new();
    for model in &provider.models {
        if model.id.trim().is_empty()
            || model.id.len() > 512
            || !ids.insert(&model.id)
            || model.currency.len() > 16
            || model.currency.trim().is_empty()
            || [model.price_input, model.price_output]
                .into_iter()
                .flatten()
                .any(|price| !price.is_finite() || price < 0.0)
        {
            return Err(StorageError("invalid model ID, currency or price".into()));
        }
    }
    Ok(())
}
