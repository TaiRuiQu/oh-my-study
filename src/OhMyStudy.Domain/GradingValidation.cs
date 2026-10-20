using System.Text;

namespace OhMyStudy.Domain;

public static class GradingValidation
{
    private static void Require(bool condition, string message) => DomainValidationException.Require(condition, message);

    public static void ValidateRegion(Region region, Document document, string role)
    {
        Require(document.Pages.Any(p => p.Id == region.PageId && p.Role == role), "来源页不存在、已删除或角色不匹配");
        Require(double.IsFinite(region.X) && double.IsFinite(region.Y) && double.IsFinite(region.Width) && double.IsFinite(region.Height)
            && region.X >= 0 && region.Y >= 0 && region.X < 1 && region.Y < 1 && region.Width > 0 && region.Height > 0
            && region.X + region.Width <= 1.000001 && region.Y + region.Height <= 1.000001,
            "来源区域必须位于未旋转原页的归一化坐标内");
    }

    public static void ValidateGrade(Grade grade)
    {
        Require((grade.Earned is not { } earned || (double.IsFinite(earned) && earned >= 0)) && (grade.Possible is not { } possible || (double.IsFinite(possible) && possible >= 0)), "分数必须是非负有限数");
        Require(grade.Earned is null || grade.Possible is not null, "满分未知时不可制造确定得分，请保留教师扣分原文");
        if (grade.Earned is { } a && grade.Possible is { } b)
        {
            Require(a <= b, "得分超出满分");
            Require(grade.Verdict switch
            {
                Verdict.Correct => Math.Abs(a - b) < 0.0001,
                Verdict.Partial => a > 0 && a < b,
                Verdict.Incorrect or Verdict.Unanswered => a == 0,
                _ => false
            }, "判定与分数不一致；无法判定不应有确定分数");
        }
        Require(grade.Verdict != Verdict.Uncertain || grade.Earned is null, "无法判定的得分必须为空");
    }

    public static void ValidateQuestion(Question question, Document document, IReadOnlyList<ReferenceAnswer> references, bool manual)
    {
        try { Require(QuestionByteSize(question) <= 1_048_576, "单题超过 1 MiB"); }
        catch (System.Text.Json.JsonException) { throw new DomainValidationException("题目编码失败"); }
        Visit(question, document, references, manual, new(StringComparer.Ordinal), 0);
    }

    public static int QuestionByteSize(Question question) => SerdeJson.ByteSize(DomainJson.ToElement(question), DomainJson.Context.Question);

