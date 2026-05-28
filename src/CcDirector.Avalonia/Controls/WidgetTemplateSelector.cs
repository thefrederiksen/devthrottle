using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Routes CleanWidgetViewModel items to the appropriate DataTemplate
/// based on their WidgetKind.
/// </summary>
public class WidgetTemplateSelector : IDataTemplate
{
    public IDataTemplate? TextTemplate { get; set; }
    public IDataTemplate? ThinkingTemplate { get; set; }
    public IDataTemplate? BashTemplate { get; set; }
    public IDataTemplate? ToolTemplate { get; set; }
    public IDataTemplate? UserTemplate { get; set; }
    public IDataTemplate? PendingQuestionTemplate { get; set; }

    /// <summary>
    /// Chromeless render of an assistant text block - just the markdown, no card border or
    /// "Claude" avatar header. Used in the Wingman tab so the final answer reads as one clean
    /// block of text, not a chat bubble.
    /// </summary>
    public IDataTemplate? ResponseTextTemplate { get; set; }

    /// <summary>
    /// When true (the Wingman tab), assistant text renders chromeless via
    /// <see cref="ResponseTextTemplate"/> instead of the bordered card template.
    /// </summary>
    public bool ResponseOnly { get; set; }

    public Control? Build(object? param)
    {
        if (param is not CleanWidgetViewModel vm)
            return new TextBlock { Text = "?" };

        var template = vm.Kind switch
        {
            WidgetKind.Text => ResponseOnly ? ResponseTextTemplate : TextTemplate,
            WidgetKind.Thinking => ThinkingTemplate,
            WidgetKind.Bash => BashTemplate,
            WidgetKind.UserMessage => UserTemplate,
            WidgetKind.PendingQuestion => PendingQuestionTemplate,
            _ => ToolTemplate,
        };

        return template?.Build(param) ?? new TextBlock { Text = vm.Header };
    }

    public bool Match(object? data) => data is CleanWidgetViewModel;
}
