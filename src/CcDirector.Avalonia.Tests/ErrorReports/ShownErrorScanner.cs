using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CcDirector.Avalonia.Tests.ErrorReports;

/// <summary>
/// Finds every place the desktop puts an error on a screen, from the source itself, and says whether each one
/// goes through the reporting helper (<c>ShownError.Report</c> / <c>ShownErrorBox</c>) - issue #3675, step 4a.
///
/// It reads the C# syntax tree, not lines, so a call split over several lines or a string with a brace in it
/// cannot hide a site. What it counts as a DISPLAY SITE:
///
///   message box     - <c>MessageBox.ShowAsync</c> / <c>ShowConfirmAsync</c>, <c>new MessageDialog(...)</c> and
///                     <c>NativeNotice.Show</c>. Every one, because a box is always seen; a box that shows no error
///                     (a confirmation, a notice) says so with a written reason.
///   control text    - an assignment to a <c>.Text</c> property, or to a property the layout files bind, whose value
///                     carries error text; and any non-clearing assignment to the text of a control whose own name
///                     says it holds an error (<c>ErrorText</c>, <c>TxtFailure</c>).
///   error call      - a call to a method whose name says it shows an error (<c>ShowError</c>,
///                     <c>ShowFailure</c>, <c>SwitchToFailed</c>, <c>Fail</c>, <c>SetIncomplete</c>); and a call that
///                     puts text on a screen (<c>ShowStatus</c>, <c>ShowNotification</c>, <c>SetDetectResult</c>,
///                     <c>Notified?.Invoke</c>, <c>AppendLog</c>) whose arguments carry error text or say
///                     <c>error: true</c>.
///   layout text     - static text in a <c>.axaml</c> file that carries error words: it is shown by code that makes
///                     the element visible, so it names that code (see <see cref="AxamlMarker"/>).
///   helper call     - any call to the helper not already inside one of the above.
///
/// "Carries error text" means any of:
///   - a string literal with a failure word in it (failed, could not, cannot, unable to, error);
///   - an exception's <c>.Message</c>;
///   - a variable, field or member whose NAME says it holds a failure (<c>moved.ErrorMessage</c>, <c>cache.Error</c>,
///     <c>_existingIdsProblem</c>, a list named <c>failed</c>);
///   - a local or a field of the same class that was given error text anywhere - assigned it, initialised with it,
///     or had it added to it (<c>parts.Add("Could NOT fully delete ...")</c> then <c>ShowBanner(string.Join(.., parts))</c>);
///   - a value the same method also writes into a FAILED line (<c>FileLog.Write($"... FAILED: {result.Message}")</c>
///     next to <c>StatusText.Text = result.Message</c>): the code itself says it is a failure.
///
/// A site is ON THE HELPER when the helper is called inside it, when what it shows is a local the helper returned
/// earlier in the same method or a field that only ever holds what the helper returned, or when it sits inside a
/// call to a method of the
/// scanned source whose body calls the helper (<c>BusyAction.RunAsync</c> reports for the callbacks given to it).
/// That last rule trusts the method to report what its callbacks show: it holds for <c>BusyAction</c>, whose work
/// callback must THROW to fail, but a callback that put error text on screen without throwing would pass unreported.
/// The body of a method whose name says it shows an error, and of a <c>Show...</c> method that shows the text it is
/// given, is that method's own business: its CALLS are the sites, so its insides are not counted again.
///
/// A site may instead be EXEMPT, with a written reason in a comment on the line above the statement:
///   <c>// shown-error-exempt (user input): the name field is empty - the user's own entry</c>
///   <c>// shown-error-exempt (not an error): a confirmation, nothing has failed</c>
///   <c>// shown-error-exempt (reported above): the advice under the error label the line before reported</c>
/// The reason is required, and "reported above" is checked: an earlier statement in the same block must call the
/// helper. An exemption comment that no site uses is itself a finding, so they cannot pile up.
///
/// WHAT IT CANNOT SEE: error text that crosses a method or class boundary under a name that does not say failure
/// (<c>PathFaultProgress.Text = repair.Detail</c>, where only <c>repair</c>'s producer knows it failed). Within one
/// method or class it follows locals and fields; across them it relies on names.
/// </summary>
public sealed class ShownErrorScanner
{
    public const string ExemptPrefix = "shown-error-exempt";
    public const string UserInputKind = "user input";
    public const string NotAnErrorKind = "not an error";
    /// <summary>A second display of a failure the statement before it in the same block already reported - the advice
    /// under an error label, say. Checked, not trusted: an earlier statement in the block must call the helper.</summary>
    public const string ReportedAboveKind = "reported above";
    private const int MinimumReason = 12;

