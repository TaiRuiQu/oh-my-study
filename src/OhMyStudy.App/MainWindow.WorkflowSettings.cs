using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private string workflowBaseline = "";
    private string WorkflowState() => string.Join('\u001f', (WorkflowClassifier.SelectedItem as ComboBoxItem)?.Tag, (WorkflowSplitter.SelectedItem as ComboBoxItem)?.Tag, (WorkflowGrader.SelectedItem as ComboBoxItem)?.Tag,
        QuestionRegionPath.Text, ChineseQuestionRegionPath.Text, WorkflowParallel.Value, WorkflowRounds.Value);
    private bool HasWorkflowDraft => _settingsFormsReady && (WorkflowState() != workflowBaseline || MinerUToken.Password.Length > 0);

    private void PopulateWorkflowSettings()
    {
        var workflow = _settings.ExamWorkflow ?? new();
        QuestionRegionPath.Text = workflow.RegionModelPath ?? "";
        ChineseQuestionRegionPath.Text = workflow.ChineseRegionModelPath ?? "";
        WorkflowParallel.Value = workflow.ParallelQuestions;
        WorkflowRounds.Value = workflow.MaxQuestionRounds;
        MinerUToken.Password = "";
        MinerUToken.PlaceholderText = UiText.Get(workflow.MinerUTokenRef is null ? "MinerU Bearer token" : "Saved key; leave blank to keep");
        WorkflowCredentialStatus.Text = UiText.Get(workflow.MinerUTokenRef is null ? "Add a MinerU token to enable OCR." : "MinerU token saved. Leave the field blank to keep it.");
        PopulateWorkflowSplitter(workflow.SplitterModel);
        PopulateWorkflowClassifier(workflow.ClassifierModel);
        PopulateWorkflowGrader(workflow.GraderModel);
        workflowBaseline = WorkflowState();
    }

    private WorkflowModel? ResolveWorkflowModel(string? selected) => _providers.SelectMany(p => p.Models.Select(m => new WorkflowModel(p, m)))
        .FirstOrDefault(m => m.Provider.Id + ":" + m.Model.Id == selected);

    private string? ConfiguredSplitterSelection()
    {
        if (_settings.ExamWorkflow is not { } workflow) return null;
        if (workflow.SplitterModel is not null) return workflow.SplitterModel;
        return _providers.SelectMany(p => p.Models.Where(m => m.Id == workflow.Splitter.Model
            && p.BaseUrl.TrimEnd('/').Equals(workflow.Splitter.BaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            .Select(m => p.Id + ":" + m.Id)).FirstOrDefault();
    }

    private void PopulateWorkflowSplitter(string? selected)
    {
        selected ??= ConfiguredSplitterSelection();
        WorkflowSplitter.Items.Clear();
        foreach (var provider in _providers.Where(p => p.KeyRef is not null))
            foreach (var model in provider.Models)
                WorkflowSplitter.Items.Add(new ComboBoxItem { Content = provider.Name + " / " + model.Label, Tag = provider.Id + ":" + model.Id });
        WorkflowSplitter.SelectedItem = WorkflowSplitter.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected);
    }

    private void RefreshWorkflowModelPickers()
    {
        bool clean = !HasWorkflowDraft;
        string? splitter = (WorkflowSplitter.SelectedItem as ComboBoxItem)?.Tag as string ?? ConfiguredSplitterSelection();
        string? grader = (WorkflowGrader.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.ExamWorkflow?.GraderModel;
        string? classifier = (WorkflowClassifier.SelectedItem as ComboBoxItem)?.Tag as string ?? _settings.ExamWorkflow?.ClassifierModel;
        PopulateWorkflowSplitter(splitter);
        PopulateWorkflowClassifier(classifier);
        PopulateWorkflowGrader(grader);
        if (clean) workflowBaseline = WorkflowState();
    }

    private void PopulateWorkflowClassifier(string? selected)
    {
        WorkflowClassifier.Items.Clear();
        WorkflowClassifier.Items.Add(new ComboBoxItem { Content = UiText.Get("Use the splitting model"), Tag = "" });
        foreach (var provider in _providers.Where(p => p.KeyRef is not null))
            foreach (var model in provider.Models)
                WorkflowClassifier.Items.Add(new ComboBoxItem { Content = provider.Name + " / " + model.Label, Tag = provider.Id + ":" + model.Id });
        WorkflowClassifier.SelectedItem = WorkflowClassifier.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? WorkflowClassifier.Items[0];
    }

    private void PopulateWorkflowGrader(string? selected)
    {
        WorkflowGrader.Items.Clear();
        WorkflowGrader.Items.Add(new ComboBoxItem { Content = UiText.Get("Use the solving model"), Tag = "" });
        foreach (var provider in _providers.Where(p => p.KeyRef is not null))
            foreach (var model in provider.Models)
                WorkflowGrader.Items.Add(new ComboBoxItem { Content = provider.Name + " / " + model.Label, Tag = provider.Id + ":" + model.Id });
        WorkflowGrader.SelectedItem = WorkflowGrader.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == selected) ?? WorkflowGrader.Items[0];
    }

    private async void SaveWorkflowSettings_Click(object sender, RoutedEventArgs e) => await SaveWorkflowSettingsAsync();

    private async Task SaveWorkflowSettingsAsync()
    {
        if (_library is not { } library) { WorkflowFeedback.Text = UiText.Get("Select an available library first."); return; }
        SettingsPanel.IsEnabled = false;
        byte[]? token = null;
        bool entered = false;
        try
        {
            var splitter = ResolveWorkflowModel((WorkflowSplitter.SelectedItem as ComboBoxItem)?.Tag as string)
                ?? throw new InvalidOperationException(UiText.Get("Choose a splitting model from a saved provider."));
            var workflow = (_settings.ExamWorkflow ?? new()) with
            {
                SplitterModel = splitter.Provider.Id + ":" + splitter.Model.Id,
                ClassifierModel = WorkflowClassifier.SelectedItem is ComboBoxItem { Tag: string classifier } && classifier.Length > 0 ? classifier : null,
                Splitter = new(splitter.Provider.BaseUrl, splitter.Model.Id, splitter.Provider.KeyRef),
                GraderModel = WorkflowGrader.SelectedItem is ComboBoxItem { Tag: string selected } && selected.Length > 0 ? selected : null,
                RegionModelPath = string.IsNullOrWhiteSpace(QuestionRegionPath.Text) ? null : QuestionRegionPath.Text.Trim(),
                ChineseRegionModelPath = string.IsNullOrWhiteSpace(ChineseQuestionRegionPath.Text) ? null : ChineseQuestionRegionPath.Text.Trim(),
                ParallelQuestions = double.IsFinite(WorkflowParallel.Value) ? checked((int)WorkflowParallel.Value) : 3,
                MaxQuestionRounds = double.IsFinite(WorkflowRounds.Value) ? checked((int)WorkflowRounds.Value) : 4
            };
            ConfigurationValidation.ValidateWorkflow(workflow);
            foreach (var path in new[] { workflow.RegionModelPath, workflow.ChineseRegionModelPath }.OfType<string>())
                if (!File.Exists(path)) throw new FileNotFoundException("Question region ONNX model not found.", path);
            if (MinerUToken.Password.Length > 0) token = Encoding.UTF8.GetBytes(MinerUToken.Password);
            await _settingsGate.WaitAsync(_lifetime.Token); entered = true;
            if (!ReferenceEquals(library, _library)) throw new OperationCanceledException();
            _settings = await new WorkflowCredentialService(library, new WindowsCredentialVault()).SaveAsync(workflow,
                token is null ? null : token.AsMemory(), null, _lifetime.Token);
            PopulateWorkflowSettings();
            WorkflowFeedback.Text = UiText.Get("Saved.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("WorkflowSettings.Save", error); WorkflowFeedback.Text = UiText.Get("Choose a saved provider model and check the workflow limits and ONNX paths."); }
        finally
        {
            if (token is not null) CryptographicOperations.ZeroMemory(token);
            if (entered) _settingsGate.Release();
            SettingsPanel.IsEnabled = true;
        }
    }

    private GradingInput CreateWorkflowInput(Document document, ProcessingChoice processing)
    {
        var workflow = _settings.ExamWorkflow ?? throw new InvalidOperationException(UiText.Get("Configure the exam workflow in Settings first."));
        var splitter = processing.Splitter;
        workflow = workflow with { SplitterModel = splitter.Provider.Id + ":" + splitter.Model.Id,
            Splitter = new(splitter.Provider.BaseUrl, splitter.Model.Id, splitter.Provider.KeyRef),
            ClassifierModel = processing.Classifier is { } classifier ? classifier.Provider.Id + ":" + classifier.Model.Id : null,
            GraderModel = processing.Grader is { } grader ? grader.Provider.Id + ":" + grader.Model.Id : null };
        return new() { Document = document, Provider = processing.Provider, Model = processing.Model, Thinking = processing.Thinking,
            AllowUnknown = processing.AllowUnknown, Workflow = workflow, Classifier = processing.Classifier ?? splitter, Splitter = splitter, Grader = processing.Grader };
    }
}
