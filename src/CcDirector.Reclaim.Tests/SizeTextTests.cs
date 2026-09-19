using CcDirector.Reclaim.Reporting;
using Xunit;

namespace CcDirector.Reclaim.Tests;

/// <summary>How a number of bytes reads in a report.</summary>
public class SizeTextTests
{
    [Theory]
    [InlineData(0, "0 bytes")]
    [InlineData(1, "1 bytes")]
    [InlineData(1023, "1023 bytes")]
    [InlineData(1024, "1.0 kilobytes")]
    [InlineData(1536, "1.5 kilobytes")]
    [InlineData(1048576, "1.0 megabytes")]
    [InlineData(1073741824, "1.0 gigabytes")]
    [InlineData(1099511627776, "1.0 terabytes")]
    [InlineData(1125899906842624, "1.0 petabytes")]
    public void Describe_ASize_IsWrittenInWordsAPersonReads(long bytes, string expected)
    {
        Assert.Equal(expected, SizeText.Describe(bytes));
    }

    /// <summary>
    /// The difference between what a scan saw and what a volume counts can fall on either side, and a
    /// report never hides which side it fell on.
    /// </summary>
    [Fact]
    public void Describe_ANegativeSize_KeepsItsSign()
    {
        Assert.Equal("-1.0 gigabytes", SizeText.Describe(-1073741824));
    }

    [Fact]
    public void Describe_TheSmallestNumberThereIs_DoesNotOverflow()
    {
        Assert.StartsWith("-", SizeText.Describe(long.MinValue), StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_ASizeUnderOneKilobyte_SaysItOnceAndNotTwice()
    {
        Assert.Equal("512 bytes", SizeText.Exact(512));
    }

    [Fact]
    public void Exact_ALargeSize_GivesTheExactCountAndTheWordsBesideIt()
    {
        Assert.Equal("1536 bytes (1.5 kilobytes)", SizeText.Exact(1536));
    }
}
