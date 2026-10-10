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
///                     <c>ShowFailure</c>, <c>SwitchToFailed</c>); and a call that puts text on a
///                     screen (<c>ShowStatus</c>, <c>ShowNotification</c>, <c>SetDetectResult</c>, <c>Notified?.Invoke</c>,
///                     <c>AppendLog</c>) whose arguments carry error text or say <c>error: true</c>.
///   layout text     - static text in a <c>.axaml</c> file that carries error words: it is shown by code that makes
///                     the element visible, so it names that code (see <see cref="AxamlMarker"/>).
///   helper call     - any call to the helper not already inside one of the above.
///
/// "Carries error text" means a string literal with a failure word in it (failed, could not, cannot, unable to,
/// error) or an exception's <c>.Message</c>.
///
/// A site is ON THE HELPER when the helper is called inside it, when what it shows is a local the helper returned
/// earlier in the same method or a field that only ever holds what the helper returned, or when it sits inside a
/// call to a method of the
/// scanned source whose body calls the helper (<c>BusyAction.RunAsync</c> reports for the callbacks given to it).
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
/// WHAT IT CANNOT SEE: error text built somewhere else and passed in a variable with no failure word near the
/// display (<c>PathFaultProgress.Text = repair.Detail</c>). It follows literal text and exception messages.
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
    private static bool CarriesErrorText(SyntaxNode expression)
    {
        foreach (var n in expression.DescendantNodesAndSelf(d => d is not LambdaExpressionSyntax))
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
            }
        }
        return false;
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
