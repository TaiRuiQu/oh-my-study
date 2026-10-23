using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    private List<SavedQuestion> Questions(string documentId)
    {
        int version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version < 2) return [];
        return Query("SELECT id,content_json,updated_at FROM questions WHERE document_id=$p0 ORDER BY rowid", r => new SavedQuestion
        {
            Id = r.GetString(0),
            DocumentId = documentId,
            Question = DomainJson.Deserialize<Question>(version < 4 ? LegacyQuestionFormat.UpgradeQuestion(r.GetString(1)) : r.GetString(1)),
            UpdatedAt = r.GetString(2)
        }, documentId);
    }

    private List<ReferenceAnswer> References(string documentId)
    {
        int version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version < 2) return [];
        return Scalar("SELECT r.references_json FROM grading_runs r JOIN jobs j ON j.id=r.job_id WHERE r.document_id=$p0 AND r.references_json IS NOT NULL ORDER BY j.created_at DESC,j.rowid DESC LIMIT 1", documentId) is string json
            ? DomainJson.Deserialize<List<ReferenceAnswer>>(version < 4 ? LegacyQuestionFormat.UpgradeReferences(json) : json) : [];
    }

    private GradingInput CheckRun(string jobId)
    {
        var result = Query("SELECT j.state,r.input_json FROM jobs j JOIN grading_runs r ON r.job_id=j.id WHERE j.id=$p0", r => (State: r.GetString(0), Json: r.GetString(1)), jobId).SingleOrDefault();
        if (result.State != "RUNNING") throw new InvalidOperationException("The task is not running. Results cannot be written");
        var input = DomainJson.Deserialize<GradingInput>(result.Json);
        if (!JsonEqual(DomainJson.Serialize(Document(input.Document.Id)), DomainJson.Serialize(input.Document)))
            throw new InvalidOperationException("Document page order or rotation has changed. Restart the whole-paper task");
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
        WriteQuestionSummary(documentId, question);
        return Questions(documentId).Single(q => q.Question.DraftKey == question.DraftKey);
    }

    private void WriteQuestionSummary(string documentId, Question question) =>
        Execute("UPDATE questions SET number=$p0,subject=$p1,question_type=$p2,preview=$p3,search_text=$p4,item_count=$p5,verdict=$p6,earned=$p7,possible=$p8,mistake=$p9,review=$p10 WHERE document_id=$p11 AND draft_key=$p12",
            question.Number, question.Subject.Trim(), question.QuestionType.Trim(), QuestionText.Preview(question), QuestionText.Search(question), question.Items.Count,
            question.Outcome is { } outcome ? JsonNamingPolicy.SnakeCaseLower.ConvertName(outcome.ToString()) : null, question.Earned, question.Points,
            question.IsMistake, question.NeedsReview, documentId, question.DraftKey);

    private void UpgradeQuestionRecords()
    {
        foreach (var row in Query("SELECT document_id,draft_key,content_json FROM questions", r => (DocumentId: r.GetString(0), Key: r.GetString(1), Json: r.GetString(2))))
        {
            string json = LegacyQuestionFormat.UpgradeQuestion(row.Json);
            Execute("UPDATE questions SET content_json=$p0 WHERE document_id=$p1 AND draft_key=$p2", json, row.DocumentId, row.Key);
            WriteQuestionSummary(row.DocumentId, DomainJson.Deserialize<Question>(json));
        }
        foreach (var row in Query("SELECT job_id,references_json FROM grading_runs WHERE references_json IS NOT NULL", r => (Job: r.GetString(0), Json: r.GetString(1))))
            Execute("UPDATE grading_runs SET references_json=$p0 WHERE job_id=$p1", LegacyQuestionFormat.UpgradeReferences(row.Json), row.Job);
    }

    public Task BeginGradingAsync(Job job, CancellationToken cancellationToken = default)
    {
        var input = job.Input ?? throw new ArgumentException("Task input is missing");
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
            next = next with { Notes = previous?.Question.Notes ?? "" };
            if (previous is not null) next = Preserve(next, previous.Question, input.Document.Mode == ImportMode.TeacherGraded);
            var references = References(input.Document.Id);
            GradingValidation.ValidateQuestion(next, input.Document, references, true);
            CheckManual(next, previous?.Question);
            if (next.Items.Any(item => item.Attempt?.Grade.Source != GradeSource.Manual))
                GradingValidation.ValidateQuestion(next with { Items = next.Items.Where(item => item.Attempt?.Grade.Source != GradeSource.Manual).ToList() }, input.Document, references, false);
            RequireUniqueItemKeys(existing.Where(q => q.Question.DraftKey != next.DraftKey), next);
            return WriteQuestion(input.Document.Id, next, previous?.Id);
        }), cancellationToken, true);
    }

    private static void RequireUniqueItemKeys(IEnumerable<SavedQuestion> others, Question question)
    {
        var incoming = GradingValidation.ItemKeys(question).ToHashSet(StringComparer.Ordinal);
        if (others.Any(q => GradingValidation.ItemKeys(q.Question).Any(incoming.Contains)))
            throw new InvalidOperationException("An item's stable key conflicts with another question");
    }

    private static bool Protected(QuestionItem item, bool teacherMode) => item.Attempt is { } a &&
        (a.Grade.Source == GradeSource.Manual || teacherMode && (a.Grade.Source == GradeSource.Teacher || a.Teacher?.Grade is not null));

    private static Question Preserve(Question next, Question old, bool teacherMode)
    {
        if (old.Items.Any(item => Protected(item, teacherMode) && !next.Items.Any(n => n.Key == item.Key)))
            throw new InvalidOperationException("Regrading cannot remove items with teacher or manual grades. Preserve the original structure");
        return next with { Items = next.Items.Select(item => old.Items.FirstOrDefault(o => o.Key == item.Key) is { } previous ? Preserve(item, previous, teacherMode) : item).ToList() };
    }

    private static QuestionItem Preserve(QuestionItem next, QuestionItem old, bool teacherMode)
    {
        if (old.Attempt is not { } previous) return next;
        var preserve = previous.Grade.Source == GradeSource.Manual || teacherMode && (previous.Grade.Source == GradeSource.Teacher || previous.Teacher?.Grade is not null);
        if (preserve && next.Attempt is null) throw new InvalidOperationException("Regrading cannot remove existing teacher or manual grades");
        if (next.Attempt is not { } attempt) return next;
        if (previous.Grade.Source == GradeSource.Manual) return next with { Attempt = attempt with { Grade = previous.Grade } };
        if (teacherMode && (previous.Grade.Source == GradeSource.Teacher || previous.Teacher?.Grade is not null)) return next with { Attempt = attempt with { Teacher = previous.Teacher, Grade = previous.Grade } };
        return next;
    }

    private static void CheckManual(Question question, Question? old)
    {
        foreach (var item in question.Items)
            if (item.Attempt?.Grade.Source == GradeSource.Manual && old?.Items.FirstOrDefault(o => o.Key == item.Key)?.Attempt?.Grade.Source != GradeSource.Manual)
                throw new InvalidOperationException("AI cannot generate a manual grading source");
    }

    public Task SaveGradingReferencesAsync(string jobId, IReadOnlyList<ReferenceAnswer> references, CancellationToken cancellationToken = default)
    {
        var json = DomainJson.Serialize(references.ToList());
        return Run(() => Transaction(cancellationToken, () =>
        {
            var stored = DomainJson.Deserialize<List<ReferenceAnswer>>(json); var input = CheckRun(jobId);
            var keys = new HashSet<string>(StringComparer.Ordinal); var matches = new HashSet<string>(StringComparer.Ordinal);
            if (stored.Count > 1000) throw new ArgumentException("The number of reference answers exceeds the limit");
            foreach (var reference in stored)
            {
                if (string.IsNullOrWhiteSpace(reference.Key) || !keys.Add(reference.Key) || reference.Sources.Count == 0 || string.IsNullOrWhiteSpace(reference.Answer))
                    throw new ArgumentException("Reference answers require unique keys, content, and original image sources");
                if (reference.MatchedItemKey is { } match)
                { if (!matches.Add(match)) throw new ArgumentException("An item cannot match multiple reference answers"); }
                else if (string.IsNullOrWhiteSpace(reference.UncertainReason)) throw new ArgumentException("Unmatched reference answers require a reason");
                foreach (var source in reference.Sources) GradingValidation.ValidateRegion(source, input.Document, "reference");
            }
            foreach (var question in Questions(input.Document.Id))
                if (ReferenceKeys(question.Question).Any(key => !keys.Contains(key))) throw new InvalidOperationException("Reference keys used by existing grades must be preserved");
            return Execute("UPDATE grading_runs SET references_json=$p0 WHERE job_id=$p1", json, jobId);
        }), cancellationToken, true);
    }

    private static IEnumerable<string> ReferenceKeys(Question question) =>
        question.Items.Select(item => item.Attempt?.ReferenceKey).OfType<string>();

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
                if (!questions.Select(q => q.Question.DraftKey).ToHashSet().SetEquals(saved)) throw new InvalidOperationException("This round must save all questions. Existing questions must not be silently omitted");
                foreach (var question in questions) GradingValidation.ValidateQuestion(question.Question, input.Document, references, true);
            }
            var allKeys = questions.SelectMany(q => GradingValidation.ItemKeys(q.Question)).ToHashSet();
            if (references.Any(r => r.MatchedItemKey is { } key && !allKeys.Contains(key))) throw new InvalidOperationException("A reference answer points to an unsaved item");
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
            if (next.DraftKey != row.Key) throw new InvalidOperationException("A question's stable key cannot be changed");
            var existing = Questions(row.DocumentId);
            next = Manual(next, existing.Single(q => q.Id == id).Question);
            RequireUniqueItemKeys(existing.Where(q => q.Id != id), next);
            GradingValidation.ValidateQuestion(next, Document(row.DocumentId), References(row.DocumentId), true);
            return WriteQuestion(row.DocumentId, next, id);
        }), cancellationToken, true);
    }

    private static Question Manual(Question question, Question old) => question with
    {
        Items = question.Items.Select(item => item.Attempt is { } attempt
            && (old.Items.FirstOrDefault(o => o.Key == item.Key)?.Attempt is not { } previous || DomainJson.Serialize(previous) != DomainJson.Serialize(attempt))
            ? item with { Attempt = attempt with { Grade = attempt.Grade with { Source = GradeSource.Manual } } } : item).ToList()
    };

    public Task<SavedQuestion> SaveQuestionNotesAsync(string id, string notes, CancellationToken cancellationToken = default)
    {
        ValidId(id);
        if (notes.Length > 100_000) throw new ArgumentException("Notes are too long");
        return Run(() => Transaction(cancellationToken, () =>
        {
            var row = Query("SELECT document_id,draft_key FROM questions WHERE id=$p0", r => (DocumentId: r.GetString(0), Key: r.GetString(1)), id).SingleOrDefault();
            if (row.DocumentId is null) throw new KeyNotFoundException("Question not found");
            var saved = Questions(row.DocumentId).Single(q => q.Id == id);
            return WriteQuestion(row.DocumentId, saved.Question with { Notes = notes }, id);
        }), cancellationToken, true);
    }

    public Task<List<SavedQuestion>> ListQuestionsAsync(CancellationToken cancellationToken = default) => Run(() =>
        Convert.ToInt32(Scalar("PRAGMA user_version")) < 2 ? [] : Query("SELECT DISTINCT document_id FROM questions", r => r.GetString(0)).SelectMany(Questions).ToList(), cancellationToken);
}
