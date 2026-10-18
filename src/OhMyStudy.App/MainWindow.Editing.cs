using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private bool _questionEditorOpen;

    private async Task EditSelectedQuestionAsync()
    {
        if (_questionEditorOpen || _library is not { } library || ViewModel.SelectedQuestion.Saved is not { } saved) return;
        _questionEditorOpen = true;
        try
        {
            var selected = ViewModel.SelectedQuestion;
            var document = await library.GetDocumentAsync(saved.DocumentId, _lifetime.Token);
            var editor = new QuestionFields(selected.EditedQuestion, document);
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            var fields = new StackPanel { Spacing = 12, MinWidth = 380 };
            fields.Children.Add(error); fields.Children.Add(editor.Panel);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "编辑题目",
                Content = new ScrollViewer { Content = fields, MaxHeight = 620 },
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                try
                {
                    var update = editor.Read();
                    var result = await library.EditQuestionAsync(saved.Id, update, _lifetime.Token);
                    selected.AcceptSaved(result);
                    try { await ReloadQuestionsAsync(); ViewModel.StatusMessage = "已保存。"; }
                    catch { ViewModel.StatusMessage = "已保存，请重新打开资料查看最新内容。"; }
                }
                catch (Exception exception)
                {
                    args.Cancel = true;
                    error.Text = exception is DomainValidationException or FormatException or OverflowException
                        ? "未保存。请检查题号、分数、小问合计和来源范围。" : "未保存，请等待任务结束后重试。";
                    error.Visibility = Visibility.Visible;
                }
                finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
            };
            await dialog.ShowAsync();
        }
        catch { ViewModel.StatusMessage = "题目无法打开，请重新选择。"; }
        finally { _questionEditorOpen = false; }
    }

    // Controls hold a local draft. Reading never mutates the saved question or teacher evidence.
    private sealed class QuestionFields
    {
        private readonly Question _original;
        private readonly TextBox _number, _subject, _type, _answer, _explanation, _knowledge, _points, _uncertainties;
        private readonly TextBox? _student, _earned, _reason, _solution;
        private readonly ComboBox? _verdict;
        private readonly EditorList<Content> _content;
        private readonly EditorList<string> _options;
        private readonly EditorList<Region> _sources;
        private readonly EditorList<Question> _parts;
        private readonly EditorList<Region>? _studentRegions;
        public StackPanel Panel { get; } = new() { Spacing = 12 };

        public QuestionFields(Question question, Document document, int depth = 0)
        {
            _original = question;
            _number = AddText(Panel, "题号", question.Number);
            _subject = AddText(Panel, "学科", question.Subject);
            _type = AddText(Panel, "题型", question.QuestionType);
            _content = new(question.Content, value => ContentField(value, document));
            AddSection(Panel, "题干", _content.Panel);
            AddButton(_content.Actions, "添加文字", () => _content.Add(new TextContent { Text = "" }));
            AddButton(_content.Actions, "添加公式", () => _content.Add(new FormulaContent { Latex = "" }));
            AddButton(_content.Actions, "添加表格", () => _content.Add(new TableContent { Rows = [[""]] }));
            var material = document.Pages.FirstOrDefault(p => p.Role == "material");
            if (material is not null)
                AddButton(_content.Actions, "添加配图", () => _content.Add(new ImageContent { Caption = "", ContainsHandwriting = false, Region = FullPage(material.Id) }));
            _options = new(question.Options, value =>
            {
                var field = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                return new(field, () => field.Text);
            });
            AddSection(Panel, "选项", _options.Panel);
            AddButton(_options.Actions, "添加选项", () => _options.Add(""));
            _answer = AddText(Panel, "标准答案", question.StandardAnswer, true);
            _explanation = AddText(Panel, "解析", question.Explanation, true);
            _knowledge = AddText(Panel, "知识点（每行一项）", string.Join("\n", question.KnowledgePoints), true);
            _points = AddText(Panel, "满分（未知留空）", ScoreText(question.Points));
            _sources = RegionList(question.Sources, document, "material");
            AddSection(Panel, "题目来源", _sources.Panel);
            _uncertainties = AddText(Panel, "待确认内容（每行一项）", string.Join("\n", question.Uncertainties), true);
            if (question.Attempt is { } attempt)
            {
                _student = AddText(Panel, "学生作答", attempt.StudentAnswer, true);
                _studentRegions = RegionList(attempt.StudentRegions, document, "material");
                AddSection(Panel, "作答来源", _studentRegions.Panel);
                _verdict = new ComboBox { Header = "判定", HorizontalAlignment = HorizontalAlignment.Stretch };
                foreach (var label in new[] { "正确", "部分正确", "错误", "未作答", "待确认" }) _verdict.Items.Add(label);
                _verdict.SelectedIndex = (int)attempt.Grade.Verdict; Panel.Children.Add(_verdict);
                _earned = AddText(Panel, "得分（无法判定留空）", ScoreText(attempt.Grade.Earned));
                _reason = AddText(Panel, "评分说明", attempt.Grade.Reason, true);
                _solution = AddText(Panel, "解题过程", attempt.AiSolution, true);
                if (attempt.Teacher is { } teacher)
                    Panel.Children.Add(new TextBlock { Text = "教师批注：" + teacher.Raw, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
                if (!string.IsNullOrWhiteSpace(attempt.Conflict))
                    Panel.Children.Add(new TextBlock { Text = "判断差异：" + attempt.Conflict, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            }
            _parts = new(question.Parts, part =>
            {
                QuestionFields? nested = null;
                var expander = new Expander { Header = "小问 " + part.Number, HorizontalAlignment = HorizontalAlignment.Stretch };
                expander.Expanding += (_, _) =>
                {
                    nested ??= new QuestionFields(part, document, depth + 1);
                    expander.Content = nested.Panel;
                };
                return new(expander, () => nested?.Read() ?? part);
            });
            AddSection(Panel, "小问", _parts.Panel);
            if (depth < 8)
                AddButton(_parts.Actions, "添加小问", () => _parts.Add(NewPart(question, document, (_parts.Count + 1).ToString(CultureInfo.InvariantCulture))));
        }

        public Question Read()
        {
            var points = ParseScore(_points.Text);
            return _original with
            {
                Number = _number.Text.Trim(),
                Subject = _subject.Text.Trim(),
                QuestionType = _type.Text.Trim(),
                Content = _content.Read(),
                Options = _options.Read(),
                StandardAnswer = _answer.Text,
                Explanation = _explanation.Text,
                KnowledgePoints = Lines(_knowledge.Text),
                Points = points,
                Sources = _sources.Read(),
                Uncertainties = Lines(_uncertainties.Text),
                Parts = _parts.Read(),
                Attempt = _original.Attempt is { } attempt ? attempt with
                {
                    StudentAnswer = _student!.Text,
                    StudentRegions = _studentRegions!.Read(),
                    AiSolution = _solution!.Text,
                    Grade = attempt.Grade with
                    {
                        Verdict = (Verdict)_verdict!.SelectedIndex,
                        Earned = ParseScore(_earned!.Text),
                        Possible = points == _original.Points && ParseScore(_earned!.Text) == attempt.Grade.Earned ? attempt.Grade.Possible : points,
                        Reason = _reason!.Text
                    }
                } : null
            };
        }
    }

    private sealed record EditorField<T>(FrameworkElement Element, Func<T> Read);
    private sealed class EditorList<T>
    {
        private readonly Func<T, EditorField<T>> _create;
        private readonly List<(Border Container, EditorField<T> Field)> _items = [];
        private readonly StackPanel _rows = new() { Spacing = 12 };
        public StackPanel Actions { get; } = new() { Spacing = 6, Orientation = Orientation.Horizontal };
        public StackPanel Panel { get; } = new() { Spacing = 8 };
        public int Count => _items.Count;
        public EditorList(IEnumerable<T> items, Func<T, EditorField<T>> create)
        { _create = create; Panel.Children.Add(_rows); Panel.Children.Add(Actions); foreach (var item in items) Add(item); }
        public void Add(T value)
        {
            var field = _create(value);
            var content = new StackPanel { Spacing = 8 };
            var buttons = new StackPanel { Spacing = 6, Orientation = Orientation.Horizontal };
            var border = new Border { Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(10), BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardStrokeColorDefaultBrush"] };
            content.Children.Add(field.Element); content.Children.Add(buttons);
            AddButton(buttons, "上移", () => Move(border, -1));
            AddButton(buttons, "下移", () => Move(border, 1));
            AddButton(buttons, "删除", () => { _items.RemoveAll(item => ReferenceEquals(item.Container, border)); _rows.Children.Remove(border); });
            _items.Add((border, field)); _rows.Children.Add(border);
        }
        private void Move(Border border, int delta)
        {
            int index = _items.FindIndex(item => ReferenceEquals(item.Container, border)), target = index + delta;
            if (target < 0 || target >= _items.Count) return;
            var item = _items[index]; _items.RemoveAt(index); _items.Insert(target, item);
            _rows.Children.RemoveAt(index); _rows.Children.Insert(target, border);
        }
        public List<T> Read() => _items.Select(item => item.Field.Read()).ToList();
    }

    private static EditorField<Content> ContentField(Content content, Document document)
    {
        var panel = new StackPanel { Spacing = 8 };
        switch (content)
        {
            case TextContent text:
                var textBox = AddText(panel, "文字", text.Text, true);
                return new(panel, () => text with { Text = textBox.Text });
            case FormulaContent formula:
                var formulaBox = AddText(panel, "公式", formula.Latex, true);
                return new(panel, () => formula with { Latex = formulaBox.Text });
            case TableContent table:
                var rows = new EditorList<List<string>>(table.Rows, row =>
                {
                    var cells = new EditorList<string>(row, value =>
                    {
                        var cell = new TextBox { Text = value, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap };
                        return new(cell, () => cell.Text);
                    });
                    AddButton(cells.Actions, "添加单元格", () => cells.Add(""));
                    return new(cells.Panel, cells.Read);
                });
                AddButton(rows.Actions, "添加行", () => rows.Add([""]));
                AddSection(panel, "表格", rows.Panel);
                return new(panel, () => table with { Rows = rows.Read() });
            case ImageContent image:
                var caption = AddText(panel, "配图说明", image.Caption, true);
                var handwriting = new CheckBox { Content = "含手写内容", IsChecked = image.ContainsHandwriting }; panel.Children.Add(handwriting);
                var region = RegionField(image.Region, document, "material"); panel.Children.Add(region.Element);
                return new(panel, () => image with { Caption = caption.Text, ContainsHandwriting = handwriting.IsChecked == true, Region = region.Read() });
            default: throw new InvalidOperationException("无法编辑此内容。");
        }
    }

    private static EditorList<Region> RegionList(IEnumerable<Region> regions, Document document, string role)
    {
        var list = new EditorList<Region>(regions, region => RegionField(region, document, role));
        if (document.Pages.FirstOrDefault(page => page.Role == role) is { } page)
            AddButton(list.Actions, "添加来源", () => list.Add(FullPage(page.Id)));
        return list;
    }
    private static EditorField<Region> RegionField(Region region, Document document, string role)
    {
        var panel = new StackPanel { Spacing = 8 };
        var pages = document.Pages.Where(page => page.Role == role).ToList();
        var pageChoice = new ComboBox { Header = "原件页", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var page in pages) pageChoice.Items.Add("第 " + (page.PageIndex + 1) + " 页");
        pageChoice.SelectedIndex = pages.FindIndex(page => page.Id == region.PageId); panel.Children.Add(pageChoice);
        var x = AddText(panel, "左距（%）", ScoreText(region.X * 100));
        var y = AddText(panel, "上距（%）", ScoreText(region.Y * 100));
        var width = AddText(panel, "宽度（%）", ScoreText(region.Width * 100));
        var height = AddText(panel, "高度（%）", ScoreText(region.Height * 100));
        return new(panel, () => region with
        {
            PageId = pageChoice.SelectedIndex < 0 ? region.PageId : pages[pageChoice.SelectedIndex].Id,
            X = ParseRequired(x.Text) / 100,
            Y = ParseRequired(y.Text) / 100,
            Width = ParseRequired(width.Text) / 100,
            Height = ParseRequired(height.Text) / 100
        });
    }
    private static TextBox AddText(StackPanel panel, string label, string value, bool multiline = false)
    {
        var field = new TextBox { Header = label, Text = value, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap, MaxHeight = multiline ? 180 : 80 };
        panel.Children.Add(field); return field;
    }
    private static void AddSection(StackPanel panel, string label, FrameworkElement content)
    { panel.Children.Add(new Expander { Header = label, Content = content, HorizontalAlignment = HorizontalAlignment.Stretch }); }
    private static void AddButton(StackPanel panel, string label, Action action)
    { var button = new Button { Content = label }; button.Click += (_, _) => action(); panel.Children.Add(button); }
    private static string ScoreText(double? value) => value?.ToString("G", CultureInfo.InvariantCulture) ?? "";
    private static double? ParseScore(string value) => string.IsNullOrWhiteSpace(value) ? null : ParseRequired(value);
    private static double ParseRequired(string value) => double.Parse(value, CultureInfo.InvariantCulture);
    private static List<string> Lines(string value) => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    private static Region FullPage(string page) => new() { PageId = page, X = 0, Y = 0, Width = 1, Height = 1 };
    private static Question NewPart(Question parent, Document document, string number) => parent with
    {
        DraftKey = "manual-" + Guid.NewGuid().ToString("N"),
        Number = number,
        Content = [new TextContent { Text = "" }],
        Options = [],
        Points = null,
        StandardAnswer = "",
        Explanation = "",
        KnowledgePoints = [],
        Uncertainties = [],
        Parts = [],
        Sources = parent.Sources.Take(1).ToList(),
        Attempt = document.Mode == ImportMode.Blank ? null : new Attempt
        {
            StudentAnswer = "",
            StudentRegions = [],
            AiSolution = "",
            Grade = new Grade { Verdict = Verdict.Uncertain, Source = GradeSource.Manual, Reason = "待确认" }
        }
    };
}
