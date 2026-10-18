namespace OhMyStudy.Application;

// Values are safe for presentation. Provider payloads and secrets never belong in diagnostics.
public sealed record OperationError(string Code, string Message, bool Retryable = false);

public sealed record OperationResult<T>(T? Value, OperationError? Error)
{
    public bool IsSuccess => Error is null;
    public static OperationResult<T> Success(T value) => new(value, null);
    public static OperationResult<T> Failure(string code, string message, bool retryable = false)
        => new(default, new(code, message, retryable));
}
