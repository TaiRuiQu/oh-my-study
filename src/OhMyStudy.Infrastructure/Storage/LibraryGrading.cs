using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    private List<SavedQuestion> Questions(string documentId)
    {
        if (Convert.ToInt32(Scalar("PRAGMA user_version")) < 2) return [];
        return Query("SELECT id,content_json,updated_at FROM questions WHERE document_id=$p0 ORDER BY rowid", r => new SavedQuestion
        { Id = r.GetString(0), DocumentId = documentId, Question = DomainJson.Deserialize<Question>(r.GetString(1)), UpdatedAt = r.GetString(2) }, documentId);
    }

    private List<ReferenceAnswer> References(string documentId)
    {
        if (Convert.ToInt32(Scalar("PRAGMA user_version")) < 2) return [];
        return Scalar("SELECT r.references_json FROM grading_runs r JOIN jobs j ON j.id=r.job_id WHERE r.document_id=$p0 AND r.references_json IS NOT NULL ORDER BY j.created_at DESC,j.rowid DESC LIMIT 1", documentId) is string json
            ? DomainJson.Deserialize<List<ReferenceAnswer>>(json) : [];
    }

    private GradingInput CheckRun(string jobId)
    {
        var result = Query("SELECT j.state,r.input_json FROM jobs j JOIN grading_runs r ON r.job_id=j.id WHERE j.id=$p0", r => (State: r.GetString(0), Json: r.GetString(1)), jobId).SingleOrDefault();
        if (result.State != "RUNNING") throw new InvalidOperationException("任务不是运行状态，拒绝结果写入");
        var input = DomainJson.Deserialize<GradingInput>(result.Json);
        if (!JsonEqual(DomainJson.Serialize(Document(input.Document.Id)), DomainJson.Serialize(input.Document)))
            throw new InvalidOperationException("材料页序或方向已改变，请重新开始整卷任务");
        return input;
    }

    private static bool JsonEqual(string left, string right)
    {
        using var l = JsonDocument.Parse(left); using var r = JsonDocument.Parse(right);
        return JsonElement.DeepEquals(l.RootElement, r.RootElement);
    }

    private SavedQuestion WriteQuestion(string documentId, Question question, string? id = null)
    {
        Execute("INSERT INTO questions(id,document_id,draft_key,content_json,updated_at) VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(document_id,draft_key) DO UPDATE SET content_json=excluded.content_json,updated_at=excluded.updated_at", id ?? NewId(), documentId, question.DraftKey, DomainJson.Serialize(question), Now());
        return Questions(documentId).Single(q => q.Question.DraftKey == question.DraftKey);
    }

    public Task BeginGradingAsync(Job job, CancellationToken cancellationToken = default)
    {
        var input = job.Input ?? throw new ArgumentException("任务输入缺失");
        var json = DomainJson.Serialize(input);
        return Run(() => Transaction(cancellationToken, () => Execute("INSERT INTO grading_runs(job_id,document_id,input_json) VALUES($p0,$p1,$p2)", job.Id, input.Document.Id, json)), cancellationToken, true);
    }

    public Task<List<SavedQuestion>> GradingQuestionsAsync(string documentId, CancellationToken cancellationToken = default)
    { ValidId(documentId); return Run(() => Questions(documentId), cancellationToken); }
    public Task<List<ReferenceAnswer>> GradingReferencesAsync(string documentId, CancellationToken cancellationToken = default)
    { ValidId(documentId); return Run(() => References(documentId), cancellationToken); }

    public Task<SavedQuestion> SaveGradingQuestionAsync(string jobId, Question question, CancellationToken cancellationToken = default)
    {
        ValidId(jobId);
        var json = DomainJson.Serialize(question);
        return Run(() => Transaction(cancellationToken, () =>
        {
            var next = DomainJson.Deserialize<Question>(json);
            var input = CheckRun(jobId); var existing = Questions(input.Document.Id);
            var previous = existing.SingleOrDefault(q => q.Question.DraftKey == next.DraftKey);
            if (previous is not null) next = Preserve(next, previous.Question, input.Document.Mode == ImportMode.TeacherGraded);
            var references = References(input.Document.Id);
            GradingValidation.ValidateQuestion(next, input.Document, references, true);
            CheckManual(next, previous?.Question);
            ValidateAi(next, input.Document, references);
            var incoming = GradingValidation.QuestionKeys(next).ToHashSet(StringComparer.Ordinal);
            if (existing.Where(q => q.Question.DraftKey != next.DraftKey).Any(q => GradingValidation.QuestionKeys(q.Question).Any(incoming.Contains)))
                throw new InvalidOperationException("小问的稳定键与其他题冲突");
            return WriteQuestion(input.Document.Id, next, previous?.Id);
        }), cancellationToken, true);
    }

    private static bool Protected(Question question, bool teacherMode) => question.Attempt is { } a &&
        (a.Grade.Source == GradeSource.Manual || teacherMode && a.Teacher?.Grade is not null) || question.Parts.Any(p => Protected(p, teacherMode));

    private static Question Preserve(Question next, Question old, bool teacherMode)
    {
        if (old.Parts.Any(p => Protected(p, teacherMode) && !next.Parts.Any(n => n.DraftKey == p.DraftKey)))
            throw new InvalidOperationException("不能在重批中移除已有教师或手动评分的小问，请保留原结构");
        if (old.Attempt is { } previous)
        {
            var preserve = previous.Grade.Source == GradeSource.Manual || teacherMode && previous.Teacher?.Grade is not null;
            if (preserve && next.Attempt is null) throw new InvalidOperationException("不能在重批中移除已有教师或手动评分");
            if (next.Attempt is { } attempt)
            {
                if (previous.Grade.Source == GradeSource.Manual) next = next with { Attempt = attempt with { Grade = previous.Grade } };
                else if (teacherMode && previous.Teacher?.Grade is not null) next = next with { Attempt = attempt with { Teacher = previous.Teacher, Grade = previous.Grade } };
            }
        }
        return next with { Parts = next.Parts.Select(p => old.Parts.FirstOrDefault(o => o.DraftKey == p.DraftKey) is { } previous ? Preserve(p, previous, teacherMode) : p).ToList() };
    }

    private static void CheckManual(Question question, Question? old)
    {
        if (question.Attempt?.Grade.Source == GradeSource.Manual && old?.Attempt?.Grade.Source != GradeSource.Manual)
            throw new InvalidOperationException("AI 不能生成手动批改来源");
        foreach (var part in question.Parts) CheckManual(part, old?.Parts.FirstOrDefault(p => p.DraftKey == part.DraftKey));
    }

    private static void ValidateAi(Question question, Document document, IReadOnlyList<ReferenceAnswer> references)
    {
        if (question.Attempt?.Grade.Source != GradeSource.Manual) GradingValidation.ValidateQuestion(question with { Parts = [] }, document, references, false);
        foreach (var part in question.Parts) ValidateAi(part, document, references);
    }

    public Task SaveGradingReferencesAsync(string jobId, IReadOnlyList<ReferenceAnswer> references, CancellationToken cancellationToken = default)
    {
        var json = DomainJson.Serialize(references.ToList());
        return Run(() => Transaction(cancellationToken, () =>
        {
            var stored = DomainJson.Deserialize<List<ReferenceAnswer>>(json); var input = CheckRun(jobId);
            var keys = new HashSet<string>(StringComparer.Ordinal); var matches = new HashSet<string>(StringComparer.Ordinal);
            if (stored.Count > 1000) throw new ArgumentException("参考答案数量超过限制");
            foreach (var reference in stored)
            {
                if (string.IsNullOrWhiteSpace(reference.Key) || !keys.Add(reference.Key) || reference.Sources.Count == 0 || string.IsNullOrWhiteSpace(reference.Answer))
                    throw new ArgumentException("参考答案需有唯一键、正文与原图来源");
                if (reference.MatchedDraftKey is { } match)
                { if (!matches.Add(match)) throw new ArgumentException("同一题目不能匹配多条参考答案"); }
                else if (string.IsNullOrWhiteSpace(reference.UncertainReason)) throw new ArgumentException("未匹配的参考答案需要说明原因");
                foreach (var source in reference.Sources) GradingValidation.ValidateRegion(source, input.Document, "reference");
            }
            foreach (var question in Questions(input.Document.Id))
                if (ReferenceKeys(question.Question).Any(key => !keys.Contains(key))) throw new InvalidOperationException("已有批改引用的参考答案键不能丢弃");
            return Execute("UPDATE grading_runs SET references_json=$p0 WHERE job_id=$p1", json, jobId);
        }), cancellationToken, true);
    }

    private static IEnumerable<string> ReferenceKeys(Question question)
    {
        if (question.Attempt?.ReferenceKey is { } key) yield return key;
        foreach (var part in question.Parts) foreach (var nested in ReferenceKeys(part)) yield return nested;
    }

    public Task FinishGradingAsync(string jobId, IReadOnlyList<Coverage> coverage, IReadOnlySet<string> viewed, bool referenceOnly,
        IReadOnlySet<string> savedKeys, CancellationToken cancellationToken = default)
    {
        var json = DomainJson.Serialize(coverage.ToList()); var viewedCopy = viewed.ToHashSet(); var saved = savedKeys.ToHashSet();
        return Run(() => Transaction(cancellationToken, () =>
        {
            var input = CheckRun(jobId); var questions = Questions(input.Document.Id); var references = References(input.Document.Id);
            GradingValidation.ValidateCoverage(input.Document, DomainJson.Deserialize<List<Coverage>>(json), questions, referenceOnly, viewedCopy);
            if (!referenceOnly)
            {
                if (!questions.Select(q => q.Question.DraftKey).ToHashSet().SetEquals(saved)) throw new InvalidOperationException("本轮必须保存全部题目，已有题不能被静默遗漏");
                foreach (var question in questions) GradingValidation.ValidateQuestion(question.Question, input.Document, references, true);
            }
            var allKeys = questions.SelectMany(q => GradingValidation.QuestionKeys(q.Question)).ToHashSet();
            if (references.Any(r => r.MatchedDraftKey is { } key && !allKeys.Contains(key))) throw new InvalidOperationException("参考答案引用了未保存的题目");
            return Execute("UPDATE grading_runs SET coverage_json=$p0 WHERE job_id=$p1", json, jobId);
        }), cancellationToken, true);
    }

    public Task RecordModelCallAsync(string jobId, uint ordinal, JsonElement details, CancellationToken cancellationToken = default)
    {
        var json = details.GetRawText();
        return Run(() => Transaction(cancellationToken, () => { CheckRun(jobId); return Execute("INSERT INTO model_calls(job_id,ordinal,details_json) VALUES($p0,$p1,$p2)", jobId, ordinal, json); }), cancellationToken, true);
    }

    public Task<GradingResult> GradingResultAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ValidId(documentId);
        return Run(() =>
        {
            var current = Document(documentId);
            if (Convert.ToInt32(Scalar("PRAGMA user_version")) < 2) return GradingResult.Empty;
            var previous = Query("SELECT r.coverage_json,r.input_json FROM grading_runs r JOIN jobs j ON j.id=r.job_id WHERE r.document_id=$p0 AND r.coverage_json IS NOT NULL ORDER BY j.created_at DESC,j.rowid DESC LIMIT 1", r => (Coverage: r.GetString(0), Input: r.GetString(1)), documentId).SingleOrDefault();
            var coverage = previous.Input is not null && JsonEqual(DomainJson.Serialize(DomainJson.Deserialize<GradingInput>(previous.Input).Document), DomainJson.Serialize(current)) ? DomainJson.Deserialize<List<Coverage>>(previous.Coverage) : [];
            return new GradingResult
            {
                Questions = Questions(documentId),
                References = References(documentId),
                Coverage = coverage,
                ModelCalls = Query("SELECT c.details_json FROM model_calls c JOIN grading_runs r ON r.job_id=c.job_id JOIN jobs j ON j.id=c.job_id WHERE r.document_id=$p0 ORDER BY j.created_at,c.ordinal", r => DomainJson.Deserialize<JsonElement>(r.GetString(0)), documentId)
            };
        }, cancellationToken);
    }

    public Task<SavedQuestion> EditQuestionAsync(string id, Question question, CancellationToken cancellationToken = default)
    {
        ValidId(id); var json = DomainJson.Serialize(question);
        return Run(() => Transaction(cancellationToken, () =>
        {
            var row = Query("SELECT document_id,draft_key FROM questions WHERE id=$p0", r => (DocumentId: r.GetString(0), Key: r.GetString(1)), id).Single();
            DocumentIdle(row.DocumentId); var next = DomainJson.Deserialize<Question>(json);
            if (next.DraftKey != row.Key) throw new InvalidOperationException("不能修改题目的稳定键");
            next = Manual(next);
            var existing = Questions(row.DocumentId); var incoming = GradingValidation.QuestionKeys(next).ToHashSet();
            if (existing.Where(q => q.Id != id).Any(q => GradingValidation.QuestionKeys(q.Question).Any(incoming.Contains))) throw new InvalidOperationException("小问的稳定键与其他题冲突");
            GradingValidation.ValidateQuestion(next, Document(row.DocumentId), References(row.DocumentId), true);
            return WriteQuestion(row.DocumentId, next, id);
        }), cancellationToken, true);
    }
    private static Question Manual(Question question) => question with
    {
        Attempt = question.Attempt is { } attempt ? attempt with { Grade = attempt.Grade with { Source = GradeSource.Manual } } : null,
        Parts = question.Parts.Select(Manual).ToList()
    };
    public Task<List<SavedQuestion>> ListQuestionsAsync(CancellationToken cancellationToken = default) => Run(() =>
        Convert.ToInt32(Scalar("PRAGMA user_version")) < 2 ? [] : Query("SELECT DISTINCT document_id FROM questions", r => r.GetString(0)).SelectMany(Questions).ToList(), cancellationToken);
}
