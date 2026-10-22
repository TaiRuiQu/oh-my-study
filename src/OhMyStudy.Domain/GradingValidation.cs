using System.Text;

namespace OhMyStudy.Domain;

public static class GradingValidation
{
    private static void Require(bool condition, string message) => DomainValidationException.Require(condition, message);

    public static void ValidateRegion(Region region, Document document, string role)
    {
        Require(document.Pages.Any(p => p.Id == region.PageId && p.Role == role), "The source page is missing, deleted, or has the wrong role");
        Require(double.IsFinite(region.X) && double.IsFinite(region.Y) && double.IsFinite(region.Width) && double.IsFinite(region.Height)
            && region.X >= 0 && region.Y >= 0 && region.X < 1 && region.Y < 1 && region.Width > 0 && region.Height > 0
            && region.X + region.Width <= 1.000001 && region.Y + region.Height <= 1.000001,
            "Source regions must use normalized coordinates within the unrotated original page");
    }

    public static void ValidateGrade(Grade grade)
    {
        Require((grade.Earned is not { } earned || (double.IsFinite(earned) && earned >= 0)) && (grade.Possible is not { } possible || (double.IsFinite(possible) && possible >= 0)), "Scores must be finite nonnegative numbers");
        Require(grade.Earned is null || grade.Possible is not null, "Do not assign a definite score when possible points are unknown. Preserve the teacher's deduction text");
        if (grade.Earned is { } a && grade.Possible is { } b)
        {
            Require(a <= b, "Earned points exceed possible points");
            Require(grade.Verdict switch
            {
                Verdict.Correct => Math.Abs(a - b) < 0.0001,
                Verdict.Partial => a > 0 && a < b,
                Verdict.Incorrect or Verdict.Unanswered => a == 0,
                _ => false
            }, "The verdict and score disagree; an uncertain verdict cannot have a definite score");
        }
        Require(grade.Verdict != Verdict.Uncertain || grade.Earned is null, "Uncertain earned points must be null");
    }

    public static void ValidateQuestion(Question question, Document document, IReadOnlyList<ReferenceAnswer> references, bool manual)
    {
        try { Require(QuestionByteSize(question) <= 1_048_576, "A question exceeds 1 MiB"); }
        catch (System.Text.Json.JsonException) { throw new DomainValidationException("Question encoding failed"); }
        Visit(question, document, references, manual, new(StringComparer.Ordinal), 0);
    }

    public static int QuestionByteSize(Question question) => SerdeJson.ByteSize(DomainJson.ToElement(question), DomainJson.Context.Question);

