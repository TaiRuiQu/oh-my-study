using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using OhMyStudy.Application.Search;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;
using Windows.System;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private sealed record SemanticBankResult(string Request, IReadOnlyList<VectorMatch> Matches);
    private readonly Dictionary<string, SemanticBankResult> _semanticBankResults = new(StringComparer.Ordinal);
    private CancellationTokenSource? _semanticSearchCancellation;
    private bool IsMeaningSearch => QuestionSearchMode.SelectedIndex == 1;
    private string SemanticRequest => string.Join('\u001f', _bankSection, QuestionSearch.Text.Trim(), SelectedBankSubject(), QuestionResultFilter.SelectedIndex);

    private void CancelSemanticSearch()
    {
        _semanticSearchCancellation?.Cancel();
        _semanticSearchCancellation = null;
        if (SemanticSearchActivity is null) return;
        SemanticSearchActivity.Visibility = Visibility.Collapsed;
        SemanticSearchButton.IsEnabled = true;
    }

    private void SetSemanticFeedback(string message)
    {
        SemanticSearchFeedback.Text = message;
        SemanticSearchFeedback.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateSearchMode()
    {
        SemanticSearchButton.Visibility = IsMeaningSearch ? Visibility.Visible : Visibility.Collapsed;
        QuestionSort.IsEnabled = !IsMeaningSearch;
        QuestionSearch.PlaceholderText = IsMeaningSearch ? UiText.Get("Describe the question or concept") : UiText.GetByKey("S0651");
        bool compact = RootGrid.ActualWidth < 600;
        Grid.SetRow(QuestionSearchMode, compact ? 1 : 0);
        Grid.SetColumn(QuestionSearchMode, compact ? 0 : 1);
        Grid.SetRow(SemanticSearchButton, compact ? 1 : 0);
        Grid.SetColumn(SemanticSearchButton, compact ? 1 : 2);
        Grid.SetColumnSpan(QuestionSearch, compact ? 3 : 1);
    }

    private async void QuestionSearchMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingBankFilters) return;
        CancelSemanticSearch();
        ++_bankSearchRevision;
        UpdateSearchMode();
        SetSemanticFeedback("");
        await ReloadBankAsync();
    }

    private async void QuestionSearch_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!IsMeaningSearch || e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await RunSemanticSearchAsync();
    }

    private async void SemanticSearch_Click(object sender, RoutedEventArgs e) => await RunSemanticSearchAsync();
    private void CancelSemanticSearch_Click(object sender, RoutedEventArgs e)
    {
        CancelSemanticSearch();
        SetSemanticFeedback(UiText.Get("Search canceled. Completed embeddings are kept locally."));
    }

    private async Task<QuestionPage> SemanticBankPageAsync(LibraryRepository library, QuestionQuery query)
    {
        var scope = await library.QueryQuestionsAsync(query with { Search = null, Limit = 1 }, _lifetime.Token);
        if (!_semanticBankResults.TryGetValue(_bankSection, out var result) || result.Request != SemanticRequest) return new([], 0, scope.Total);
        var summaries = await library.ReadRankedQuestionsAsync(result.Matches.Select(match => match.QuestionId).ToList(), query, _lifetime.Token);
        SetSemanticFeedback(UiText.Get("Showing up to 50 questions ranked by meaning."));
        return new(summaries, summaries.Count, scope.Total);
    }

    private async Task RunSemanticSearchAsync()
    {
        using var trace = OperationDiagnostics.Begin(Guid.NewGuid().ToString("N"));
        if (!IsMeaningSearch || _semanticSearchCancellation is not null) return;
        if (_library is not { } library || _settings.EmbeddingModel is not { } embedding)
        {
            OperationDiagnostics.Record("Search.Blocked", new() { ["reason"] = "embedding_configuration_missing" });
            SetSemanticFeedback(UiText.Get("Configure an embedding Base URL and model name in Settings first."));
            return;
        }
        string query = QuestionSearch.Text.Trim();
        if (query.Length == 0) { OperationDiagnostics.Record("Search.Blocked", new() { ["reason"] = "empty_query" }); SetSemanticFeedback(UiText.Get("Describe the question or concept first.")); return; }
        string request = SemanticRequest;
        string section = _bankSection;
        var bankQuery = BankQuery(0, BankPageSize);
        var scope = new SearchScope(bankQuery.Subject, bankQuery.Filter == QuestionFilter.Mistakes, bankQuery.Filter == QuestionFilter.NeedsReview, bankQuery.Filter == QuestionFilter.Correct);
        var reranker = _settings.RerankerModel;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _semanticSearchCancellation = cancellation;
        SemanticSearchButton.IsEnabled = false;
        SemanticSearchActivity.Visibility = Visibility.Visible;
        SemanticSearchStatus.Text = UiText.Get("Preparing local search index…");
        SetSemanticFeedback("");
        try
        {
            using var client = new SearchModelClient(new WindowsCredentialVault());
            var service = new SemanticSearchService(library, client);
            var plan = await service.PrepareAsync(embedding, scope, cancellation.Token);
            if (plan.Documents.Count == 0) { SetSemanticFeedback(UiText.Get("No questions match the current filters.")); return; }
            var pending = plan.Pending;
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = UiText.Get("Search by meaning"),
                Content = UiText.Format("Send this query and {0} new or changed question texts ({1} characters) to {2} using {3}?", pending.Count, pending.Sum(document => (long)document.Text.Length), embedding.BaseUrl, embedding.Model)
                    + "\n\n" + (reranker is null ? UiText.Get("Results will be ranked locally by vector similarity.")
                        : UiText.Format("Up to 50 candidate question texts will also be sent to {0} using {1} for reranking.", reranker.BaseUrl, reranker.Model))
                    + "\n\n" + UiText.Get("Original files, notes and student answers are excluded. Model calls may incur charges; the cost cannot be estimated here. Vectors are cached in this library."),
                PrimaryButtonText = UiText.Get("Search"),
                CloseButtonText = UiText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Close
            };
            var confirmation = await dialog.ShowAsync();
            if (confirmation != ContentDialogResult.Primary) return;
            cancellation.Token.ThrowIfCancellationRequested();
            var progress = new Progress<SearchProgress>(value =>
            {
                if (!ReferenceEquals(_semanticSearchCancellation, cancellation)) return;
                SemanticSearchStatus.Text = value.Reranking ? UiText.Get("Reranking matching questions…")
                    : UiText.Format("Preparing embeddings: {0} / {1}", value.Indexed, value.Total);
            });
            var matches = await service.SearchAsync(plan, embedding, reranker, query, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(library, _library) || request != SemanticRequest || QuestionsPanel.Visibility != Visibility.Visible) return;
            _semanticBankResults[section] = new(request, matches);
            OperationDiagnostics.Record("Search.Present", new() { ["results"] = matches.Count, ["section"] = section });
            await ReloadBankAsync();
        }
        catch (OperationCanceledException) { OperationDiagnostics.Record("Search.Cancelled", new() { ["query"] = query }); }
        catch (SearchModelException error) { AppLog.Error("Search.Failed." + OperationDiagnostics.CorrelationId, error); if (ReferenceEquals(_semanticSearchCancellation, cancellation)) SetSemanticFeedback(UiText.Get(error.Message)); }
        catch (InvalidDataException error) { AppLog.Error("Search.Failed." + OperationDiagnostics.CorrelationId, error); if (ReferenceEquals(_semanticSearchCancellation, cancellation)) SetSemanticFeedback(UiText.Get("The search vectors or model response are inconsistent. Clear the local search index in Settings and try again.")); }
        catch (Exception error) { AppLog.Error("Search.Failed." + OperationDiagnostics.CorrelationId, error); if (ReferenceEquals(_semanticSearchCancellation, cancellation)) SetSemanticFeedback(UiText.Get("Meaning search failed. Check the search models in Settings and try again.")); }
        finally
        {
            if (ReferenceEquals(_semanticSearchCancellation, cancellation))
            {
                _semanticSearchCancellation = null;
                SemanticSearchActivity.Visibility = Visibility.Collapsed;
                SemanticSearchButton.IsEnabled = true;
            }
        }
    }
}
