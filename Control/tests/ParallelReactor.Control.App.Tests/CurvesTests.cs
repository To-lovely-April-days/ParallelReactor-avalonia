using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Controls.Chart;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.Control.Theme;
using ParallelReactor.Control.Views.Pages.Gallery;
using Xunit;
using CoreGeometry = ParallelReactor.Control.Core.Chart.ChartGeometry;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 曲线（PORT-PLAN §5 M5「曲线」，样稿 js/ui.js lineChart / reactorChartOpts、js/pages/reactor.js curvesPanel、js/pages/overview.js 温度趋势、placeholder.js 陈列）：
/// <list type="bullet">
/// <item>画出来的东西逐元素对照样稿：同一个地址的演示数据经视图模型（<see cref="ReactorCurvesVm"/> / <see cref="OverviewTrendVm"/> / <see cref="GalleryCharts"/>）给出曲线输入，
///   按样稿在 WebKit 里量到的容器尺寸与文字宽度排版（Core 的 ChartLayout），再由 <see cref="ChartScene"/>（TimeSeriesChart 的绘制列表）拼回 SVG —— 与样稿自己画出来的 SVG
///   （<see cref="CurvesMockVectors"/>：反应釜页 11 种状态 × 三个尺寸、总览温度趋势、组件陈列的三张）逐元素相同：刻度、网格、坐标框、轴标题、上限线与文字的位置、标记、
///   曲线的路径（摘要）与先后（虚线在实线下面）、末端圆点、空数据文字。图例按面板宽度的取舍同样稿。</item>
/// <item>数据：演示数据经 Core 的环形缓冲（DeviceHistory.SyncDemo）后与样稿的 series（ReactorChartOptions.FromDemo）逐点相同；分段 10 / 30 / 60；数据没变时不重建；
///   未安装 / 设备移除时没有曲线。</item>
/// <item>温度趋势：只在样稿会画时显示（一台设备、下面剩余 ≥ 200 px），只画选中的设备、最多 4 条，图例与「显示 n/m」。</item>
/// <item>实时：每一帧进环形缓冲（10 s 一格）、断线插断开标记、HMI 重启清空。</item>
/// </list>
/// 只用视图模型与 Core（不创建窗口、不启动 Avalonia）。
/// </summary>
public class CurvesTests
{
    // ———————————————————— 样稿向量 ————————————————————

