using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>PR_CHANNELS 解析（设计稿 §1.4）。</summary>
public class ChannelRangeTests
{
    [Theory]
    [InlineData("1-4", new[] { 1, 2, 3, 4 })]
    [InlineData("1,3,5-8", new[] { 1, 3, 5, 6, 7, 8 })]
    [InlineData("2", new[] { 2 })]
    [InlineData(" 8 , 1 ", new[] { 1, 8 })]
    [InlineData("1-8", new[] { 1, 2, 3, 4, 5, 6, 7, 8 })]
    [InlineData("3-3", new[] { 3 })]
    public void Parse_Valid(string spec, int[] expect) => Assert.Equal(expect, ChannelRange.Parse(spec));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0-4")]
    [InlineData("1-9")]
    [InlineData("4-1")]
    [InlineData("a-b")]
    [InlineData("1,,x")]
    [InlineData("1-2-3")]
    public void Parse_Invalid_FallsBackToAllEight(string? spec)
    {
        Assert.False(ChannelRange.TryParse(spec, out _));
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8 }, ChannelRange.Parse(spec));
    }
}
