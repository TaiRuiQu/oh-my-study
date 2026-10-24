using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    public Task<SavedQuestion> SaveStudentAnswersAsync(string questionId, IReadOnlyDictionary<string, string> answers, CancellationToken cancellationToken = default)
    {
        ValidId(questionId);
        var submitted = answers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return Run(() => Transaction(cancellationToken, () =>
        {
            string documentId = Scalar("SELECT document_id FROM questions WHERE id=$p0", questionId) as string ?? throw new KeyNotFoundException("Question not found");
            DocumentIdle(documentId);
            var document = Document(documentId);
            var saved = Questions(documentId).Single(question => question.Id == questionId);
            if (submitted.Keys.Any(key => !saved.Question.Items.Any(item => item.Key == key))) throw new ArgumentException("The answer does not match an existing item");
            bool changed = false;
            var items = saved.Question.Items.Select(item =>
            {
                if (!submitted.TryGetValue(item.Key, out var answer) || answer == (item.Attempt?.StudentAnswer ?? "")) return item;
                if (answer is null) throw new ArgumentException("Student answers cannot be null");
                changed = true;
                return item with
                {
                    Attempt = new Attempt
                    {
                        StudentAnswer = answer,
                        StudentAnswerEdited = true,
                        AiSolution = item.Attempt?.AiSolution ?? "",
                        Grade = new Grade { Verdict = Verdict.Uncertain, Possible = item.Points, Source = GradeSource.AiSolution, Reason = "Student answer changed; recognize again to grade it." }
                    }
                };
            }).ToList();
            if (!changed) return saved;
            var next = saved.Question with { Items = items };
            if (document.Mode == ImportMode.Blank) document = document with { Mode = ImportMode.Auto };
            GradingValidation.ValidateQuestion(next, document, References(documentId), true);
            Execute("UPDATE documents SET mode=$p0 WHERE id=$p1", DomainJson.Serialize(document.Mode), documentId);
            Execute("UPDATE paper_info SET earned=NULL WHERE document_id=$p0", documentId);
            return WriteQuestion(documentId, next, saved.Id);
        }), cancellationToken, true);
    }
}
