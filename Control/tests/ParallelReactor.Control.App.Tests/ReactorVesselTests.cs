using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using ParallelReactor.Control.Controls.Vessel;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Core.Vessel;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 反应釜页「釜体 / 属性查看器」页签面板（PORT-PLAN §5 M5；样稿 js/pages/reactor.js coView / stageLayout / stateActions / propCards / propsHTML）：
/// <list type="bullet">
/// <item>版面（Controls/Vessel/StageLayout）：用样稿自己量出来的字宽，逐个场景对照样稿在 WebKit 里排出来的结果（<see cref="StageMockVectors"/>：
///   样式、六个框的位置与尺寸、釜体框、引线的 path）；另查高度 / 宽度两条选样式的规则（两列之间给釜体留 ≥ 96 px）、窄面板里釜体不被盖住。</item>
/// <item>标注的文字（ReactorVesselVm.Callouts，逐字照样稿）：反应中、独立搅拌 + H₂、超压报警（只看还没解除的超压错误报警）、断线、急停、从未连上。</item>
/// <item>属性卡片：八张的文字与实时边、只显示完整的行（列 / 行由内容区尺寸算）、「另有 N 项」、× 隐藏 / 恢复全部、展开 / 收起；页签切换。</item>
/// </list>
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口。
/// </summary>
public class ReactorVesselTests
{
    private static (ShellVm Shell, ReactorPageVm Page) Open(int k, int n, string query = "freeze=1")
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(p, Route.Reactor(k, n));
        var page = Assert.IsType<ReactorPageVm>(s.Page);
        return (s, page);
    }

    private static IReadOnlyDictionary<VesselCallout, CalloutContent> Contents(ReactorVesselVm v) => new Dictionary<VesselCallout, CalloutContent>
    {
        [VesselCallout.Sv] = v.Sv, [VesselCallout.Ht] = v.Ht, [VesselCallout.Tt] = v.Tt,
        [VesselCallout.M] = v.M, [VesselCallout.Pt] = v.Pt, [VesselCallout.Lv] = v.Lv,
    };

    // ———————————————————— 版面：对照样稿 ————————————————————

    private sealed record MockStage(double W, double H, string[] Cos, string[] Vbox, string[] Leads);

    private static (MockStage Stage, Dictionary<(string, double, bool), double> Widths) Vector(string name)
    {
        MockStage? st = null;
        var widths = new Dictionary<(string, double, bool), double>();
        foreach (var raw in StageMockVectors.Lines.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(name + " ", StringComparison.Ordinal)) continue;
            var rest = line[(name.Length + 1)..];
            if (rest.StartsWith("STAGE ", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(rest[6..]);
                var r = doc.RootElement;
                st = new MockStage(r.GetProperty("W").GetDouble(), r.GetProperty("H").GetDouble(),
                    r.GetProperty("cos").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                    r.GetProperty("vbox").EnumerateArray().Select(e => e.GetString()!).ToArray(),
                    r.GetProperty("leads").EnumerateArray().Select(e => e.GetString()!).ToArray());
            }
            else if (rest.StartsWith("WIDTHS ", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(rest[7..]);
                foreach (var e in doc.RootElement.EnumerateArray())
                {
                    var a = e.EnumerateArray().ToArray();
                    widths[(StageLayout.Digits0(a[0].GetString()!), a[1].GetDouble(), a[2].GetDouble() >= 600)] = a[3].GetDouble();
                }
            }
        }
        Assert.NotNull(st);
        return (st!, widths);
    }

    private static double Px(string s) => double.Parse(s.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture);

    private static string N(double v) => JsNumber.ToString(v);

    /// <summary>引线 → 样稿的 path 写法（'M' + ax + ' ' + ay + 'H' + ex.toFixed(1) + 'V' + cy + 'H' + edge；同高时 'M' + ax + ' ' + cy + 'H' + edge）。</summary>
    private static string PathOf(StageLead l) => l.Points.Count == 2
        ? "M" + N(l.Points[0].X) + " " + N(l.Points[0].Y) + "H" + N(l.Points[1].X)
        : "M" + N(l.Points[0].X) + " " + N(l.Points[0].Y) + "H" + JsNumber.ToFixed(l.Points[1].X, 1) + "V" + N(l.Points[2].Y) + "H" + N(l.Points[3].X);

    [Theory]
    [InlineData("rv11-1920", 1, 1, "freeze=1")]
    [InlineData("rv11-1600", 1, 1, "freeze=1")]
    [InlineData("rv11-1366", 1, 1, "freeze=1")]
    [InlineData("rv311-1920", 3, 11, "freeze=1&sel=3/11")]
    [InlineData("rv311-1366", 3, 11, "freeze=1&sel=3/11")]
    [InlineData("rv21-1920", 2, 1, "freeze=1")]
    [InlineData("rvs1-1920", 1, 1, "freeze=1&stress=1")]
    [InlineData("rvs1-1366", 1, 1, "freeze=1&stress=1")]
    public void StageLayout_SameAsMock_WithMockTextWidths(string name, int k, int n, string query)
    {
        var (mock, widths) = Vector(name);
        var (_, page) = Open(k, n, query);
        var v = page.Vessel;
        var asked = new List<string>();
        double Tw(string text, double size, bool bold)
        {
            if (widths.TryGetValue((text, size, bold), out var w)) return w;
            asked.Add($"「{text}」{size}/{(bold ? 600 : 400)}");
            return 0;
        }
        var levelY = VesselAnchors.LevelY(v.R!.VesselLevel);
        var r = StageLayout.Compute(mock.W, mock.H, Contents(v), levelY, Tw);
        Assert.True(asked.Count == 0, "样稿没量过的字宽：" + string.Join("、", asked));

        // 标注框：类（一侧、样式、主读数 / 实时 / 超限）与位置尺寸
        var names = new Dictionary<VesselCallout, string> { [VesselCallout.Sv] = "sv", [VesselCallout.Ht] = "ht", [VesselCallout.Tt] = "tt", [VesselCallout.M] = "m", [VesselCallout.Pt] = "pt", [VesselCallout.Lv] = "lv" };
        var c = Contents(v);
        foreach (var co in mock.Cos)
        {
            var parts = co.Split(' ');
            var key = names.Single(kv => "co-" + kv.Value == parts[0]).Key;
            var box = r.Box(key);
            Assert.NotNull(box);
            var b = box!.Value;
            string mode = "is-" + r.Mode.ToString().ToLowerInvariant();
            var cls = new List<string> { b.Side == CalloutSide.Left ? "is-l" : "is-r", mode };
            if (c[key].Main) cls.Add("is-main");
            if (c[key].Live) cls.Add("is-live");
            if (c[key].Err) cls.Add("is-err");
            var px = parts.Where(p => p.EndsWith("px", StringComparison.Ordinal)).Select(Px).ToArray();
            var mockCls = parts.Skip(1).Where(p => !p.EndsWith("px", StringComparison.Ordinal)).ToList();
            Assert.Equal(string.Join(" ", mockCls), string.Join(" ", cls));
            Assert.Equal((px[0], px[1], px[2], px[3]), (b.Left, b.Top, b.Width, b.Height));
        }
        // 釜体框
        Assert.Equal(Px(mock.Vbox[0]), r.Vx);
        Assert.Equal(Px(mock.Vbox[1]), r.Vy);
        Assert.Equal(Px(mock.Vbox[2]), Math.Round(r.Vw, 1), 3);
        Assert.Equal(Px(mock.Vbox[3]), Math.Round(r.Vh, 1), 3);
        // 引线（顺序同样稿：左列按锚点高度、再右列）
        Assert.Equal(mock.Leads, r.Leads.Select(PathOf).ToArray());
    }

    // ———————————————————— 版面：规则 ————————————————————

    /// <summary>假的量法：西文 0.6 em、中文 1 em（够区分宽窄）。</summary>
    private static double Fake(string text, double size, bool bold) =>
        text.Sum(ch => ch > 0x2e80 ? 1.0 : 0.6) * size * (bold ? 1.05 : 1);

    private static IReadOnlyDictionary<VesselCallout, CalloutContent> Rv11() => Contents(Open(1, 1).Page.Vessel);

    [Fact]
    public void Mode_ByHeight_FullBaseCompactTight()
    {
        var v = Rv11();
        // 样稿 need：完整 / 基本 = 3 × 86 + 2 × 8 + 2 × 10 = 294；紧凑 = 3 × 62 + 12 + 12 = 210；一行式 = 3 × 38 + 12 + 12 = 138
        Assert.Equal(294, StageLayout.Need(v, CalloutMode.Full));
        Assert.Equal(210, StageLayout.Need(v, CalloutMode.Compact));
        Assert.Equal(138, StageLayout.Need(v, CalloutMode.Tight));
        Assert.Equal(CalloutMode.Full, StageLayout.PickMode(700, 294, v, Fake));
        Assert.Equal(CalloutMode.Compact, StageLayout.PickMode(700, 293, v, Fake));
        Assert.Equal(CalloutMode.Tight, StageLayout.PickMode(700, 209, v, Fake));
        Assert.Equal(CalloutMode.Tight, StageLayout.PickMode(700, 100, v, Fake));   // 都放不下：一行式
    }

    [Fact]
    public void Mode_ByWidth_KeepsRoomForVessel()
    {
        var v = Rv11();
        // 高度放得下完整样式，但宽度不够给釜体留 96 px：换更紧凑的样式（样稿第 3 轮）
        double full = StageLayout.Room(0, StageLayout.ColumnWidth(v, StageLayout.LeftKeys, CalloutMode.Full, Fake),
            StageLayout.ColumnWidth(v, StageLayout.RightKeys, CalloutMode.Full, Fake), CalloutMode.Full);
        double needW = -full + VesselAnchors.VesselMinWidth;   // 完整样式刚好留出 96 px 的面板宽度
        Assert.Equal(CalloutMode.Full, StageLayout.PickMode(needW, 400, v, Fake));
        var narrower = StageLayout.PickMode(needW - 1, 400, v, Fake);
        Assert.NotEqual(CalloutMode.Full, narrower);
        double room(CalloutMode md, double w) => StageLayout.Room(w, StageLayout.ColumnWidth(v, StageLayout.LeftKeys, md, Fake),
            StageLayout.ColumnWidth(v, StageLayout.RightKeys, md, Fake), md);
        Assert.True(room(narrower, needW - 1) >= VesselAnchors.VesselMinWidth || narrower == CalloutMode.Tight);
        // 都留不出 96 px：取高度放得下的样式里釜体最宽的那个
        var tiny = StageLayout.PickMode(300, 400, v, Fake);
        Assert.Equal(StageLayout.Modes.Where(md => StageLayout.Need(v, md) <= 400).OrderByDescending(md => room(md, 300)).First(), tiny);
    }

    [Theory]
    [InlineData(661, 348)]
    [InlineData(598, 265)]
    [InlineData(640, 196)]
    [InlineData(1200, 700)]
    [InlineData(560, 420)]
    [InlineData(480, 300)]
    public void Boxes_NeverCoverVessel_WhenRoomFits_AndStayInPanel(double w, double h)
    {
        var v = Rv11();
        var r = StageLayout.Compute(w, h, v, VesselAnchors.LevelY(0.3), Fake);
        Assert.False(r.Empty);
        double room = StageLayout.Room(w, StageLayout.ColumnWidth(v, StageLayout.LeftKeys, r.Mode, Fake),
            StageLayout.ColumnWidth(v, StageLayout.RightKeys, r.Mode, Fake), r.Mode);
        foreach (var b in r.Boxes)
        {
            Assert.True(b.Left >= 0 && b.Right <= w + 0.5, $"{b.Key} 横向在面板里（{b.Left}–{b.Right} / {w}）");
            if (room >= VesselAnchors.VesselMinWidth)
                Assert.True(b.Right <= r.Vx || b.Left >= r.Vx + r.Vw, $"{b.Key} 不盖住釜体（框 {b.Left}–{b.Right}，釜体 {r.Vx}–{r.Vx + r.Vw}）");
        }
        // 同一列的框不重叠，引线从锚点连到框边
        foreach (var side in new[] { StageLayout.LeftKeys, StageLayout.RightKeys })
        {
            var col = side.Select(k => r.Box(k)!.Value).OrderBy(b => b.Top).ToList();
            for (int i = 1; i < col.Count; i++) Assert.True(col[i].Top >= col[i - 1].Bottom, $"{col[i].Key} 与 {col[i - 1].Key} 不重叠");
        }
        foreach (var l in r.Leads)
        {
            var b = r.Box(l.Key)!.Value;
            var end = l.Points[^1];
            Assert.Equal(b.Side == CalloutSide.Left ? b.Right : b.Left, end.X);
            Assert.InRange(end.Y, b.Top, b.Bottom);
            Assert.Equal(l.Dot, l.Points[0]);
        }
        Assert.Equal(VesselAnchors.ViewW / VesselAnchors.ViewH, r.Vw / r.Vh, 6);
    }

    [Fact]
    public void TooSmall_IsEmpty()
    {
        var v = Rv11();
        Assert.True(StageLayout.Compute(119, 400, v, 145, Fake).Empty);
        Assert.True(StageLayout.Compute(400, 119, v, 145, Fake).Empty);
        Assert.False(StageLayout.Compute(400, 200, v, 145, Fake).Empty);
    }

    [Fact]
    public void Inner_FollowsMockFormula()
    {
        var c = new CalloutContent("SV1", "进气阀", "开", "气体 A", "耗气 1.82 mmol", Icon: "droplet", Touch: true, HandVal: true);
        // 样稿 coInner：标签行 = tag(14/600) + 12 + 6 + label(14)；读数行 = 读数 + (3 + 单位 14 + 2) + (3 + 19)；说明行 = 18 + sub；取最宽 + 2，向上取整
        double tw(string t, double s, bool b) => t switch { "SV0" => 27.7, "进气阀" => 41.7, "开" => 25.6, "气体 A" => s == 14 ? 40.9 : 38.1, "耗气 0.00 mmol" => 99.8, _ => throw new InvalidOperationException(t) };
        Assert.Equal(Math.Ceiling(Math.Max(Math.Max(27.7 + 12 + 6 + 41.7, 25.6 + 3 + 40.9 + 2 + 3 + 19), 18 + 99.8) + 2), StageLayout.Inner(c, CalloutMode.Full, 26, tw));
        Assert.Equal(Math.Ceiling(Math.Max(27.7 + 12 + 6 + 41.7, 25.6 + 3 + 40.9 + 2 + 3 + 19) + 2), StageLayout.Inner(c, CalloutMode.Compact, 26, tw));
        Assert.Equal(Math.Ceiling(25.6 + 5 + 40.9 + 2 + 5 + 19 + 5 + 27.7 + 12 + 2), StageLayout.Inner(c, CalloutMode.Tight, 26, tw));
        Assert.Equal("耗气 0.00 mmol", StageLayout.Digits0("耗气 1.82 mmol"));
        Assert.True(c.CjkUnit);
        Assert.False(new CalloutContent("TT", "温度", "119.8", "℃", "").CjkUnit);
        Assert.True(new CalloutContent("SV1", "进气阀", "开", "H₂", "").CjkUnit);
    }

    [Fact]
    public void ChemText_SplitsSubscripts()
    {
        Assert.Equal(new[] { ("H", false), ("2", true) }, ChemText.Segments("H₂").ToArray());
        Assert.Equal(new[] { ("气体 A", false) }, ChemText.Segments("气体 A").ToArray());
        Assert.Equal(new[] { ("C", false), ("6", true), ("H", false), ("12", true) }, ChemText.Segments("C₆H₁₂").ToArray());
        Assert.Equal(13, ChemText.SubSize(14));
        Assert.Equal(20.28, ChemText.SubSize(26), 6);
    }

    // ———————————————————— 标注的文字 ————————————————————

    /// <summary>「tag|label|读数|单位|说明|图标|附加|标志」，标志 = touch / val / main / live / err 里设了的，逗号分隔。</summary>
    private static string Show(CalloutContent c)
    {
        var flags = new List<string>();
        if (c.Touch) flags.Add("touch");
        if (c.HandVal) flags.Add("val");
        if (c.Main) flags.Add("main");
        if (c.Live) flags.Add("live");
        if (c.Err) flags.Add("err");
        return $"{c.Tag}|{c.Label}|{c.Value}|{c.Unit}|{c.Sub}|{c.Icon}|{c.Extra}|" + string.Join(",", flags);
    }

    [Fact]
    public void Callouts_Rv11_Reacting()
    {
        var v = Open(1, 1).Page.Vessel;
        Assert.Equal("SV1|进气阀|开|气体 A|耗气 1.82 mmol|droplet||touch,val", Show(v.Sv));
        Assert.Equal("耗气 1.82 mmol · 触屏操作 · 排空阀 关", v.Sv.Tip);
        Assert.Equal("触屏操作", v.Sv.HandTip);
        Assert.Equal("HT|加热|38|%|保温|flame||", Show(v.Ht));
        Assert.Equal("TT|温度|119.8|℃|SP 120.0 ℃|||main,live", Show(v.Tt));
        Assert.Equal("M|搅拌|300|rpm|全釜共用|||live", Show(v.M));
        Assert.Equal("PT|压力|0.349|MPa|50.6 psi|||main,live", Show(v.Pt));
        Assert.Equal("LV|液位|15|mL|触屏操作|hand||touch", Show(v.Lv));
        Assert.False(v.Stale);
        Assert.True(v.HasBadge && v.ShowBadge);
        Assert.Equal((StateKey.React, "反应中", null), (v.BadgeKey, v.BadgeText, v.BadgeTip));
        Assert.Equal("50 mL 釜 · 线描釜体与测点", v.VesselTab.Tip);
        // 紧凑 / 一行式的整框提示（样稿 coHTML 的 title）
        Assert.Equal("进气阀 · 耗气 1.82 mmol · 触屏操作 · 排空阀 关", v.Sv.BoxTip(CalloutMode.Compact));
        Assert.Equal("压力 0.349 MPa · 50.6 psi", v.Pt.BoxTip(CalloutMode.Tight));
        Assert.Null(v.Pt.BoxTip(CalloutMode.Full));
    }

    [Fact]
    public void Callouts_Rv21_IndependentStir_H2()
    {
        var v = Open(2, 1).Page.Vessel;
        Assert.Equal("SV1|进气阀|开|H₂|耗气 12.60 mmol|droplet||touch,val", Show(v.Sv));
        Assert.Equal("M|搅拌|800|rpm|本釜独立|||live", Show(v.M));
        Assert.Equal("PT|压力|5.20|MPa|754.2 psi|||main,live", Show(v.Pt));
        Assert.Equal("TT|温度|150.0|℃|SP 150.0 ℃|||main,live", Show(v.Tt));
        Assert.Equal("LV|液位|50|mL|触屏操作|hand||touch", Show(v.Lv));
    }

    [Fact]
    public void Callouts_Rv311_OverPressure_LimitLine_NoBadge()
    {
        var (_, page) = Open(3, 11, "freeze=1&sel=3/11");
        var v = page.Vessel;
        Assert.Equal("SV11|进气阀|关||触屏操作|hand||touch", Show(v.Sv));
        Assert.Equal("PT|压力|3.120|MPa|452.5 psi||上限 3.000 MPa|main,err", Show(v.Pt));
        Assert.Equal("HT|加热|34|%|保温|flame||", Show(v.Ht));
        Assert.False(v.HasBadge);   // 有错误报警：顶部报警条已经写了，标题栏不再写状态
        Assert.False(v.ShowBadge);
        // 临时的状态条：超压报警（程序在第 2/4 步暂停）
        var a = Assert.Single(page.Alerts);
        Assert.Equal(("超压报警", "RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11 · 程序在第 2/4 步暂停", "10:09:42"), (a.Title, a.Text, a.Time));
        Assert.True(a.IsErr);
    }

    [Fact]
    public void Callouts_PtErr_OnlyFromActivePressureAlarm_NotFromChannelState()
    {
        var (s, page) = Open(3, 11, "freeze=1&sel=3/11");
        var d = page.D!;
        var r = d.Reactor(11)!;
        Assert.Equal(StateKey.Alarm, r.Own);
        // 报警清掉（alarm=3:0）：通道状态可以还是报警，压力标注不再写「上限」、也不算超限
        s.ApplyDemoParams(DemoParams.Parse("freeze=1&sel=3/11&alarm=3:0") with { Freeze = true });
        page.Refresh(s.Fleet);
        Assert.DoesNotContain(page.D!.AlarmList, x => x.Ch == 11 && x.IsErr);
        Assert.False(page.Vessel.Pt.Err);
        Assert.DoesNotContain("上限", page.Vessel.Pt.Extra);
        Assert.True(page.Vessel.HasBadge);
    }

    [Fact]
    public void Callouts_Offline_BeforeLossWords_NoFlame_Stale()
    {
        var v = Open(1, 1, "freeze=1&offline=1").Page.Vessel;
        Assert.True(v.Stale);
        Assert.Equal("HT|加热|38|%|断线前保温|||", Show(v.Ht));
        Assert.Equal("TT|温度|119.8|℃|SP 120.0 ℃|||main", Show(v.Tt));
        Assert.Equal("M|搅拌|300|rpm|全釜共用|||", Show(v.M));
        Assert.Equal("PT|压力|0.349|MPa|50.6 psi|||main", Show(v.Pt));
        Assert.Equal((StateKey.Offline, "离线", "断线前：反应中"), (v.BadgeKey, v.BadgeText, v.BadgeTip));
    }

    [Fact]
    public void Callouts_Estop_Stopped()
    {
        var v = Open(1, 1, "freeze=1&estop=1").Page.Vessel;
        Assert.Equal("SV1|进气阀|关||耗气 1.82 mmol|droplet||touch,val", Show(v.Sv));
        Assert.Equal("HT|加热|0|%|加热关闭|||", Show(v.Ht));
        Assert.Equal("TT|温度|119.8|℃|无设定值|||main,live", Show(v.Tt));
        Assert.Equal("M|搅拌|0|rpm|已停止|||", Show(v.M));
        Assert.Equal((StateKey.Stopped, "已停止"), (v.BadgeKey, v.BadgeText));
    }

    [Fact]
    public void Callouts_NeverConnected_Dashes()
    {
        var s = new ShellVm(FleetVm.CreateDemo(DemoParams.Parse("freeze=1")));
        s.Toasts.Frozen = true;
        s.Fleet.DemoData!.AddDevice(new DemoNewDevice(Host: "192.168.1.50", Port: 8080, Token: "x", Alias: "5号机", AutoConnect: true, ModelKey: "PR8"));
        s.Fleet.RefreshNow();
        var k = s.Fleet.Devices.Last().K;
        s.Navigate(Route.Reactor(k, 1));
        var v = Assert.IsType<ReactorPageVm>(s.Page).Vessel;
        Assert.Equal(Fmt.Dash, v.Tt.Value);
        Assert.Equal(Fmt.Dash, v.Ht.Sub);   // 从未连上：「—」（不写「断线前」）
        Assert.Equal(Fmt.Dash, v.Pt.Sub);
        Assert.Equal("已停止", v.M.Sub);
        Assert.Equal((StateKey.Offline, "未连接"), (v.BadgeKey, v.BadgeText));
        Assert.True(v.NeverConnected);
    }

    // ———————————————————— 属性查看器 ————————————————————

    [Fact]
    public void Props_Rv11_TextsAndEdges()
    {
        var v = Open(1, 1).Page.Vessel;
        var lines = v.Props.Select(p => $"{p.Key}|{p.Path}|{p.Icon}|{p.Title}|{p.Sub}|{p.Value}|{p.Unit}|{p.Unit2}|{(p.Edge ? "edge" : "")}").ToArray();
        Assert.Equal(new[]
        {
            "pv|/1号机/RV1/模拟输入|thermo|反应器温度|温度 PV|119.8|℃||edge",
            "p|/1号机/RV1/模拟输入|gauge|压力传感器|压力|0.349|MPa|50.6 psi|edge",
            "sp|/1号机/RV1/设备/加热器|flame|加热器|温度设定值 SP|120.0|℃||",
            "out|/1号机/RV1/设备/加热器|flame|加热器|加热输出|38|%||",
            "stir|/1号机/全局/搅拌电机|fan|搅拌（全局）|RV1–RV8 共用|300|rpm||edge",
            "gas|/1号机/RV1/计算|droplet|耗气量|自进气起累计|1.82|mmol||",
            "valve|/1号机/RV1/设备/阀门|valve|进气阀 SV1|排空阀 关|开||触屏操作|",
            "vol|/1号机/RV1/设备/反应釜|flask|溶液体积|触屏操作|15|mL|≤ 20 mL|",
        }, lines);
        Assert.All(v.Props, p => Assert.False(p.Stale));
        var off = Open(1, 1, "freeze=1&offline=1").Page.Vessel;
        Assert.All(off.Props, p => Assert.True(p.Stale && !p.Edge));
        var pr4 = Open(2, 1).Page.Vessel;
        Assert.Equal("/2号机/RV1/设备/搅拌电机|搅拌（本釜）|转速", pr4.Props[4].Path + "|" + pr4.Props[4].Title + "|" + pr4.Props[4].Sub);
    }

    [Fact]
    public void Props_FitRows_More_Hide_Restore_Open()
    {
        var (s, page) = Open(1, 1);
        var v = page.Vessel;
        // 1920：内容区 661 × 348 → 2 列 × 4 行 = 8 张全显示，没有「另有」
        v.SetPropsViewport(661, 348);
        Assert.Equal((2, 4, 8, false), (v.PropCols, v.PropRows, v.ShownProps.Count, v.HasMore));
        // 1600：598 × 265 → 2 × 3：「另有 2 项」；1366：640 × 196 → 2 × 2：「另有 4 项」
        v.SetPropsViewport(598, 265);
        Assert.Equal((2, 3, 6, true, "另有 2 项", "down"), (v.PropCols, v.PropRows, v.ShownProps.Count, v.HasMore, v.MoreText, v.MoreIcon));
        v.SetPropsViewport(640, 196);
        Assert.Equal((4, "另有 4 项"), (v.ShownProps.Count, v.MoreText));
        Assert.Equal(new[] { "pv", "p", "sp", "out" }, v.ShownProps.Select(p => p.Key));
        // × 隐藏一张：后面的补上来
        var first = v.ShownProps[0];
        v.Hide("p");
        Assert.Equal(new[] { "pv", "sp", "out", "stir" }, v.ShownProps.Select(p => p.Key));
        Assert.Same(first, v.ShownProps[0]);   // 身份不变
        Assert.Equal("另有 3 项", v.MoreText);
        // 展开：全部（面板内滚动）、「收起 ▴」
        v.ToggleMoreCommand.Execute(null);
        Assert.Equal((7, "收起", "up"), (v.ShownProps.Count, v.MoreText, v.MoreIcon));
        v.ToggleMoreCommand.Execute(null);
        Assert.Equal(4, v.ShownProps.Count);
        // 恢复全部：toast「已恢复全部属性」
        v.RestoreCommand.Execute(null);
        Assert.Empty(v.Hidden);
        Assert.Equal(new[] { "pv", "p", "sp", "out" }, v.ShownProps.Select(p => p.Key));
        Assert.Contains(s.Toasts.Items, t => t.Text == "已恢复全部属性");
        // 列数：宽度规则（≥ 233 的卡片 + 14 的间距，内边距 28）
        Assert.Equal(1, ReactorVesselVm.ColumnsFor(400));
        Assert.Equal(2, ReactorVesselVm.ColumnsFor(508));
        Assert.Equal(3, ReactorVesselVm.ColumnsFor(755));
        Assert.Equal(4, ReactorVesselVm.ColumnsFor(2000));
        // 页签藏着（高 0）时不改
        v.SetPropsViewport(661, 0);
        Assert.Equal(2, v.PropRows);
    }

    [Fact]
    public void Tabs_SwitchKeepsPanel_ResetView()
    {
        var v = Open(1, 1).Page.Vessel;
        int switched = 0;
        v.TabSwitched += (_, _) => switched++;
        Assert.True(v.IsVesselTab && v.ShowBadge);
        v.Tab = ReactorVesselVm.PropsTabValue;
        Assert.True(v.IsPropsTab && !v.ShowBadge);
        Assert.Equal(1, switched);
        v.Tab = "nonsense";
        Assert.Equal(ReactorVesselVm.VesselTabValue, v.Tab);
        Assert.Equal(new[] { "釜体", "属性查看器" }, v.TabOptions.Select(o => o.Label));
        Assert.Equal("全部属性：温度、压力、加热、搅拌、耗气量、阀门、溶液体积", v.PropsTab.Tip);
        v.SetPropsViewport(640, 196);
        v.Hide("pv");
        v.ToggleMoreCommand.Execute(null);
        v.ResetView(ReactorVesselVm.PropsTabValue);
        Assert.True(v.IsPropsTab && !v.PropsOpen && v.Hidden.Count == 0 && v.ShownProps[0].Key == "pv");
    }

    [Fact]
    public void PageVm_GetsShell_AndRefreshesVessel()
    {
        var (s, page) = Open(1, 3);
        Assert.Same(s, page.Shell);
        Assert.Equal("1号机 › RV3", page.Crumb);
        Assert.Equal(3, page.Vessel.N);
        Assert.NotNull(page.Vessel.R);
        Assert.Same(page.D!.Reactor(3), page.Vessel.R);
        // 提醒（升温较慢）：一条中性的状态条
        var a = Assert.Single(page.Alerts);
        Assert.Equal(("提醒", ReactorAlertTone.Warn), (a.Title, a.Tone));
        Assert.Matches(new Regex("^RV3 升温较慢"), a.Text);
    }
}
