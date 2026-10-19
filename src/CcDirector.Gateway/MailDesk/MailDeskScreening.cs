using System.Globalization;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// One reported message, as the screening reads it: the fields of phase 2's context (phase 2 contract, section 4.2)
/// that a layer decides on. The preview is shown to the model and never kept.
/// </summary>
/// <param name="MessageKey">The check's short key for the message.</param>
/// <param name="ReceivedUtc">When the mailbox received it.</param>
/// <param name="Sender">The sender address in lower case; empty when the mailbox gave none.</param>
/// <param name="SenderName">The sender's display name, cleaned.</param>
/// <param name="Subject">The subject, cleaned.</param>
/// <param name="KnownSender">Whether the account has written to this sender (the Sent-folder cache holds it).</param>
/// <param name="Preview">The start of the body, cleaned and cut to 200 characters.</param>
public sealed record MailDeskMessage(
    string MessageKey,
    DateTime ReceivedUtc,
    string Sender,
    string SenderName,
    string Subject,
    bool KnownSender,
    string Preview);

/// <summary>
/// WHAT THE MAIL DESK HAS SPENT, WITHIN ONE SCREENING RUN (contract sections 1 and 3). Made from the account's limit and
/// what its decision rows say was spent before the run, and charged with every answered question, so the limit is
/// checked before EACH call - the second question about one email included. At or over the limit, no call is made.
/// It is the run's arithmetic, not memory: nothing outlives the run but the decision rows' costs.
/// </summary>
public sealed class MailDeskSpendLedger
{
    /// <summary>The account's spend limit, in US dollars.</summary>
    public decimal LimitUsd { get; }

    /// <summary>What has been spent, the questions of this run included, in US dollars.</summary>
    public decimal SpentUsd { get; private set; }

    public MailDeskSpendLedger(decimal limitUsd, decimal spentUsd)
    {
        if (limitUsd < 0) throw new ArgumentOutOfRangeException(nameof(limitUsd), limitUsd, "A spend limit is not below zero.");
        if (spentUsd < 0) throw new ArgumentOutOfRangeException(nameof(spentUsd), spentUsd, "What was spent is not below zero.");
        LimitUsd = limitUsd;
        SpentUsd = spentUsd;
    }

    /// <summary>A ledger for an account with these settings that had spent <paramref name="spentMicros"/> before the run.</summary>
    public static MailDeskSpendLedger Start(MailDeskSettings settings, long spentMicros)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new MailDeskSpendLedger(settings.SpendLimitUsd, spentMicros / 1_000_000m);
    }

    /// <summary>Whether the limit is reached: no model question may be asked.</summary>
    public bool IsReached => SpentUsd >= LimitUsd;

    /// <summary>Add one answered question's cost. A question whose cost is unknown adds nothing.</summary>
    public void Charge(decimal? costUsd)
    {
        if (costUsd is { } cost) SpentUsd += cost;
    }
}

/// <summary>
/// The decision for one message (contract section 3): which layer decided it, what it decided, where to, why in one
/// line, how sure the model was, what the questions cost, and whether a model question failed.
/// </summary>
/// <param name="Layer">One of <see cref="MailDeskWords.Layers"/>.</param>
/// <param name="Decision">One of <see cref="MailDeskWords.Decisions"/>.</param>
/// <param name="RouteFactory">The factory, when the decision is <c>route</c>; otherwise null.</param>
/// <param name="RouteSeat">The seat, when the decision is <c>route</c>; otherwise null.</param>
/// <param name="Reason">One line saying why. It names a rule, a pattern, a label, a confidence or a threshold - never
/// a word of the email's body.</param>
/// <param name="Confidence">The deciding answer's confidence; null for a rule or a list, and when none came back.</param>
/// <param name="CostUsd">What this message's answered questions cost; null when no question was answered.</param>
/// <param name="RulesVersion">The version of the rules document the message was decided by.</param>
/// <param name="ModelQuestionFailed">A question was asked and got no answer: the decision is <c>failed</c>, and the
/// email is read again next run.</param>
/// <param name="SpendLimitReached">A question was needed and not asked because the spend limit is reached: the
/// decision is <c>failed</c>, and it is NOT retried - the limit does not lift by itself.</param>
/// <param name="ErrorKind">Why it failed: one of <see cref="MailDeskModelErrorKinds"/>, or
/// <see cref="MailDeskScreening.SpendLimitErrorKind"/>; null when it did not.</param>
public sealed record MailDeskScreeningDecision(
    string Layer,
    string Decision,
    string? RouteFactory,
    string? RouteSeat,
    string Reason,
    double? Confidence,
    decimal? CostUsd,
    int RulesVersion,
    bool ModelQuestionFailed,
    bool SpendLimitReached,
    string? ErrorKind)
{
    /// <summary>The cost in the millionths of a dollar a decision row keeps; null when nothing was spent.</summary>
    public long? CostMicros => CostUsd is { } usd ? MailDeskMoney.Micros(usd) : null;
}

