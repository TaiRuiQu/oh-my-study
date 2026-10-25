using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyStudy.Domain;

[JsonConverter(typeof(StrictEnumConverter<Protocol>))]
public enum Protocol { Responses, Messages }

public sealed record Capabilities
{
    public bool? Vision { get; init; }
    public bool? Tools { get; init; }
    public required List<Thinking> Thinking { get; init; }
    public bool? MessagesBudget { get; init; }
    public string? Source { get; init; }
    public static Capabilities Unknown => new() { Thinking = [] };
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ResponsesThinking), "responses")]
[JsonDerivedType(typeof(AdaptiveThinking), "adaptive")]
[JsonDerivedType(typeof(BudgetThinking), "budget")]
public abstract record Thinking;
public sealed record ResponsesThinking : Thinking { public required string Effort { get; init; } }
public sealed record AdaptiveThinking : Thinking { public required string Effort { get; init; } }
public sealed record BudgetThinking : Thinking { public required ulong Tokens { get; init; } }

public sealed record Model
{
    public required string Id { get; init; }
    public required Capabilities Capabilities { get; init; }
    public required JsonElement Raw { get; init; }
    public static Model Manual(string id) => new() { Id = id, Capabilities = Capabilities.Unknown, Raw = JsonSerializer.SerializeToElement((string?)null, DomainJson.Context.String) };
}

public sealed record Image
{
    public required string Mime { get; init; }
    public required string Base64 { get; init; }
}

public sealed record Tool
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required JsonElement Schema { get; init; }
}

public sealed record ToolCall
{
    public string? Id { get; init; }
    public required string Name { get; init; }
    public required JsonElement Arguments { get; init; }
}

public sealed record ToolResult
{
    public required ToolCall Call { get; init; }
    public required string Text { get; init; }
    public required List<Image> Images { get; init; }
    public required bool IsError { get; init; }
}

public sealed record RequestOptions
{
    public required Model Model { get; init; }
    public required ulong MaxOutputTokens { get; init; }
    public required bool Stream { get; init; }
    public Thinking? Thinking { get; init; }
    public required bool AllowUnknownCapabilities { get; init; }
    public required Dictionary<string, JsonElement> Extra { get; init; }
    public string? SessionId { get; init; }
    public string? PromptCacheKey { get; init; }

    public void Validate(Protocol protocol, bool vision, bool tools)
    {
        DomainValidationException.Require(Enum.IsDefined(protocol), "unknown protocol");
        DomainValidationException.Require(!string.IsNullOrWhiteSpace(Model.Id) && MaxOutputTokens > 0, "model and positive token limit are required");
        DomainValidationException.Require(SessionId is null || SessionId.Length is > 0 and <= 256, "invalid session identifier");
        DomainValidationException.Require(PromptCacheKey is null || PromptCacheKey.Length is > 0 and <= 256, "invalid prompt cache key");
        ValidateCapability(vision, Model.Capabilities.Vision, "vision");
        ValidateCapability(tools, Model.Capabilities.Tools, "tools");
        if (Thinking is not { } thinking) return;
        bool correctProtocol = (protocol, thinking) is
            (Protocol.Responses, ResponsesThinking) or
            (Protocol.Messages, AdaptiveThinking or BudgetThinking);
        bool metadataBudget = thinking is BudgetThinking && Model.Capabilities.MessagesBudget == true;
        DomainValidationException.Require(correctProtocol && (metadataBudget || Model.Capabilities.Thinking.Contains(thinking) || AllowUnknownCapabilities), "thinking configuration is unsupported or unknown");
        DomainValidationException.Require(thinking is not BudgetThinking budget || (budget.Tokens >= 1024 && budget.Tokens < MaxOutputTokens), "Messages thinking budget must be >=1024 and below max_tokens");
    }

    private void ValidateCapability(bool needed, bool? capability, string name) =>
        DomainValidationException.Require(!needed || (capability != false && (capability.HasValue || AllowUnknownCapabilities)), $"{name} is unsupported or unknown; configure capabilities or explicitly test this model");
}

public sealed record Usage
{
    public ulong? Input { get; init; }
    public ulong? InputUncached { get; init; }
    public ulong? CacheRead { get; init; }
    public ulong? CacheWrite { get; init; }
    public ulong? Output { get; init; }
    public ulong? Reasoning { get; init; }
    public ulong? Total { get; init; }
    public required JsonElement Raw { get; init; }
}

public sealed record Turn
{
    public required Protocol Protocol { get; init; }
    public required string Text { get; init; }
    public required List<ToolCall> Calls { get; init; }
    public required JsonElement Context { get; init; }
    public required Usage Usage { get; init; }
    public string? StopReason { get; init; }
    public required JsonElement Raw { get; init; }
}

[JsonConverter(typeof(StrictEnumConverter<ErrorKind>))]
public enum ErrorKind { Refusal, Authentication, Quota, RateLimit, UnsupportedParameter, InvalidRequest, Server, Network, Cancelled, Protocol, Incomplete }

public sealed record ProviderError
{
    public required ErrorKind Kind { get; init; }
    public ushort? Status { get; init; }
    public required string Message { get; init; }
    public required bool Retryable { get; init; }
    public string? RetryAfter { get; init; }
    public static ProviderError New(ErrorKind kind, string message) => new() { Kind = kind, Message = message, Retryable = kind is ErrorKind.RateLimit or ErrorKind.Server or ErrorKind.Network };
    public static ProviderError Invalid(string message) => New(ErrorKind.InvalidRequest, message);
    public static ProviderError Protocol(string message) => New(ErrorKind.Protocol, message);
}

public sealed record ModelPage
{
    public required List<Model> Models { get; init; }
    public string? Next { get; init; }
    public required JsonElement Raw { get; init; }
}
