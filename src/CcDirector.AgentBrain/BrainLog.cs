namespace CcDirector.AgentBrain;

/// <summary>
/// Default diagnostic log sink for the AgentBrain library: a daily file under
/// %LOCALAPPDATA%\cc-director\logs\agent-brain\. The library is reused across many
/// host programs, so it cannot depend on CcDirector.Core's FileLog; hosts that want
/// their own sink set the Log action on their options (e.g. HostedAgentOptions.Log).
/// </summary>
public static class BrainLog
{
    private static readonly object Gate = new();

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "logs", "agent-brain");

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var file = Path.Combine(Dir, $"agent-brain-{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}