/// <summary>
/// THE FOUR SCREENING LAYERS (Mail Desk phase 3 contract, section 3), as one pure decision for one message: the fixed
/// rules, then the allow-list, then the block list, then the unknown-sender screen, then the route question. The
/// first layer that decides wins.
///
/// Matching is plain code: an exact address or <c>*@domain</c>, and "subject contains", all without regard to case.
/// The model only picks a label - nothing it answers is stored, and nothing it answers does more than choose among
/// the labels offered. Nothing here writes a row, moves a message, starts a session or keeps memory: the caller
/// records the decision (task F). Nothing of the email is logged.
/// </summary>
public static class MailDeskScreening
{
    /// <summary>The error kind of a question not asked because the spend limit is reached.</summary>
    public const string SpendLimitErrorKind = "spend-limit";

    /// <summary>The reason of a route question with no routes to offer.</summary>
    public const string NoRoutesReason = "no routes are defined";

    /// <summary>The model's label for a person writing to us.</summary>
    public const string PersonLabel = "person";

    /// <summary>The model's label for spam.</summary>
    public const string SpamLabel = "spam";

    /// <summary>
    /// Decide <paramref name="message"/> by the rules of <paramref name="rules"/>, the thresholds of
    /// <paramref name="settings"/>, and - only when the rules and lists have not decided - the model's answers, each
    /// asked only while <paramref name="spend"/> is under its limit and charged to it when answered.
    /// </summary>
    public static async Task<MailDeskScreeningDecision> DecideAsync(MailDeskMessage message, MailDeskRulesVersion rules,
        MailDeskSettings settings, IMailDeskModel model, MailDeskSpendLedger spend, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(spend);

        var decision = await DecideCoreAsync(message, rules, settings, model, spend, ct);
        FileLog.Write($"[MailDeskScreening] DecideAsync: rules version {rules.Version} -> {decision.Layer} {decision.Decision}" +
                      $"{(decision.ErrorKind is { } kind ? $" ({kind})" : "")}, cost={decision.CostUsd?.ToString(CultureInfo.InvariantCulture) ?? "none"}");
        return decision;
    }

    private static async Task<MailDeskScreeningDecision> DecideCoreAsync(MailDeskMessage message, MailDeskRulesVersion rules,
        MailDeskSettings settings, IMailDeskModel model, MailDeskSpendLedger spend, CancellationToken ct)
    {
        var document = rules.Document;
        var version = rules.Version;
        var sender = message.Sender ?? "";

        // 1. The fixed rules, in document order.
        foreach (var rule in document.Rules)
        {
            if (!RuleMatches(rule, sender, message.Subject ?? "")) continue;
            var target = rule.Route ?? throw new InvalidOperationException($"The Mail Desk rule '{rule.Name}' has no route; the validator lets none through.");
            return Decided(MailDeskWords.Layers.Rule, MailDeskWords.Decisions.Route, target.Factory, target.Seat, $"rule {rule.Name}", version);
        }

        // 2. The allow-list.
        if (document.Allow.FirstOrDefault(a => MailDeskAddresses.Matches(a.From, sender)) is { } allow)
        {
            var reason = $"allow {allow.From} ({allow.What})";
            var todo = allow.Do ?? throw new InvalidOperationException($"The Mail Desk allow entry '{allow.From}' has no 'do'; the validator lets none through.");
            if (todo.Route is { } to)
                return Decided(MailDeskWords.Layers.Allow, MailDeskWords.Decisions.Route, to.Factory, to.Seat, reason, version);
            var word = todo.Action switch
            {
                MailDeskWords.AllowActions.Archive => MailDeskWords.Decisions.Archive,
                MailDeskWords.AllowActions.KeepForOwner => MailDeskWords.Decisions.KeepForOwner,
                _ => throw new InvalidOperationException($"The Mail Desk allow entry '{allow.From}' does '{todo.Action}', which the screening does not know."),
            };
            return Decided(MailDeskWords.Layers.Allow, word, null, null, reason, version);
        }

        // 3a. The block list.
        if (document.Block.FirstOrDefault(b => MailDeskAddresses.Matches(b.From, sender)) is { } block)
            return Decided(MailDeskWords.Layers.Block, MailDeskWords.Decisions.Junk, null, null, $"block {block.From} ({block.Why})", version);

        var email = new MailDeskEmail(message.SenderName ?? "", sender, message.Subject ?? "", message.Preview ?? "");
        decimal? cost = null;

        // 3b. An unknown sender: a person, or spam? A known sender skips it.
        if (!message.KnownSender)
        {
            const string layer = MailDeskWords.Layers.SenderScreen;
            if (spend.IsReached) return SpendStopped(layer, spend, cost, version);
            var answer = await model.AskPersonOrSpamAsync(email, ct);
            cost = Add(cost, answer.CostUsd);
            spend.Charge(answer.CostUsd);
            switch (answer.Kind)
            {
                case MailDeskAnswerKind.Failed:
                    return QuestionFailed(layer, answer, cost, version);
                case MailDeskAnswerKind.OffLabels:
                    return ToAgent(layer, $"{layer}: answered outside the labels", null, cost, version);
                case MailDeskAnswerKind.Label when answer.Label == SpamLabel:
                    if (answer.Confidence is not { } sure)
                        return ToAgent(layer, $"{layer}: no confidence returned", null, cost, version);
                    var threshold = Number(settings.SpamThreshold);
                    return sure >= (double)settings.SpamThreshold
                        ? Decided(layer, MailDeskWords.Decisions.Junk, null, null, $"{layer}: spam at {Number(sure)} (threshold {threshold})", version, sure, cost)
                        : ToAgent(layer, $"{layer}: spam at {Number(sure)}, under the threshold {threshold}", sure, cost, version);
                case MailDeskAnswerKind.Label when answer.Label == PersonLabel:
                    break; // a person, at any confidence: on to the route question
                default:
                    throw new InvalidOperationException($"The person-or-spam question answered the label '{answer.Label}', which it does not offer.");
            }
        }

        // 4. The route question, for what is left.
        {
            const string layer = MailDeskWords.Layers.RouteModel;
            var routes = document.Routes;
            if (routes.Count == 0) return ToAgent(layer, NoRoutesReason, null, cost, version);
            if (routes.Count > MailDeskRulesValidator.MaxRoutes)
                throw new InvalidOperationException(
                    $"The Mail Desk rules version {version} lists {routes.Count} routes; the validator lets at most {MailDeskRulesValidator.MaxRoutes} through.");
            if (spend.IsReached) return SpendStopped(layer, spend, cost, version);
            var answer = await model.AskRouteAsync(email, routes.Select(r => new MailDeskRouteChoice(r.Key, r.Handles)).ToList(), ct);
            cost = Add(cost, answer.CostUsd);
            spend.Charge(answer.CostUsd);
            switch (answer.Kind)
            {
                case MailDeskAnswerKind.Failed:
                    return QuestionFailed(layer, answer, cost, version);
                case MailDeskAnswerKind.OffLabels:
                    return ToAgent(layer, $"{layer}: answered outside the labels", null, cost, version);
                case MailDeskAnswerKind.Label when answer.Label == MailDeskModel.UnsureLabel:
                    return ToAgent(layer, $"{layer}: unsure", answer.Confidence, cost, version);
            }
            var chosen = routes.FirstOrDefault(r => r.Key == answer.Label)
                         ?? throw new InvalidOperationException($"The route question answered the label '{answer.Label}', which it did not offer.");
            if (answer.Confidence is not { } sure)
                return ToAgent(layer, $"{layer}: {chosen.Key} with no confidence returned", null, cost, version);
            var threshold = Number(settings.RouteThreshold);
            return sure >= (double)settings.RouteThreshold
                ? Decided(layer, MailDeskWords.Decisions.Route, chosen.Factory, chosen.Seat, $"{layer}: {chosen.Key} at {Number(sure)} (threshold {threshold})", version, sure, cost)
                : ToAgent(layer, $"{layer}: {chosen.Key} at {Number(sure)}, under the threshold {threshold}", sure, cost, version);
        }
    }

