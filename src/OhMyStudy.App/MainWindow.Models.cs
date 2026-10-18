using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async void ModelSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null || ProviderPicker.SelectedItem is not Provider provider || provider.Models.Count == 0)
        { ProviderFeedback.Text = "请先添加并保存模型。"; return; }
        var selector = new ComboBox { Header = "模型", MinWidth = 320 };
        foreach (var model in provider.Models) selector.Items.Add(model.Id);
        var vision = new CheckBox { Content = "可读取图像", IsThreeState = true };
        var tools = new CheckBox { Content = "可调用工具", IsThreeState = true };
        var priceIn = new TextBox { Header = "输入单价（每百万）" };
        var priceOut = new TextBox { Header = "输出单价（每百万）" };
        var currency = new TextBox { Header = "货币" };
        var effort = new TextBox { Header = "可选思考强度", PlaceholderText = "用逗号分隔，留空保持原有设置" };
        var budget = new TextBox { Header = "思考预算", PlaceholderText = "填写模型支持的 token 数，留空保持原有设置" };
        var feedback = new TextBlock { Text = "未确认的能力保持方框中的横线，缺少单价时不会估算费用。", TextWrapping = TextWrapping.Wrap };
        void Populate()
        {
            var model = provider.Models[Math.Max(0, selector.SelectedIndex)]; vision.IsChecked = model.Capabilities.Vision; tools.IsChecked = model.Capabilities.Tools;
            priceIn.Text = model.PriceInput?.ToString(CultureInfo.InvariantCulture) ?? ""; priceOut.Text = model.PriceOutput?.ToString(CultureInfo.InvariantCulture) ?? ""; currency.Text = model.Currency;
            effort.Text = string.Join(", ", model.Capabilities.Thinking.Select(t => t switch { ResponsesThinking v => v.Effort, AdaptiveThinking v => v.Effort, GeminiLevelThinking v => v.Level, _ => "" }).Where(t => t.Length > 0));
            budget.Text = model.Capabilities.Thinking.FirstOrDefault(t => t is BudgetThinking or GeminiBudgetThinking) switch { BudgetThinking v => v.Tokens.ToString(CultureInfo.InvariantCulture), GeminiBudgetThinking v => v.Tokens.ToString(CultureInfo.InvariantCulture), _ => "" };
        }
        selector.SelectionChanged += (_, _) => Populate(); selector.SelectedIndex = 0;
        var fields = new StackPanel { Spacing = 12 };
        foreach (var control in new FrameworkElement[] { selector, vision, tools, effort, budget, priceIn, priceOut, currency, feedback }) fields.Children.Add(control);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "模型设置", Content = new ScrollViewer { Content = fields, MaxHeight = 600 }, PrimaryButtonText = "保存", CloseButtonText = "取消" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            static double? Price(string value) => string.IsNullOrWhiteSpace(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);
            var old = provider.Models[selector.SelectedIndex];
            var thinking = new List<Thinking>();
            foreach (var value in effort.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                thinking.Add(provider.Protocol switch { Protocol.Responses => new ResponsesThinking { Effort = value }, Protocol.Messages => new AdaptiveThinking { Effort = value }, _ => new GeminiLevelThinking { Level = value } });
            if (!string.IsNullOrWhiteSpace(budget.Text))
                thinking.Add(provider.Protocol == Protocol.Messages ? new BudgetThinking { Tokens = ulong.Parse(budget.Text, CultureInfo.InvariantCulture) }
                    : provider.Protocol == Protocol.Gemini ? new GeminiBudgetThinking { Tokens = long.Parse(budget.Text, CultureInfo.InvariantCulture) } : throw new ArgumentException());
            var updated = old with
            {
                PriceInput = Price(priceIn.Text),
                PriceOutput = Price(priceOut.Text),
                Currency = currency.Text.Trim(),
                Capabilities = old.Capabilities with
                { Vision = vision.IsChecked, Tools = tools.IsChecked, Thinking = thinking, MessagesBudget = thinking.Any(t => t is BudgetThinking), Source = "manual" }
            };
            var models = provider.Models.ToList(); models[selector.SelectedIndex] = updated;
            await _library.UpsertProviderAsync(provider with { Models = models }, _lifetime.Token); await RefreshProvidersAsync(); ProviderFeedback.Text = "已保存。";
        }
        catch { ProviderFeedback.Text = "未能保存，请检查单价、思考选项和预算。"; }
    }
}
