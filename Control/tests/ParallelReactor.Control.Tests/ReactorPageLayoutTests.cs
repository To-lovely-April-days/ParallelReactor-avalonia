using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Text;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Layout.ReactorPageLayout（反应釜页的面板网格）与 Core.Programs.ProgramPanelText（「程序」面板的状态行、安全联锁框、面包屑）对照样稿：
/// MockVectors/ReactorMockVectors.cs 的 grid 场景（1920 / 1600 / 1366、收起资源管理器、报警条、宽版式 2200 / 2400 / 3000、关掉各种面板组合）
/// 与 flow 场景（每种程序状态、断线、压力测试数据）。
/// </summary>
public class ReactorPageLayoutTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(ReactorMockVectors.Json));

    public static IEnumerable<object[]> Grids() => Doc.Value.RootElement.GetProperty("grid").EnumerateArray().Select(v => new object[] { v.GetProperty("name").GetString()! });

    private static readonly Dictionary<string, ReactorPanel> Ids = new()
    {
        ["rv-prog"] = ReactorPanel.Program, ["rv-vessel"] = ReactorPanel.Vessel, ["rv-props"] = ReactorPanel.Props, ["rv-curves"] = ReactorPanel.Curves,
    };

    [Theory]
    [MemberData(nameof(Grids))]
    public void Grid_MatchesMock(string name)
    {
        var v = Doc.Value.RootElement.GetProperty("grid").EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        var p = v.GetProperty("probe");
        double w = p.GetProperty("grid")[0].GetDouble(), h = p.GetProperty("grid")[1].GetDouble();
        var closed = Regex.Matches(v.GetProperty("pre").GetString() ?? "", "rv-[a-z]+").Select(m => Ids[m.Value]).ToArray();
        var g = ReactorPageLayout.Compute(w, h, closed);
        string cls = p.GetProperty("cls").GetString()!;
        Assert.Equal(cls.Contains("is-wide"), g.Wide);
        Assert.Equal(cls.Contains("is-prog2"), g.Prog2);
        foreach (var panel in p.GetProperty("panels").EnumerateObject())
        {
            var want = panel.Value;
            var mine = g[Ids[panel.Name]];
            if (want.GetProperty("closed").GetBoolean()) { Assert.True(mine is null, name + " " + panel.Name + " 应该关着"); continue; }
            Assert.True(mine is not null, name + " " + panel.Name + " 不见了");
            var r = want.GetProperty("rect");
            var m = mine!.Value;
            Assert.True(Math.Abs(m.X - r[0].GetDouble()) <= 0.06 && Math.Abs(m.Y - r[1].GetDouble()) <= 0.06 && Math.Abs(m.W - r[2].GetDouble()) <= 0.06 && Math.Abs(m.H - r[3].GetDouble()) <= 0.06,
                $"{name} {panel.Name}：C# {m} ≠ 样稿 {r}");
        }
        if (!g.Wide) Assert.Null(g[ReactorPanel.Props]);   // 页签版式没有单独的属性查看器面板
    }

    [Fact]
    public void Grid_ReadableFacts()
    {
        Assert.False(ReactorPageLayout.IsWide(1799));
        Assert.True(ReactorPageLayout.IsWide(1800));
        Assert.True(ReactorPageLayout.IsProg2(1100));
        Assert.False(ReactorPageLayout.IsProg2(1099));
        // 关掉「程序」：右边一栏铺满整个宽度
        var g = ReactorPageLayout.Compute(936, 592, new[] { ReactorPanel.Program });
        Assert.Equal(936, g[ReactorPanel.Vessel]!.Value.W, 6);
        Assert.Equal(936, g[ReactorPanel.Curves]!.Value.W, 6);
        // 窄版式的程序栏 320–400
        Assert.Equal(320, ReactorPageLayout.Compute(700, 500).Columns[0], 6);
        Assert.Equal(360, ReactorPageLayout.Compute(1000, 500).Columns[0], 6);
        Assert.Equal(558, ReactorPageLayout.Compute(1100, 500).Columns[0], 6);   // 每行 2 张：至少 558
    }

    // ———————————————————— 「程序」面板的文字 ————————————————————

    public static IEnumerable<object[]> Flows() => Doc.Value.RootElement.GetProperty("flow").EnumerateArray()
        .Where(v => v.GetProperty("probe").TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.Object && !(v.GetProperty("pre").GetString() ?? "").Contains("zoom"))
        .Select(v => new object[] { v.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Flows))]
    public void PanelTexts_MatchMock(string name)
    {
        var v = Doc.Value.RootElement.GetProperty("flow").EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        var p = v.GetProperty("probe");
        string route = v.GetProperty("route").GetString()!;
        var m = Regex.Match(route, @"^/reactor/(\d+)/(\d+)\?(.*)$");
        var f = new DemoFleet(DemoParams.Parse(m.Groups[3].Value));
        var d = f.Device(int.Parse(m.Groups[1].Value))!;
        int n = int.Parse(m.Groups[2].Value);
        var ch = d.Channel(n)!;
        var st = ProgramPanelText.Status(ch.Program, d.Progress(ch), d.Online, d.DataText, f.Now)!;
        var ps = p.GetProperty("status");
        Assert.Equal(ps.GetProperty("title").GetString(), st.Title);
        Assert.Equal(ps.GetProperty("l").GetString(), st.Long);
        Assert.Equal(ps.GetProperty("s").GetString(), st.Short);
        if (ps.TryGetProperty("key", out var key)) Assert.Equal(key.GetString(), StateKeys.Code(st.Key));   // 已结束的点是勾，没有 st-* 类
        var safe = p.GetProperty("safe");
        if (safe.ValueKind == JsonValueKind.Object)
        {
            var err = d.Alarms.FirstOrDefault(a => a.Ch == n && a.Level == AlarmLevel.Err);
            var sb = ProgramPanelText.Safety(n, d.Model, d.Online, d.DataAt, d.Estop.Active, ch.State == StateKey.Alarm, err?.Text);
            Assert.Equal(safe.GetProperty("cls").GetString(), "reactor-safe is-" + sb.Tone.ToString().ToLowerInvariant());
            Assert.Equal(safe.GetProperty("v").GetString(), sb.ValueText);
            Assert.Equal(safe.GetProperty("note").GetString(), Keep.Web(sb.Note));
        }
        Assert.Equal(p.GetProperty("crumb").GetProperty("title").GetString(), ProgramPanelText.CrumbTitle(d.Alias, n, ch.Program?.Name));
    }

    [Fact]
    public void PanelTexts_LiveOnlyCases()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        var d = f.Device(1)!;
        var ch = d.Channel(1)!;
        var failed = ch.Program! with { Status = ProgramStatus.Failed };
        var st = ProgramPanelText.Status(failed, Progress.Of(failed, d.ProgressContextOf(ch)), true, "", f.Now)!;
        Assert.Equal("报警中止 · 第 3/4 步", st.Title);
        Assert.Equal("报警中止 · 3/4", st.Short);
        // 真机的等待气路：不写「需在触屏开阀」
        var ctx = new ProgressContext(f.Now, ChannelState: StateKey.Wait, Model: d.Model, WaitGasWord: StateMap.WaitGasWord);
        var press = ch.Program! with { StepIndex = 1 };
        var w = ProgramPanelText.Status(press, Progress.Of(press, ctx), true, "", f.Now)!;
        Assert.Equal("等待气路 · 第 2/4 步", w.Title);
        Assert.Null(ProgramPanelText.Status(null, null, true, "", f.Now));
        Assert.Equal("程序 · 无程序", ProgramPanelText.PanelName(null));
        Assert.Equal("1号机 › RV2 › 无程序", ProgramPanelText.CrumbTitle("1号机", 2, null));
        var sb = ProgramPanelText.Safety(3, d.Model, false, null, false, false, null);
        Assert.Equal(("RV3 :", "= 未知", SafetyTone.Mute, "连接中断 · 状态停在 —"), (sb.RvText, sb.ValueText, sb.Tone, sb.Note));
        Assert.Equal("超压报警", ProgramPanelText.Safety(3, d.Model, true, null, false, true, null).Note);
        // 条件框的悬停说明：当前步 / 已完成的步骤
        var p = ch.Program!;
        var pr = d.Progress(ch);
        Assert.StartsWith("1号机 · RV1 · 步骤时间 > 72 min · 已进行 ", ProgramPanelText.CondTip(d.Alias, 1, p, 2, pr, d.Model));
        Assert.EndsWith(" · 剩 42 min", ProgramPanelText.CondTip(d.Alias, 1, p, 2, pr, d.Model));
        Assert.Matches(@"^1号机 · RV1 · 结束条件 温度 ≥ 119\.5 ℃ · 用时 \d+ min（\d\d:\d\d–\d\d:\d\d）$", ProgramPanelText.CondTip(d.Alias, 1, p, 0, pr, d.Model));
        Assert.Equal("1号机 · RV1 · 结束条件 温度 < 50 ℃", ProgramPanelText.CondTip(d.Alias, 1, p, 3, pr, d.Model));
        Assert.Equal(0.04, ProgramPanelText.CondFill(null));
    }
}
