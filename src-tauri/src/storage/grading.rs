use super::{new_id, now, valid_id, Database, Result, Storage, StorageError};
use crate::{domain::grading::*, jobs::Job};
use rusqlite::{params, OptionalExtension};
use std::collections::HashSet;

impl Database {
    pub(super) fn page_has_results(&self, document_id: &str, page_id: &str) -> Result<bool> {
        fn contains(value: &serde_json::Value, page: &str) -> bool {
            match value {
                serde_json::Value::Object(map) => {
                    map.get("page_id").and_then(|v| v.as_str()) == Some(page)
                        || map.values().any(|v| contains(v, page))
                }
                serde_json::Value::Array(items) => items.iter().any(|v| contains(v, page)),
                _ => false,
            }
        }
        for q in self.questions(document_id)? {
            if contains(&serde_json::to_value(q.question)?, page_id) {
                return Ok(true);
            }
        }
        for r in self.references(document_id)? {
            if contains(&serde_json::to_value(r)?, page_id) {
                return Ok(true);
            }
        }
        Ok(false)
    }
    pub(super) fn document_idle(&self, id: &str) -> Result<()> {
        let mut query=self.connection.prepare("SELECT snapshot_json FROM jobs WHERE state IN ('QUEUED','RUNNING','WAITING_PERMISSION')")?;
        for value in query.query_map([], |row| row.get::<_, String>(0))? {
            let value: serde_json::Value = serde_json::from_str(&value?)?;
            if value.pointer("/input/document/id").and_then(|v| v.as_str()) == Some(id) {
                return Err(StorageError(
                    "该材料正在处理，请先取消任务再编辑页面或结果".into(),
                ));
            }
        }
        Ok(())
    }
    fn questions(&self, document_id: &str) -> Result<Vec<SavedQuestion>> {
        let mut query = self.connection.prepare(
            "SELECT id,content_json,updated_at FROM questions WHERE document_id=?1 ORDER BY rowid",
        )?;
        let result = query
            .query_map([document_id], |r| {
                Ok((
                    r.get::<_, String>(0)?,
                    r.get::<_, String>(1)?,
                    r.get::<_, String>(2)?,
                ))
            })?
            .map(|r| {
                let (id, json, updated_at) = r?;
                Ok(SavedQuestion {
                    id,
                    document_id: document_id.into(),
                    updated_at,
                    question: serde_json::from_str(&json)?,
                })
            })
            .collect();
        result
    }
    fn references(&self, document_id: &str) -> Result<Vec<ReferenceAnswer>> {
        let value:Option<String>=self.connection.query_row("SELECT r.references_json FROM grading_runs r JOIN jobs j ON j.id=r.job_id WHERE r.document_id=?1 AND r.references_json IS NOT NULL ORDER BY j.created_at DESC,j.rowid DESC LIMIT 1",[document_id],|r|r.get(0)).optional()?;
        value
            .map(|json| serde_json::from_str(&json).map_err(Into::into))
            .unwrap_or(Ok(vec![]))
    }
    fn check_run(&self, job_id: &str) -> Result<GradingInput> {
        let (state,json):(String,String)=self.connection.query_row("SELECT j.state,r.input_json FROM jobs j JOIN grading_runs r ON r.job_id=j.id WHERE j.id=?1",[job_id],|r|Ok((r.get(0)?,r.get(1)?)))?;
        if state != "RUNNING" {
            return Err(StorageError("任务不是运行状态，拒绝结果写入".into()));
        }
        let input: GradingInput = serde_json::from_str(&json)?;
        if serde_json::to_value(self.document(&input.document.id)?)?
            != serde_json::to_value(&input.document)?
        {
            return Err(StorageError(
                "材料页序或方向已改变，请重新开始整卷任务".into(),
            ));
        }
        Ok(input)
    }
    fn write_question(
        &mut self,
        document_id: &str,
        question: Question,
        id: Option<String>,
    ) -> Result<SavedQuestion> {
        let id = id.unwrap_or_else(new_id);
        let updated_at = now();
        self.connection.execute("INSERT INTO questions(id,document_id,draft_key,content_json,updated_at) VALUES(?1,?2,?3,?4,?5) ON CONFLICT(document_id,draft_key) DO UPDATE SET content_json=excluded.content_json,updated_at=excluded.updated_at",params![id,document_id,question.draft_key,serde_json::to_string(&question)?,updated_at])?;
        self.questions(document_id)?
            .into_iter()
            .find(|q| q.question.draft_key == question.draft_key)
            .ok_or_else(|| StorageError("题目保存后未找到".into()))
    }
}