    /// <summary>The axaml marker on the line before an element that holds error text:
    /// <c>&lt;!-- shown-error-reported-by: ShowFailure --&gt;</c> names the code-behind method that shows it and
    /// reports; the two exempt forms work as in C#.</summary>
    public const string AxamlMarker = "shown-error-reported-by";

    private static readonly Regex FailureWord = new(
        @"\b(failed|failure|could ?not|couldn't|cannot|can't|unable to|error)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ErrorName = new(@"(Error|Failure|Failed)", RegexOptions.CultureInvariant);
    // A call whose name says it shows an error: ShowError, ShowGatewayFailure, SwitchToFailed. A callback named for a
    // failure (onFailed, OnBrowserError) is plumbing, not a screen: the code it hands the error to is where the site is.
    private static readonly Regex ErrorCallName = new(@"^_?(Show|show|SwitchTo)", RegexOptions.CultureInvariant);
    // A call whose own name is a failure, with no Show prefix: Fail(...), MarkFailed(...), SetIncomplete(...). Callbacks
    // (on/On) and test seams are plumbing, as above, and a Log... method writes the log, not a screen.
    private static readonly Regex FailureCallName = new(
        @"^(?!_?(on|On|Try|Is|Has|Assert|Clear|Log))_?\w*(Fail|Failed|Failure|Incomplete)$", RegexOptions.CultureInvariant);
    // A name that says the value IS a failure: Error, ErrorMessage, FailureReason, _existingIdsProblem, failed.
    // Not a flag about one (IsFailed, HasError, AnyErrors) and not a count.
    private static readonly Regex FailureValueName = new(
        @"^_?(error|failure|failures|failed|problem|fault)$|^_?(?!(is|Is|has|Has|any|Any|show|Show|on|On)[A-Z])\w*?(Error|ErrorMessage|ErrorDetail|Failure|FailureReason|Failures|Failed|Problem|Fault)$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> CollectsText = new(StringComparer.Ordinal)
        { "Add", "AddRange", "Append", "AppendLine", "Insert", "Prepend" };
    // A call that puts text on a screen - a status line, the notification bar, a result line, a log pane the user
    // reads: ShowStatus, ShowNotification, _showMessage, Notified, SetDetectResult, AppendLog.
    private static readonly Regex DisplayCallName = new(
        @"^_?([Ss]how|Notif|Set\w*(Status|Result|Message|Notice|Banner|Error)$|Append\w*Log$)", RegexOptions.CultureInvariant);
    private static readonly Regex ExceptionName = new(@"^(ex|e|exception|inner|err|.*Exception|.*Ex)$", RegexOptions.CultureInvariant);
    private static readonly Regex Exemption = new(
        @"//\s*" + ExemptPrefix + @"\s*\((?<kind>[^)]*)\)\s*:\s*(?<reason>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.Multiline);
    private static readonly Regex Binding = new(@"\{(?:Compiled)?Binding\s+(?:Path=)?(?<path>[A-Za-z_][\w.]*)", RegexOptions.CultureInvariant);
    private static readonly Regex AxamlText = new(
        @"\b(?:Text|Content)=""(?<text>[^""{][^""]*)""", RegexOptions.CultureInvariant);
    private static readonly Regex AxamlComment = new(@"<!--\s*(?<body>.*?)\s*-->", RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly HashSet<string> HelperClasses = new(StringComparer.Ordinal) { "ShownError", "ShownErrorBox" };

    public sealed record Site(string File, int Line, string Kind, string Status, string Text, string? Reason = null);

    public sealed class Result
    {
        public List<Site> Sites { get; } = new();
        public List<string> Findings { get; } = new();
        public int FilesRead { get; set; }
        public IEnumerable<Site> Violations => Sites.Where(s => s.Status == "NOT REPORTED");
        public int Count(string status) => Sites.Count(s => s.Status == status);
        public int CountKind(string kind) => Sites.Count(s => s.Kind == kind);
    }

    /// <summary>Scan C# and axaml sources. <paramref name="sources"/> maps a path (as it should be reported) to its text.</summary>
    public Result Scan(IReadOnlyDictionary<string, string> sources)
    {
        var result = new Result();
        var boundNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, text) in sources.Where(s => s.Key.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)))
            foreach (Match m in Binding.Matches(text))
                boundNames.Add(m.Groups["path"].Value.Split('.')[^1]);

        var trees = sources
            .Where(s => s.Key.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(s => (Path: s.Key, Root: CSharpSyntaxTree.ParseText(s.Value, path: s.Key).GetCompilationUnitRoot()))
            .ToList();
        result.FilesRead = sources.Count;

        // Methods whose body calls the helper, by class and by name: calls to them report.
        var reportingMethods = new HashSet<string>(StringComparer.Ordinal);
        var methodsByClass = new Dictionary<string, List<MethodDeclarationSyntax>>(StringComparer.Ordinal);
        foreach (var (_, root) in trees)
            foreach (var method in root.DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                var cls = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text ?? "";
                if (!methodsByClass.TryGetValue(cls, out var list)) methodsByClass[cls] = list = new();
                list.Add(method);
            }
        foreach (var (cls, methods) in methodsByClass)
            foreach (var group in methods.GroupBy(m => m.Identifier.Text))
                // Every overload must report, or the name does not count: a call cannot say which one it hits.
                if (group.All(CallsHelper)) reportingMethods.Add($"{cls}.{group.Key}");

        var usedExemptions = new HashSet<(string, int)>();
        foreach (var (path, root) in trees)
            ScanTree(path, root, boundNames, reportingMethods, result, usedExemptions);

        // Exemption comments no site used: stale, or on the wrong line.
        foreach (var (path, root) in trees)
            foreach (var trivia in root.DescendantTrivia().Where(t => t.IsKind(SyntaxKind.SingleLineCommentTrivia)))
            {
                if (!trivia.ToString().Contains(ExemptPrefix, StringComparison.Ordinal)) continue;
                var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (!usedExemptions.Contains((path, line)))
                    result.Findings.Add($"{path}:{line}: an exemption comment that no display site uses - remove it, or put it on the line above the statement it exempts");
            }

        foreach (var (path, text) in sources.Where(s => s.Key.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)))
            ScanAxaml(path, text, sources, reportingMethods, result);

        return result;
    }

    private void ScanTree(string path, CompilationUnitSyntax root, HashSet<string> boundNames,
        HashSet<string> reportingMethods, Result result, HashSet<(string, int)> usedExemptions)
    {
        var siteNodes = new List<(SyntaxNode Node, string Kind)>();
        foreach (var node in root.DescendantNodes())
        {
            var kind = SiteKind(node, boundNames);
            if (kind is null) continue;
            if (IsWrapperInsides(node)) continue;
            siteNodes.Add((node, kind));
        }

        // A helper call that is not inside a site already counted is a site of its own.
        foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(IsHelperCall))
        {
            if (siteNodes.Any(s => s.Node.Span.Contains(call.Span))) continue;
            siteNodes.Add((call, "helper call"));
        }

        foreach (var (node, kind) in siteNodes)
        {
            var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            var text = Squash(node.ToString());
            if (node.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().Any(IsHelperCall)
                || ShowsAReportedLocal(node)
                || ShowsAReportedField(node)
                || InsideReportingCall(node, reportingMethods)
                || IsReportingCall(node, reportingMethods))
            {
                result.Sites.Add(new Site(path, line, kind, "on the helper", text));
                continue;
            }

            var exemption = ExemptionFor(node);
            if (exemption is { } ex)
            {
                usedExemptions.Add((path, ex.Line));
                if (ex.Kind is not (UserInputKind or NotAnErrorKind or ReportedAboveKind))
                    result.Findings.Add($"{path}:{ex.Line}: exemption kind \"{ex.Kind}\" - it must be \"{UserInputKind}\", \"{NotAnErrorKind}\" or \"{ReportedAboveKind}\"");
                else if (ex.Kind == ReportedAboveKind && !AnEarlierStatementReports(node))
                    result.Findings.Add($"{path}:{ex.Line}: says \"{ReportedAboveKind}\", but no earlier statement in its block calls the helper");
                else if (ex.Reason.Length < MinimumReason)
                    result.Findings.Add($"{path}:{ex.Line}: an exemption needs a written reason, at least {MinimumReason} characters");
                result.Sites.Add(new Site(path, line, kind, $"exempt: {ex.Kind}", text, ex.Reason));
                continue;
            }

            result.Sites.Add(new Site(path, line, kind, "NOT REPORTED", text));
        }
    }

