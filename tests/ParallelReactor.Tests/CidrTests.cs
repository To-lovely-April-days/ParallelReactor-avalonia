using System.Net;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>白名单网段匹配（设计稿 §4.2）。</summary>
public class CidrTests
{
    [Fact]
    public void Empty_AllowsLoopbackOnly()
    {
        var m = new CidrMatcher(new string[0]);
        Assert.True(m.IsAllowed(IPAddress.Loopback));
        Assert.True(m.IsAllowed(IPAddress.IPv6Loopback));
        Assert.True(m.IsAllowed(IPAddress.Parse("::ffff:127.0.0.1")));
        Assert.False(m.IsAllowed(IPAddress.Parse("192.168.1.5")));
        Assert.True(m.IsAllowed(null));
    }

    [Theory]
    [InlineData("192.168.10.0/24", "192.168.10.77", true)]
    [InlineData("192.168.10.0/24", "192.168.11.1", false)]
    [InlineData("10.0.0.5/32", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "10.0.0.5", true)]
    [InlineData("10.0.0.5", "10.0.0.6", false)]
    [InlineData("172.16.0.0/12", "172.31.255.255", true)]
    [InlineData("172.16.0.0/12", "172.32.0.1", false)]
    [InlineData("0.0.0.0/0", "8.8.8.8", true)]
    public void IPv4Ranges(string cidr, string ip, bool expect)
        => Assert.Equal(expect, new CidrMatcher(new[] { cidr }).IsAllowed(IPAddress.Parse(ip)));

    [Fact]
    public void IPv4MappedIPv6_IsNormalized()
    {
        var m = new CidrMatcher(new[] { "192.168.1.0/24" });
        Assert.True(m.IsAllowed(IPAddress.Parse("::ffff:192.168.1.5")));
        Assert.False(m.IsAllowed(IPAddress.Parse("::ffff:192.168.2.5")));
    }

    [Fact]
    public void IPv6Ranges()
    {
        var m = new CidrMatcher(new[] { "fd00::/8" });
        Assert.True(m.IsAllowed(IPAddress.Parse("fd12:3456::1")));
        Assert.False(m.IsAllowed(IPAddress.Parse("fe80::1")));
        Assert.False(m.IsAllowed(IPAddress.Parse("10.0.0.1")));   // 族不同不匹配
    }

    [Fact]
    public void LoopbackNotImplicitlyAllowed_WhenListNonEmpty()
    {
        var m = new CidrMatcher(new[] { "10.255.0.0/24" });
        Assert.False(m.IsAllowed(IPAddress.Loopback));
        Assert.True(m.IsAllowed(IPAddress.Parse("10.255.0.9")));
    }

    [Fact]
    public void MalformedEntries_AreIgnoredAndReported()
    {
        var m = new CidrMatcher(new[] { "nonsense", "10.0.0.0/33", "192.168.1.0/24", "", "1.2.3.4/-1" });
        Assert.Equal(1, m.Count);
        Assert.Equal(4, m.Invalid.Count);
        Assert.True(m.IsAllowed(IPAddress.Parse("192.168.1.9")));
    }
}