impl Storage {
    pub fn begin_grading(&self, job: &Job) -> std::result::Result<(), String> {
        let job = job.clone();
        self.run_blocking(move |db| {
            let input = job
                .input
                .as_ref()
                .ok_or_else(|| StorageError("任务输入缺失".into()))?;
            db.connection.execute(
                "INSERT INTO grading_runs(job_id,document_id,input_json) VALUES(?1,?2,?3)",
                params![job.id, input.document.id, serde_json::to_string(input)?],
            )?;
            Ok(())
        })
    }
    pub fn grading_questions(
        &self,
        document_id: String,
    ) -> std::result::Result<Vec<SavedQuestion>, String> {
        self.run_blocking(move |db| db.questions(&document_id))
    }
    pub fn grading_references(
        &self,
        document_id: String,
    ) -> std::result::Result<Vec<ReferenceAnswer>, String> {
        self.run_blocking(move |db| db.references(&document_id))
    }
    pub fn save_grading_question(
        &self,
        job_id: String,
        mut question: Question,
    ) -> std::result::Result<SavedQuestion, String> {
        self.run_blocking(move |db| {
            let input = db.check_run(&job_id)?;
            let existing = db.questions(&input.document.id)?;
            // Regrading keeps explicit teacher evidence and manual edits; the model cannot erase them.
            fn preserve(next: &mut Question, old: &Question, teacher_mode: bool) -> Result<()> {
                fn protected(q: &Question, teacher: bool) -> bool {
                    q.attempt.as_ref().is_some_and(|a| {
                        a.grade.source == GradeSource::Manual
                            || teacher && a.teacher.as_ref().is_some_and(|t| t.grade.is_some())
                    }) || q.parts.iter().any(|p| protected(p, teacher))
                }
                if old.parts.iter().any(|p| {
                    protected(p, teacher_mode)
                        && !next.parts.iter().any(|n| n.draft_key == p.draft_key)
                }) {
                    return Err(StorageError(
                        "不能在重批中移除已有教师或手动评分的小问；请保留原结构，或由用户明确编辑"
                            .into(),
                    ));
                }
                if let (Some(a), Some(b)) = (&mut next.attempt, &old.attempt) {
                    if b.grade.source == GradeSource::Manual {
                        a.grade = b.grade.clone();
                    } else if teacher_mode && b.teacher.as_ref().is_some_and(|t| t.grade.is_some())
                    {
                        a.teacher = b.teacher.clone();
                        a.grade = b.grade.clone();
                    }
                }
                for p in &mut next.parts {
                    if let Some(previous) = old.parts.iter().find(|o| o.draft_key == p.draft_key) {
                        preserve(p, previous, teacher_mode)?;
                    }
                }
                Ok(())
            }
            let previous = existing
                .iter()
                .find(|q| q.question.draft_key == question.draft_key);
            if let Some(old) = previous {
                preserve(
                    &mut question,
                    &old.question,
                    input.document.mode == crate::domain::ImportMode::TeacherGraded,
                )?;
            }
            let refs = db.references(&input.document.id)?;
            validate_question(&question, &input.document, &refs, true).map_err(StorageError)?;
            // A new AI result must not claim manual provenance.
            fn check(q: &Question, old: Option<&Question>) -> Result<()> {
                if q.attempt
                    .as_ref()
                    .is_some_and(|a| a.grade.source == GradeSource::Manual)
                    && !old
                        .and_then(|o| o.attempt.as_ref())
                        .is_some_and(|a| a.grade.source == GradeSource::Manual)
                {
                    return Err(StorageError("AI 不能生成手动批改来源".into()));
                }
                for p in &q.parts {
                    check(
                        p,
                        old.and_then(|o| o.parts.iter().find(|p0| p0.draft_key == p.draft_key)),
                    )?;
                }
                Ok(())
            }
            check(&question, previous.map(|p| &p.question))?;
            // Validate teacher/reference rules unless preserving an existing manual result.
            fn validate_ai(
                q: &Question,
                doc: &crate::domain::Document,
                refs: &[ReferenceAnswer],
            ) -> Result<()> {
                if !q
                    .attempt
                    .as_ref()
                    .is_some_and(|a| a.grade.source == GradeSource::Manual)
                {
                    let mut flat = q.clone();
                    flat.parts.clear();
                    validate_question(&flat, doc, refs, false).map_err(StorageError)?;
                }
                for part in &q.parts {
                    validate_ai(part, doc, refs)?;
                }
                Ok(())
            }
            validate_ai(&question, &input.document, &refs)?;
            let incoming: HashSet<_> = question_keys(&question).into_iter().collect();
            if existing
                .iter()
                .filter(|q| q.question.draft_key != question.draft_key)
                .any(|q| {
                    question_keys(&q.question)
                        .iter()
                        .any(|key| incoming.contains(key))
                })
            {
                return Err(StorageError("小问 draft_key 与其他题冲突".into()));
            }
            db.write_question(&input.document.id, question, previous.map(|p| p.id.clone()))
        })
    }
    pub fn save_grading_references(
        &self,
        job_id: String,
        references: Vec<ReferenceAnswer>,
    ) -> std::result::Result<(), String> {
        self.run_blocking(move |db| {
            let input = db.check_run(&job_id)?;
            let mut keys = HashSet::new();
            let mut matches = HashSet::new();
            if references.len() > 1000 {
                return Err(StorageError("参考答案数量超过限制".into()));
            }
            for r in &references {
                if let Some(key) = &r.matched_draft_key {
                    if !matches.insert(key) {
                        return Err(StorageError(
                            "同一题目不能匹配多条互相冲突的参考答案".into(),
                        ));
                    }
                } else if r
                    .uncertain_reason
                    .as_ref()
                    .is_none_or(|r| r.trim().is_empty())
                {
                    return Err(StorageError("未匹配的参考答案需要说明原因".into()));
                }
                if r.key.trim().is_empty()
                    || !keys.insert(r.key.clone())
                    || r.sources.is_empty()
                    || r.answer.trim().is_empty()
                {
                    return Err(StorageError("参考答案需有唯一键、正文与原图来源".into()));
                }
                for region in &r.sources {
                    validate_region(region, &input.document, "reference").map_err(StorageError)?;
                }
            }
            fn reference_keys(q: &Question) -> Vec<String> {
                let mut keys = q
                    .attempt
                    .as_ref()
                    .and_then(|a| a.reference_key.clone())
                    .into_iter()
                    .collect::<Vec<_>>();
                for p in &q.parts {
                    keys.extend(reference_keys(p));
                }
                keys
            }
            for q in db.questions(&input.document.id)? {
                if reference_keys(&q.question)
                    .iter()
                    .any(|key| !keys.contains(key))
                {
                    return Err(StorageError(
                        "已有批改引用的参考答案键不能丢弃；请保留稳定键后更新匹配".into(),
                    ));
                }
            }
            db.connection.execute(
                "UPDATE grading_runs SET references_json=?1 WHERE job_id=?2",
                params![serde_json::to_string(&references)?, job_id],
            )?;
            Ok(())
        })
    }
    pub fn finish_grading(
        &self,
        job_id: String,
        coverage: Vec<Coverage>,
        viewed: HashSet<String>,
        reference_only: bool,
        saved_keys: HashSet<String>,
    ) -> std::result::Result<(), String> {
        self.run_blocking(move |db| {
            let input = db.check_run(&job_id)?;
            let questions = db.questions(&input.document.id)?;
            validate_coverage(
                &input.document,
                &coverage,
                &questions,
                reference_only,
                &viewed,
            )
            .map_err(StorageError)?;
            if !reference_only {
                let all: HashSet<_> = questions
                    .iter()
                    .map(|q| q.question.draft_key.clone())
                    .collect();
                if all != saved_keys {
                    return Err(StorageError(
                        "本轮必须保存全部题目；已有题不能被静默遗漏".into(),
                    ));
                }
                for q in &questions {
                    validate_question(
                        &q.question,
                        &input.document,
                        &db.references(&input.document.id)?,
                        true,
                    )
                    .map_err(StorageError)?;
                }
            }
            let refs = db.references(&input.document.id)?;
            let all_keys: HashSet<_> = questions
                .iter()
                .flat_map(|q| question_keys(&q.question))
                .collect();
            if refs
                .iter()
                .filter_map(|r| r.matched_draft_key.as_ref())
                .any(|key| !all_keys.contains(key))
            {
                return Err(StorageError(
                    "参考答案引用未保存的题目，请修正匹配关系".into(),
                ));
            }
            db.connection.execute(
                "UPDATE grading_runs SET coverage_json=?1 WHERE job_id=?2",
                params![serde_json::to_string(&coverage)?, job_id],
            )?;
            Ok(())
        })
    }
    pub fn record_model_call(
        &self,
        job_id: String,
        ordinal: u32,
        details: serde_json::Value,
    ) -> std::result::Result<(), String> {
        self.run_blocking(move |db| {
            db.check_run(&job_id)?;
            db.connection.execute(
                "INSERT INTO model_calls(job_id,ordinal,details_json) VALUES(?1,?2,?3)",
                params![job_id, ordinal, details.to_string()],
            )?;
            Ok(())
        })
    }
    pub async fn grading_result(&self, document_id: String) -> Result<GradingResult> {
        valid_id(&document_id)?;
        self.run(move|db|{
            let current=db.document(&document_id)?;
            let previous:Option<(String,String)>=db.connection.query_row("SELECT r.coverage_json,r.input_json FROM grading_runs r JOIN jobs j ON j.id=r.job_id WHERE r.document_id=?1 AND r.coverage_json IS NOT NULL ORDER BY j.created_at DESC,j.rowid DESC LIMIT 1",[&document_id],|r|Ok((r.get(0)?,r.get(1)?))).optional()?;
            let json=match previous {Some((coverage,input)) if serde_json::from_str::<GradingInput>(&input).ok().is_some_and(|input|serde_json::to_value(input.document).ok()==serde_json::to_value(&current).ok())=>Some(coverage),_=>None};
            let mut query=db.connection.prepare("SELECT c.details_json FROM model_calls c JOIN grading_runs r ON r.job_id=c.job_id JOIN jobs j ON j.id=c.job_id WHERE r.document_id=?1 ORDER BY j.created_at,c.ordinal")?;
            let calls=query.query_map([&document_id],|r|r.get::<_,String>(0))?.map(|r|Ok(serde_json::from_str(&r?)?)).collect::<Result<Vec<_>>>()?;
            Ok(GradingResult{questions:db.questions(&document_id)?,coverage:json.map(|s|serde_json::from_str(&s)).transpose()?.unwrap_or_default(),references:db.references(&document_id)?,model_calls:calls})
        }).await
    }
    pub async fn edit_question(&self, id: String, mut question: Question) -> Result<SavedQuestion> {
        valid_id(&id)?;
        self.run(move |db| {
            let (document_id, key): (String, String) = db.connection.query_row(
                "SELECT document_id,draft_key FROM questions WHERE id=?1",
                [&id],
                |r| Ok((r.get(0)?, r.get(1)?)),
            )?;
            db.document_idle(&document_id)?;
            if question.draft_key != key {
                return Err(StorageError("不能修改题目的稳定键".into()));
            }
            fn manual(q: &mut Question) {
                if let Some(a) = &mut q.attempt {
                    a.grade.source = GradeSource::Manual;
                }
                for p in &mut q.parts {
                    manual(p);
                }
            }
            manual(&mut question);
            validate_question(
                &question,
                &db.document(&document_id)?,
                &db.references(&document_id)?,
                true,
            )
            .map_err(StorageError)?;
            db.write_question(&document_id, question, Some(id))
        })
        .await
    }
    pub async fn list_questions(&self) -> Result<Vec<SavedQuestion>> {
        self.run(|db| {
            let mut query = db
                .connection
                .prepare("SELECT DISTINCT document_id FROM questions")?;
            let ids = query
                .query_map([], |r| r.get::<_, String>(0))?
                .collect::<std::result::Result<Vec<_>, _>>()?;
            let mut result = Vec::new();
            for id in ids {
                result.extend(db.questions(&id)?);
            }
            Ok(result)
        })
        .await
    }
}
