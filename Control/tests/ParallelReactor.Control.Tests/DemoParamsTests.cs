using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Control.Core.Demo;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Demo.DemoParams / DemoRoute：样稿 README「常用查询参数」与 js/core.js PR.route、js/sim.js parseDevs / parseMap / parseSet 的语法与默认值；
/// 设备组合的解析逐条对照样稿 JS（DemoMockVectors 的 devs 表）；Parse(ToString()) 往返相等。
/// </summary>
public class DemoParamsTests
{
    [Fact]
    public void DeviceModels_MatchMockParseDevs()
    {
        using var doc = JsonDocument.Parse(DemoMockVectors.Json);
        int n = 0;
        foreach (var row in doc.RootElement.GetProperty("devs").EnumerateArray())
        {
            string devs = row[0].GetString()!;
            var p = new DemoParams { Devs = devs.Length == 0 ? null : devs };
            Assert.Equal(row[1].GetString(), string.Join(",", p.DeviceModels()));
            n++;
        }
        Assert.True(n >= 10);
    }

    [Fact]
    public void Defaults()
    {
        var p = DemoParams.Parse("");
        Assert.Equal(DemoParams.Default, p);
        Assert.Equal(new[] { "PR8-4", "PR4", "PR16" }, p.DeviceModels());
        Assert.False(p.Bare);
        Assert.False(p.Freeze);
        Assert.Null(p.Res);
        Assert.Null(p.Sel);
        Assert.Null(p.Explorer);
        Assert.Null(p.TimelineCollapsed);
        Assert.Null(p.Offline);                 // 默认：4 号位断线
        Assert.True(p.OfflineFor(4, defaultOffline: true));
        Assert.False(p.OfflineFor(1, defaultOffline: false));
        Assert.Empty(p.Estop);
        Assert.Equal(0, p.Stress);
        Assert.Equal("", p.ToString());
    }

    [Fact]
    public void ParsesEveryReadmeParameter()
    {
        var p = DemoParams.Parse("?devs=4&bare=1&freeze=1&res=1366&sel=3/11&explorer=1&tl=0&lease=2:requested,1:lost&offline=1,4&alarm=2:1,3:0&estop=1,3&stress=2&dialog=lease:2&scroll=end&frame=workspace");
        Assert.Equal("4", p.Devs);
        Assert.Equal(new[] { "PR8-4", "PR4", "PR16", "PR8" }, p.DeviceModels());
        Assert.True(p.Bare);
        Assert.True(p.Freeze);
        Assert.Equal(1366, p.Res);
        Assert.Equal((1366, 768), p.Resolution);
        Assert.Equal(new DemoSelection(3, 11), p.Sel);
        Assert.Equal("3/11", p.Sel.ToString());
        Assert.True(p.Explorer);
        Assert.True(p.TimelineCollapsed);
        Assert.Equal("requested", p.LeaseFor(2));
        Assert.Equal("lost", p.LeaseFor(1));
        Assert.Null(p.LeaseFor(3));
        Assert.True(p.OfflineFor(1, false));
        Assert.False(p.OfflineFor(4 - 2, true));   // offline 给了就不看默认
        Assert.Equal("1", p.AlarmFor(2));
        Assert.Equal("0", p.AlarmFor(3));
        Assert.True(p.EstopFor(3));
        Assert.False(p.EstopFor(2));
        Assert.Equal(2, p.Stress);
        Assert.Equal(("lease", "2"), (p.DialogName, p.DialogArg));
        Assert.True(p.ScrollToEnd);
        Assert.Equal(new[] { new KeyValuePair<string, string>("frame", "workspace") }, p.Extra);
    }

    [Theory]
    [InlineData("2:requested,1:lost", "2=requested;1=lost")]
    [InlineData(" 2 : remote ", "2=remote")]          // 键、值都去空白
    [InlineData("3", "3=1")]                            // 没有冒号 → '1'
    [InlineData("3:", "3=")]                            // 空值（按默认处理）
    [InlineData("a:remote,2:lost", "2=lost")]           // 键不是数字：丢掉
    [InlineData("01:remote", "01=remote")]              // 键保留原文（不等于 1 号机）
    [InlineData("2:a:b", "2=a")]                        // 只取第二段
    [InlineData("2:local,2:remote", "2=remote")]        // 同一键后者为准
    [InlineData("", "")]
    public void ParseMap_FollowsMock(string s, string expected) =>
        Assert.Equal(expected, string.Join(";", DemoParams.ParseMap(s).Select(kv => kv.Key + "=" + kv.Value)));

    [Theory]
    [InlineData(null, null)]
    [InlineData("none", "")]
    [InlineData("0", "")]
    [InlineData("", "")]
    [InlineData("1,4", "1,4")]
    [InlineData(" 4 ,x,4,2", "4,2")]
    public void ParseSet_FollowsMock(string? s, string? expected)
    {
        var set = DemoParams.ParseSet(s);
        Assert.Equal(expected, set is null ? null : string.Join(",", set));
    }

