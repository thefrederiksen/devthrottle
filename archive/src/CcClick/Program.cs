using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;
using FlaUI.UIA3;
using CcClick;
using CcClick.Commands;

var windowOption = new Option<string?>("--window", "-w") { Description = "Window title (substring match)" };
var nameOption = new Option<string?>("--name") { Description = "Element name / display text" };
var idOption = new Option<string?>("--id") { Description = "Element AutomationId" };

// ── list-windows ──
var listWindowsCmd = new Command("list-windows", "List visible top-level windows");
var filterOption = new Option<string?>("--filter", "-f") { Description = "Filter windows by title substring" };
listWindowsCmd.Options.Add(filterOption);
listWindowsCmd.SetAction(parseResult => Run(() =>
{
    var filter = parseResult.GetValue(filterOption);
    using var automation = new UIA3Automation();
    return ListWindowsCommand.Execute(automation, filter);
}));

// ── list-elements ──
var listElementsCmd = new Command("list-elements", "List UI elements in a window");
var typeOption = new Option<string?>("--type", "-t") { Description = "Filter by ControlType (e.g. Button, TextBox)" };
var depthOption = new Option<int>("--depth", "-d") { Description = "Max tree traversal depth", DefaultValueFactory = _ => 25 };
listElementsCmd.Options.Add(windowOption);
listElementsCmd.Options.Add(typeOption);
listElementsCmd.Options.Add(depthOption);
listElementsCmd.SetAction(parseResult => Run(() =>
{
    var window = parseResult.GetValue(windowOption);
    var type = parseResult.GetValue(typeOption);
    var depth = parseResult.GetValue(depthOption);
    if (string.IsNullOrEmpty(window))
        throw new InvalidOperationException("--window is required");
    using var automation = new UIA3Automation();
    return ListElementsCommand.Execute(automation, window, type, depth);
}));

// ── click ──
var clickCmd = new Command("click", "Click a UI element");
var xyOption = new Option<string?>("--xy") { Description = "Absolute screen coordinates (e.g. \"500,300\")" };
var rightOption = new Option<bool>("--right", "-r") { Description = "Right-click instead of left-click" };
clickCmd.Options.Add(windowOption);
clickCmd.Options.Add(nameOption);
clickCmd.Options.Add(idOption);
clickCmd.Options.Add(xyOption);
clickCmd.Options.Add(rightOption);
clickCmd.SetAction(parseResult => Run(() =>
{
    var window = parseResult.GetValue(windowOption);
    var name = parseResult.GetValue(nameOption);
    var id = parseResult.GetValue(idOption);
    var xy = parseResult.GetValue(xyOption);
    var right = parseResult.GetValue(rightOption);
    using var automation = new UIA3Automation();
    return ClickCommand.Execute(automation, window, name, id, xy, right);
}));

// ── type ──
var typeCmd = new Command("type", "Type text into a UI element");
var textOption = new Option<string>("--text") { Description = "Text to type", Required = true };
typeCmd.Options.Add(windowOption);
typeCmd.Options.Add(nameOption);
typeCmd.Options.Add(idOption);
typeCmd.Options.Add(textOption);
typeCmd.SetAction(parseResult => Run(() =>
{
    var window = parseResult.GetValue(windowOption);
    var name = parseResult.GetValue(nameOption);
    var id = parseResult.GetValue(idOption);
    var text = parseResult.GetValue(textOption)!;
    if (string.IsNullOrEmpty(window))
        throw new InvalidOperationException("--window is required");
    using var automation = new UIA3Automation();
    return TypeTextCommand.Execute(automation, window, name, id, text);
}));

// ── screenshot ──
var screenshotCmd = new Command("screenshot", "Capture a screenshot");
var outputOption = new Option<string>("--output", "-o") { Description = "Output file path", Required = true };
screenshotCmd.Options.Add(windowOption);
screenshotCmd.Options.Add(outputOption);
screenshotCmd.SetAction(parseResult => Run(() =>
{
    var window = parseResult.GetValue(windowOption);
    var output = parseResult.GetValue(outputOption)!;
    using var automation = new UIA3Automation();
    return ScreenshotCommand.Execute(automation, window, output);
}));

// ── read-text ──
var readTextCmd = new Command("read-text", "Read text content of a UI element");
readTextCmd.Options.Add(windowOption);
readTextCmd.Options.Add(nameOption);
readTextCmd.Options.Add(idOption);
readTextCmd.SetAction(parseResult => Run(() =>
{
    var window = parseResult.GetValue(windowOption);
    var name = parseResult.GetValue(nameOption);
    var id = parseResult.GetValue(idOption);
    if (string.IsNullOrEmpty(window))
        throw new InvalidOperationException("--window is required");
    using var automation = new UIA3Automation();
    return ReadTextCommand.Execute(automation, window, name, id);
}));

// ── root command ──
var rootCommand = new RootCommand("cc_click — CLI UI automation tool for LLM agents");
rootCommand.Subcommands.Add(listWindowsCmd);
rootCommand.Subcommands.Add(listElementsCmd);
rootCommand.Subcommands.Add(clickCmd);
rootCommand.Subcommands.Add(typeCmd);
rootCommand.Subcommands.Add(screenshotCmd);
rootCommand.Subcommands.Add(readTextCmd);

return rootCommand.Parse(args).Invoke();

// ── error wrapper ──
static int Run(Func<int> action)
{
    try
    {
        return action();
    }
    catch (Exception ex)
    {
        var error = new { error = ex.Message };
        Console.Error.WriteLine(JsonSerializer.Serialize(error, JsonOptions.Default));
        return 1;
    }
}
