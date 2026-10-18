use super::{Document, ImportMode, ModelConfig, Provider};
use crate::ai::providers::Thinking;
use schemars::JsonSchema;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct GradingInput {
    pub document: Document,
    pub provider: Provider,
    pub model: ModelConfig,
    pub thinking: Option<Thinking>,
    pub allow_unknown: bool,
}

/// Coordinates are in the unrotated source raster, normalized to [0,1].
#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct Region {
    pub page_id: String,
    pub x: f64,
    pub y: f64,
    pub width: f64,
    pub height: f64,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(tag = "kind", rename_all = "snake_case", deny_unknown_fields)]
pub enum Content {
    Text {
        text: String,
    },
    Formula {
        latex: String,
    },
    Table {
        rows: Vec<Vec<String>>,
    },
    Image {
        region: Region,
        contains_handwriting: bool,
        caption: String,
    },
}

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum Verdict {
    Correct,
    Partial,
    Incorrect,
    Unanswered,
    Uncertain,
}
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize, JsonSchema)]
#[serde(rename_all = "snake_case")]
pub enum GradeSource {
    Teacher,
    AiReference,
    AiSolution,
    Manual,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct Grade {
    pub verdict: Verdict,
    pub earned: Option<f64>,
    pub possible: Option<f64>,
    pub source: GradeSource,
    pub reason: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct TeacherMark {
    pub raw: String,
    pub regions: Vec<Region>,
    pub grade: Option<Grade>,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct Attempt {
    pub student_answer: String,
    pub student_regions: Vec<Region>,
    pub teacher: Option<TeacherMark>,
    pub reference_key: Option<String>,
    pub ai_solution: String,
    pub conflict: Option<String>,
    pub grade: Grade,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct Question {
    pub draft_key: String,
    pub number: String,
    pub subject: String,
    pub question_type: String,
    pub content: Vec<Content>,
    pub options: Vec<String>,
    pub points: Option<f64>,
    pub standard_answer: String,
    pub explanation: String,
    pub knowledge_points: Vec<String>,
    pub sources: Vec<Region>,
    pub uncertainties: Vec<String>,
    pub attempt: Option<Attempt>,
    pub parts: Vec<Question>,
}

#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct SavedQuestion {
    pub id: String,
    pub document_id: String,
    pub updated_at: String,
    pub question: Question,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct ReferenceAnswer {
    pub key: String,
    pub number: String,
    pub answer: String,
    pub explanation: String,
    pub sources: Vec<Region>,
    /// Null means unmatched. Display matches before a separately requested regrade.
    pub matched_draft_key: Option<String>,
    pub uncertain_reason: Option<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, JsonSchema)]
#[serde(deny_unknown_fields)]
pub struct Coverage {
    pub page_id: String,
    pub status: String,
    pub question_keys: Vec<String>,
    pub reason: String,
}

#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct GradingResult {
    pub questions: Vec<SavedQuestion>,
    pub coverage: Vec<Coverage>,
    pub references: Vec<ReferenceAnswer>,
    pub model_calls: Vec<serde_json::Value>,
}

fn require(condition: bool, message: &str) -> Result<(), String> {
    if condition {
        Ok(())
    } else {
        Err(message.into())
    }
}
pub fn validate_region(region: &Region, document: &Document, role: &str) -> Result<(), String> {
    require(
        document
            .pages
            .iter()
            .any(|p| p.id == region.page_id && p.role == role),
        "来源页不存在、已删除或角色不匹配",
    )?;
    require(
        [region.x, region.y, region.width, region.height]
            .iter()
            .all(|v| v.is_finite())
            && region.x >= 0.0
            && region.y >= 0.0
            && region.x < 1.0
            && region.y < 1.0
            && region.width > 0.0
            && region.height > 0.0
            && region.x + region.width <= 1.000001
            && region.y + region.height <= 1.000001,
        "来源区域必须位于未旋转原页的归一化坐标内",
    )
}
fn validate_grade(grade: &Grade) -> Result<(), String> {
    require(
        [grade.earned, grade.possible]
            .into_iter()
            .flatten()
            .all(|s| s.is_finite() && s >= 0.0),
        "分数必须是非负有限数",
    )?;
    require(
        grade.earned.is_none() || grade.possible.is_some(),
        "满分未知时不可制造确定得分，请保留教师扣分原文",
    )?;
    if let (Some(a), Some(b)) = (grade.earned, grade.possible) {
        require(a <= b, "得分超出满分")?;
        require(
            match grade.verdict {
                Verdict::Correct => (a - b).abs() < 0.0001,
                Verdict::Partial => a > 0.0 && a < b,
                Verdict::Incorrect | Verdict::Unanswered => a == 0.0,
                Verdict::Uncertain => false,
            },
            "判定与分数不一致；无法判定不应有确定分数",
        )?;
    }
    require(
        grade.verdict != Verdict::Uncertain || grade.earned.is_none(),
        "无法判定的得分必须为空",
    )?;
    require(
        grade.verdict != Verdict::Uncertain || !grade.reason.trim().is_empty(),
        "无法判定需要说明原因",
    )
}

pub fn validate_question(
    question: &Question,
    document: &Document,
    references: &[ReferenceAnswer],
    manual: bool,
) -> Result<(), String> {
    fn visit(
        q: &Question,
        doc: &Document,
        refs: &[ReferenceAnswer],
        manual: bool,
        keys: &mut HashSet<String>,
        depth: usize,
    ) -> Result<(), String> {
        require(depth <= 8 && keys.len() < 1000, "题目结构超过限制")?;
        require(
            !q.draft_key.trim().is_empty()
                && q.draft_key.len() <= 128
                && keys.insert(q.draft_key.clone()),
            "题目及小问必须使用不重复的稳定 draft_key",
        )?;
        require(
            !q.number.trim().is_empty() && !q.content.is_empty() && !q.sources.is_empty(),
            "题号、内容与原图来源不能为空",
        )?;
        require(
            q.points.is_none_or(|p| p.is_finite() && p >= 0.0),
            "题目分值无效",
        )?;
        for region in &q.sources {
            validate_region(region, doc, "material")?;
        }
        for content in &q.content {
            if let Content::Image { region, .. } = content {
                validate_region(region, doc, "material")?;
            }
        }
        if doc.mode == ImportMode::Blank {
            require(q.attempt.is_none(), "空白试卷不得创建学生作答或未作答记录")?;
        } else {
            require(
                q.attempt.is_some(),
                "已作答材料需保留作答；无法识别时标记 uncertain",
            )?;
        }
        if let Some(a) = &q.attempt {
            for region in &a.student_regions {
                validate_region(region, doc, "material")?;
            }
            validate_grade(&a.grade)?;
            require(
                manual || a.grade.source != GradeSource::Manual,
                "AI 不能将结果标为用户手动修改",
            )?;
            require(
                a.grade.possible.is_none() || q.points.is_none() || a.grade.possible == q.points,
                "评分满分与题目分值不一致",
            )?;
            if let Some(t) = &a.teacher {
                require(
                    !t.raw.trim().is_empty() && !t.regions.is_empty(),
                    "教师结果必须保留原始批注和原图证据",
                )?;
                for region in &t.regions {
                    validate_region(region, doc, "material")?;
                }
                if let Some(g) = &t.grade {
                    validate_grade(g)?;
                    require(
                        g.source == GradeSource::Teacher,
                        "教师批改来源必须为 teacher",
                    )?;
                    if doc.mode == ImportMode::TeacherGraded && !manual {
                        require(
                            serde_json::to_value(g).unwrap()
                                == serde_json::to_value(&a.grade).unwrap(),
                            "教师模式不得覆盖已明确的教师结果",
                        )?;
                    }
                }
            }
            if a.grade.source == GradeSource::Teacher {
                require(
                    a.teacher.as_ref().is_some_and(|t| t.grade.is_some()),
                    "教师评分缺少证据",
                )?;
            }
            if !manual
                && !(doc.mode == ImportMode::TeacherGraded
                    && a.teacher.as_ref().is_some_and(|t| t.grade.is_some()))
            {
                if let Some(reference) = refs
                    .iter()
                    .find(|r| r.matched_draft_key.as_deref() == Some(q.draft_key.as_str()))
                {
                    require(
                        a.reference_key.as_deref() == Some(reference.key.as_str()),
                        "本题有匹配参考答案，不能绕过其评分依据",
                    )?;
                }
            }
            if let Some(key) = &a.reference_key {
                require(
                    refs.iter().any(|r| {
                        &r.key == key
                            && r.matched_draft_key.as_deref() == Some(q.draft_key.as_str())
                    }),
                    "参考答案未匹配到本题",
                )?;
                if !(doc.mode == ImportMode::TeacherGraded
                    && a.teacher.as_ref().is_some_and(|t| t.grade.is_some()))
                    && !manual
                {
                    require(
                        a.grade.source == GradeSource::AiReference,
                        "有匹配参考答案时必须按参考答案评分",
                    )?;
                }
            } else {
                require(
                    a.grade.source != GradeSource::AiReference,
                    "参考答案评分缺少匹配关系",
                )?;
            }
        }
        for p in &q.parts {
            visit(p, doc, refs, manual, keys, depth + 1)?;
        }
        if !q.parts.is_empty() {
            let total: Option<f64> = q.parts.iter().map(|p| p.points).sum();
            if let (Some(total), Some(points)) = (total, q.points) {
                require(
                    (total - points).abs() < 0.0001,
                    "小问满分合计与题目满分不一致",
                )?;
            }
            if let Some(parent) = q.attempt.as_ref().and_then(|a| a.grade.earned) {
                let sum: Option<f64> = q
                    .parts
                    .iter()
                    .map(|p| p.attempt.as_ref().and_then(|a| a.grade.earned))
                    .sum();
                require(
                    sum.is_some_and(|sum| (sum - parent).abs() < 0.0001),
                    "小问得分缺失或合计不一致，不能给出确定总分",
                )?;
            }
        }
        Ok(())
    }
    require(
        serde_json::to_vec(question)
            .map_err(|_| "题目编码失败")?
            .len()
            <= 1024 * 1024,
        "单题超过 1 MiB",
    )?;
    visit(
        question,
        document,
        references,
        manual,
        &mut HashSet::new(),
        0,
    )
}

pub fn question_keys(question: &Question) -> Vec<String> {
    let mut keys = vec![question.draft_key.clone()];
    for part in &question.parts {
        keys.extend(question_keys(part));
    }
    keys
}

pub fn validate_coverage(
    document: &Document,
    coverage: &[Coverage],
    questions: &[SavedQuestion],
    reference_only: bool,
    viewed: &HashSet<String>,
) -> Result<(), String> {
    let expected: HashSet<_> = document
        .pages
        .iter()
        .filter(|p| !reference_only || p.role == "reference")
        .map(|p| p.id.as_str())
        .collect();
    let actual: HashSet<_> = coverage.iter().map(|c| c.page_id.as_str()).collect();
    require(
        expected == actual && actual.len() == coverage.len(),
        "覆盖检查必须逐一包含全部输入页，不得重复或漏页",
    )?;
    for c in coverage {
        require(viewed.contains(&c.page_id), "尚未读取的页面不能声明已覆盖")?;
        require(
            ["covered", "unreadable", "no_questions"].contains(&c.status.as_str()),
            "无效的覆盖状态",
        )?;
        require(
            c.status == "covered" || !c.reason.trim().is_empty(),
            "无法识别或无题目页面需要说明原因",
        )?;
        for key in &c.question_keys {
            require(
                questions
                    .iter()
                    .any(|q| question_keys(&q.question).contains(key)),
                "覆盖检查引用了未保存的题目",
            )?;
        }
        if !reference_only
            && document
                .pages
                .iter()
                .any(|p| p.id == c.page_id && p.role == "material")
            && c.status == "covered"
        {
            require(
                !c.question_keys.is_empty(),
                "材料页标为 covered 时需要列出本页题目（含续题）",
            )?;
            for key in &c.question_keys {
                fn has(q: &Question, key: &str, page: &str) -> bool {
                    (q.draft_key == key && q.sources.iter().any(|r| r.page_id == page))
                        || q.parts.iter().any(|p| has(p, key, page))
                }
                require(
                    questions.iter().any(|q| has(&q.question, key, &c.page_id)),
                    "覆盖题目必须实际引用本页原图",
                )?;
            }
        }
    }
    Ok(())
}
