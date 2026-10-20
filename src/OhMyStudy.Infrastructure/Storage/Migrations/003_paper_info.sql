CREATE TABLE paper_info (
    document_id TEXT PRIMARY KEY REFERENCES documents(id) ON DELETE CASCADE,
    subject TEXT,
    earned REAL CHECK(earned IS NULL OR earned >= 0),
    possible REAL CHECK(possible IS NULL OR possible >= 0),
    manual INTEGER NOT NULL DEFAULT 0,
    CHECK(earned IS NULL OR possible IS NULL OR earned <= possible)
);
