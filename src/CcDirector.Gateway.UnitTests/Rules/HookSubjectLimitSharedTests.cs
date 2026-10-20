using System.Reflection;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// Whole-phase review, question 8: the evaluator cuts a command to a length before it records it, and the
/// Gateway's subject column and write gate refuse anything longer. They were two literals that happened to
/// agree, so a change to one would compile and pass, and every long command would then be refused by id in the
/// Director's log. The evaluator may reference no project (ruling P11), so the one constant lives in the
/// evaluator's own source file and the Gateway compiles that same file in. This proves the Gateway's limit IS
/// that file's constant, not a copy of its value.
/// </summary>
public sealed class HookSubjectLimitSharedTests
{
    private const string SharedTypeName = "CcDirector.RuleHook.HookWireLimits";

    [Fact]
    public void SubjectMaxLength_GatewayAssembly_CompilesTheEvaluatorsOwnLimitsFile()
    {
        var shared = typeof(HookRuleWords).Assembly.GetType(SharedTypeName);

        Assert.NotNull(shared);
    }

    [Fact]
    public void SubjectMaxLength_GatewayLimit_IsTheEvaluatorsConstant()
    {
        var shared = typeof(HookRuleWords).Assembly.GetType(SharedTypeName)
            ?? throw new InvalidOperationException($"{SharedTypeName} is not compiled into the Gateway");
        var field = shared.GetField("SubjectMaxLength", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"{SharedTypeName}.SubjectMaxLength does not exist");

        Assert.True(field.IsLiteral);
        Assert.Equal(HookRuleWords.SubjectMaxLength, (int)field.GetRawConstantValue()!);
    }
}
