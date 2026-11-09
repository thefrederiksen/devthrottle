using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;

namespace CcClick.Helpers;

public static class ElementFinder
{
    /// <summary>
    /// Find a single element by name (substring match) within a parent element.
    /// </summary>
    public static AutomationElement FindByName(AutomationBase automation, AutomationElement parent, string name)
    {
        var cf = automation.ConditionFactory;
        var element = parent.FindFirstDescendant(cf.ByName(name));
        if (element == null)
            throw new InvalidOperationException($"No element found with name \"{name}\"");
        return element;
    }

    /// <summary>
    /// Find a single element by AutomationId within a parent element.
    /// </summary>
    public static AutomationElement FindById(AutomationBase automation, AutomationElement parent, string automationId)
    {
        var cf = automation.ConditionFactory;
        var element = parent.FindFirstDescendant(cf.ByAutomationId(automationId));
        if (element == null)
            throw new InvalidOperationException($"No element found with AutomationId \"{automationId}\"");
        return element;
    }

    /// <summary>
    /// Find all elements, optionally filtered by ControlType, with depth limit.
    /// </summary>
    public static List<AutomationElement> FindAll(
        AutomationBase automation,
        AutomationElement parent,
        ControlType? controlType = null,
        int maxDepth = int.MaxValue)
    {
        var results = new List<AutomationElement>();
        CollectElements(parent, controlType, maxDepth, 0, results);
        return results;
    }

    private static void CollectElements(
        AutomationElement element,
        ControlType? controlType,
        int maxDepth,
        int currentDepth,
        List<AutomationElement> results)
    {
        if (currentDepth > maxDepth)
            return;

        if (currentDepth > 0) // skip the root
        {
            if (controlType == null || element.ControlType == controlType)
                results.Add(element);
        }

        var children = element.FindAllChildren();
        foreach (var child in children)
        {
            CollectElements(child, controlType, maxDepth, currentDepth + 1, results);
        }
    }

    /// <summary>
    /// Find element by either --name or --id option.
    /// </summary>
    public static AutomationElement FindElement(AutomationBase automation, AutomationElement parent, string? name, string? id)
    {
        if (!string.IsNullOrEmpty(id))
            return FindById(automation, parent, id);
        if (!string.IsNullOrEmpty(name))
            return FindByName(automation, parent, name);
        throw new InvalidOperationException("Either --name or --id must be specified");
    }
}