    private static string? SiteKind(SyntaxNode node, HashSet<string> boundNames)
    {
        switch (node)
        {
            case ObjectCreationExpressionSyntax create when LastName(create.Type) == "MessageDialog":
                return "message box";

            case InvocationExpressionSyntax call:
            {
                var callee = call.Expression;
                var name = CalleeName(callee);
                if (name is null || IsHelperCall(call)) return null;
                var owner = callee is MemberAccessExpressionSyntax ma ? LastName(ma.Expression) : null;
                if (owner == "MessageBox" && name.StartsWith("Show", StringComparison.Ordinal)) return "message box";
                if (owner == "NativeNotice" && name == "Show") return "message box";
                if (name is "ShowDialog" or "Show" or "ShowAsync") return null;
                // A delegate called through Invoke is named by the delegate: Notified?.Invoke(...), onFailed.Invoke(...).
                if (name == "Invoke") name = InvokedName(callee) ?? name;
                if (ErrorName.IsMatch(name) && ErrorCallName.IsMatch(name)) return "error call";
                // A failure-named call that SHOWS something: it is given text, and it is not a result factory on a type
                // (OperationResult.Fail(...), LauncherCommandResult.Fail(...) build a value; they show nothing).
                // Raising an event (BrowserLaunchFailed?.Invoke(...)) hands the error on; its handler is the site.
                if (FailureCallName.IsMatch(name) && CalleeName(callee) != "Invoke"
                    && call.ArgumentList.Arguments.Count > 0 && !IsCallOnAType(callee))
                    return "error call";
                if (DisplayCallName.IsMatch(name)
                    && (call.ArgumentList.Arguments.Any(a => CarriesErrorText(a.Expression)) || SaysError(call)))
                    return "error call";
                return null;
            }

            case AssignmentExpressionSyntax assign when assign.IsKind(SyntaxKind.SimpleAssignmentExpression):
            {
                var target = assign.Left;
                var targetName = LastName(target);
                if (targetName is null) return null;
                if (targetName == "Text" && target is MemberAccessExpressionSyntax textOf)
                {
                    var control = LastName(textOf.Expression) ?? "";
                    if (ErrorName.IsMatch(control) && !IsClearing(assign.Right)) return "control text";
                    return CarriesErrorText(assign.Right) ? "control text" : null;
                }
                if (boundNames.Contains(targetName) && CarriesErrorText(assign.Right)) return "control text";
                return null;
            }
        }
        return null;
    }

