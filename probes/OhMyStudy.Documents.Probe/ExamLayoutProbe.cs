using OhMyStudy.Documents;

internal static class ExamLayoutProbe
{
    internal static async Task VerifyAsync(string output, TypstDocumentService service, PdfiumRasterizer rasterizer, List<string> checks)
    {
        var questions = new List<ExamQuestion>
        {
            new(1, [new("SHORT_OPTIONS 选出正确答案。")], Options: ["A. 1", "B. 2", "C. 3", "D. 4"], Points: 3),
            new(2, [new("MEDIUM_OPTIONS 下列说法正确的是（　　）。")], Options:
                ["A. 物体的运动状态保持不变", "B. 物体受到的合外力为零", "C. 速度的大小和方向均不变", "D. 加速度的大小保持不变"], Points: 4),
            new(3, [new("LONG_OPTIONS 阅读下列实验结论，选择合理的解释。")], Options:
                ["A. 保持其他实验条件完全相同，仅改变一个自变量，并通过多次重复测量分析实验数据的变化趋势。",
                 "B. 根据题干给出的边界条件和测量结果，说明实验误差的主要来源，并给出合理的改进方法。",
                 "C. 在控制实验变量的前提下，完整记录测量数据，分析各物理量之间的关系并说明结论成立的范围。",
                 "D. 对比实验组与对照组的结果，根据实际测量数据判断所提出的假设是否成立，并解释判断依据。"], Points: 4),
            new(4, [new("FILL_IN 计算结果为________，单位为________。")], Answer: "PRIVATE_FILL", Points: 4, QuestionType: "填空题"),
            new(5, [new("PARTS 阅读材料，完成下列小问。")], Parts:
                [new("original-a", [new("PART_ONE 写出实验中保持不变的条件。")], Answer: "PRIVATE_PART_ONE", Points: 2),
                 new("original-b", [new("PART_TWO 说明实验结果与结论之间的关系。")], Answer: "PRIVATE_PART_TWO", Points: 4)], Points: 6),
            new(6, [new("FORMULA_OPTIONS 比较下列表达式的值。")], Options:
                [@"A. $\frac{1}{2}$", @"B. $\sqrt{2}$", @"C. $x^2$", @"D. $\frac{3}{4}$"], Points: 4),
        };
        for (int number = 7; number <= 16; number++)
            questions.Add(new(number, [new($"SUBJECTIVE_{number} 根据已知条件写出计算过程，并说明所得结论的适用范围。")],
                Answer: "PRIVATE_SOLUTION\n列出方程，代入条件并化简。\n检验结果，说明结论。", Points: number % 2 == 0 ? 8 : 4));
        var document = new ExamDocument("试卷排版验证", questions,
            new ExamPrintLabels("班级", "姓名", "学号", "（{0:g}分）"));
        foreach (var paper in Enum.GetValues<ExamPaper>())
        {
            int compactPages = 0, fixedPages = 0;
            foreach (int mode in new[] { 0, 1, 2 })
            {
                bool space = mode > 0, distribute = mode == 2;
                string directory = Path.Combine(output, "exam-layout", paper + (mode == 0 ? "-compact" : mode == 1 ? "-writing" : "-distributed"));
                var result = await service.ExportExamAsync(document, paper, ExamEdition.Questions, directory,
                    includeAnswerSpace: space, distributeAnswerSpace: distribute);
                string source = await File.ReadAllTextAsync(result.SourcePath);
                Require(!source.Contains("PRIVATE_", StringComparison.Ordinal), "answer-length estimation does not expose answer text");
                Require(!source.Contains("original-a", StringComparison.Ordinal), "part labels are sequential");
                Require(source.Contains("\"0\":", StringComparison.Ordinal) == distribute, "space distribution is independent of minimum writing areas");
                Require(source.Split("#exam-mark(\"blank\"", StringSplitOptions.None).Length - 1 == (space ? 12 : 0), "only subjective leaves receive writing areas");
                using var pdf = rasterizer.Open(result.PdfPath);
                Require(pdf.PageCount > 0, "exam PDF pages");
                if (space) Require(pdf.PageCount >= compactPages, "compact edition saves paper");
                else compactPages = pdf.PageCount;
                if (mode == 1) fixedPages = pdf.PageCount;
                if (distribute) Require(pdf.PageCount == fixedPages, "distribution preserves the minimum-space page count");
                string text = string.Join("\n", Enumerable.Range(0, pdf.PageCount).Select(pdf.ExtractText)).Replace("\u200b", "");
                string flat = string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
                Require(flat.Contains("1.（3分）SHORT_OPTIONS", StringComparison.Ordinal), "number, score and stem share the question opening");
                Require(flat.Contains("（1）PART_ONE", StringComparison.Ordinal)
                    && flat.Contains("（2）PART_TWO", StringComparison.Ordinal), "consecutive parenthesized part numbers");
                foreach (string marker in new[] { "SHORT_OPTIONS", "MEDIUM_OPTIONS", "LONG_OPTIONS", "FORMULA_OPTIONS", "SUBJECTIVE_16" })
                    Require(text.Contains(marker, StringComparison.Ordinal), "retained exam content " + marker);
                Require(!text.Contains("PRIVATE_", StringComparison.Ordinal), "no answers on question paper");
                for (int page = 0; page < pdf.PageCount; page++)
                {
                    string pageText = pdf.ExtractText(page);
                    Require(pageText.Contains("OPTIONS", StringComparison.Ordinal) || pageText.Contains("SUBJECTIVE_", StringComparison.Ordinal)
                        || pageText.Contains("PART_", StringComparison.Ordinal), "no empty page with only header and footer");
                    foreach (string field in new[] { "班级", "姓名", "学号" })
                        Require(pageText.Contains(field, StringComparison.Ordinal), "repeated writable header " + field);
                    pdf.RenderPage(page, 1400).SaveBmp(Path.Combine(directory, "page-" + (page + 1) + ".bmp"));
                }
                await File.WriteAllTextAsync(Path.Combine(directory, "extracted.txt"), text);
            }
        }
        var answers = await service.ExportExamAsync(document, ExamPaper.A4Portrait, ExamEdition.Answers,
            Path.Combine(output, "exam-layout", "answers"), includeAnswerSpace: true, distributeAnswerSpace: true);
        Require(!(await File.ReadAllTextAsync(answers.SourcePath)).Contains("#exam-mark(\"blank\"", StringComparison.Ordinal), "answer edition has no writing areas");
        using var answerPdf = rasterizer.Open(answers.PdfPath);
        Require(string.Join("\n", Enumerable.Range(0, answerPdf.PageCount).Select(answerPdf.ExtractText)).Contains("PRIVATE_SOLUTION", StringComparison.Ordinal), "answer edition retains standard solutions");
        checks.Add("exam layout: A4/A3 compact and writing editions, repeated writable headers, inline scores, sequential parts, mixed text/formula options, subjective-only dynamic space, answer isolation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
