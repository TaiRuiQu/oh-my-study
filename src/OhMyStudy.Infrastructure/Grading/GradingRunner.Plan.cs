using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

public sealed partial class GradingRunner
{
    private static QuestionSplitPlan ResolveAnswerRegions(QuestionSplitPlan plan, List<OcrPage> ocr, Document document)
    {
        var blocks = ocr.SelectMany(p => p.Blocks).ToDictionary(b => b.Id, StringComparer.Ordinal);
        var studentPages = document.Pages.Where(page => page.Role == "student").Select(page => page.Id).ToHashSet(StringComparer.Ordinal);
        return plan with { Questions = plan.Questions.Select(q =>
        {
            var bindings = q.AnswerBindings.Select(binding =>
            {
                var unknown = binding.BlockIds.Where(id => !blocks.ContainsKey(id)).ToList();
                DomainValidationException.Require(unknown.Count == 0,
                    $"Answer binding contains unknown OCR block IDs. Question: {q.Key}; item: {binding.ItemKey}; IDs: {string.Join(", ", unknown)}.");
                return binding with { Regions = binding.Regions.Concat(binding.BlockIds.Select(id => blocks[id].Region)).Distinct().ToList() };
            }).ToList();
            string number = q.Number.Trim().TrimEnd('.', '．', '、');
            var labels = blocks.Values.Where(block => block.Kind == "text" && studentPages.Contains(block.Region.PageId)
                && new[] { number + ".", number + "．", number + "、" }.Contains(block.Markdown.Trim(), StringComparer.Ordinal)
                && !plan.IgnoredBlockIds.Contains(block.Id, StringComparer.Ordinal)
                && bindings.Any(binding => binding.Status is "matched" or "blank" && binding.Regions.Any(region => region.PageId == block.Region.PageId)))
                .GroupBy(block => block.Region.PageId).Where(group => group.Count() == 1).Select(group => group.Single());
            foreach (var label in labels)
            {
                if (bindings.Any(binding => binding.Regions.Any(region => Contains(region, label.Region)))) continue;
                int index = bindings.FindIndex(binding => binding.Status is "matched" or "blank" && binding.Regions.Any(region => region.PageId == label.Region.PageId));
                bindings[index] = bindings[index] with { BlockIds = bindings[index].BlockIds.Append(label.Id).Distinct(StringComparer.Ordinal).ToList(),
                    Regions = bindings[index].Regions.Append(label.Region).Distinct().ToList() };
                OperationDiagnostics.Record("Workflow.AnswerLabel", new() { ["question_key"] = q.Key, ["item_key"] = bindings[index].ItemKey, ["block_id"] = label.Id });
            }
            return q with { AnswerBindings = bindings };
        }).ToList() };
    }