    private static readonly Lazy<List<JsonElement>> Vectors = new(() =>
        CurvesMockVectors.Lines.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonDocument.Parse(l).RootElement).ToList());

    private static JsonElement Vector(string name, int w) =>
        Vectors.Value.First(v => v.GetProperty("name").GetString() == name && v.GetProperty("w").GetInt32() == w);

    public static IEnumerable<object[]> CurveScenes() =>
        Vectors.Value.Where(v => v.TryGetProperty("curves", out _)).Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetProperty("w").GetInt32() });

    public static IEnumerable<object[]> TrendScenes() =>
        Vectors.Value.Where(v => v.TryGetProperty("trend", out _)).Select(v => new object[] { v.GetProperty("name").GetString()!, v.GetProperty("w").GetInt32() });

    /// <summary>样稿地址 → （路径, 演示参数）：bare / freeze 之外的参数原样交给 DemoParams（同截图）。</summary>
    private static (string Path, string Query) Split(string route)
    {
        int q = route.IndexOf('?');
        return q < 0 ? (route.Trim('/'), "") : (route[..q].Trim('/'), route[(q + 1)..]);
    }

    private static ShellVm Shell(string query, int w = 1920, int h = 1080)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        return s;
    }

    private static (ShellVm Shell, ReactorPageVm Page) OpenReactor(string route, int w = 1920, int h = 1080)
    {
        var (path, query) = Split(route);
        var r = Route.Parse(path) ?? throw new ArgumentException(path);
        var s = Shell(query, w, h);
        s.ApplyDemoParams(DemoParams.Parse(query) with { Freeze = true }, r);
        return (s, Assert.IsType<ReactorPageVm>(s.Page));
    }

    private static (ShellVm Shell, OverviewPageVm Page) OpenOverview(string route, int w = 1920, int h = 1080)
    {
        var (_, query) = Split(route);
        var s = Shell(query, w, h);
        s.ApplyDemoParams(DemoParams.Parse(query) with { Freeze = true }, Route.Overview);
        return (s, Assert.IsType<OverviewPageVm>(s.Page));
    }

    /// <summary>样稿 ui.textWidth 量的参考线 / 标记文字宽度（没量过的按 Core 的估计）。</summary>
    private static Func<string, double> Widths(JsonElement chart)
    {
        var d = chart.GetProperty("labels").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
        return t => d.TryGetValue(t, out var w) ? w : ChartLayout.EstimateWidth(t);
    }

    /// <summary>按样稿的容器尺寸与字宽排版、拼成绘制列表，再拼回 SVG 与样稿逐元素比较。</summary>
    private static void AssertScene(string what, ChartOptions? opts, JsonElement chart)
    {
        Assert.NotNull(opts);
        var size = chart.GetProperty("size");
        var g = ChartLayout.Compute(opts!, size[0].GetDouble(), size[1].GetDouble(), Widths(chart));
        var scene = new ChartScene();
        scene.Build(g, opts!);
        var mine = Svg(scene, g);
        var theirs = ParseSvg(chart.GetProperty("svg").GetString()!);
        var bad = new List<string>();
        for (int i = 0; i < Math.Max(mine.Count, theirs.Count); i++)
        {
            string a = i < mine.Count ? mine[i] : "（无）", b = i < theirs.Count ? theirs[i] : "（无）";
            if (a != b) { bad.Add($"第 {i} 个元素：\n  App  {a}\n  样稿 {b}"); if (bad.Count > 6) break; }
        }
        Assert.True(bad.Count == 0, what + "：\n" + string.Join("\n", bad));
    }

    [Theory]
    [MemberData(nameof(CurveScenes))]
    public void ReactorCurves_DrawSameSvgAsMock(string name, int w)
    {
        var v = Vector(name, w);
        var (_, page) = OpenReactor(v.GetProperty("route").GetString()!, w, v.GetProperty("h").GetInt32());
        var c = v.GetProperty("curves");
        Assert.Equal(c.GetProperty("range").GetString(), page.Curves.Range);
        AssertScene($"{name}-{w}", page.Curves.Options, c.GetProperty("chart"));
        // 图例：按面板的（小数）宽度取舍，同样稿的容器查询
        double panelW = c.GetProperty("rect")[2].GetDouble();
        var want = c.GetProperty("legendShown").GetBoolean() ? c.GetProperty("legend").EnumerateArray().Select(x => x.GetString()).ToArray() : Array.Empty<string>();
        Assert.Equal(want, ReactorChartOptions.Legend(panelW).Select(x => x.Label).ToArray());
    }

    [Theory]
    [MemberData(nameof(TrendScenes))]
    public void OverviewTrend_DrawsSameSvgAsMock(string name, int w)
    {
        var v = Vector(name, w);
        int h = v.GetProperty("h").GetInt32();
        var (s, page) = OpenOverview(v.GetProperty("route").GetString()!, w, h);
        // 总览 = 工作区（宽 = 样稿温度趋势面板的宽；高 = 窗口 − 菜单 34 − 页签 44 − 时间轴（≥ 1000 高时 220，否则 200）− 状态栏 30）
        var rect = v.GetProperty("trend").GetProperty("rect");
        page.SetViewport(rect[2].GetDouble(), h - 34 - 44 - (h >= 1000 ? 220 : 200) - 30);
        Assert.True(page.Trend.IsShown, name + "：样稿画了温度趋势");
        Assert.True(page.TrendFill > 0);
        AssertScene($"{name}-{w}", page.Trend.Options, v.GetProperty("trend").GetProperty("chart"));
        _ = s;
    }

    [Fact]
    public void GalleryCharts_DrawSameSvgAsMock()
    {
        var v = Vector("gallery", 1920);
        var (a, b, c) = GalleryCharts.Options;
        var charts = v.GetProperty("gallery").EnumerateArray().ToArray();
        Assert.Equal(3, charts.Length);
        AssertScene("gallery 1", a, charts[0]);
        AssertScene("gallery 2", b, charts[1]);
        AssertScene("gallery 3", c, charts[2]);
    }

    // ———————————————————— SVG（同 Core 的 ChartTicksTests：每个元素写成「标签 属性=值 …（按属性名排序）|文字」） ————————————————————

    private static string S(double v) => JsNumber.ToString(v);
    private static string F1(double v) => JsNumber.ToFixed(v, 1);
    private static string Hex(string token) => Palette.Find(token)?.Hex ?? "?" + token;

    private static List<string> ParseSvg(string svg)
    {
        if (svg.Length == 0) return new List<string>();
        var x = XElement.Parse(svg);
        var list = new List<string> { Elem(x.Name.LocalName, x.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value), null) };
        foreach (var e in x.Descendants())
            list.Add(Elem(e.Name.LocalName, e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value), e.HasElements || e.Name.LocalName == "g" ? null : e.Value));
        return list;
    }

    private static string Elem(string tag, Dictionary<string, string> attrs, string? text) =>
        tag + " " + string.Join(" ", attrs.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value)) + (string.IsNullOrEmpty(text) ? "" : "|" + text);

    private static Dictionary<string, string> A(params (string? K, string V)[] a) => a.Where(p => p.K is not null).ToDictionary(p => p.K!, p => p.V);

    /// <summary>App 的绘制列表 → 样稿 draw() 的 SVG 元素序列（数字的写法照样稿：哪些是 toFixed(1)、哪些是原数）。</summary>
    private static List<string> Svg(ChartScene s, CoreGeometry g)
    {
        var list = new List<string>();
        if (!s.Drawn) return list;
        double W = s.Width, H = s.Height, padL = s.PlotX, padT = s.PlotY, pw = s.PlotW, ph = s.PlotH;
        list.Add(Elem("svg", A(("class", "pr-chart-svg"), ("width", S(W)), ("height", S(H)), ("viewBox", "0 0 " + S(W) + " " + S(H))), null));
        foreach (var op in s.Ops)
        {
            switch (op.Kind)
            {
                case ChartOpKind.Plot:
                    list.Add(Elem("rect", A(("class", "pr-chart-plot"), ("x", S(padL)), ("y", S(padT)), ("width", S(pw)), ("height", S(ph))), null));
                    break;
                case ChartOpKind.Line:
                {
                    var l = s.Lines[op.Index];
                    list.Add(l.Kind switch
                    {
                        ChartLineKind.Grid => Elem("line", A(("class", "pr-chart-grid"), ("x1", S(l.X1)), ("x2", S(l.X2)), ("y1", F1(l.Y1)), ("y2", F1(l.Y2))), null),
                        ChartLineKind.GridV => Elem("line", A(("class", "pr-chart-grid pr-chart-grid--v"), ("x1", F1(l.X1)), ("x2", F1(l.X2)), ("y1", S(l.Y1)), ("y2", S(l.Y2))), null),
                        ChartLineKind.Axis => Elem("line", A(("class", "pr-chart-axis"), ("x1", S(l.X1)), ("x2", S(l.X2)), ("y1", S(l.Y1)), ("y2", S(l.Y2))), null),
                        ChartLineKind.Ref => Elem("line", A(("class", "pr-chart-ref"), ("x1", S(l.X1)), ("x2", S(l.X2)), ("y1", F1(l.Y1)), ("y2", F1(l.Y2)), ("stroke", Hex(l.Color!)),
                            (l.Dashed ? "stroke-dasharray" : null, "6 4")), null),
                        _ => Elem("line", A(("class", "pr-chart-marker"), ("x1", F1(l.X1)), ("x2", F1(l.X2)), ("y1", S(l.Y1)), ("y2", S(l.Y2)), ("stroke", Hex(l.Color!))), null),
                    });
                    break;
                }
                case ChartOpKind.Text:
                {
                    var t = s.Texts[op.Index];
                    string anchor = t.Anchor switch { ChartAnchor.End => "end", ChartAnchor.Middle => "middle", _ => "start" };
                    list.Add(t.Kind switch
                    {
                        // 左轴刻度的基线是原数（+y + 4.5），右轴的是 toFixed(1)：同一个 F1 / S 写法
                        ChartTextKind.Tick when t.Anchor == ChartAnchor.Start => Elem("text", A(("class", "pr-chart-tick"), ("x", S(t.X)), ("y", F1(t.Y)), ("text-anchor", anchor)), t.Text),
                        ChartTextKind.Tick when t.Anchor == ChartAnchor.Middle => Elem("text", A(("class", "pr-chart-tick"), ("x", F1(t.X)), ("y", S(t.Y)), ("text-anchor", anchor)), t.Text),
                        ChartTextKind.Tick => Elem("text", A(("class", "pr-chart-tick"), ("x", S(t.X)), ("y", S(t.Y)), ("text-anchor", anchor)), t.Text),
                        ChartTextKind.AxisTitle => Elem("text", A(("class", "pr-chart-axis-title"), ("x", S(t.X)), ("y", S(t.Y)), ("text-anchor", anchor),
                            (t.Color != ChartStyle.TickInk ? "fill" : null, Hex(t.Color))), t.Text),
                        ChartTextKind.RefLabel => Elem("text", A(("class", "pr-chart-ref-label"), ("x", F1(t.X)), ("y", F1(t.Y)), ("text-anchor", anchor), ("fill", Hex(t.Color))), t.Text),
                        ChartTextKind.MarkerLabel => Elem("text", A(("class", "pr-chart-marker-label"), ("x", F1(t.X)), ("y", S(t.Y)), (t.Anchor == ChartAnchor.End ? "text-anchor" : null, "end"),
                            ("fill", Hex(t.Color))), t.Text),
                        _ => Elem("text", A(("class", "pr-chart-empty"), ("x", S(t.X)), ("y", S(t.Y)), ("text-anchor", "middle")), t.Text),
                    });
                    break;
                }
                case ChartOpKind.Series:
                {
                    var p = s.Paths[op.Index];
                    var d = new StringBuilder();
                    bool pen = false;
                    for (int i = p.Start; i < p.Start + p.Count; i++)
                    {
                        if (double.IsNaN(s.Xs[i])) { pen = false; continue; }
                        d.Append(pen ? 'L' : 'M').Append(F1(s.Xs[i])).Append(' ').Append(F1(s.Ys[i]));
                        pen = true;
                    }
                    string ds = d.ToString();
                    if (ds.Length > 400) ds = "#" + Fnv(ds) + ":" + ds.Length;
                    list.Add(Elem("path", A(("class", "pr-chart-line"), ("d", ds), ("stroke", Hex(p.Color)), ("stroke-width", S(p.Width)), (p.Dash is null ? null : "stroke-dasharray", p.Dash ?? "")), null));
                    break;
                }
                case ChartOpKind.EndDot:
                {
                    var e = s.Dots[op.Index];
                    list.Add(Elem("circle", A(("class", "pr-chart-end"), ("cx", F1(e.X)), ("cy", F1(e.Y)), ("r", "3"), ("fill", Hex(e.Color))), null));
                    break;
                }
            }
        }
        // 悬停层（样稿 draw() 的最后一个元素；App 是单独的一层 ChartHoverLayer）
        list.Add(Elem("g", A(("class", "pr-chart-cross"), ("style", "display:none")), null));
        list.Add(Elem("line", A(("class", "pr-chart-cross-line"), ("y1", S(g.PadT)), ("y2", S(g.PadT + g.Ph))), null));
        return list;
    }

    private static string Fnv(string s)
    {
        uint h = 0x811c9dc5;
        foreach (char c in s + "\n") { h ^= c; h = unchecked(h * 0x01000193); }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    // ———————————————————— 数据：环形缓冲 = 样稿的 series ————————————————————

    [Theory]
    [InlineData("reactor/1/1", "")]
    [InlineData("reactor/1/3", "")]
    [InlineData("reactor/2/1", "")]
    [InlineData("reactor/3/11", "sel=3/11")]
    [InlineData("reactor/1/1", "stress=1")]
    [InlineData("reactor/3/16", "stress=2")]
    [InlineData("reactor/1/1", "offline=1")]
    [InlineData("reactor/1/1", "estop=1")]
    [InlineData("reactor/2/1", "alarm=2:1")]
    public void ReactorCurves_SameAsMockSeries(string path, string query)
    {
        var (s, page) = OpenReactor("/" + path + "?" + query);
        var d = s.Fleet.DemoData!.Device(page.Route.K)!;
        foreach (var range in new[] { "10", "30", "60" })
        {
            page.Curves.Range = range;
            int min = int.Parse(range, CultureInfo.InvariantCulture);
            var want = ReactorChartOptions.FromDemo(d, d.Channel(page.Route.N)!, min, offline: !d.Online);
            var got = page.Curves.Options!;
            Assert.Equal(want.XMin, got.XMin);
            Assert.Equal(want.XMax, got.XMax);
            Assert.Equal(want.EmptyText, got.EmptyText);
            Assert.Equal(want.Series.Count, got.Series.Count);
            for (int i = 0; i < want.Series.Count; i++)
            {
                Assert.Equal(want.Series[i].Name, got.Series[i].Name);
                Assert.Equal(want.Series[i].Points.Select(p => (p.T, p.Ok ? p.V : double.NaN)), got.Series[i].Points.Select(p => (p.T, p.Ok ? p.V : double.NaN)));
            }
            Assert.Equal(want.YAxes.Select(a => (a.Min, a.Max, a.MaxTicks, a.Dec)), got.YAxes.Select(a => (a.Min, a.Max, a.MaxTicks, a.Dec)));
            Assert.Equal(want.RefLines.Select(r => (r.V, r.Label)), got.RefLines.Select(r => (r.V, r.Label)));
            Assert.Equal(want.Markers.Select(m => (m.T, m.Label)), got.Markers.Select(m => (m.T, m.Label)));
        }
    }

    [Fact]
    public void Range_SwitchesWindow_InvalidFallsBackTo30()
    {
        var (s, page) = OpenReactor("/reactor/1/1?");
        var c = page.Curves;
        Assert.Equal("30", c.Range);
        Assert.Equal(new[] { "10", "30", "60" }, c.RangeOptions.Select(o => o.Label));
        Assert.Equal(new[] { "近 10 min", "近 30 min", "近 60 min" }, c.RangeOptions.Select(o => o.Tip));
        double now = ChartTime.Ms(s.Fleet.Now);
        c.Range = "10";
        Assert.Equal(now - 10 * 60000.0, c.Options!.XMin);
        Assert.Equal(now, c.Options.XMax);
        c.Range = "60";
        Assert.Equal(now - 60 * 60000.0, c.Options!.XMin);
        c.Range = "45";
        Assert.Equal("30", c.Range);
        Assert.Equal(now - 30 * 60000.0, c.Options!.XMin);
        c.Range = "60";
        c.ResetView();
        Assert.Equal("30", c.Range);
    }

    [Fact]
    public void NoRebuild_WhenNothingChanged_RebuildsWhenClockMoves()
    {
        var p = DemoParams.Parse("");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(p, Route.Reactor(1, 1));
        var page = Assert.IsType<ReactorPageVm>(s.Page);
        var c = page.Curves;
        var first = c.Options;
        int builds = c.Builds;
        s.Fleet.RefreshNow();
        page.Refresh(s.Fleet);
        Assert.Same(first, c.Options);                         // 冻结（时钟没走）：同一个输入，曲线不重排
        Assert.Equal(builds, c.Builds);
        // 推进 12 s：横轴右移、多一个 10 s 采样 → 重建（每次刷新时钟都走了：每秒至多一次）
        var demo = s.Fleet.DemoData!;
        for (int i = 0; i < 12; i++) demo.Tick();
        page.Refresh(s.Fleet);
        Assert.NotSame(first, c.Options);
        Assert.True(c.Builds > builds);
        Assert.Equal(ChartTime.Ms(demo.Now), c.Options!.XMax);
        int b2 = c.Builds;
        var o2 = c.Options;
        page.Refresh(s.Fleet);                                 // 时钟没走：不重建
        Assert.Equal(b2, c.Builds);
        Assert.Same(o2, c.Options);
        var d = demo.Device(1)!;
        var want = ReactorChartOptions.FromDemo(d, d.Channel(1)!, 30);
        Assert.Equal(want.Series[0].Points.Select(q => (q.T, q.V)), c.Options.Series[0].Points.Select(q => (q.T, q.V)));
    }

    [Fact]
    public void Uninstalled_OrRemoved_NoChart()
    {
        var (s, page) = OpenReactor("/reactor/2/1?");
        var un = new ReactorCurvesVm(1, 6);
        un.Refresh(s.Fleet.Device(1));
        Assert.Null(un.Options);                               // 1号机只装了 RV1–RV4：没有曲线（页面是「未安装」说明）
        Assert.NotNull(page.Curves.Options);
        page.Curves.Refresh(null);                             // 设备被移除
        Assert.Null(page.Curves.Options);
    }

    [Theory]
    [InlineData(1.15, 1.1)]       // 精确值 1.1499999…：JS 给 1.1（不是 Math.Round(11.5) 的 1.2）
    [InlineData(2.25, 2.3)]       // 精确的一半：取大的
    [InlineData(283.04999999999995, 283.0)]
    [InlineData(0.05, 0.1)]       // 0.05000000000000000277 > 0.05
    [InlineData(-1.25, -1.3)]     // 符号单独处理
    [InlineData(589, 589)]
    [InlineData(0, 0)]
    public void SceneRounding_IsJsToFixed1(double v, double want)
    {
        Assert.Equal(want, ChartScene.F1(v));
        Assert.Equal(JsNumber.ToFixed(v, 1), JsNumber.ToFixed(ChartScene.F1(v), 1));
    }

    [Fact]
    public void PointWindow_ReusesBuffer()
    {
        var h = new ChannelHistory();
        var t0 = new DateTime(2026, 11, 8, 2, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 30; i++) h.Append(t0.AddSeconds(i * 10), 20 + i, 0.1);
        var w = new PointWindow();
        Assert.Empty(w.Fill(null, HistoryField.TempC, t0, 10));
        w.Fill(h, HistoryField.TempC, t0.AddSeconds(290), 1);   // 最近 1 min：7 个点（含两端）
        Assert.Equal(new double[] { 43, 44, 45, 46, 47, 48, 49 }, w.Select(p => p.V));
        w.Fill(h, HistoryField.TempC, t0.AddSeconds(290), 10);
        Assert.Equal(30, w.Count);
        Assert.Equal(20, w[0].V);
        Assert.Throws<ArgumentOutOfRangeException>(() => w[30]);
        Assert.Empty(w.Clear());
    }

    // ———————————————————— 总览温度趋势 ————————————————————

    [Fact]
    public void Trend_OnlyWhenRoomLeft_OneDevice_AtMostFour()
    {
        var (_, three) = OpenOverview("/overview?");
        three.SetViewport(1226, 752);
        Assert.False(three.Trend.IsShown);                     // 三台设备：下面不剩 200 px
        Assert.Equal(0, three.TrendFill);

        var (s, one) = OpenOverview("/overview?devs=1");
        one.SetViewport(1226, 752);
        Assert.True(one.Trend.IsShown);
        Assert.Equal(752, one.TrendFill);
        var t = one.Trend;
        Assert.Equal("温度趋势", t.Title);
        Assert.Equal("近 60 min · 选中设备的运行中反应釜", t.Sub);
        Assert.Equal("1号机", t.Alias);
        Assert.Null(t.MoreText);
        Assert.Equal(new[] { ("RV1", "--ch-s1", (string?)null), ("RV2", "--ch-s2", null), ("RV3", "--ch-s1", "4 2"), ("RV4", "--ch-s2", "4 2") },
            t.Items.Where(i => i.IsShown).Select(i => (i.Label, i.Color, i.SampleDash)).ToArray());
        var want = OverviewTrendOptions.FromDemo(s.Fleet.DemoData!, s.Selection.K);
        Assert.Equal(want.Options.Series.Select(x => x.Name), t.Options!.Series.Select(x => x.Name));
        for (int i = 0; i < want.Options.Series.Count; i++)
            Assert.Equal(want.Options.Series[i].Points.Select(p => (p.T, p.V)), t.Options.Series[i].Points.Select(p => (p.T, p.V)));
        Assert.Equal("没有运行中的反应釜", t.Options.EmptyText);

        // 放大一个分区：趋势不显示（样稿放大的分区盖住整个滚动区）
        one.SetMax(1, true);
        Assert.False(one.Trend.IsShown);
        one.SetMax(1, false);
        Assert.True(one.Trend.IsShown);
    }

    [Fact]
    public void Trend_FollowsSelection_AndShowsMore()
    {
        var (s, p) = OpenOverview("/overview?devs=PR16,PR4&stress=2");
        p.SetViewport(1226, 752);
        Assert.True(p.Trend.IsShown);
        Assert.Equal("显示 4/20", p.Trend.MoreText);
        Assert.Equal("选中别的设备可切换 · 全部曲线在「曲线」视图", p.Trend.MoreTip);
        var before = p.Trend.Alias;
        int builds = p.Trend.Builds;
        p.Refresh(s.Fleet);
        Assert.Equal(builds, p.Trend.Builds);                  // 冻结：数据没变，不重建
        s.Select(2, null);
        Assert.NotEqual(before, p.Trend.Alias);               // 选中 2号机：画它的反应釜
        Assert.Equal(s.Fleet.Device(2)!.Alias, p.Trend.Alias);
        Assert.True(p.Trend.Builds > builds);
    }

    // ———————————————————— 实时：环形缓冲 ————————————————————

    [Fact]
    public async Task Live_FramesAppended_GapOnDisconnect_ClearedOnRestart()
    {
        var post = new ManualPost();
        FakeReactorConnection? fake = null;
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "tok");
        var start = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))!;
        var src = new LiveDeviceSource(cfg, "PC-测试", post.Post, o => fake = new FakeReactorConnection(o)
        {
            StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
            StartSnapshot = start,
        });
        await src.ConnectAsync(CancellationToken.None);
        post.Run();
        int n = start.Channels.First(c => c.Installed).Id;
        // 横轴的「现在」= HMI 时钟，向上取整到整秒（一秒内多次刷新不重排）
        var now = src.History.Now;
        Assert.Equal(0, now.ToUniversalTime().Ticks % TimeSpan.TicksPerSecond);
        Assert.InRange((now.ToUniversalTime() - DateTime.UtcNow).TotalSeconds, -0.01, 1.01);
        var h = src.History.Channel(n);
        Assert.NotNull(h);                                     // 起步的那一帧已经进来
        int count0 = h!.Count;
        // 每 2 s 一帧，共 30 s：10 s 一格、同一格只留最后一帧
        for (int i = 1; i <= 15; i++)
            fake!.RaiseTelemetry(start with { Seq = start.Seq + i, Ts = start.Ts.AddSeconds(2 * i) });
        post.Run();
        Assert.InRange(h.Count, count0 + 3, count0 + 4);
        Assert.Equal(start.Ts.AddSeconds(30), h.Last!.Value.T);
        var pts = h.Series(HistoryField.PressureMPa, start.Ts.AddSeconds(30), 60);   // 帧的时刻是设备时钟（样本是录下来的）
        Assert.Equal(Units.PsiToMpa(start.Channels.First(c => c.Id == n).PressurePsi), pts[^1].V);
        // 断线：插断开标记（曲线在这里断开）
        int v = h.Version;
        fake!.RaiseConnection(ConnectionState.Reconnecting);
        post.Run();
        Assert.True(h.Version > v);
        Assert.True(h.Last!.Value.Gap);
        // HMI 重启：缓冲清空（提示「曲线缓冲已清空」）
        fake.RaiseRestarted();
        post.Run();
        Assert.Equal(0, h.Count);
        Assert.Null(src.History.Channel(n));
        Assert.Empty(src.History.Marks);
        await src.DisposeAsync();
        post.Run();
    }
}
