using OhMyStudy.Documents;

internal static class ExamPreview
{
    internal static async Task WriteAsync(string output, TypstDocumentService service)
    {
        var document = new ExamDocument("数学综合练习", [
            new(1, [new("下列各数中，最小的数是（　　）。")], Options: ["A. -2", "B. -1", "C. 0", "D. 1"], Points: 3),
            new(2, [new("关于一次函数的图象与性质，下列说法正确的是（　　）。")], Options:
                ["A. 图象与横轴一定有一个交点", "B. 图象一定经过坐标原点", "C. 函数值始终随自变量增大而增大", "D. 自变量可以取任意实数"], Points: 3),
            new(3, [new("某小组用描点法研究函数图象。下列做法中，能使研究结论更可靠的是（　　）。")], Options:
                ["A. 只选取两个自变量的值，直接用直线连接所描出的点，并据此判断函数在整个定义域内的变化趋势。",
                 "B. 在给定范围内适当增加取值，核对计算和描点结果，结合函数表达式说明图象的形状与变化规律。",
                 "C. 将所有测量结果都保留到整数，再根据坐标纸上各点的位置判断图象与坐标轴的交点是否准确。",
                 "D. 如果某个数据与预想结果不一致，先删去该数据，再用其余各点绘制符合预想结论的函数图象。"], Points: 4),
            new(4, [new(@"若 $2x+3=11$，则 $x=$________；边长为 $3$ 的正方形面积是________。")],
                Answer: "4；9", Points: 4, QuestionType: "填空题"),
            new(5, [new(@"已知一次函数 $y=kx+b$ 的图象经过点 $A(1,3)$ 和点 $B(3,7)$。")], Parts:
                [new("1", [new("求这个一次函数的表达式。")], Answer: "由 k+b=3，3k+b=7，解得 k=2，b=1，所以 y=2x+1。", Points: 3),
                 new("2", [new("求该图象与两条坐标轴的交点坐标。")], Answer: "令 x=0，得 y=1；令 y=0，得 x=-1/2。交点为 (0,1)、(-1/2,0)。", Points: 3)], Points: 6),
            new(6, [new("下列各式的值与其他三项不同的是（　　）。")], Options:
                [@"A. $\frac{1}{2}$", @"B. $\frac{2}{4}$", @"C. $\sqrt{\frac{1}{4}}$", @"D. $\frac{3}{4}$"], Points: 3),
            new(7, [new(@"解方程 $\frac{x-1}{2}+\frac{x+2}{3}=4$，并检验所得结果。")],
                Answer: "两边同乘 6，得 3(x-1)+2(x+2)=24。\n整理，得 5x+1=24，所以 x=23/5。\n代入原方程检验，左右两边相等。", Points: 6),
            new(8, [new(@"某长方形的周长为 $36$ 米，长比宽多 $4$ 米。求这个长方形的长、宽和面积，并写出计算过程。")],
                Answer: "设宽为 x 米，则长为 (x+4) 米。\n由题意，2(x+x+4)=36，解得 x=7。\n所以长为 11 米，宽为 7 米，面积为 77 平方米。", Points: 8),
            new(9, [new(@"已知三角形的三个内角的度数之比为 $2:3:4$，求各内角的度数，并判断该三角形的类型。")],
                Answer: "设三个内角分别为 2x、3x、4x。\n由内角和为 180 度，得 9x=180，x=20。\n三个内角为 40、60、80 度，因此是锐角三角形。", Points: 6),
            new(10, [new(@"某商店销售一种商品，每件进价为 $40$ 元，按标价的八折出售后，每件仍可获利 $8$ 元。求这件商品的标价。")],
                Answer: "设标价为 x 元，则售价为 0.8x 元。\n由题意，0.8x-40=8，解得 x=60。\n所以这件商品的标价为 60 元。", Points: 6)
        ], new ExamPrintLabels("班级", "姓名", "学号", "（{0:g}分）"));
        foreach (var paper in Enum.GetValues<ExamPaper>())
            foreach (int mode in new[] { 0, 1, 2 })
            {
                string name = (paper == ExamPaper.A4Portrait ? "a4" : "a3") + (mode == 0 ? "-compact" : mode == 1 ? "-writing" : "-distributed");
                var result = await service.ExportExamAsync(document, paper, ExamEdition.Questions, Path.Combine(output, name),
                    includeAnswerSpace: mode > 0, distributeAnswerSpace: mode == 2);
                Console.WriteLine(result.PdfPath);
            }
    }
}
