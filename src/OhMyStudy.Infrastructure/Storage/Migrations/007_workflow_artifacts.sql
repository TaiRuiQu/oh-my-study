CREATE TABLE workflow_artifacts (
  job_id TEXT NOT NULL REFERENCES grading_runs(job_id) ON DELETE CASCADE,
  name TEXT NOT NULL,
  mime TEXT NOT NULL,
  content BLOB NOT NULL,
  PRIMARY KEY(job_id, name)
);