    /// <summary>Whether every condition the rule gives holds: any of its subject texts, and any of its senders.</summary>
    private static bool RuleMatches(MailDeskRule rule, string sender, string subject)
    {
        var subjects = rule.When?.SubjectContains ?? Array.Empty<string>();
        var senders = rule.When?.FromAny ?? Array.Empty<string>();
        if (subjects.Count == 0 && senders.Count == 0) return false;
        if (subjects.Count > 0 && !subjects.Any(text => text.Length > 0 && subject.Contains(text, StringComparison.OrdinalIgnoreCase)))
            return false;
        return senders.Count == 0 || senders.Any(pattern => MailDeskAddresses.Matches(pattern, sender));
    }

    private static MailDeskScreeningDecision Decided(string layer, string decision, string? factory, string? seat, string reason,
        int version, double? confidence = null, decimal? cost = null)
        => new(layer, decision, factory, seat, reason, confidence, cost, version, false, false, null);

    private static MailDeskScreeningDecision ToAgent(string layer, string reason, double? confidence, decimal? cost, int version)
        => Decided(layer, MailDeskWords.Decisions.MailDeskAgent, null, null, reason, version, confidence, cost);

    private static MailDeskScreeningDecision QuestionFailed(string layer, MailDeskModelResult answer, decimal? cost, int version)
        => new(layer, MailDeskWords.Decisions.Failed, null, null,
            $"{layer}: the model could not be asked ({answer.ErrorKind}); read again next run", null, cost, version, true, false, answer.ErrorKind);

    private static MailDeskScreeningDecision SpendStopped(string layer, MailDeskSpendLedger spend, decimal? cost, int version)
        => new(layer, MailDeskWords.Decisions.Failed, null, null,
            $"the Mail Desk spend limit of {MailDeskMoney.Dollars(spend.LimitUsd)} is reached; the model was not asked",
            null, cost, version, false, true, SpendLimitErrorKind);

    private static decimal? Add(decimal? total, decimal? cost) => cost is { } c ? (total ?? 0m) + c : total;

    /// <summary>A confidence or a threshold as a reason writes it: two places, more only when they matter.</summary>
    private static string Number(double value) => value.ToString("0.00##", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.00##", CultureInfo.InvariantCulture);
}