    [Fact]
    public void LeaseKeyIsAString_01IsNotDevice1()
    {
        var p = DemoParams.Parse("lease=01:remote");
        Assert.Null(p.LeaseFor(1));
        var f = new DemoFleet(p with { Freeze = true });
        Assert.Equal(Core.State.LeaseStatus.Local, f.Device(2)!.Lease.State);
    }

    [Theory]
    [InlineData("tl=0", true)]
    [InlineData("tl=1", false)]
    [InlineData("tl=", false)]       // 给了别的值 = 展开
    [InlineData("", null)]
    public void Timeline(string q, bool? collapsed) => Assert.Equal(collapsed, DemoParams.Parse(q).TimelineCollapsed);

    [Theory]
    [InlineData("explorer=0", false)]
    [InlineData("explorer=1", true)]
    [InlineData("explorer=2", null)]
    [InlineData("res=1920", null)]
    public void Explorer(string q, bool? open) => Assert.Equal(open, DemoParams.Parse(q).Explorer);

    [Theory]
    [InlineData("res=1920", 1920)]
    [InlineData("res=1600", 1600)]
    [InlineData("res=1366", 1366)]
    [InlineData("res=1440", null)]
    [InlineData("res=01920", null)]
    public void Res(string q, int? res) => Assert.Equal(res, DemoParams.Parse(q).Res);

    [Theory]
    [InlineData("stress=1", 1)]
    [InlineData("stress=2", 2)]
    [InlineData("stress=3", 0)]
    [InlineData("stress=", 0)]
    public void Stress(string q, int level) => Assert.Equal(level, DemoParams.Parse(q).Stress);

    [Fact]
    public void UrlDecoding_LikeUrlSearchParams()
    {
        var p = DemoParams.Parse("devs=PR8-4%2CPR4&dialog=rename%3A2&x=a+b&freeze=0&freeze=1");
        Assert.Equal("PR8-4,PR4", p.Devs);
        Assert.Equal(("rename", "2"), (p.DialogName, p.DialogArg));
        Assert.Equal("a b", p.Extra.Single().Value);
        Assert.True(p.Freeze);   // 同名参数后者为准
    }

    [Theory]
    [InlineData("")]
    [InlineData("devs=4&freeze=1&bare=1")]
    [InlineData("devs=PR8-4,PR4,PR16,PR8&lease=2:requested,1:lost&offline=none&alarm=3:0&estop=1,2")]
    [InlineData("sel=2&explorer=0&tl=0&res=1600&stress=1&dialog=estop:2&scroll=end")]
    [InlineData("offline=&tl=&explorer=1&sel=3/11")]
    [InlineData("frame=workspace&devs=1&z=%E4%B8%AD")]
    public void RoundTrip(string q)
    {
        var p = DemoParams.Parse(q);
        var again = DemoParams.Parse(p.ToString());
        Assert.Equal(p, again);
        Assert.Equal(p.ToString(), again.ToString());
        Assert.Equal(p.GetHashCode(), again.GetHashCode());
    }

    [Fact]
    public void ToString_IsCanonical()
    {
        Assert.Equal("devs=4&freeze=1&lease=2:requested&offline=none&tl=0&sel=3/11&dialog=lease:2",
            DemoParams.Parse("dialog=lease:2&sel=3/11&tl=0&offline=&lease=2:requested&freeze=1&devs=4").ToString());
        Assert.Equal("freeze=1", (DemoParams.Default with { Freeze = true }).ToString());
    }

    [Fact]
    public void Route_ParseAndBuild()
    {
        var r = DemoRoute.Parse("#/reactor/1/1?bare=1&freeze=1");
        Assert.Equal("reactor", r.Page);
        Assert.Equal(new[] { "1", "1" }, r.Args);
        Assert.Equal("reactor/1/1", r.Id);
        Assert.Equal((1, 1), (r.DeviceK, r.ReactorN));
        Assert.True(r.Params.Bare && r.Params.Freeze);
        Assert.Equal("#/reactor/1/1?bare=1&freeze=1", r.ToString());

        Assert.Equal("overview", DemoRoute.Parse("").Page);
        Assert.Equal("overview", DemoRoute.Parse("#/").Page);
        Assert.Equal("#/overview", DemoRoute.Parse("#/overview").ToString());
        var dev = DemoRoute.Parse("#/device/2?devs=4&dialog=lease:2");
        Assert.Equal((2, (int?)null), (dev.DeviceK, dev.ReactorN));
        var ph = DemoRoute.Parse("#/placeholder/%E5%85%A8%E9%83%A8%E6%9B%B2%E7%BA%BF");
        Assert.Equal("全部曲线", ph.Args[0]);
        Assert.Equal("#/placeholder/%E5%85%A8%E9%83%A8%E6%9B%B2%E7%BA%BF", ph.ToString());
        Assert.Equal("a-z_.!~*'()%20%2C", DemoRoute.Encode("a-z_.!~*'() ,"));
        Assert.Equal("1:lost,2:remote/x", DemoRoute.EncodeValue("1:lost,2:remote/x"));
    }

    [Fact]
    public void GlobalKeys_MatchMock() =>
        Assert.Equal(new[] { "devs", "bare", "freeze", "res", "lease", "offline", "alarm", "estop", "explorer", "tl", "sel", "stress" }, DemoParams.GlobalKeys);
}