    private async Task<(List<Image> Images, List<Region> Regions)> EvidenceAsync(Document document, List<Region> regions, CancellationToken cancellationToken)
    {
        var result = new List<Image>();
        foreach (var region in regions)
        {
            var page = document.Pages.Single(p => p.Id == region.PageId);
            GradingValidation.ValidateRegion(region, document, page.Role);
            var asset = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellationToken).ConfigureAwait(false);
            result.Add((await images.ReadAsync(asset.Bytes, region, page.Rotation, cancellationToken).ConfigureAwait(false)).Image);
        }
        return (result, regions);
    }

    private static Question CreateDraft(SplitQuestion split, List<OcrPage> ocr, List<DetectedQuestionRegion> detections, ImportMode mode,
        List<ReferenceAnswer> references, Question? old)
    {
        var blocks = split.BlockIds.Select(id => ocr.SelectMany(p => p.Blocks).Single(b => b.Id == id)).ToList();
        var regions = blocks.Select(b => b.Region).Concat(split.DetectionIds.Select(id => detections.Single(d => d.Id == id).Region))
            .GroupBy(r => r.PageId).Select(g => new Region { PageId = g.Key, X = g.Min(r => r.X), Y = g.Min(r => r.Y),
                Width = g.Max(r => r.X + r.Width) - g.Min(r => r.X), Height = g.Max(r => r.Y + r.Height) - g.Min(r => r.Y) }).ToList();
        var stem = new List<Content> { new TextContent { Text = split.Markdown } };
        foreach (var block in blocks.Where(b => b.Kind == "image" && b.AssetId is not null))
            stem.Add(new ImageContent { AssetId = block.AssetId, Region = block.Region });
        return new Question { DraftKey = split.Key, Number = split.Number, Subject = split.Subject, QuestionType = split.QuestionType,
            Stem = stem, Sources = regions, Uncertainties = new[] { "Awaiting independent solution." }.Concat(split.AnswerBindings.Where(b => b.Status == "unmatched").Select(b => b.Reason)).ToList(), Items = split.Items.Select(i => new QuestionItem { Key = i.Key, Label = i.Label,
                Prompt = string.IsNullOrWhiteSpace(i.Prompt) ? [] : [new TextContent { Text = i.Prompt }], Options = i.Options, Points = i.Points,
                Attempt = DraftAttempt(split, i, mode, references, old?.Items.FirstOrDefault(o => o.Key == i.Key)?.Attempt) }).ToList() };
    }

    private static Attempt? DraftAttempt(SplitQuestion split, SplitQuestionItem item, ImportMode mode, List<ReferenceAnswer> references, Attempt? previous)
    {
        if (mode == ImportMode.Blank) return null;
        var binding = split.AnswerBindings.Single(b => b.ItemKey == item.Key);
        if (previous is not null && (previous.Grade.Source == GradeSource.Manual
            || mode is ImportMode.TeacherGraded or ImportMode.Auto && (previous.Grade.Source == GradeSource.Teacher || previous.Teacher?.Grade is not null))) return previous;
        if (mode == ImportMode.Auto && binding.Status == "none" && previous is null) return null;
        var reference = references.FirstOrDefault(r => r.MatchedItemKey == item.Key);
        return new Attempt { StudentAnswer = previous?.StudentAnswer ?? "", StudentAnswerEdited = previous?.StudentAnswerEdited ?? false,
            StudentRegions = binding.Regions, Teacher = previous?.Teacher,
            Grade = new Grade { Verdict = Verdict.Uncertain, Possible = item.Points, Source = reference is null ? GradeSource.AiSolution : GradeSource.AiReference,
                Reason = binding.Status == "unmatched" ? binding.Reason : "Awaiting independent question assessment." }, ReferenceKey = reference?.Key };
    }

    private static void ValidatePlan(QuestionSplitPlan plan, List<OcrPage> ocr, List<DetectedQuestionRegion> detections, Document document,
        List<SavedQuestion> existing, bool referencesOnly, ExamLayout? layout)
    {
        void Require(bool valid, string message)
        {
            if (!valid) OperationDiagnostics.Record("Workflow.InvalidPlan", new()
            {
                ["stage"] = "split",
                ["mode"] = DomainJson.ToElement(document.Mode).AsNode(),
                ["reason"] = message,
                ["response"] = DomainJson.ToElement(plan).AsNode()
            }, true);
            DomainValidationException.Require(valid, message);
        }
        var blocks = ocr.SelectMany(p => p.Blocks).ToList();
        var blockIds = blocks.Select(b => b.Id).ToHashSet(StringComparer.Ordinal);
        var detectionIds = detections.Select(d => d.Id).ToHashSet(StringComparer.Ordinal);
        var studentPageIds = document.Pages.Where(p => p.Role == "student").Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var answerRegions = plan.Questions.SelectMany(q => q.AnswerBindings).Where(b => b.Status is "matched" or "blank")
            .SelectMany(b => b.Regions).Where(r => studentPageIds.Contains(r.PageId)).ToList();
        var accountedBlockIds = plan.Questions.SelectMany(q => q.BlockIds).Concat(plan.IgnoredBlockIds)
            .Concat(blocks.Where(b => answerRegions.Any(r => Contains(r, b.Region))).Select(b => b.Id)).ToHashSet(StringComparer.Ordinal);
        var accountedDetectionIds = plan.Questions.SelectMany(q => q.DetectionIds).Concat(plan.IgnoredDetectionIds)
            .Concat(detections.Where(d => answerRegions.Any(r => Contains(r, d.Region))).Select(d => d.Id)).ToHashSet(StringComparer.Ordinal);
        if (!blockIds.SetEquals(accountedBlockIds) || !detectionIds.SetEquals(accountedDetectionIds))
        {
            var missingBlockIds = blockIds.Except(accountedBlockIds).ToList();
            var unknownBlockIds = accountedBlockIds.Except(blockIds).ToList();
            var missingDetectionIds = detectionIds.Except(accountedDetectionIds).ToList();
            var unknownDetectionIds = accountedDetectionIds.Except(detectionIds).ToList();
            OperationDiagnostics.Record("Workflow.InvalidPlan", new()
            {
                ["stage"] = "split",
                ["missing_blocks"] = DomainJson.ToElement(blocks.Where(b => missingBlockIds.Contains(b.Id)).ToList()).AsNode(),
                ["unknown_block_ids"] = DomainJson.ToElement(unknownBlockIds).AsNode(),
                ["missing_detections"] = DomainJson.ToElement(detections.Where(d => missingDetectionIds.Contains(d.Id)).ToList()).AsNode(),
                ["unknown_detection_ids"] = DomainJson.ToElement(unknownDetectionIds).AsNode(),
                ["response"] = DomainJson.ToElement(plan).AsNode()
            }, true);
            Require(false, $"The splitting result omitted or invented evidence: {missingBlockIds.Count} missing OCR blocks, "
                + $"{unknownBlockIds.Count} unknown OCR blocks, {missingDetectionIds.Count} missing detected regions, "
                + $"{unknownDetectionIds.Count} unknown detected regions. Missing OCR block IDs: {string.Join(", ", missingBlockIds)}; "
                + $"unknown OCR block IDs: {string.Join(", ", unknownBlockIds)}; missing detection IDs: {string.Join(", ", missingDetectionIds)}; "
                + $"unknown detection IDs: {string.Join(", ", unknownDetectionIds)}.");
        }
        Require(plan.Questions.Select(q => q.Key).Distinct().Count() == plan.Questions.Count, "Duplicate question keys in splitting result.");
        var itemKeys = plan.Questions.SelectMany(q => q.Items).Select(i => i.Key).ToList();
        Require(itemKeys.Distinct().Count() == itemKeys.Count, "Duplicate item keys in splitting result.");
        if (!referencesOnly) Require(existing.All(q => plan.Questions.Any(n => n.Key == q.Question.DraftKey)), "The splitting result omitted an existing question.");
        foreach (var q in plan.Questions)
        {
            Require(!referencesOnly && !string.IsNullOrWhiteSpace(q.Markdown) && q.BlockIds.Count + q.DetectionIds.Count > 0, "Split questions require material evidence and nonempty markdown.");
            foreach (var block in q.BlockIds.Select(id => ocr.SelectMany(p => p.Blocks).Single(b => b.Id == id))) GradingValidation.ValidateRegion(block.Region, document, "material");
            foreach (var region in q.DetectionIds.Select(id => detections.Single(d => d.Id == id).Region)) GradingValidation.ValidateRegion(region, document, "material");
            foreach (var region in q.StudentRegions) GradingValidation.ValidateRegion(region, document, document.Pages.Single(p => p.Id == region.PageId).Role == "student" ? "student" : "material");
            Require(q.Items.Count is > 0 and <= GradingValidation.MaximumItems, "Split questions require valid item counts.");
            Require(q.AnswerBindings.Count == q.Items.Count && q.AnswerBindings.Select(b => b.ItemKey).Distinct().Count() == q.Items.Count
                && q.AnswerBindings.Select(b => b.ItemKey).ToHashSet().SetEquals(q.Items.Select(i => i.Key)),
                $"Every item needs exactly one answer-region binding. Question: {q.Key}; expected item keys: {string.Join(", ", q.Items.Select(i => i.Key))}; supplied binding keys: {string.Join(", ", q.AnswerBindings.Select(b => b.ItemKey))}.");
            foreach (var binding in q.AnswerBindings)
            {
                Require(binding.Status is "matched" or "blank" or "unmatched" or "none", "Invalid answer-region binding status.");
                Require(binding.Status is not ("matched" or "blank") || binding.Regions.Count > 0, "Matched or blank responses require source evidence.");
                Require(binding.Status != "unmatched" || !string.IsNullOrWhiteSpace(binding.Reason), "Unmatched answers require an explanation.");
                Require(binding.Status != "none" || binding.Regions.Count == 0, "Unassessed items cannot claim response regions.");
                if (document.Mode == ImportMode.Blank) Require(binding.Status == "none" && !q.HasStudentWork && q.StudentRegions.Count == 0, "Blank exams cannot claim student responses.");
                else if (document.Mode != ImportMode.Auto) Require(binding.Status != "none",
                    $"Answered exams require a matched, blank, or unmatched response binding. Question: {q.Key}; item: {binding.ItemKey}; mode: {document.Mode}; status: {binding.Status}.");
                foreach (var region in binding.Regions)
                {
                    GradingValidation.ValidateRegion(region, document, document.Pages.Single(p => p.Id == region.PageId).Role == "student" ? "student" : "material");
                    Require(layout?.HasSeparateAnswers != true || document.Pages.Single(p => p.Id == region.PageId).Role == "student"
                        || layout.Pages.Single(p => p.PageId == region.PageId).Kind == ExamPageKind.Mixed,
                        "Separate-sheet responses must come from answer-sheet or mixed pages.");
                }
            }
            Require(!plan.IgnoredBlockIds.Intersect(q.BlockIds).Any() && !plan.IgnoredDetectionIds.Intersect(q.DetectionIds).Any(), "Question evidence cannot also be ignored.");
        }
        var keys = plan.Questions.Select(q => q.Key).ToHashSet();
        Require(plan.Coverage.Select(c => c.PageId).ToHashSet().SetEquals(ocr.Select(p => p.PageId)) && plan.Coverage.Count == ocr.Count, "Splitting coverage must account for every input page exactly once.");
        foreach (var coverage in plan.Coverage)
            Require(coverage.QuestionKeys.All(keys.Contains) || referencesOnly && coverage.QuestionKeys.All(k => existing.Any(q => q.Question.DraftKey == k)), "Coverage contains unknown question keys.");
    }

    private static void RequireItems(Question question, IEnumerable<string> keys)
    {
        var actual = keys.ToList();
        DomainValidationException.Require(actual.Count == question.Items.Count && actual.ToHashSet().SetEquals(question.Items.Select(i => i.Key)), "The question result changed or duplicated item keys.");
    }
}
