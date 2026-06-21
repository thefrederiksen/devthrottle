namespace CcDirector.Core.Utilities;

/// <summary>
/// Result object for operations that can fail in expected ways (validation, external checks) -
/// the pattern in docs/CodingStyle.md section 2. Lets a service return success-or-reason without
/// throwing for an expected failure, so the calling boundary renders the reason to the user.
/// </summary>
public sealed class OperationResult<T>
{
    public bool Success { get; init; }
    public T? Value { get; init; }
    public string? ErrorMessage { get; init; }

    public static OperationResult<T> Ok(T value) =>
        new() { Success = true, Value = value };

    public static OperationResult<T> Fail(string message) =>
        new() { Success = false, ErrorMessage = message };
}
