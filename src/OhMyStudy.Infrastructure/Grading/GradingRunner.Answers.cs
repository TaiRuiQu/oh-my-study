using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

public sealed partial class GradingRunner
{
    private async Task<Question> AssessQuestionAsync(JobContext context, GradingInput input, Document document, ExamLayout? layout,
        SplitQuestion split, Question solved, List<ReferenceAnswer> references, ExamWorkflowSettings settings, GradingWorkspace workspace,
        CancellationToken cancellationToken)
    {
        await StageAsync(context, solved, "assess", "Assess student work", "running").ConfigureAwait(false);
        var bindings = split.AnswerBindings.ToDictionary(b => b.ItemKey, StringComparer.Ordinal);
        var gradeable = solved.Items.Where(i => bindings[i.Key].Status is "matched" or "blank").ToList();
        var results = new Dictionary<string, Attempt>(StringComparer.Ordinal);
        var uncertainties = new List<string>();
        if (gradeable.Count > 0)
        {
            var regions = gradeable.SelectMany(i => bindings[i.Key].Regions).Distinct().ToList();
            var evidence = await EvidenceAsync(document, regions, cancellationToken).ConfigureAwait(false);
            var payload = new JsonObject
            {
                ["question"] = DomainJson.ToElement(solved with { Items = gradeable }).AsNode(),
                ["mode"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(document.Mode.ToString()),
                ["exam_layout"] = layout is null ? null : DomainJson.ToElement(layout).AsNode(),
                ["answer_bindings"] = DomainJson.ToElement(gradeable.Select(i => bindings[i.Key]).ToList()).AsNode(),
                ["references"] = DomainJson.ToElement(references.Where(r => gradeable.Any(i => i.Key == r.MatchedItemKey)).ToList()).AsNode(),
                ["image_regions"] = DomainJson.ToElement(evidence.Regions).AsNode(), ["image_rotations"] = ImageRotations(document, evidence.Regions)
            };
            var grader = input.Grader ?? new WorkflowModel(input.Provider, input.Model);
            void ValidateAssessment(QuestionAssessment candidate)
            {
                RequireItems(solved with { Items = gradeable }, candidate.Items.Select(i => i.Key));
                foreach (var item in candidate.Items)
                    foreach (var region in item.Attempt.StudentRegions.Concat(item.Attempt.Teacher?.Regions ?? []))
                        DomainValidationException.Require(bindings[item.Key].Regions.Any(source => Contains(source, region)),
                            $"The grading result assigned evidence from a different answer region. Item: {item.Key}.");
                var assessedItems = gradeable.Select(item =>
                {
                    var attempt = candidate.Items.Single(i => i.Key == item.Key).Attempt;
                    return item with { Points = item.Points ?? attempt.Grade.Possible, Attempt = attempt };
                }).ToList();
                GradingValidation.ValidateQuestion(solved with { Items = assessedItems }, document, references, false);
            }
            var assessment = await CallAsync<QuestionAssessment>(context, grader.Provider, grader.Model,
                input.Grader is null ? input.Thinking : null, input.AllowUnknown, "assess", WorkflowPrompts.Assess,
                Parse(payload), evidence.Images, settings, workspace, cancellationToken, ValidateAssessment).ConfigureAwait(false);
            foreach (var item in assessment.Items)
            {
                var binding = bindings[item.Key];
                results[item.Key] = item.Attempt with { StudentRegions = binding.Regions };
            }
            uncertainties.AddRange(assessment.Uncertainties);
        }
        var items = solved.Items.Select(item =>
        {
            var binding = bindings[item.Key];
            if (results.TryGetValue(item.Key, out var assessment)) return item with { Points = item.Points ?? assessment.Grade.Possible, Attempt = assessment };
            if (binding.Status == "none") return item;
            var reference = references.FirstOrDefault(r => r.MatchedItemKey == item.Key);
            uncertainties.Add(binding.Reason);
            return item with { Attempt = new Attempt { StudentRegions = binding.Regions, AiSolution = item.Answer, ReferenceKey = reference?.Key,
                Grade = new Grade { Verdict = Verdict.Uncertain, Earned = null, Possible = item.Points,
                    Source = reference is null ? GradeSource.AiSolution : GradeSource.AiReference, Reason = binding.Reason } } };
        }).ToList();
        await StageAsync(context, solved, "assess", "Assess student work", "completed",
            uncertainties.Count > 0 ? "Answer matching or grading needs review." : "").ConfigureAwait(false);
        return solved with { Items = items, Uncertainties = solved.Uncertainties.Concat(uncertainties).Distinct().ToList() };
    }

    private static bool Contains(Region outer, Region inner) => outer.PageId == inner.PageId && inner.X >= outer.X - .000001
        && inner.Y >= outer.Y - .000001 && inner.X + inner.Width <= outer.X + outer.Width + .000001
        && inner.Y + inner.Height <= outer.Y + outer.Height + .000001;
}
