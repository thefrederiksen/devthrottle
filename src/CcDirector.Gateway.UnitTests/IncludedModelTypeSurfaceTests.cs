using System.Reflection;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The phase-2 inspection (round 2) bypassed the previous runtime guards by CONSTRUCTION, two ways:
/// <see cref="HostedInferenceBrain"/> accepted a catalog id when the base URL was spelled
/// <c>https://devthrottle.com:443/api/v1</c> (same endpoint, defeats string equality), and the Car Mode
/// chat and warmup transports publicly accepted raw resolver tuples around their guarded default resolver.
/// Those two transports were removed with the Assistant (the Fleet Manager mission, step 9); the first
/// construction stays converted into permanent evidence: every public seam that puts a chat model on a request authenticated by
/// the deployment credential is typed <see cref="IncludedModelId"/>, so the bypasses are no longer
/// expressible with a raw catalog string. Weaken any signature back to a raw string and this goes red.
/// </summary>
public sealed class IncludedModelTypeSurfaceTests
{
    [Fact]
    public void HostedInferenceBrain_Constructor_TakesTheProvenType_NeverARawModelString()
    {
        var ctors = typeof(HostedInferenceBrain).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.NotEmpty(ctors);
        Assert.All(ctors, c =>
        {
            var model = c.GetParameters().SingleOrDefault(p => p.Name == "model");
            Assert.NotNull(model);
            Assert.Equal(typeof(IncludedModelId), model!.ParameterType);
        });
    }
}
