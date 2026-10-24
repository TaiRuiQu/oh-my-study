# SQLite schema, assets, search indexes, and recovery

[Home](Home.md) · [Tech for nerds](Tech-for-nerds.md) · [简体中文](Tech-Storage-Recovery-zh-CN.md)

## Schema v6

The library contains `study.sqlite3` and managed `assets`. The current SQLite `user_version` is **6**. Earlier writable libraries receive a `study.sqlite3.pre-v6-…` database snapshot before transactional migration. Schema v4 converts recursive legacy questions into shared stems and flat items and adds query-summary columns; v5 records ownership of generated question images in `question_assets`.

Schema v6 adds a separate `student` source-page role for answer attachments while preserving original page IDs, order, rotations, and PDF relationships. Student responses and nullable error analysis remain in the question JSON. Questions retain typed JSON payloads, while summary columns support list search/filter/sort without deserializing every question. Reference answers match `matched_item_key`. Grading rollback records support explicit task retry; semantic-index tables cache embeddings independently of question payloads. Older application versions cannot open the upgraded schema.

## Ownership and indexing

LibraryRepository serializes operations on one owner thread, uses transactions, foreign keys, WAL, and a writer lease, and rejects unsupported schemas or invalid asset paths. A successful save establishes persistence; UI drafts are separate.

Processed images belong to their exam and keep original source regions. The repository validates asset ownership before saving or exporting and cleans managed assets during deletion/retry according to references.

Meaning search hashes eligible question text and embedding configuration. Saved vectors are reused while valid; changed text/model needs indexing again. Recognition saves can trigger background embedding when configured. Search uses local exact cosine similarity and optional remote reranking, with no vector extension. See [meaning search](Features-Search.md).

## Coherent copies and recovery

The backup service refuses existing targets and targets inside the source. It stages a SQLite online snapshot, copies managed assets, writes a hash manifest, validates, and finalizes the new directory. Online backup includes committed WAL data; copying only the database while running does not.

Copy and switch uses the backup service and retains the old library. User-facing manual backup remains: save, exit, and copy the entire library folder. Preserve the backup and select a working copy as an existing library when recovering. A pre-v6 database snapshot alone is not a complete asset backup. Internal restore and legacy migration services do not imply dedicated menu buttons.

Provider and search keys live in Windows Credential Manager, outside library copies. Language preference and active-library location are computer-level settings. Logs can contain study text and code; inspect them before sharing.

Sources: [repository and migration](../../src/OhMyStudy.Infrastructure/Storage/LibraryRepository.cs), [SQL migrations](../../src/OhMyStudy.Infrastructure/Storage/Migrations), [backup](../../src/OhMyStudy.Infrastructure/Storage/LibraryBackup.cs), [legacy conversion](../../src/OhMyStudy.Infrastructure/Storage/LegacyQuestionFormat.cs), [semantic storage](../../src/OhMyStudy.Infrastructure/Storage/LibrarySemanticSearch.cs), [retry](../../src/OhMyStudy.Infrastructure/Storage/LibraryJobRetry.cs).