    private static void Visit(Question q, Document doc, IReadOnlyList<ReferenceAnswer> refs, bool manual, HashSet<string> keys, int depth)
    {
        Require(depth <= 8 && keys.Count < 1000, "题目结构超过限制");
        Require(!string.IsNullOrWhiteSpace(q.DraftKey) && Encoding.UTF8.GetByteCount(q.DraftKey) <= 128 && keys.Add(q.DraftKey), "题目及小问必须使用不重复的稳定 draft_key");
        Require(!string.IsNullOrWhiteSpace(q.Number) && q.Content.Count != 0 && q.Sources.Count != 0, "题号、内容与原图来源不能为空");
        Require(q.Points is not { } points || (double.IsFinite(points) && points >= 0), "题目分值无效");
        foreach (var region in q.Sources) ValidateRegion(region, doc, "material");
        foreach (var image in q.Content.OfType<ImageContent>()) ValidateRegion(image.Region, doc, "material");
        Require(doc.Mode == ImportMode.Blank ? q.Attempt is null : q.Attempt is not null,
            doc.Mode == ImportMode.Blank ? "空白试卷不得创建学生作答或未作答记录" : "已作答材料需保留作答；无法识别时标记 uncertain");
        if (q.Attempt is { } a)
        {
            foreach (var region in a.StudentRegions) ValidateRegion(region, doc, "material");
            ValidateGrade(a.Grade);
            Require(manual || a.Grade.Source != GradeSource.Manual, "AI 不能将结果标为用户手动修改");
            Require(a.Grade.Possible is null || q.Points is null || a.Grade.Possible == q.Points, "评分满分与题目分值不一致");
            if (a.Teacher is { } t)
            {
                foreach (var region in t.Regions) ValidateRegion(region, doc, "material");
                if (t.Grade is { } g)
                {
                    ValidateGrade(g);
                    Require(g.Source == GradeSource.Teacher, "教师批改来源必须为 teacher");
                    if (doc.Mode == ImportMode.TeacherGraded && !manual) Require(g.Verdict == a.Grade.Verdict && g.Earned == a.Grade.Earned && g.Possible == a.Grade.Possible && g.Source == a.Grade.Source, "教师模式不得覆盖已明确的教师结果");
                }
            }
            bool teacherPriority = doc.Mode == ImportMode.TeacherGraded && (a.Grade.Source == GradeSource.Teacher || a.Teacher?.Grade is not null);
            if (!manual && !teacherPriority && refs.FirstOrDefault(r => r.MatchedDraftKey == q.DraftKey) is { } reference)
                Require(a.ReferenceKey == reference.Key, "本题有匹配参考答案，不能绕过其评分依据");
            if (a.ReferenceKey is { } key)
            {
                Require(refs.Any(r => r.Key == key && r.MatchedDraftKey == q.DraftKey), "参考答案未匹配到本题");
                if (!teacherPriority && !manual) Require(a.Grade.Source == GradeSource.AiReference, "有匹配参考答案时必须按参考答案评分");
            }
            else Require(a.Grade.Source != GradeSource.AiReference, "参考答案评分缺少匹配关系");
        }
        foreach (var part in q.Parts) Visit(part, doc, refs, manual, keys, depth + 1);
        if (q.Parts.Count == 0) return;
        if (q.Parts.All(p => p.Points.HasValue) && q.Points is { } totalPoints)
            Require(Math.Abs(q.Parts.Sum(p => p.Points!.Value) - totalPoints) < 0.0001, "小问满分合计与题目满分不一致");
        if (q.Attempt?.Grade.Earned is { } parent)
            Require(q.Parts.All(p => p.Attempt?.Grade.Earned is not null) && Math.Abs(q.Parts.Sum(p => p.Attempt!.Grade.Earned!.Value) - parent) < 0.0001, "小问得分缺失或合计不一致，不能给出确定总分");
    }

    public static List<string> QuestionKeys(Question question) => [question.DraftKey, .. question.Parts.SelectMany(QuestionKeys)];
    public static bool IsWrongQuestion(Question question) => question.Attempt?.Grade.Verdict is Verdict.Partial or Verdict.Incorrect or Verdict.Unanswered;

    public static void ValidateCoverage(Document document, IReadOnlyList<Coverage> coverage, IReadOnlyList<SavedQuestion> questions, bool referenceOnly, IReadOnlySet<string> viewed)
    {
        var expected = document.Pages.Where(p => !referenceOnly || p.Role == "reference").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var actual = coverage.Select(c => c.PageId).ToHashSet(StringComparer.Ordinal);
        Require(expected.SetEquals(actual) && actual.Count == coverage.Count, "覆盖检查必须逐一包含全部输入页，不得重复或漏页");
        var keys = questions.SelectMany(q => QuestionKeys(q.Question)).ToHashSet(StringComparer.Ordinal);
        foreach (var c in coverage)
        {
            Require(viewed.Contains(c.PageId), "尚未读取的页面不能声明已覆盖");
            Require(c.Status is "covered" or "unreadable" or "no_questions", "无效的覆盖状态");
            Require(c.Status == "covered" || !string.IsNullOrWhiteSpace(c.Reason), "无法识别或无题目页面需要说明原因");
            foreach (var key in c.QuestionKeys) Require(keys.Contains(key), "覆盖检查引用了未保存的题目");
            if (referenceOnly || !document.Pages.Any(p => p.Id == c.PageId && p.Role == "material") || c.Status != "covered") continue;
            Require(c.QuestionKeys.Count != 0, "材料页标为 covered 时需要列出本页题目（含续题）");
            foreach (var key in c.QuestionKeys) Require(questions.Any(q => HasSource(q.Question, key, c.PageId)), "覆盖题目必须实际引用本页原图");
        }
    }

    private static bool HasSource(Question q, string key, string page) =>
        (q.DraftKey == key && q.Sources.Any(r => r.PageId == page)) || q.Parts.Any(p => HasSource(p, key, page));
}
