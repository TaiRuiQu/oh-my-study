using System.Text;

namespace OhMyStudy.Domain;

public static class ConfigurationValidation
{
    public static void ValidateSettings(AppSettings settings) => DomainValidationException.Require(
        settings.ExecutionMode is "request_permission" or "sandbox" or "bypass_permission"
        && (settings.DefaultModel is null || Encoding.UTF8.GetByteCount(settings.DefaultModel) <= 1024), "invalid application settings");

    public static void ValidateProvider(Provider provider)
    {
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
            DomainValidationException.Require(!string.IsNullOrWhiteSpace(model.Id) && Encoding.UTF8.GetByteCount(model.Id) <= 512 && ids.Add(model.Id)
                && !string.IsNullOrWhiteSpace(model.Currency) && Encoding.UTF8.GetByteCount(model.Currency) <= 16
                && (model.PriceInput is not { } input || double.IsFinite(input) && input >= 0)
                && (model.PriceOutput is not { } output || double.IsFinite(output) && output >= 0), "invalid model ID, currency or price");
    }
}
