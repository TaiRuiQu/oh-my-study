ALTER TABLE source_pages ADD COLUMN original_asset_id TEXT REFERENCES assets(id);
ALTER TABLE source_pages ADD COLUMN pdf_page_index INTEGER;
CREATE TABLE grading_runs (
    job_id TEXT PRIMARY KEY NOT NULL REFERENCES jobs(id),
    document_id TEXT NOT NULL REFERENCES documents(id),
    input_json TEXT NOT NULL,
    coverage_json TEXT,
    references_json TEXT
);
CREATE TABLE questions (
    id TEXT PRIMARY KEY NOT NULL,
    document_id TEXT NOT NULL REFERENCES documents(id),
    draft_key TEXT NOT NULL,
    content_json TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE(document_id, draft_key)
);
CREATE TABLE model_calls (
    job_id TEXT NOT NULL REFERENCES jobs(id),
    ordinal INTEGER NOT NULL,
    details_json TEXT NOT NULL,
    PRIMARY KEY(job_id, ordinal)
);
