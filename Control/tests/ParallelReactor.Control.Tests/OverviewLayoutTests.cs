using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Models;
using Xunit;
using static ParallelReactor.Control.Tests.PageVectorData;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Layout.OverviewLayout 对照样稿：MockVectors/PageMockVectors.cs 的 ov-* 场景（样稿在 WebKit 里实际渲染的总览，1920 / 1600 / 1366 ×
/// 3 台 / 4 台 / stress=1、2 / 资源管理器收起 / 时间轴收起 / 8 路装满 / 急停 + 断线 …），逐个分区比较网格、卡片位置与尺寸、卡片内容宽、
/// 每张卡片按内容宽的取舍（釜体、目标、单位、程序名、标题 / 程序行换行、读数字号阶梯、宽卡第三行），以及整页的报警条 / 温度趋势判断。
/// 另有 3 种型号 × 3 种宽度的直接断言（可读的版面事实）和不连续装机等样稿碰不到的情况。
/// </summary>
public class OverviewLayoutTests
{
    public static IEnumerable<object[]> Scenarios() => Names("overview");

    private const double Px = 0.51;   // 样稿量到的是渲染后的数（WebKit 的布局单位 1/64 px；位置是整数时四舍五入）

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void MatchesMock(string name)
    {
        var v = Get("overview", name);
        var p = v.GetProperty("probe");
        double w = D(p.GetProperty("root")[0]), h = D(p.GetProperty("root")[1]);
        var devs = p.GetProperty("devs").EnumerateArray().ToArray();
        var bad = new List<string>();
        void Check(bool ok, string what) { if (!ok) bad.Add(what); }

        var sections = new List<OverviewSection>();
        for (int i = 0; i < devs.Length; i++)
        {
            var dv = devs[i];
            var (model, online, ever, estop) = Device(dv.GetProperty("dev"));
            var items = dv.GetProperty("items").EnumerateArray().ToArray();
            bool anyProgram = items.Any(it => it[0].GetString() == "C" && !it[7].GetProperty("none").GetBoolean());
            var s = OverviewLayout.Section(model, w, ever, note: ever && (!online || estop), last: i == devs.Length - 1, anyProgram: anyProgram);
            sections.Add(s);
            string tag = name + " 设备 " + dv.GetProperty("dev").GetProperty("k").GetInt32() + "（" + model.Key + "）";
            string cls = dv.GetProperty("cls").GetString()!;
            Check(cls.Contains("overview-dev--" + s.Kind.ToString().ToLowerInvariant()), tag + "：卡片种类 " + s.Kind + " ≠ " + cls);
            if (dv.GetProperty("gridH").ValueKind == JsonValueKind.Number)
                Check(Math.Abs(D(dv.GetProperty("gridH")) - s.GridHeight) <= Px, tag + $"：网格高 {s.GridHeight} ≠ {D(dv.GetProperty("gridH"))}");
            Check((dv.GetProperty("note").GetInt32() > 0) == s.Note, tag + "：说明条 " + s.Note);
            Check(items.Length == s.Tiles.Count, tag + $"：块数 {s.Tiles.Count} ≠ {items.Length}");
            for (int t = 0; t < Math.Min(items.Length, s.Tiles.Count); t++)
            {
                var it = items[t];
                var tile = s.Tiles[t];
                string tt = tag + " 第 " + (t + 1) + " 块 " + tile.Label;
                bool isCard = it[0].GetString() == "C";
                Check(isCard == tile.IsCard, tt + "：卡片 / 未安装不符");
                Check(Math.Abs(D(it[1]) - tile.X) <= Px && Math.Abs(D(it[2]) - tile.Y) <= Px, tt + $"：位置 ({tile.X}, {tile.Y}) ≠ ({D(it[1])}, {D(it[2])})");
                Check(Math.Abs(D(it[3]) - tile.Width) <= Px && Math.Abs(D(it[4]) - tile.Height) <= Px, tt + $"：尺寸 {tile.Width}×{tile.Height} ≠ {D(it[3])}×{D(it[4])}");
                if (!isCard)
                {
                    Check(it[6].GetString() == tile.Label, tt + "：标签 " + it[6].GetString());
                    continue;
                }
                Check(Math.Abs(D(it[5]) - s.CardContentWidth) <= 0.05, tt + $"：内容宽 {s.CardContentWidth} ≠ {D(it[5])}");
                var c = s.Card;
                var o = it[7];
                var vs = o.GetProperty("v");
                if (vs.ValueKind == JsonValueKind.Array) Check(c.Vessel && D(vs[0]) == c.VesselWidth && D(vs[1]) == c.VesselHeight, tt + $"：釜体 {c.VesselWidth}×{c.VesselHeight} ≠ {vs}");
                else Check(!c.Vessel, tt + "：釜体应不画");
                foreach (var key in new[] { "tgt", "ptg" })
                {
                    var g = o.GetProperty(key);
                    if (g.ValueKind != JsonValueKind.Array) continue;
                    Check(g[0].GetBoolean() == c.Target, tt + $"：{key} 显示 {c.Target} ≠ {g[0]}");
                    if (g[1].ValueKind is JsonValueKind.True or JsonValueKind.False) Check(g[1].GetBoolean() == c.TargetUnit, tt + $"：{key} 单位 {c.TargetUnit} ≠ {g[1]}");
                }
                var len = o.GetProperty("len");
                int tl = Len(len[0]) ?? 0, pl = Len(len[1]) ?? 0;
                var tf = c.Temp(tl);
                var pf = c.Pressure(pl);
                Check(D(o.GetProperty("t")[0]) == tf.Size && o.GetProperty("t")[1].GetBoolean() == tf.Unit, tt + $"：温度 {tf} ≠ {o.GetProperty("t")}（len {tl}）");
                Check(D(o.GetProperty("p")[0]) == pf.Size && o.GetProperty("p")[1].GetBoolean() == pf.Unit, tt + $"：压力 {pf} ≠ {o.GetProperty("p")}（len {pl}）");
                bool none = o.GetProperty("none").GetBoolean();
                Check(o.GetProperty("pname").GetBoolean() == (none || c.ProgramName), tt + $"：程序名 {c.ProgramName} ≠ {o.GetProperty("pname")}");
                Check(o.GetProperty("stWrap").GetBoolean() == c.HeadWrap, tt + $"：状态字换行 {c.HeadWrap}");
                double prog = D(o.GetProperty("prog"));
                bool barfit = o.GetProperty("barfit").GetBoolean();
                double expProg = c.Kind != CardKind.Compact ? 20 : !c.ProgramWrap ? 18 : none ? 20 : barfit ? 10 : 34;
                Check(prog == expProg, tt + $"：程序行高 {expProg} ≠ {prog}");
                var aux = o.GetProperty("aux");
                if (aux.ValueKind == JsonValueKind.Array)
                {
                    Check(c.Kind == CardKind.Wide, tt + "：只有宽卡有第三行");
                    var a = c.Aux(Len(aux[3]) ?? 0);
                    Check(D(aux[0]) == a.ValueFont && aux[1].GetString() == a.Gap + "px" && aux[2].GetBoolean() == a.RpmUnit, tt + $"：第三行 {a} ≠ {aux}");
                }
            }
        }
        var page = OverviewLayout.Page(w, h, sections, p.GetProperty("strip").GetInt32() > 0);
        // 分区高按整页算（有温度趋势时最后一个分区的底边回来）
        for (int i = 0; i < devs.Length; i++)
            Check(Math.Abs(D(devs[i].GetProperty("h")) - page.Sections[i].Height) <= Px, name + " 第 " + (i + 1) + $" 个分区：高 {page.Sections[i].Height} ≠ {D(devs[i].GetProperty("h"))}");
        Check(page.AlarmStrip == p.GetProperty("bar").GetBoolean(), name + $"：报警条 {page.AlarmStrip}（已用 {page.Used}，高 {h}）");
        Check(page.Trend == p.GetProperty("trend").GetBoolean(), name + $"：温度趋势 {page.Trend}");
        Check(Math.Abs(D(p.GetProperty("scroll")[1]) - page.ScrollHeight) <= Px, name + $"：滚动区高 {page.ScrollHeight}");
        Check(Math.Abs(D(p.GetProperty("scroll")[2]) - Math.Max(page.ScrollHeight, page.Used)) <= 1.01, name + $"：内容高 {page.Used} ≠ {p.GetProperty("scroll")[2]}");
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(40)));
    }

    [Fact]
    public void VectorsCoverRequiredScenarios()
    {
        var names = Names("overview").Select(o => (string)o[0]).ToHashSet();
        foreach (int w in new[] { 1920, 1600, 1366 })
            foreach (var i in new[] { 0, 1, 2, 3, 6, 7, 8 })   // 3 台 / 4 台 / stress=1 / stress=2 / 资源管理器收起 / 时间轴收起 / 8 路装满
                Assert.Contains("ov-" + i + "-" + w, names);
    }

    // ———————————————————— 3 种型号 × 3 种宽度（工作区宽：1920 = 1226、1600 = 936、1366 = 1002（资源管理器收起）） ————————————————————

    private static ModelProfile M(string key) => ModelProfiles.Embedded.Get(key)!;

    [Theory]
    // 型号、工作区宽 → 卡片种类、列数（含未安装窄列）、行数、卡片外框宽、内容宽、卡片高、釜体、目标、程序名
    [InlineData("PR8-4", 1226, CardKind.Row, 5, 1, 264.5, 234.5, 144, 36, true, true)]
    [InlineData("PR8-4", 936, CardKind.Row, 5, 1, 192, 162, 144, 22, false, false)]
    [InlineData("PR8-4", 1002, CardKind.Row, 5, 1, 208.5, 178.5, 144, 22, false, false)]
    [InlineData("PR4", 1226, CardKind.Wide, 4, 1, 291, 261, 168, 44, true, true)]
    [InlineData("PR4", 936, CardKind.Wide, 4, 1, 218.5, 188.5, 168, 0, true, false)]
    [InlineData("PR4", 1002, CardKind.Wide, 4, 1, 235, 205, 168, 0, true, false)]
    [InlineData("PR16", 1226, CardKind.Compact, 8, 2, 140.5, 120.5, 120, 26, false, false)]
    [InlineData("PR16", 936, CardKind.Compact, 8, 2, 104.25, 84.25, 156, 0, false, false)]
    [InlineData("PR16", 1002, CardKind.Compact, 8, 2, 112.5, 92.5, 156, 0, false, false)]
    [InlineData("PR8", 1226, CardKind.Compact, 8, 1, 140.5, 120.5, 120, 26, false, false)]
    public void ThreeModels_ThreeWidths(string key, double width, CardKind kind, int cols, int rows, double cardW, double contentW, double cardH, double vessel, bool target, bool name)
    {
        var s = OverviewLayout.Section(M(key), width);
        Assert.Equal(kind, s.Kind);
        Assert.Equal(cols, s.Columns);
        Assert.Equal(rows, s.Rows);
        Assert.Equal(cardW, s.CardWidth, 3);
        Assert.Equal(contentW, s.CardContentWidth, 3);
        Assert.Equal(cardH, s.CardHeight, 3);
        Assert.Equal(vessel, s.Card.VesselWidth);
        Assert.Equal(target, s.Card.Target);
        Assert.Equal(name, s.Card.ProgramName);
    }

    [Fact]
    public void Untile_MergesUninstalledRun_AsFixedColumn()
    {
        var s = OverviewLayout.Section(M("PR8-4"), 1226);
        var un = s.Tiles.Single(t => !t.IsCard);
        Assert.Equal("RV5–RV8", un.Label);
        Assert.Equal(OverviewLayout.UntileWidth, un.Width);
        Assert.Equal(new[] { false, false, false, false, true }, s.Tracks.Select(t => t.Fixed).ToArray());
        Assert.Equal(1226 - 16 - 96, un.X, 3);
    }

    [Fact]
    public void PartialInstall_NonContiguous_UntilesInDomOrder()
    {
        // 真机「HT-PR8 · 现场」装 RV1–2、RV5–6：卡片 1fr、每段未安装 96 px，按通道顺序（样稿 bodyHTML 的 cols 列表）
        var dev = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!;
        dev = dev with
        {
            Channels = Enumerable.Range(1, 8).Select(n => new ChannelInstalledDto(n, n is 1 or 2 or 5 or 6)).ToArray(),
            InstalledChannels = new[] { 1, 2, 5, 6 },
        };
        var m = ModelProfiles.Embedded.Resolve(dev);
        var s = OverviewLayout.Section(m, 1226);
        Assert.Equal(CardKind.Row, s.Kind);
        Assert.Equal(new[] { "RV1", "RV2", "RV3–RV4", "RV5", "RV6", "RV7–RV8" }, s.Tiles.Select(t => t.Label).ToArray());
        Assert.Equal(new[] { false, false, true, false, false, true }, s.Tracks.Select(t => t.Fixed).ToArray());
        Assert.Equal((1226 - 32 - 50 - 2 * 96) / 4.0, s.CardWidth, 6);
        // 紧凑卡（8 路装满以外的 16 路缺几路）：未安装段不跨过 8 列的边界，跨它自己的列数
        var dev16 = dev with
        {
            ChannelCount = 16,
            Channels = Enumerable.Range(1, 16).Select(n => new ChannelInstalledDto(n, n <= 6 || n >= 11)).ToArray(),
            InstalledChannels = Enumerable.Range(1, 16).Where(n => n <= 6 || n >= 11).ToArray(),
        };
        var m16 = ModelProfiles.Embedded.Resolve(dev16);
        var c = OverviewLayout.Section(m16, 1226);
        Assert.Equal(CardKind.Compact, c.Kind);
        var runs = c.Tiles.Where(t => !t.IsCard).ToArray();
        Assert.Equal(new[] { "RV7–RV8", "RV9–RV10" }, runs.Select(t => t.Label).ToArray());
        Assert.Equal((6, 0, 2), (runs[0].Column, runs[0].Row, runs[0].Span));
        Assert.Equal((0, 1, 2), (runs[1].Column, runs[1].Row, runs[1].Span));
        Assert.Equal(2, c.Rows);
    }

    [Fact]
    public void NeverConnected_OnlyNote()
    {
        var s = OverviewLayout.Section(M("PR16"), 1226, everOnline: false);
        Assert.True(s.NeverConnected);
        Assert.Empty(s.Tiles);
        Assert.Equal(42 + 12 + 44 + 12 + 1, s.Height);
    }

    [Fact]
    public void OfflineOrEstop_AddsNote()
    {
        var a = OverviewLayout.Section(M("PR4"), 1226);
        var b = OverviewLayout.Section(M("PR4"), 1226, note: true);
        Assert.Equal(a.Height + 12 + 44, b.Height);
        Assert.Equal(a.Height - 1, OverviewLayout.Section(M("PR4"), 1226, last: true).Height);
    }

    // ———————————————————— 整页：报警条 / 温度趋势 ————————————————————

    [Fact]
    public void Page_ThreeDevices_1920_NoStrip_1366_Strip()
    {
        var three = new[] { (M("PR8-4"), true, false), (M("PR4"), true, false), (M("PR16"), true, false) };
        var p1920 = OverviewLayout.Compute(1226, 752, three, hasStripItems: true);
        Assert.Equal(203 + 227 + 308, p1920.Used);
        Assert.False(p1920.AlarmStrip);                 // 1920×1080：3 台一屏放下
        Assert.False(p1920.Trend);                      // 剩 14 px
        var p1366 = OverviewLayout.Compute(1002, 460, three, hasStripItems: true);
        Assert.True(p1366.AlarmStrip);                  // 1366×768：3号机 · RV11 超压在折叠线以上可见
        Assert.Equal(460 - 46, p1366.ScrollHeight);
        Assert.False(OverviewLayout.Compute(1002, 460, three, hasStripItems: false).AlarmStrip);   // 没有未确认的错误报警：不出报警条
    }

    [Fact]
    public void Page_FourDevices_1920_Strip()
    {
        var four = new[] { (M("PR8-4"), true, false), (M("PR4"), true, false), (M("PR16"), true, false), (M("PR8"), true, true) };
        var p = OverviewLayout.Compute(1226, 752, four, hasStripItems: true);
        Assert.Equal(203 + 227 + 309 + 234, p.Used);
        Assert.True(p.AlarmStrip);
        Assert.Equal(706, p.ScrollHeight);
    }

    [Fact]
    public void Page_OneDevice_TrendWhenRoomBelow()
    {
        var one = new[] { (M("PR8-4"), true, false) };
        var p = OverviewLayout.Compute(1002, 460, one, hasStripItems: false);
        Assert.True(p.Trend);                           // 460 − 202 ≥ 200
        Assert.False(p.Sections[^1].Last);              // 趋势面板在下面：最后一个分区的底边回来
        Assert.Equal(203, p.Used);
        var noChart = OverviewLayout.Compute(1002, 460, one, hasStripItems: false, drawsTrend: false);   // M4：还没有曲线控件
        Assert.True(noChart.Trend);
        Assert.True(noChart.Sections[^1].Last);
        Assert.Equal(202, noChart.Used);
        Assert.False(OverviewLayout.Compute(1002, 401, one, hasStripItems: false).Trend);
        Assert.True(OverviewLayout.ShowTrend(260, 460, 1, false));
        Assert.False(OverviewLayout.ShowTrend(261, 460, 1, false));
        Assert.False(OverviewLayout.ShowTrend(0, 460, 0, false));
        Assert.False(OverviewLayout.ShowAlarmStrip(461, 460, true));   // 超出 1 px 以内不算
        Assert.True(OverviewLayout.ShowAlarmStrip(461.5, 460, true));
    }

    [Fact]
    public void Page_NarrowWorkspace_HidesSessionThenModel()
    {
        var one = new[] { (M("PR4"), true, false) };
        Assert.False(OverviewLayout.Compute(901, 600, one, false).HideSession);
        Assert.True(OverviewLayout.Compute(900, 600, one, false).HideSession);
        Assert.False(OverviewLayout.Compute(821, 600, one, false).HideModel);
        Assert.True(OverviewLayout.Compute(820, 600, one, false).HideModel);
    }

    // ———————————————————— 卡片按内容宽的阈值（css/overview.css:157-220） ————————————————————

    [Theory]
    [InlineData(CardKind.Row, 252, 36, true, true, true)]
    [InlineData(CardKind.Row, 239, 36, true, false, true)]
    [InlineData(CardKind.Row, 200, 36, true, false, true)]
    [InlineData(CardKind.Row, 199, 22, false, false, true)]
    [InlineData(CardKind.Row, 189, 22, false, false, false)]
    [InlineData(CardKind.Row, 149, 0, false, false, false)]
    [InlineData(CardKind.Wide, 252, 44, true, true, true)]
    [InlineData(CardKind.Wide, 251, 0, true, true, true)]
    [InlineData(CardKind.Wide, 214, 0, true, false, false)]
    [InlineData(CardKind.Compact, 120, 26, false, false, false)]
    [InlineData(CardKind.Compact, 119, 0, false, false, false)]
    public void Card_Thresholds(CardKind kind, double w, double vessel, bool target, bool unit, bool name)
    {
        var c = OverviewLayout.Card(kind, w);
        Assert.Equal(vessel, c.VesselWidth);
        Assert.Equal(target, c.Target);
        Assert.Equal(unit && target, c.Target && c.TargetUnit);
        Assert.Equal(name, c.ProgramName);
    }

    [Fact]
    public void Compact_LongReadingLadder()
    {
        // 紧凑卡 4 位温度「199.9 ℃」：24 → 22 → 20 → 去单位；≥ 120（画釜体）时 22 / 20
        Assert.Equal(new ReadingFont(24, 28), OverviewLayout.Card(CardKind.Compact, 100).Temp(5));
        Assert.Equal(new ReadingFont(22, 28), OverviewLayout.Card(CardKind.Compact, 94).Temp(5));
        Assert.Equal(new ReadingFont(20, 28), OverviewLayout.Card(CardKind.Compact, 89).Temp(5));
        Assert.Equal(new ReadingFont(20, 28, false), OverviewLayout.Card(CardKind.Compact, 83).Temp(5));
        Assert.Equal(new ReadingFont(20, 28), OverviewLayout.Card(CardKind.Compact, 120.5).Temp(5));
        Assert.Equal(new ReadingFont(22, 28), OverviewLayout.Card(CardKind.Compact, 125).Temp(5));
        Assert.Equal(new ReadingFont(24, 28), OverviewLayout.Card(CardKind.Compact, 130).Temp(5));
        Assert.Equal(new ReadingFont(24, 28), OverviewLayout.Card(CardKind.Compact, 84).Temp(4));   // 短读数不变
        // 压力（总是 4 位数字）
        Assert.Equal(new ReadingFont(17, 22), OverviewLayout.Card(CardKind.Compact, 120.5).Pressure(5));
        Assert.Equal(new ReadingFont(16, 22), OverviewLayout.Card(CardKind.Compact, 84.25).Pressure(5));
        Assert.Equal(new ReadingFont(16, 22, false), OverviewLayout.Card(CardKind.Compact, 83).Pressure(5));
        // 一排 / 宽卡没有阶梯
        Assert.Equal(new ReadingFont(26, 32), OverviewLayout.Card(CardKind.Row, 150).Temp(5));
        Assert.Equal(new ReadingFont(20, 26), OverviewLayout.Card(CardKind.Wide, 150).Pressure(5));
    }

    [Fact]
    public void Wide_AuxRow()
    {
        Assert.Equal(new OverviewAux(18, 16, 2, true), OverviewLayout.Card(CardKind.Wide, 261).Aux(5));
        Assert.Equal(new OverviewAux(16, 10, 0, true), OverviewLayout.Card(CardKind.Wide, 261).Aux(7));   // 1500 rpm + 100 %
        Assert.Equal(new OverviewAux(16, 10, 0, true), OverviewLayout.Card(CardKind.Wide, 214).Aux(5));
        Assert.Equal(new OverviewAux(16, 10, 0, false), OverviewLayout.Card(CardKind.Wide, 188.5).Aux(7));
    }
}
