using System.Text;

namespace OhMyStudy.Domain;

public static class ConfigurationValidation
{
    public static void ValidateSettings(AppSettings settings)
    {
        DomainValidationException.Require(settings.ExecutionMode is "request_permission" or "sandbox" or "bypass_permission"
            && (settings.DefaultModel is null || Encoding.UTF8.GetByteCount(settings.DefaultModel) <= 1024), "invalid application settings");
        if (settings.EmbeddingModel is { } embedding) ValidateSearchModel(embedding);
        if (settings.RerankerModel is { } reranker) ValidateSearchModel(reranker);
        if (settings.ExamWorkflow is { } workflow) ValidateWorkflow(workflow);
    }

    public static void ValidateWorkflow(ExamWorkflowSettings workflow)
    {
        ValidateSearchModel(workflow.Splitter);
        DomainValidationException.Require(workflow.ParallelQuestions is >= 1 and <= 8
            && workflow.MaxQuestionRounds is >= 1 and <= 8 && workflow.SplitterMaxTokens is >= 1024 and <= 32768,
            "Invalid exam workflow concurrency or output limits.");
        if (workflow.MinerUTokenRef is { } reference)
            DomainValidationException.Require(reference.StartsWith("OhMyStudy/provider/", StringComparison.Ordinal)
                && Guid.TryParse(reference["OhMyStudy/provider/".Length..], out _), "Invalid MinerU credential reference.");
        foreach (var path in new[] { workflow.RegionModelPath, workflow.ChineseRegionModelPath })
            DomainValidationException.Require(path is null || Path.IsPathFullyQualified(path) && path.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase),
                "Question region models require an absolute ONNX path.");
    }

    public static void ValidateSearchModel(SearchModel model)
    {
        DomainValidationException.Require(model.ApiType is "openai" or "dashscope", "unknown search API type");
        DomainValidationException.Require(Uri.TryCreate(model.BaseUrl, UriKind.Absolute, out var url)
            && url.Scheme is "http" or "https" && url.Host.Length > 0 && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0
            && (url.Scheme == "https" || url.IsLoopback), "search model URL must use HTTPS or loopback HTTP, without credentials, query or fragment");
        DomainValidationException.Require(!string.IsNullOrWhiteSpace(model.Model) && Encoding.UTF8.GetByteCount(model.Model) <= 512, "invalid search model name");
        if (model.KeyRef is { } reference)
            DomainValidationException.Require(reference.StartsWith("OhMyStudy/provider/", StringComparison.Ordinal)
                && Guid.TryParse(reference["OhMyStudy/provider/".Length..], out _), "invalid credential reference");
    }

    public static void ValidateProvider(Provider provider)
    {
        DomainValidationException.Require(Enum.IsDefined(provider.Protocol), "unknown protocol");
        DomainValidationException.Require(Guid.TryParse(provider.Id, out _), "invalid identifier");
        DomainValidationException.Require(Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var url), "invalid provider base URL");
        DomainValidationException.Require(url is not null && url.Scheme is "http" or "https" && url.Host.Length > 0 && url.UserInfo.Length == 0 && url.Query.Length == 0 && url.Fragment.Length == 0,
            "provider base URL must use HTTP(S), without credentials, query or fragment");
        DomainValidationException.Require(url!.Scheme != "http" || url.Host is "localhost" or "127.0.0.1" or "[::1]", "HTTP is only allowed for loopback providers");
        DomainValidationException.Require(!string.IsNullOrWhiteSpace(provider.Name) && Encoding.UTF8.GetByteCount(provider.Name) <= 200 && provider.Models.Count <= 500, "invalid provider name or model count");
        if (provider.KeyRef is { } keyRef)
            DomainValidationException.Require(keyRef.StartsWith("OhMyStudy/provider/", StringComparison.Ordinal) && Guid.TryParse(keyRef["OhMyStudy/provider/".Length..], out _), "invalid credential reference");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var model in provider.Models)
        {
            ValidateModelTokenLimit(model, provider.Protocol);
            DomainValidationException.Require(!string.IsNullOrWhiteSpace(model.Id) && Encoding.UTF8.GetByteCount(model.Id) <= 512 && ids.Add(model.Id)
                && (model.DisplayName is null || Encoding.UTF8.GetByteCount(model.DisplayName) <= 200)
                && model.ContextWindow is not 0
                && !string.IsNullOrWhiteSpace(model.Currency) && Encoding.UTF8.GetByteCount(model.Currency) <= 16
                && (model.PriceInput is not { } input || double.IsFinite(input) && input >= 0)
                && (model.PriceCacheRead is not { } read || double.IsFinite(read) && read >= 0)
                && (model.PriceCacheWrite is not { } write || double.IsFinite(write) && write >= 0)
                && (model.PriceOutput is not { } output || double.IsFinite(output) && output >= 0), "invalid model ID, currency or price");
        }
    }

    public static void ValidateModelTokenLimit(ModelConfig model, Protocol protocol)
    {
        if (model.MaxTokens is not { } limit) return;
        DomainValidationException.Require(limit > 0 && limit <= (protocol == Protocol.Responses ? (ulong)int.MaxValue : (ulong)long.MaxValue),
            "Invalid max_tokens. Enter a positive integer supported by this API.");
        DomainValidationException.Require(model.Capabilities.Thinking.All(thinking => thinking is not BudgetThinking budget || budget.Tokens < limit),
            "Thinking budget must be smaller than max_tokens.");
    }
}
