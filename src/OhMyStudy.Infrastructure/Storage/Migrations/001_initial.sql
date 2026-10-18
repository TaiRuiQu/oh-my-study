CREATE TABLE providers (
    id TEXT PRIMARY KEY NOT NULL,
    config_json TEXT NOT NULL
);
CREATE TABLE assets (
    id TEXT PRIMARY KEY NOT NULL,
    mime TEXT NOT NULL,
    byte_size INTEGER NOT NULL CHECK(byte_size > 0),
    width INTEGER,
    height INTEGER,
    original_file TEXT NOT NULL UNIQUE,
    thumbnail_file TEXT
);
CREATE TABLE documents (
    id TEXT PRIMARY KEY NOT NULL,
    name TEXT NOT NULL,
    mode TEXT NOT NULL,
    created_at TEXT NOT NULL,
    pending_pdf_pages INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE source_pages (
    id TEXT PRIMARY KEY NOT NULL,
    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    asset_id TEXT NOT NULL REFERENCES assets(id),
    page_index INTEGER NOT NULL CHECK(page_index >= 0),
    rotation INTEGER NOT NULL DEFAULT 0 CHECK(rotation IN (0,90,180,270)),
    preview_status TEXT NOT NULL,
    role TEXT NOT NULL CHECK(role IN ('material','reference')),
    UNIQUE(document_id,page_index)
);
CREATE TABLE jobs (
    id TEXT PRIMARY KEY NOT NULL,
    kind TEXT NOT NULL,
    state TEXT NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    progress REAL NOT NULL CHECK(progress >= 0 AND progress <= 1),
    message TEXT NOT NULL,
    snapshot_json TEXT NOT NULL
);
CREATE TABLE job_events (
    id TEXT PRIMARY KEY NOT NULL,
    job_id TEXT NOT NULL REFERENCES jobs(id) ON DELETE CASCADE,
    sequence INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    kind TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    UNIQUE(job_id,sequence)
);
CREATE INDEX job_events_job_sequence ON job_events(job_id,sequence);
CREATE INDEX documents_created_at ON documents(created_at DESC);
CREATE TABLE settings (id INTEGER PRIMARY KEY CHECK(id=1), settings_json TEXT NOT NULL);
