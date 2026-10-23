CREATE TABLE question_assets (
    asset_id TEXT PRIMARY KEY REFERENCES assets(id),
    document_id TEXT NOT NULL REFERENCES documents(id)
);
CREATE INDEX question_assets_document ON question_assets(document_id);
