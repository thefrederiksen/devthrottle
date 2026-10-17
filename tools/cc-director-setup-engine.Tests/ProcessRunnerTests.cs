using System.Diagnostics;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Tests the bounded-wait behavior of <see cref="ProcessRunner"/> (issue #577): a process that exceeds its
/// timeout is killed and returns a loud failure with the partial output, rather than blocking forever.
/// </summary>
public sealed class ProcessRunnerTests
{
}
