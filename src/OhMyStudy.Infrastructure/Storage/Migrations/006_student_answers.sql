CREATE TABLE source_pages_next (
  id TEXT PRIMARY KEY NOT NULL,
  document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
  asset_id TEXT NOT NULL REFERENCES assets(id),
  page_index INTEGER NOT NULL CHECK(page_index >= 0),
  rotation INTEGER NOT NULL DEFAULT 0 CHECK(rotation IN (0,90,180,270)),
  preview_status TEXT NOT NULL,
  role TEXT NOT NULL CHECK(role IN ('material','reference','student')),
  original_asset_id TEXT REFERENCES assets(id),
  pdf_page_index INTEGER,
  UNIQUE(document_id,page_index)
);
INSERT INTO source_pages_next SELECT id,document_id,asset_id,page_index,rotation,preview_status,role,original_asset_id,pdf_page_index FROM source_pages;
DROP TABLE source_pages;
ALTER TABLE source_pages_next RENAME TO source_pages;
