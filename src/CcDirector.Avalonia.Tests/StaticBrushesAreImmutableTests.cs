using System.Reflection;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// A BRUSH HELD IN A STATIC FIELD MUST BE IMMUTABLE. A mutable brush (SolidColorBrush, a gradient, a Pen) is an
/// AvaloniaObject: its constructor checks it is on the dispatcher's thread. A static field is built by the type's
/// static constructor, on whichever thread first touches the type - so a control whose static colours are mutable
/// throws "Call from invalid thread" the first time a background task or a plain unit test reaches it after the
/// dispatcher exists. On 8 November 2026 that took down 71 desktop tests on main, in an order that only the
/// continuous integration run produced. ImmutableSolidColorBrush is not an AvaloniaObject and is safe on any thread,
/// and a shared colour should never change anyway.
/// </summary>
public sealed class StaticBrushesAreImmutableTests
{
    [AvaloniaFact]
    public void DesktopAssembly_StaticBrushFields_AreAllImmutable()
    {
        var offenders = new List<string>();
        foreach (var type in typeof(App).Assembly.GetTypes())
        {
            if (type.ContainsGenericParameters) continue;
            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!typeof(IBrush).IsAssignableFrom(field.FieldType) && !typeof(IPen).IsAssignableFrom(field.FieldType)) continue;
                if (field.GetValue(null) is AvaloniaObject)
                    offenders.Add($"{type.FullName}.{field.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "These static fields hold a mutable brush or pen, which throws when its type is first touched off the " +
            "dispatcher's thread. Use ImmutableSolidColorBrush (or another immutable brush): " + string.Join(", ", offenders));
    }
}