    private static void Visit(Question q, Document doc, IReadOnlyList<ReferenceAnswer> refs, bool manual, HashSet<string> keys, int depth)
    {
        Require(depth <= 8 && keys.Count < 1000, "The question structure exceeds the limit");
        Require(!string.IsNullOrWhiteSpace(q.DraftKey) && Encoding.UTF8.GetByteCount(q.DraftKey) <= 128 && keys.Add(q.DraftKey), "Questions and parts must have unique stable draft_key values");
        Require(!string.IsNullOrWhiteSpace(q.Number) && q.Content.Count != 0 && q.Sources.Count != 0, "Question number, content, and original image sources must not be empty");
        Require(q.Points is not { } points || (double.IsFinite(points) && points >= 0), "Invalid question points");
        foreach (var region in q.Sources) ValidateRegion(region, doc, "material");
        foreach (var image in q.Content.OfType<ImageContent>()) ValidateRegion(image.Region, doc, "material");
        Require(doc.Mode == ImportMode.Blank ? q.Attempt is null : q.Attempt is not null,
            doc.Mode == ImportMode.Blank ? "Blank exams must not create student attempts or unanswered records" : "Preserve attempts from answered materials; mark illegible answers uncertain");
        if (q.Attempt is { } a)
        {
            foreach (var region in a.StudentRegions) ValidateRegion(region, doc, "material");
            ValidateGrade(a.Grade);
            Require(manual || a.Grade.Source != GradeSource.Manual, "AI cannot mark results as manually edited by the user");
            Require(a.Grade.Possible is null || q.Points is null || a.Grade.Possible == q.Points, "Possible grade points do not match question points");
            if (a.Teacher is { } t)
            {
                foreach (var region in t.Regions) ValidateRegion(region, doc, "material");
                if (t.Grade is { } g)
                {
                    ValidateGrade(g);
                    Require(g.Source == GradeSource.Teacher, "Teacher grades must use the teacher source");
                    if (doc.Mode == ImportMode.TeacherGraded && !manual) Require(g.Verdict == a.Grade.Verdict && g.Earned == a.Grade.Earned && g.Possible == a.Grade.Possible && g.Source == a.Grade.Source, "Teacher mode must preserve explicit teacher results");
                }
            }
            bool teacherPriority = doc.Mode == ImportMode.TeacherGraded && (a.Grade.Source == GradeSource.Teacher || a.Teacher?.Grade is not null);
            if (!manual && !teacherPriority && refs.FirstOrDefault(r => r.MatchedDraftKey == q.DraftKey) is { } reference)
                Require(a.ReferenceKey == reference.Key, "This question has a matched reference answer that must be used for grading");
            if (a.ReferenceKey is { } key)
            {
                Require(refs.Any(r => r.Key == key && r.MatchedDraftKey == q.DraftKey), "The reference answer does not match this question");
                if (!teacherPriority && !manual) Require(a.Grade.Source == GradeSource.AiReference, "Grade by the reference answer when one is matched");
            }
            else Require(a.Grade.Source != GradeSource.AiReference, "Reference grading requires a matched reference");
        }
        foreach (var part in q.Parts) Visit(part, doc, refs, manual, keys, depth + 1);
        if (q.Parts.Count == 0) return;
        if (q.Parts.All(p => p.Points.HasValue) && q.Points is { } totalPoints)
            Require(Math.Abs(q.Parts.Sum(p => p.Points!.Value) - totalPoints) < 0.0001, "Possible points for parts do not add up to the question's possible points");
        if (q.Attempt?.Grade.Earned is { } parent)
            Require(q.Parts.All(p => p.Attempt?.Grade.Earned is not null) && Math.Abs(q.Parts.Sum(p => p.Attempt!.Grade.Earned!.Value) - parent) < 0.0001, "Part scores are missing or inconsistent; a definite total cannot be assigned");
    }

    public static List<string> QuestionKeys(Question question) => [question.DraftKey, .. question.Parts.SelectMany(QuestionKeys)];
    public static bool IsWrongQuestion(Question question) => question.Attempt?.Grade.Verdict is Verdict.Partial or Verdict.Incorrect or Verdict.Unanswered;

    public static void ValidateCoverage(Document document, IReadOnlyList<Coverage> coverage, IReadOnlyList<SavedQuestion> questions, bool referenceOnly, IReadOnlySet<string> viewed)
    {
        var expected = document.Pages.Where(p => !referenceOnly || p.Role == "reference").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var actual = coverage.Select(c => c.PageId).ToHashSet(StringComparer.Ordinal);
        Require(expected.SetEquals(actual) && actual.Count == coverage.Count, "Coverage must include every input page exactly once");
        var keys = questions.SelectMany(q => QuestionKeys(q.Question)).ToHashSet(StringComparer.Ordinal);
        foreach (var c in coverage)
        {
            Require(viewed.Contains(c.PageId), "Unread pages cannot be declared covered");
            Require(c.Status is "covered" or "unreadable" or "no_questions", "Invalid coverage state");
            Require(c.Status == "covered" || !string.IsNullOrWhiteSpace(c.Reason), "Unrecognized pages or pages without questions require a reason");
            foreach (var key in c.QuestionKeys) Require(keys.Contains(key), "Coverage references an unsaved question");
            if (referenceOnly || !document.Pages.Any(p => p.Id == c.PageId && p.Role == "material") || c.Status != "covered") continue;
            Require(c.QuestionKeys.Count != 0, "Covered material pages must list their questions, including continued questions");
            foreach (var key in c.QuestionKeys) Require(questions.Any(q => HasSource(q.Question, key, c.PageId)), "Covered questions must reference the original image on this page");
        }
    }

    private static bool HasSource(Question q, string key, string page) =>
        (q.DraftKey == key && q.Sources.Any(r => r.PageId == page)) || q.Parts.Any(p => HasSource(p, key, page));
}
