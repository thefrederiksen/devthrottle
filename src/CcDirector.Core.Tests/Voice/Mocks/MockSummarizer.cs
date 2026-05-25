using CcDirector.Core.Voice.Interfaces;

namespace CcDirector.Core.Tests.Voice.Mocks;

/// <summary>
/// Mock response summarizer for testing.
/// Returns a configurable hardcoded summary.
/// </summary>
public class MockSummarizer : IResponseSummarizer
{
    private readonly string _summary;
    private readonly bool _isAvailable;
    private readonly string? _unavailableReason;
    private readonly int _delayMs;

    public MockSummarizer(
        string summary = "Claude helped with the request.",
        bool isAvailable = true,
        string? unavailableReason = null,
        int delayMs = 0)
    {
        _summary = summary;
        _isAvailable = isAvailable;
        _unavailableReason = unavailableReason;
        _delayMs = delayMs;
    }

    public bool IsAvailable => _isAvailable;
    public string? UnavailableReason => _unavailableReason;

    public int SummarizeCallCount { get; private set; }
    public string? LastResponse { get; private set; }

    public int SummarizeProgressCallCount { get; private set; }
    public string? LastProgressActivity { get; private set; }

    public async Task<string> SummarizeAsync(string response, CancellationToken cancellationToken = default)
    {
        SummarizeCallCount++;
        LastResponse = response;

        if (_delayMs > 0)
            await Task.Delay(_delayMs, cancellationToken);

        return _summary;
    }

    public async Task<string> SummarizeProgressAsync(string recentActivity, CancellationToken cancellationToken = default)
    {
        SummarizeProgressCallCount++;
        LastProgressActivity = recentActivity;

        if (_delayMs > 0)
            await Task.Delay(_delayMs, cancellationToken);

        return _summary;
    }
}
