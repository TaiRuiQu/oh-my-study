use super::{now, valid_id, Result, Storage, StorageError};
use rusqlite::{params, OptionalExtension};
use serde_json::Value;

impl Storage {
    /// Synchronous bridge for the job queue's own worker. Never call on a UI thread.
    /// Snapshot and immutable event are one transaction; event sequence cannot be reused.
    pub fn save_job_event(
        &self,
        snapshot_json: Value,
        event_json: Value,
    ) -> std::result::Result<(), String> {
        self.run_blocking(move |db| {
            let id = text_field(&snapshot_json, "id")?;
            valid_id(id)?;
            if event_json.get("job_id").and_then(Value::as_str) != Some(id) {
                return Err(StorageError("event does not belong to snapshot".into()));
            }
            let sequence = event_json.get("sequence").and_then(Value::as_u64).ok_or_else(|| StorageError("event sequence is required".into()))?;
            if snapshot_json.get("sequence").and_then(Value::as_u64) != Some(sequence) {
                return Err(StorageError("snapshot and event sequence differ".into()));
            }
            let progress = snapshot_json.get("progress").and_then(Value::as_f64).unwrap_or(0.0);
            if !progress.is_finite() || !(0.0..=100.0).contains(&progress) { return Err(StorageError("invalid job progress".into())); }
            let transaction = db.connection.transaction()?;
            let previous: Option<i64> = transaction.query_row("SELECT MAX(sequence) FROM job_events WHERE job_id=?1", [id], |row| row.get(0))?;
            if previous.is_some_and(|previous| sequence <= previous as u64) { return Err(StorageError("job event sequence must increase".into())); }
            transaction.execute("INSERT INTO jobs(id,kind,state,created_at,updated_at,progress,message,snapshot_json) VALUES(?1,?2,?3,?4,?5,?6,?7,?8) ON CONFLICT(id) DO UPDATE SET state=excluded.state,updated_at=excluded.updated_at,progress=excluded.progress,message=excluded.message,snapshot_json=excluded.snapshot_json", params![id,snapshot_json.get("kind").and_then(Value::as_str).unwrap_or("unknown"),snapshot_json.get("state").and_then(Value::as_str).unwrap_or("unknown"),snapshot_json.get("created_at").map(Value::to_string).unwrap_or_else(now),snapshot_json.get("updated_at").map(Value::to_string).unwrap_or_else(now),progress/100.0,snapshot_json.get("message").and_then(Value::as_str).unwrap_or(""),serde_json::to_string(&snapshot_json)?])?;
            transaction.execute("INSERT INTO job_events(id,job_id,sequence,created_at,kind,payload_json) VALUES(?1,?2,?3,?4,?5,?6)",params![event_json.get("id").and_then(Value::as_str).map(str::to_owned).unwrap_or_else(super::new_id),id,sequence,event_json.get("created_at").map(Value::to_string).unwrap_or_else(now),text_field(&event_json,"kind")?,serde_json::to_string(&event_json)?])?;
            transaction.commit()?;
            Ok(())
        })
    }

    pub fn load_job_snapshots(&self) -> std::result::Result<Vec<Value>, String> {
        self.run_blocking(|db| {
            let mut statement = db
                .connection
                .prepare("SELECT snapshot_json FROM jobs ORDER BY created_at,id")?;
            let rows = statement.query_map([], |row| row.get::<_, String>(0))?;
            rows.map(|row| Ok(serde_json::from_str(&row?)?)).collect()
        })
    }

    pub async fn list_job_events(&self, job_id: String) -> Result<Vec<Value>> {
        valid_id(&job_id)?;
        self.run(move |db| {
            let mut query = db
                .connection
                .prepare("SELECT payload_json FROM job_events WHERE job_id=?1 ORDER BY sequence")?;
            let rows = query.query_map([job_id], |row| row.get::<_, String>(0))?;
            rows.map(|row| Ok(serde_json::from_str(&row?)?)).collect()
        })
        .await
    }

    pub fn verify_integrity(&self) -> std::result::Result<String, String> {
        self.run_blocking(|db| {
            let integrity: String = db.connection.query_row("PRAGMA integrity_check", [], |row| row.get(0))?;
            let foreign_keys: i64 = db.connection.pragma_query_value(None,"foreign_keys",|row|row.get(0))?;
            let journal: String = db.connection.pragma_query_value(None,"journal_mode",|row|row.get(0))?;
            let violation: Option<String> = db.connection.query_row("PRAGMA foreign_key_check", [], |row| row.get(0)).optional()?;
            if integrity != "ok" || foreign_keys != 1 || journal != "wal" || violation.is_some() {
                return Err(StorageError("database integrity, foreign-key or WAL check failed".into()));
            }
            Ok("SQLite integrity_check=ok; foreign_keys=on; foreign_key_check=ok; journal_mode=wal".into())
        })
    }
}

fn text_field<'a>(json: &'a Value, field: &str) -> Result<&'a str> {
    json.get(field)
        .and_then(Value::as_str)
        .ok_or_else(|| StorageError(format!("missing job {field}")))
}
