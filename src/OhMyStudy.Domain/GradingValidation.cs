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
        Require(ValidKey(question.DraftKey), "Questions must have a stable draft_key of at most 128 UTF-8 bytes");
        Require(!string.IsNullOrWhiteSpace(question.Number) && question.Stem.Count != 0 && question.Sources.Count != 0, "Question number, stem, and original image sources must not be empty");
        Require(question.Items.Count is > 0 and <= MaximumItems, "A question needs between 1 and 100 items");
        foreach (var region in question.Sources) ValidateRegion(region, document, "material");
        foreach (var image in question.Stem.OfType<ImageContent>()) ValidateRegion(image.Region, document, "material");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in question.Items)
        {
            Require(ValidKey(item.Key) && keys.Add(item.Key), "Items must have unique stable keys of at most 128 UTF-8 bytes");
            Require(question.Items.Count == 1 || !string.IsNullOrWhiteSpace(item.Label), "Every item of a multi-part question needs its printed label");
            ValidateItem(item, document, references, manual);
        }
    }

    public const int MaximumItems = 100;

    public static int QuestionByteSize(Question question) => SerdeJson.ByteSize(DomainJson.ToElement(question), DomainJson.Context.Question);

    private static bool ValidKey(string key) => !string.IsNullOrWhiteSpace(key) && Encoding.UTF8.GetByteCount(key) <= 128;

    private static void ValidateItem(QuestionItem item, Document doc, IReadOnlyList<ReferenceAnswer> refs, bool manual)
    {
        Require(item.Points is not { } points || (double.IsFinite(points) && points >= 0), "Invalid item points");
        foreach (var image in item.Prompt.OfType<ImageContent>()) ValidateRegion(image.Region, doc, "material");
        Require(doc.Mode switch { ImportMode.Blank => item.Attempt is null, ImportMode.Auto => true, _ => item.Attempt is not null },
            doc.Mode == ImportMode.Blank ? "Blank exams must not create student attempts or unanswered records" : "Preserve attempts from answered materials; mark illegible answers uncertain");
        if (item.Attempt is not { } a) return;
        foreach (var region in a.StudentRegions) ValidateRegion(region, doc, doc.Pages.FirstOrDefault(p => p.Id == region.PageId)?.Role == "student" ? "student" : "material");
        ValidateGrade(a.Grade);
        Require(manual || a.Grade.Source != GradeSource.Manual, "AI cannot mark results as manually edited by the user");
        Require(a.Grade.Possible is null || item.Points is null || a.Grade.Possible == item.Points, "Possible grade points do not match item points");
        if (a.Teacher is { } t)
        {
            foreach (var region in t.Regions) ValidateRegion(region, doc, doc.Pages.FirstOrDefault(p => p.Id == region.PageId)?.Role == "student" ? "student" : "material");
            if (t.Grade is { } g)
            {
                ValidateGrade(g);
                Require(g.Source == GradeSource.Teacher, "Teacher grades must use the teacher source");
                if (doc.Mode is (ImportMode.TeacherGraded or ImportMode.Auto) && !manual) Require(g.Verdict == a.Grade.Verdict && g.Earned == a.Grade.Earned && g.Possible == a.Grade.Possible && g.Source == a.Grade.Source, "Teacher mode must preserve explicit teacher results");
            }
        }
        bool teacherPriority = doc.Mode is (ImportMode.TeacherGraded or ImportMode.Auto) && (a.Grade.Source == GradeSource.Teacher || a.Teacher?.Grade is not null);
        if (!manual && !teacherPriority && refs.FirstOrDefault(r => r.MatchedItemKey == item.Key) is { } reference)
            Require(a.ReferenceKey == reference.Key, "This item has a matched reference answer that must be used for grading");
        if (a.ReferenceKey is { } key)
        {
            Require(refs.Any(r => r.Key == key && r.MatchedItemKey == item.Key), "The reference answer does not match this item");
            if (!teacherPriority && !manual) Require(a.Grade.Source == GradeSource.AiReference, "Grade by the reference answer when one is matched");
        }
        else Require(a.Grade.Source != GradeSource.AiReference, "Reference grading requires a matched reference");
    }

    public static List<string> ItemKeys(Question question) => question.Items.Select(item => item.Key).ToList();
    public static bool IsWrongQuestion(Question question) => question.IsMistake;

    public static void ValidateCoverage(Document document, IReadOnlyList<Coverage> coverage, IReadOnlyList<SavedQuestion> questions, bool referenceOnly, IReadOnlySet<string> viewed)
    {
        var expected = document.Pages.Where(p => !referenceOnly || p.Role == "reference").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var actual = coverage.Select(c => c.PageId).ToHashSet(StringComparer.Ordinal);
        Require(expected.SetEquals(actual) && actual.Count == coverage.Count, "Coverage must include every input page exactly once");
        var keys = questions.Select(q => q.Question.DraftKey).ToHashSet(StringComparer.Ordinal);
        foreach (var c in coverage)
        {
            Require(viewed.Contains(c.PageId), "Unread pages cannot be declared covered");
            Require(c.Status is "covered" or "unreadable" or "no_questions", "Invalid coverage state");
            Require(c.Status == "covered" || !string.IsNullOrWhiteSpace(c.Reason), "Unrecognized pages or pages without questions require a reason");
            foreach (var key in c.QuestionKeys) Require(keys.Contains(key), "Coverage references an unsaved question");
            if (referenceOnly || !document.Pages.Any(p => p.Id == c.PageId && p.Role == "material") || c.Status != "covered") continue;
            Require(c.QuestionKeys.Count != 0, "Covered material pages must list their questions, including continued questions");
            foreach (var key in c.QuestionKeys) Require(questions.Any(q => q.Question.DraftKey == key && q.Question.Sources.Any(r => r.PageId == c.PageId)), "Covered questions must reference the original image on this page");
        }
    }
}
