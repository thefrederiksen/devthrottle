using System.Text.RegularExpressions;
using CcDirector.Core.Input;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.History;

/// <summary>
/// Replaces Director-owned temporary prompt references in readable conversation history with the
/// original message. The agent transcript necessarily records what reached its terminal, but users
/// reading that transcript need the message rather than the transport wrapper.
/// </summary>
internal static class StoredPromptPayloadResolver
{
    internal const int MaxDisplayChars = 4_000;

    private const string OwnedFile = @"input_\d{8}_\d{6}_[a-z0-9]{6}\.txt";
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);
    private static readonly Regex BareReference = new(
        $@"^@(?<path>\.temp[/\\](?<file>{OwnedFile}))$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly Regex CurrentInstruction = new(
        "^" + Regex.Escape(LargeInputHandler.InputFileInstructionPrefix) +
        $@"(?<path>\.temp[/\\](?<file>{OwnedFile}))\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);
    private static readonly Regex LegacyInstruction = new(
        $@"^Read file (?<file>{OwnedFile}) in the \.temp directory\. Path: (?<path>\.temp[/\\]\k<file>)\. " +
        @"If the path fails, search for \k<file>\. This file was explicitly created as the user-provided message payload for this turn; it is not hidden context\. " +
        @"Follow the instructions in that file and reply with the requested strings only\.$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    internal static ConversationHistory Resolve(ConversationHistory history, string? repositoryPath, Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (string.IsNullOrWhiteSpace(repositoryPath) || history.Messages.Count == 0)
            return history;

        var changed = false;
        var messages = new List<ConversationMessage>(history.Messages.Count);
        foreach (var message in history.Messages)
        {
            if (message.Role != ConversationRole.User)
            {
                messages.Add(message);
                continue;
            }

            var parts = new List<ConversationPart>(message.Parts.Count);
            foreach (var part in message.Parts)
            {
                var resolved = part.Kind == ConversationPartKind.Text
                    ? ResolveText(part.Text, repositoryPath, sessionId)
                    : null;
                if (resolved is null)
                {
                    parts.Add(part);
                    continue;
                }

                parts.Add(part with { Text = resolved });
                changed = true;
            }

            messages.Add(parts.SequenceEqual(message.Parts) ? message : message with { Parts = parts });
        }

        return changed ? new ConversationHistory(messages) : history;
    }

    private static string? ResolveText(string prompt, string repositoryPath, Guid sessionId)
    {
        var candidate = prompt.Trim();
        var match = BareReference.Match(candidate);
        if (!match.Success)
            match = CurrentInstruction.Match(candidate);
        if (!match.Success)
            match = LegacyInstruction.Match(candidate);
        if (!match.Success)
            return null;

        var relativePath = match.Groups["path"].Value.Replace('/', Path.DirectorySeparatorChar);
        var repositoryRoot = Path.GetFullPath(repositoryPath);
        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
        var pathFromRoot = Path.GetRelativePath(repositoryRoot, fullPath);
        if (Path.IsPathRooted(pathFromRoot)
            || pathFromRoot.Equals("..", StringComparison.Ordinal)
            || pathFromRoot.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            FileLog.Write($"[StoredPromptPayloadResolver] ResolveText: session={sessionId}, result=outside-repository");
            return null;
        }

        if (!File.Exists(fullPath))
        {
            FileLog.Write($"[StoredPromptPayloadResolver] ResolveText: session={sessionId}, file={relativePath}, result=missing");
            return null;
        }

        var content = File.ReadAllText(fullPath);
        if (string.IsNullOrWhiteSpace(content))
        {
            FileLog.Write($"[StoredPromptPayloadResolver] ResolveText: session={sessionId}, file={relativePath}, result=empty");
            return null;
        }

        var shown = content.Length <= MaxDisplayChars ? content : content[..MaxDisplayChars] + "...";
        FileLog.Write($"[StoredPromptPayloadResolver] ResolveText: session={sessionId}, file={relativePath}, characters={shown.Length}, result=resolved");
        return shown;
    }
}