    private static bool SaysError(InvocationExpressionSyntax call)
        => call.ArgumentList.Arguments.Any(a =>
            a.NameColon?.Name.Identifier.Text is "error" or "isError"
            && a.Expression.IsKind(SyntaxKind.TrueLiteralExpression));

    private static bool IsClearing(ExpressionSyntax value)
        => value.IsKind(SyntaxKind.NullLiteralExpression)
           || value is LiteralExpressionSyntax { Token.ValueText: "" }
           || value.ToString() == "string.Empty";

    /// <summary>A string literal with a failure word in it, or an exception's message, anywhere in the expression -
    /// but not inside a lambda, whose body is code that runs later rather than the value being shown.</summary>
    private static bool CarriesErrorText(SyntaxNode expression) => CarriesErrorText(expression, follow: true);

    /// <param name="follow">Look at what a variable was given. Only for the shown value itself: what the variable was
    /// given is read for literals, messages and names, never followed a second step.</param>
    private static bool CarriesErrorText(SyntaxNode expression, bool follow)
    {
        // Not inside a lambda (code that runs later), a throw (the exception's text is not what is shown), or a pattern
        // (a "Failed" => arm tests a value; it does not show one), or an object built from text (new FleetToolCheck(..)
        // is a value its own display code reads, and that code is where the site is).
        foreach (var n in expression.DescendantNodesAndSelf(d =>
                     d is not (LambdaExpressionSyntax or ThrowExpressionSyntax or PatternSyntax or WhenClauseSyntax
                         or BaseObjectCreationExpressionSyntax)))
        {
            switch (n)
            {
                case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) && FailureWord.IsMatch(lit.Token.ValueText):
                    return true;
                case InterpolatedStringTextSyntax part when FailureWord.IsMatch(part.TextToken.ValueText):
                    return true;
                case MemberAccessExpressionSyntax { Name.Identifier.Text: "Message" } m
                    when m.Expression is IdentifierNameSyntax id && ExceptionName.IsMatch(id.Identifier.Text):
                    return true;
                case MemberAccessExpressionSyntax m when IsShownValue(m) && FailureValueName.IsMatch(m.Name.Identifier.Text)
                                                         && !IsEnumValue(m):
                    return true;
                case IdentifierNameSyntax id when IsShownValue(id) && IsVariable(id) && !IsDeclaredBool(id):
                    if (FailureValueName.IsMatch(id.Identifier.Text)) return true;
                    if (follow && WasGivenErrorText(id)) return true;
                    break;
            }
        }
        return LoggedAsFailed(expression);
    }

    /// <summary>A node that is a value being shown, not a test about one (<c>x.Failed ? red : grey</c>,
    /// <c>!result.Error</c>, <c>error is null</c>) and not the name half of a member access.</summary>
    private static bool IsShownValue(ExpressionSyntax node)
    {
        if (node.Parent is MemberAccessExpressionSyntax ma && ma.Name == node) return false;
        if (node.Parent is MemberAccessExpressionSyntax || node.Parent is InvocationExpressionSyntax) return false;
        if (node.Parent is ConditionalAccessExpressionSyntax ca && ca.Expression == node) return false;
        for (SyntaxNode? child = node, parent = node.Parent; parent is not null; child = parent, parent = parent.Parent)
        {
            switch (parent)
            {
                case ConditionalExpressionSyntax c when c.Condition == child:
                case PrefixUnaryExpressionSyntax:
                case IsPatternExpressionSyntax:
                case BinaryExpressionSyntax b when !b.IsKind(SyntaxKind.CoalesceExpression) && !b.IsKind(SyntaxKind.AddExpression):
                    return false;
                case ArgumentSyntax or StatementSyntax or EqualsValueClauseSyntax or AssignmentExpressionSyntax:
                    return true;
            }
        }
        return true;
    }

    private static IEnumerable<ExpressionSyntax> ShownValues(SyntaxNode node)
    {
        switch (node)
        {
            case ParenthesizedExpressionSyntax p:
                foreach (var e in ShownValues(p.Expression)) yield return e;
                break;
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.CoalesceExpression):
                foreach (var e in ShownValues(b.Left)) yield return e;
                break;
            case ConditionalExpressionSyntax c:
                foreach (var e in ShownValues(c.WhenTrue)) yield return e;
                foreach (var e in ShownValues(c.WhenFalse)) yield return e;
                break;
            case ArgumentListSyntax args:
                foreach (var a in args.Arguments)
                    foreach (var e in ShownValues(a.Expression)) yield return e;
                break;
            case ExpressionSyntax e:
                yield return e;
                break;
        }
    }

    /// <summary><c>StatusLevel.Error</c>, <c>RunState.Failed</c>: a value of an enumeration, which names a state rather
    /// than carrying the text of a failure.</summary>
    private static bool IsEnumValue(MemberAccessExpressionSyntax m)
        => m.Expression is IdentifierNameSyntax owner && char.IsUpper(owner.Identifier.Text[0])
           && m.Name.Identifier.Text is "Error" or "Failed" or "Failure" or "Fault" or "Problem";

    private static bool IsCallOnAType(ExpressionSyntax callee) => callee is MemberAccessExpressionSyntax ma
        && (ma.Expression is GenericNameSyntax
            || (ma.Expression is IdentifierNameSyntax owner && char.IsUpper(owner.Identifier.Text[0])));

    /// <summary>A flag (<c>bool _gatewayError</c>) says THAT something failed; it is not the text of the failure.</summary>
    private static bool IsDeclaredBool(IdentifierNameSyntax id)
    {
        var name = id.Identifier.Text;
        var type = id.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        var method = id.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        static bool IsBool(TypeSyntax? t) => t is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.BoolKeyword);
        if (method is not null)
        {
            if (method.DescendantNodes().OfType<ParameterSyntax>().Any(p => p.Identifier.Text == name && IsBool(p.Type))) return true;
            if (method.DescendantNodes().OfType<VariableDeclarationSyntax>().Any(d => IsBool(d.Type) && d.Variables.Any(v => v.Identifier.Text == name))) return true;
        }
        return type is not null && (
            type.Members.OfType<FieldDeclarationSyntax>().Any(f => IsBool(f.Declaration.Type) && f.Declaration.Variables.Any(v => v.Identifier.Text == name))
            || type.Members.OfType<PropertyDeclarationSyntax>().Any(p => p.Identifier.Text == name && IsBool(p.Type)));
    }

    private static bool IsVariable(IdentifierNameSyntax id)
        => id.Parent is not (InvocationExpressionSyntax or NameColonSyntax or TypeArgumentListSyntax)
           && id.Parent is not (QualifiedNameSyntax or VariableDeclarationSyntax);

    /// <summary>A local of the enclosing method, or a field of the enclosing class, that is given error text
    /// anywhere: initialised with it, assigned it, or has it added (<c>parts.Add("Could NOT ...")</c>). What the helper
    /// returned does not count: it has been reported (<c>parts.Add(ShownError.Report(...))</c>).</summary>
    private static bool WasGivenErrorText(IdentifierNameSyntax id)
    {
        var name = id.Identifier.Text;
        var type = id.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null) return false;
        var method = id.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        var isLocal = method is not null && method.DescendantNodes().Any(d =>
            (d is VariableDeclaratorSyntax v && v.Identifier.Text == name)
            || (d is ParameterSyntax p && p.Identifier.Text == name)
            || (d is SingleVariableDesignationSyntax sv && sv.Identifier.Text == name));
        var isField = !isLocal && type.Members.OfType<FieldDeclarationSyntax>().Any(f => f.Declaration.Variables.Any(v => v.Identifier.Text == name));
        if (!isLocal && !isField) return false;
        var scope = isLocal ? method! : type;
        foreach (var d in scope.DescendantNodes())
        {
            ExpressionSyntax? given = d switch
            {
                VariableDeclaratorSyntax v when v.Identifier.Text == name => v.Initializer?.Value,
                AssignmentExpressionSyntax a when LastName(a.Left) == name => a.Right,
                _ => null,
            };
            if (given is not null && !(given is InvocationExpressionSyntax gc && IsHelperCall(gc)) && CarriesErrorText(given, follow: false))
                return true;
            if (d is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax target } add } call
                && target.Identifier.Text == name && CollectsText.Contains(add.Name.Identifier.Text)
                && call.ArgumentList.Arguments.Any(arg => !(arg.Expression is InvocationExpressionSyntax ac && IsHelperCall(ac))
                                                          && CarriesErrorText(arg.Expression, follow: false)))
                return true;
        }
        return false;
    }

    /// <summary>The value shown also goes into a FAILED or FATAL line written in the same method: the code has
    /// already said it is a failure (<c>FileLog.Write($"... FAILED: {result.Message}")</c> beside
    /// <c>StatusText.Text = result.Message</c>).</summary>
    private static bool LoggedAsFailed(SyntaxNode expression)
    {
        var method = expression.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        if (method is null) return false;
        var logged = method.DescendantNodes().OfType<InvocationExpressionSyntax>()
            .Where(c => c.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Write" } w && LastName(w.Expression) == "FileLog")
            .SelectMany(c => c.ArgumentList.Arguments.Select(a => a.Expression))
            .OfType<InterpolatedStringExpressionSyntax>()
            .SelectMany(i => i.Contents.Zip(i.Contents.Skip(1)))
            .Where(pair => pair.First is InterpolatedStringTextSyntax t
                           && (t.TextToken.ValueText.EndsWith("FAILED: ", StringComparison.Ordinal)
                               || t.TextToken.ValueText.EndsWith("FATAL: ", StringComparison.Ordinal))
                           && pair.Second is InterpolationSyntax)
            .Select(pair => ((InterpolationSyntax)pair.Second).Expression.ToString())
            .Where(e => e is not ("ex" or "ex.Message"))
            .ToHashSet(StringComparer.Ordinal);
        if (logged.Count == 0) return false;
        // The value shown is that very expression - on its own, before a ?? default, or as an arm of a ?: choice.
        return ShownValues(expression).Any(e => logged.Contains(e.ToString()));
    }

    private static bool IsHelperCall(InvocationExpressionSyntax call)
        => call.Expression is MemberAccessExpressionSyntax ma && LastName(ma.Expression) is { } owner && HelperClasses.Contains(owner);

    /// <summary>The site shows a local whose value came from the helper: <c>var notice = ShownError.Report(...);</c>
    /// then, later in the same method, <c>NativeNotice.Show(notice, ...)</c> - for a site that must report before it
    /// does something else (start a flush) and only then show.</summary>
    private static bool ShowsAReportedLocal(SyntaxNode node)
    {
        var body = node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AccessorDeclarationSyntax);
        if (body is null) return false;
        var reportedLocals = body.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Where(v => v.Initializer?.Value is InvocationExpressionSyntax call && IsHelperCall(call))
            .Select(v => v.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        if (reportedLocals.Count == 0) return false;
        var shown = node switch
        {
            AssignmentExpressionSyntax a => (SyntaxNode)a.Right,
            InvocationExpressionSyntax i => i.ArgumentList,
            ObjectCreationExpressionSyntax o => o.ArgumentList,
            _ => null,
        };
        return shown is not null && shown.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => reportedLocals.Contains(id.Identifier.Text));
    }

    /// <summary>The site shows a field that only ever holds reported text: every assignment to it anywhere in its
    /// class is either the helper's return or a clearing value (<c>_browsersError = ShownError.Report(...)</c>, later
    /// <c>BrowsersErrorText.Text = _browsersError ?? ""</c>). One assignment of anything else, and it does not count.</summary>
    private static bool ShowsAReportedField(SyntaxNode node)
    {
        var type = node.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        if (type is null) return false;
        var fields = type.Members.OfType<FieldDeclarationSyntax>()
            .SelectMany(f => f.Declaration.Variables)
            .Where(v => v.Initializer is null || IsClearing(v.Initializer.Value))
            .Select(v => v.Identifier.Text)
            .ToHashSet(StringComparer.Ordinal);
        if (fields.Count == 0) return false;
        var shown = node switch
        {
            AssignmentExpressionSyntax a => (SyntaxNode)a.Right,
            InvocationExpressionSyntax i => i.ArgumentList,
            ObjectCreationExpressionSyntax o => o.ArgumentList,
            _ => null,
        };
        if (shown is null) return false;
        var assignments = type.DescendantNodes().OfType<AssignmentExpressionSyntax>().ToList();
        foreach (var id in shown.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var name = id.Identifier.Text;
            if (!fields.Contains(name)) continue;
            var writes = assignments.Where(a => LastName(a.Left) == name).ToList();
            if (writes.Count > 0 && writes.All(a => IsClearing(a.Right)
                    || (a.Right is InvocationExpressionSyntax call && IsHelperCall(call)))
                && writes.Any(a => !IsClearing(a.Right)))
                return true;
        }
        return false;
    }

    private static bool AnEarlierStatementReports(SyntaxNode node)
    {
        var statement = node.AncestorsAndSelf().OfType<StatementSyntax>().FirstOrDefault();
        if (statement?.Parent is not BlockSyntax block) return false;
        return block.Statements.TakeWhile(st => st != statement)
            .Any(st => st.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsHelperCall));
    }

    private static bool CallsHelper(MethodDeclarationSyntax method)
        => method.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(IsHelperCall);

    /// <summary>
    /// The insides of a display method, whose CALLERS are the sites: a method whose name says it shows an error
    /// (<c>ShowError</c>, <c>SwitchToFailed</c>) - every call to it is a site whatever its text - and a <c>Show...</c>
    /// method that puts on screen the text it was GIVEN (<c>ShowStartupNotice(text, ...)</c>), whose callers are
    /// sites when what they pass carries error text.
    /// </summary>
    private static bool IsWrapperInsides(SyntaxNode node)
    {
        if (node.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault() is not { } m) return false;
        var name = m.Identifier.Text;
        if (ErrorName.IsMatch(name) && (name.StartsWith("Show", StringComparison.Ordinal) || name.StartsWith("SwitchTo", StringComparison.Ordinal)))
            return true;
        if (FailureCallName.IsMatch(name)) return true;
        if (!name.StartsWith("Show", StringComparison.Ordinal)) return false;
        var parameters = m.ParameterList.Parameters.Select(p => p.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        SyntaxNode? value = node switch
        {
            AssignmentExpressionSyntax a => a.Right,
            InvocationExpressionSyntax i => i.ArgumentList,
            ObjectCreationExpressionSyntax o => o.ArgumentList,
            _ => null,
        };
        return value is not null && value.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Any(id => parameters.Contains(id.Identifier.Text));
    }

    private static bool IsReportingCall(SyntaxNode node, HashSet<string> reportingMethods)
        => node is InvocationExpressionSyntax call && ResolvedName(call) is { } key && reportingMethods.Contains(key);

    private static bool InsideReportingCall(SyntaxNode node, HashSet<string> reportingMethods)
        => node.Ancestors().OfType<ArgumentSyntax>()
            .Select(a => a.Parent?.Parent as InvocationExpressionSyntax)
            .Any(call => call is not null && ResolvedName(call) is { } key && reportingMethods.Contains(key));

    /// <summary>"Class.Method" for a call the source can resolve without a compiler: <c>BusyAction.RunAsync(...)</c>
    /// names its class; an unqualified <c>ShowFailure(...)</c> is a method of the class it is called from. A call
    /// on an instance (<c>viewer.ShowLoadError</c>) is not resolved, and so never counts as reporting.</summary>
    private static string? ResolvedName(InvocationExpressionSyntax call)
    {
        switch (call.Expression)
        {
            case IdentifierNameSyntax id:
                var cls = call.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
                return cls is null ? null : $"{cls}.{id.Identifier.Text}";
            case MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax owner } ma when char.IsUpper(owner.Identifier.Text[0]):
                return $"{owner.Identifier.Text}.{ma.Name.Identifier.Text}";
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } thisCall:
                var self = call.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;
                return self is null ? null : $"{self}.{thisCall.Name.Identifier.Text}";
        }
        return null;
    }

    private static (int Line, string Kind, string Reason)? ExemptionFor(SyntaxNode node)
    {
        // The comment sits on the line above the site: above the statement (or member) it is in, or - for a site that
        // starts a line of its own inside a statement, such as one property of an object initializer - above that line.
        foreach (var candidate in node.AncestorsAndSelf())
        {
            foreach (var trivia in candidate.GetLeadingTrivia().Reverse())
            {
                if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)) continue;
                var m = Exemption.Match(trivia.ToString());
                if (!m.Success) continue;
                var line = trivia.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                var siteLine = candidate.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                // On the line directly above, as the documentation says - not anywhere in the comments before it.
                if (line != siteLine - 1) break;
                return (line, m.Groups["kind"].Value.Trim(), m.Groups["reason"].Value.Trim());
            }
            if (candidate is StatementSyntax or MemberDeclarationSyntax) break;
        }
        return null;
    }

    private void ScanAxaml(string path, string text, IReadOnlyDictionary<string, string> sources,
        HashSet<string> reportingMethods, Result result)
    {
        var codeBehindPath = path + ".cs";
        sources.TryGetValue(codeBehindPath, out var codeBehind);
        var className = Path.GetFileNameWithoutExtension(path);

        foreach (Match m in AxamlText.Matches(text))
        {
            if (!FailureWord.IsMatch(m.Groups["text"].Value)) continue;
            var line = text[..m.Index].Count(c => c == '\n') + 1;
            var shown = Squash(m.Value);

            // The marker is the last comment before the element that holds this text, with nothing but the
            // element's own opening between them.
            var elementStart = text.LastIndexOf('<', m.Index);
            var before = text[..Math.Max(elementStart, 0)];
            var comment = AxamlComment.Matches(before).LastOrDefault();
            var marker = comment is not null && string.IsNullOrWhiteSpace(before[(comment.Index + comment.Length)..])
                ? comment.Groups["body"].Value
                : null;

            if (marker is not null && marker.StartsWith(AxamlMarker, StringComparison.Ordinal))
            {
                var method = marker[(marker.IndexOf(':') + 1)..].Trim();
                if (codeBehind is not null && reportingMethods.Contains($"{className}.{method}"))
                    result.Sites.Add(new Site(path, line, "layout text", "on the helper", shown));
                else
                    result.Sites.Add(new Site(path, line, "layout text", "NOT REPORTED", shown,
                        $"names {method}, which is not a method of {className} that calls the helper"));
                continue;
            }
            if (marker is not null && Exemption.Match("// " + marker) is { Success: true } ex)
            {
                var kind = ex.Groups["kind"].Value.Trim();
                var reason = ex.Groups["reason"].Value.Trim();
                if (kind is not (UserInputKind or NotAnErrorKind) || reason.Length < MinimumReason)
                    result.Findings.Add($"{path}:{line}: an axaml exemption needs the kind \"{UserInputKind}\" or \"{NotAnErrorKind}\" and a reason of at least {MinimumReason} characters");
                result.Sites.Add(new Site(path, line, "layout text", $"exempt: {kind}", shown, reason));
                continue;
            }
            result.Sites.Add(new Site(path, line, "layout text", "NOT REPORTED", shown));
        }
    }

    private static string? InvokedName(ExpressionSyntax callee) => callee switch
    {
        MemberAccessExpressionSyntax ma => LastName(ma.Expression),
        // Notified?.Invoke(...): the binding hangs off a conditional access whose expression is the delegate.
        MemberBindingExpressionSyntax mb when mb.Parent?.Parent is ConditionalAccessExpressionSyntax c => LastName(c.Expression),
        _ => null,
    };

    private static string? CalleeName(ExpressionSyntax callee) => callee switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        _ => null,
    };

    private static string? LastName(SyntaxNode? node) => node switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        AliasQualifiedNameSyntax a => a.Name.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        MemberBindingExpressionSyntax mb => mb.Name.Identifier.Text,
        _ => null,
    };

    private static string Squash(string text)
    {
        var one = Regex.Replace(text, @"\s+", " ").Trim();
        return one.Length > 140 ? one[..140] + "..." : one;
    }
}
