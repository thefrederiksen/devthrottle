namespace CcDirector.Avalonia.SmartRestart;

/// <summary>
/// What the Smart shutdown dialog is told about one running session. Built by the caller (see
/// <see cref="SmartShutdownSessionReader"/>), so the window never reaches into the session manager or
/// the engine.
/// </summary>
/// <param name="DisplayName">The name the owner knows the session by, as the session rail shows it.</param>
/// <param name="IsWorking">True when the session is mid-turn; false when it is waiting.</param>
/// <param name="HasQuestionBoxOpen">True when the session has a question box open for the owner.</param>
public sealed record SmartShutdownSession(string DisplayName, bool IsWorking, bool HasQuestionBoxOpen);
