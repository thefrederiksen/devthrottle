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

    public Control? Build(object? param)
    {
        if (param is not CleanWidgetViewModel vm)
            return new TextBlock { Text = "?" };

        var template = vm.Kind switch
        {
            WidgetKind.Text => TextTemplate,
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
