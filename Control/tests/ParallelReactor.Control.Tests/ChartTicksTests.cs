using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Chart 对照样稿：MockVectors/ChartMockVectors.cs（gen-chart-vectors.js 用样稿自己的 niceTicks / fitTicks / chartBands / reactorChartOpts / lineChart 在 JSC 里算出）。
/// <list type="bullet">
/// <item>刻度：逐个比较 lo / hi / step / 每个刻度值（double 逐位相同）/ 小数位。</item>
/// <item>分带：同一组点、量程、上限 → 两条轴的 min / max / maxTicks。</item>
/// <item>版面：ChartLayout.Compute 的结果按样稿 draw() 的写法拼成 SVG 元素（同样的顺序、同样的数字写法），与样稿画出的 SVG 逐个元素、逐个属性比较；
///   悬停（ChartLayout.Hover / TipLeft / TipTop）与样稿 onMove 写进提示框的内容比较。</item>
/// <item>反应釜曲线：ReactorChartOptions.FromDemo 与样稿 reactorChartOpts（同样参数的演示数据）比较系列（点数、点的摘要、首尾点）、轴（分带前后）、上限线、标记、横轴范围。</item>
/// </list>
/// 时区：手写参数的版面（layouts 里有 opts 的）时刻是「相对 T0（本地 10:12）」的毫秒数，刻度的 HH:MM 与整点对齐只在整小时时区里与时区无关 ——
/// 这些在 UTC 里换算（ChartTime.UseZone，只影响这个测试自己的异步流），在半点时区（阿德莱德、加尔各答）的机器上照样逐字相同（复核）。
/// 取自演示数据的版面不换：演示数据的时刻经系统时区换成 UTC（DeviceHistory 等），只换曲线这一边会两边对不上；它们的刻度不超过半小时一格，半点时区里照样对齐。
/// </summary>
public class ChartTicksTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(ChartMockVectors.Json));
    private static JsonElement Root => Doc.Value.RootElement;
    private static double? N(JsonElement e) => MockVectorData.Num(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText());
    private static double T0Ms => ChartTime.Ms(DemoFleet.T0);

    // ———————————————————— 刻度 ————————————————————

    [Fact]
    public void NiceTicks_MatchMock()
    {
        var bad = new List<string>();
        int n = 0;
        foreach (var row in Root.GetProperty("nice").EnumerateArray())
        {
            double lo = N(row[0]) ?? double.NaN, hi = N(row[1]) ?? double.NaN, count = N(row[2])!.Value;
            var t = ChartTicks.Nice(lo, hi, count);
            CompareTicks(t, row[3], $"nice({row[0]}, {row[1]}, {row[2]})", bad);
            n++;
        }
        Assert.True(n > 1000, "向量太少：" + n);
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(20)));
    }

    [Fact]
    public void FitTicks_MatchMock()
    {
        var bad = new List<string>();
        foreach (var row in Root.GetProperty("fit").EnumerateArray())
        {
            double lo = N(row[0]) ?? double.NaN, hi = N(row[1]) ?? double.NaN, ph = N(row[2])!.Value;
            int count = row[3].GetInt32();
            double? minPx = N(row[4]);
            var t = minPx is double m ? ChartTicks.Fit(lo, hi, ph, count, m) : ChartTicks.Fit(lo, hi, ph, count);
            CompareTicks(t, row[5], $"fit({row[0]}, {row[1]}, {row[2]}, {count}, {row[4]})", bad);
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(20)));
    }

    private static void CompareTicks(TickSet t, JsonElement want, string what, List<string> bad)
    {
        bool Same(double a, double? b) => b is double v && (a.Equals(v) || (a == 0 && v == 0));
        var ticks = want.GetProperty("ticks").EnumerateArray().Select(N).ToArray();
        bool ok = Same(t.Lo, N(want.GetProperty("lo"))) && Same(t.Hi, N(want.GetProperty("hi"))) && Same(t.Step, N(want.GetProperty("step")))
            && t.Dec == (want.GetProperty("dec").ValueKind == JsonValueKind.Number ? want.GetProperty("dec").GetInt32() : 0) && t.Ticks.Count == ticks.Length && t.Ticks.Zip(ticks).All(z => Same(z.First, z.Second));
        if (!ok) bad.Add(what + $"：C# lo={R(t.Lo)} hi={R(t.Hi)} step={R(t.Step)} dec={t.Dec} ticks=[{string.Join(",", t.Ticks.Select(R))}] ≠ 样稿 {want.GetRawText()}");
    }

    [Fact]
    public void MinSpan_ByUnit()
    {
        Assert.Equal(5, ChartTicks.MinSpanOf("℃"));
        Assert.Equal(0.05, ChartTicks.MinSpanOf("MPa"));
        Assert.Equal(5, ChartTicks.MinSpanOf("psi"));
        Assert.Equal(10, ChartTicks.MinSpanOf("%"));
        Assert.Equal(10, ChartTicks.MinSpanOf("rpm"));
        Assert.Equal(0.2, ChartTicks.MinSpanOf("mmol"));
        Assert.Equal(0, ChartTicks.MinSpanOf(null));
        Assert.Equal(0, ChartTicks.MinSpanOf("mL"));
    }

    // ———————————————————— 分带 ————————————————————

    [Fact]
    public void Bands_MatchMock()
    {
        var sets = Root.GetProperty("bandSets");
        var tSets = sets.GetProperty("t").EnumerateArray().Select(a => a.EnumerateArray().Select(N).ToArray()).ToArray();
        var pSets = sets.GetProperty("p").EnumerateArray().Select(a => a.EnumerateArray().Select(N).ToArray()).ToArray();
        var bad = new List<string>();
        int n = 0;
        foreach (var row in Root.GetProperty("bands").EnumerateArray())
        {
            n++;
            bool one = row.TryGetProperty("one", out var o1) && o1.GetBoolean();
            ChartOptions opts;
            if (one)
                opts = new ChartOptions
                {
                    Series = new[] { new ChartSeries("", new[] { new ChartPoint(0, 50), new ChartPoint(1, 52) }) },
                    YAxes = new[] { new ChartAxis { Min = 0, Max = 10 } },
                };
            else
            {
                var tv = tSets[row.GetProperty("t").GetInt32()];
                var pv = pSets[row.GetProperty("p").GetInt32()];
                double? dec = N(row.GetProperty("dec"));
                opts = new ChartOptions
                {
                    Series = new[]
                    {
                        new ChartSeries("t", tv.Select((v, i) => new ChartPoint(i, v ?? double.NaN)).ToArray()) { Axis = 0 },
                        new ChartSeries("p", pv.Select((v, i) => new ChartPoint(i, v ?? double.NaN)).ToArray()) { Axis = 1 },
                    },
                    YAxes = new[] { new ChartAxis { Label = "T" }, dec is double d ? new ChartAxis { Dec = (int)d } : new ChartAxis() },
                };
            }
            var r = ChartBands.Apply(opts, N(row.GetProperty("pMax")), N(row.GetProperty("limit")));
            void Ax(int i, JsonElement want)
            {
                if (want.ValueKind == JsonValueKind.Null) return;
                var a = r.YAxes[i];
                bool ok = Eq(a.Min, N(want[0])) && Eq(a.Max, N(want[1])) && Eq(a.MaxTicks, N(want[2]));
                if (!ok) bad.Add($"bands #{n} 轴 {i}：C# {a.Min}/{a.Max}/{a.MaxTicks} ≠ 样稿 {want.GetRawText()}（t={row.GetProperty("t")} p={row.GetProperty("p")} pMax={row.GetProperty("pMax")} limit={row.GetProperty("limit")}）");
            }
            Ax(0, row.GetProperty("a0"));
            Ax(1, row.GetProperty("a1"));
        }
        Assert.True(n > 500, "向量太少：" + n);
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(20)));
    }

    private static bool Eq(double? a, double? b) => a is null ? b is null : b is double v && (a.Value.Equals(v) || (a.Value == 0 && v == 0));

    private static bool Eq(int? a, double? b) => a is null ? b is null : b is double v && a.Value == v;

    // ———————————————————— 版面（SVG 逐元素） ————————————————————

    public static IEnumerable<object[]> Layouts() => Doc.Value.RootElement.GetProperty("layouts").EnumerateArray().Select(v => new object[] { v.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Layout_MatchesMockSvg(string name)
    {
        var v = Root.GetProperty("layouts").EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        double w = v.GetProperty("w").GetDouble(), h = v.GetProperty("h").GetDouble();
        bool given = v.GetProperty("opts").ValueKind == JsonValueKind.Object;
        // 手写参数（相对 T0 的时刻）：在 UTC 里换算，结果与时区无关（半点时区的机器上也逐字相同）
        using var zone = given ? ChartTime.UseZone(TimeZoneInfo.Utc) : null;
        var opts = given ? OptionsOf(v.GetProperty("opts")) : OptionsOfScene(v.GetProperty("ref"));
        var g = ChartLayout.Compute(opts, w, h, ChartLayout.EstimateWidth, now: T0Ms);
        var mine = Svg(g, opts);
        var theirs = ParseSvg(v.GetProperty("svg").GetString()!);
        var bad = new List<string>();
        for (int i = 0; i < Math.Max(mine.Count, theirs.Count); i++)
        {
            string a = i < mine.Count ? mine[i] : "（无）", b = i < theirs.Count ? theirs[i] : "（无）";
            if (a != b) { bad.Add($"第 {i} 个元素：\n  C#  {a}\n  样稿 {b}"); if (bad.Count > 6) break; }
        }
        Assert.True(bad.Count == 0, name + "：\n" + string.Join("\n", bad));

        // 悬停
        foreach (var hv in v.GetProperty("hover").EnumerateArray())
        {
            double x = hv.GetProperty("x").GetDouble(), y = hv.GetProperty("y").GetDouble();
            var r = ChartLayout.Hover(g, x, y);
            Assert.True(hv.GetProperty("shown").GetBoolean() == (r is not null), $"{name} 悬停 ({x}, {y})：显示 {r is not null}");
            if (r is null) continue;
            Assert.Equal(hv.GetProperty("cross").GetString(), JsNumber.ToFixed(r.CrossX, 1));
            var tip = new StringBuilder("<div class=\"pr-chart-tip-head\">" + Esc(r.TimeText) + "</div>");
            foreach (var row in r.Rows)
                tip.Append("<div class=\"pr-chart-tip-row\"><i style=\"background:" + Hex(row.Color) + "\"></i><span>" + Esc(row.Name) + "</span><b>" + row.Value +
                    (string.IsNullOrEmpty(row.Unit) ? "" : " <small>" + Esc(row.Unit) + "</small>") + "</b></div>");
            Assert.Equal(hv.GetProperty("tip").GetString(), tip.ToString());
            Assert.Equal(hv.GetProperty("left").GetString(), JsNumber.ToString(ChartLayout.TipLeft(g, r.CrossX, 196)) + "px");
            Assert.Equal(hv.GetProperty("top").GetString(), JsNumber.ToString(ChartLayout.TipTop(g, y, 104)) + "px");
            var dots = hv.GetProperty("dots").EnumerateArray().Select(d => d[0].GetString() + "," + d[1].GetString() + "," + d[2].GetString()).ToArray();
            Assert.Equal(dots, r.Rows.Select(d => JsNumber.ToFixed(d.DotX, 1) + "," + JsNumber.ToFixed(d.DotY, 1) + "," + Hex(d.Color)).ToArray());
        }
    }

    /// <summary>样稿 draw() 的 SVG：按出现顺序，每个元素写成「标签 属性=值 …（按属性名排序）|文字」。</summary>
    private static List<string> ParseSvg(string svg)
    {
        if (svg.Length == 0) return new List<string>();   // 容器小于 40 × 40：样稿清空容器
        var x = XElement.Parse(svg);
        var list = new List<string> { Elem(x.Name.LocalName, x.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value), null) };
        foreach (var e in x.Descendants())
        {
            if (e.Name.LocalName == "g") { list.Add(Elem("g", e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value), null)); continue; }
            list.Add(Elem(e.Name.LocalName, e.Attributes().ToDictionary(a => a.Name.LocalName, a => a.Value), e.HasElements ? null : e.Value));
        }
        return list;
    }

    private static string Elem(string tag, Dictionary<string, string> attrs, string? text) =>
        tag + " " + string.Join(" ", attrs.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => a.Key + "=" + a.Value)) + (string.IsNullOrEmpty(text) ? "" : "|" + text);

    /// <summary>C# 的版面 → 同样的 SVG 元素序列（数字的写法照样稿：toFixed(1) 或 String(number)）。</summary>
    private static List<string> Svg(ChartGeometry g, ChartOptions o)
    {
        static string S(double v) => JsNumber.ToString(v);
        static string F1(double v) => JsNumber.ToFixed(v, 1);
        var list = new List<string>();
        void Add(string tag, string? text, params (string K, string V)[] a) => list.Add(Elem(tag, a.ToDictionary(p => p.K, p => p.V), text));
        if (!g.Drawn) return list;
        double W = g.Width, H = g.Height, padL = g.PadL, padT = g.PadT, pw = g.Pw, ph = g.Ph;
        Add("svg", null, ("class", "pr-chart-svg"), ("width", S(W)), ("height", S(H)), ("viewBox", "0 0 " + S(W) + " " + S(H)));
        Add("rect", null, ("class", "pr-chart-plot"), ("x", S(padL)), ("y", S(padT)), ("width", S(pw)), ("height", S(ph)));
        foreach (var t in g.LeftTicks)
        {
            Add("line", null, ("class", "pr-chart-grid"), ("x1", S(padL)), ("x2", S(padL + pw)), ("y1", F1(t.Y)), ("y2", F1(t.Y)));
            Add("text", t.Text, ("class", "pr-chart-tick"), ("x", S(padL - 8)), ("y", S(t.TextY)), ("text-anchor", "end"));
        }
        foreach (var t in g.RightTicks)
            Add("text", t.Text, ("class", "pr-chart-tick"), ("x", S(padL + pw + 8)), ("y", F1(t.TextY)), ("text-anchor", "start"));
        foreach (var t in g.TimeTicks)
        {
            Add("line", null, ("class", "pr-chart-grid pr-chart-grid--v"), ("x1", F1(t.X)), ("x2", F1(t.X)), ("y1", S(padT)), ("y2", S(padT + ph)));
            Add("text", t.Text, ("class", "pr-chart-tick"), ("x", F1(t.X)), ("y", S(padT + ph + 21)), ("text-anchor", "middle"));
        }
        Add("line", null, ("class", "pr-chart-axis"), ("x1", S(padL)), ("x2", S(padL + pw)), ("y1", S(padT + ph)), ("y2", S(padT + ph)));
        Add("line", null, ("class", "pr-chart-axis"), ("x1", S(padL)), ("x2", S(padL)), ("y1", S(padT)), ("y2", S(padT + ph)));
        if (g.TwoAxes) Add("line", null, ("class", "pr-chart-axis"), ("x1", S(padL + pw)), ("x2", S(padL + pw)), ("y1", S(padT)), ("y2", S(padT + ph)));
        var a0 = g.Axes[0];
        if (!string.IsNullOrEmpty(a0.Label))
            list.Add(Elem("text", Attr(("class", "pr-chart-axis-title"), ("x", S(padL)), ("y", S(padT - 14)), ("text-anchor", "start"), a0.Color is null ? default : ("fill", Hex(a0.Color))), a0.Label));
        if (g.TwoAxes && !string.IsNullOrEmpty(g.Axes[1].Label))
            list.Add(Elem("text", Attr(("class", "pr-chart-axis-title"), ("x", S(padL + pw)), ("y", S(padT - 14)), ("text-anchor", "end"), g.Axes[1].Color is null ? default : ("fill", Hex(g.Axes[1].Color!))), g.Axes[1].Label));
        if (!string.IsNullOrEmpty(o.XLabel)) Add("text", o.XLabel, ("class", "pr-chart-axis-title"), ("x", S(padL + pw / 2)), ("y", S(H - 8)), ("text-anchor", "middle"));
        var labels = new List<string>();
        foreach (var r in g.RefLines)
        {
            list.Add(Elem("line", Attr(("class", "pr-chart-ref"), ("x1", S(padL)), ("x2", S(padL + pw)), ("y1", F1(r.Y)), ("y2", F1(r.Y)), ("stroke", Hex(r.Color)),
                r.Line.Dash ? ("stroke-dasharray", "6 4") : default), null));
            if (r.Label is null) continue;
            labels.Add(Elem("text", Attr(("class", "pr-chart-ref-label"), ("x", F1(r.LabelX)), ("y", F1(r.LabelBase)), ("text-anchor", r.LabelEnd ? "end" : "start"), ("fill", Hex(r.LabelColor))), r.Label));
        }
        foreach (var m in g.Markers)
        {
            Add("line", null, ("class", "pr-chart-marker"), ("x1", F1(m.X)), ("x2", F1(m.X)), ("y1", S(padT)), ("y2", S(padT + ph)), ("stroke", Hex(m.Color)));
            if (m.Label is null) continue;
            labels.Add(Elem("text", Attr(("class", "pr-chart-marker-label"), ("x", F1(m.LabelX)), ("y", S(m.LabelY)), m.LabelEnd ? ("text-anchor", "end") : default, ("fill", Hex(m.LabelColor))), m.Label));
        }
        double[] xs = Array.Empty<double>(), ys = Array.Empty<double>();
        foreach (int i in g.DrawOrder)
        {
            var s = g.Series[i];
            int k = g.ScreenPoints(i, ref xs, ref ys);
            var d = new StringBuilder();
            bool pen = false;
            for (int j = 0; j < k; j++)
            {
                if (double.IsNaN(xs[j])) { pen = false; continue; }
                d.Append(pen ? 'L' : 'M').Append(F1(xs[j])).Append(' ').Append(F1(ys[j]));
                pen = true;
            }
            if (d.Length == 0) continue;
            string ds = d.ToString();
            if (ds.Length > 400) ds = "#" + Fnv(ds) + ":" + ds.Length;
            list.Add(Elem("path", Attr(("class", "pr-chart-line"), ("d", ds), ("stroke", Hex(s.Color!)), ("stroke-width", S(s.LineWidth)),
                string.IsNullOrEmpty(s.Dash) ? default : ("stroke-dasharray", s.Dash!)), null));
            if (g.EndDot(i) is { } e) Add("circle", null, ("class", "pr-chart-end"), ("cx", F1(e.X)), ("cy", F1(e.Y)), ("r", "3"), ("fill", Hex(s.Color!)));
        }
        list.AddRange(labels);
        if (g.EmptyText is { } empty) Add("text", empty, ("class", "pr-chart-empty"), ("x", S(padL + pw / 2)), ("y", S(padT + ph / 2)), ("text-anchor", "middle"));
        Add("g", null, ("class", "pr-chart-cross"), ("style", "display:none"));
        Add("line", null, ("class", "pr-chart-cross-line"), ("y1", S(padT)), ("y2", S(padT + ph)));
        return list;
    }

    private static Dictionary<string, string> Attr(params (string K, string V)[] a) => a.Where(p => p.K is not null).ToDictionary(p => p.K, p => p.V);

    private static string Fnv(string s)
    {
        uint h = 0x811c9dc5;
        foreach (char c in s + "\n") { h ^= c; h = unchecked(h * 0x01000193); }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&#39;");

    private static readonly Lazy<Dictionary<string, string>> Palette = new(() =>
        Root.GetProperty("palette").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!));

    /// <summary>令牌名 → 样稿的十六进制。</summary>
    private static string Hex(string token) => Palette.Value.TryGetValue(token, out var h) ? h : "?" + token;

    /// <summary>十六进制 → 令牌名（同一个值有几个令牌时取曲线的 --ch-*：标记文字的颜色规则按令牌判断）。</summary>
    private static string? Token(string? hex)
    {
        if (hex is null) return null;
        var hits = Palette.Value.Where(p => p.Value == hex).Select(p => p.Key).ToArray();
        return hits.FirstOrDefault(k => k.StartsWith("--ch-", StringComparison.Ordinal)) ?? hits.FirstOrDefault() ?? hex;
    }

    /// <summary>向量里的选项（时刻相对 T0、颜色十六进制）→ ChartOptions。</summary>
    private static ChartOptions OptionsOf(JsonElement o)
    {
        string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : v.ValueKind == JsonValueKind.String ? N(v) : null : null;
        var series = new List<ChartSeries>();
        if (o.TryGetProperty("series", out var ss))
            foreach (var s in ss.EnumerateArray())
            {
                var pts = s.GetProperty("points").EnumerateArray().Select(p => new ChartPoint(T0Ms + p[0].GetDouble(), N(p[1]) ?? double.NaN)).ToArray();
                series.Add(new ChartSeries(Str(s, "name") ?? "", pts)
                {
                    Color = Token(Str(s, "color")), Axis = (int)(Num(s, "axis") ?? 0), Width = Num(s, "width") ?? 0, Dash = Str(s, "dash"),
                    Unit = Str(s, "unit"), Dec = Num(s, "dec") is double d ? (int)d : null, NoEnd = s.TryGetProperty("noEnd", out var ne) && ne.GetBoolean(),
                });
            }
        var axes = new List<ChartAxis>();
        if (o.TryGetProperty("yAxes", out var ys))
            foreach (var a in ys.EnumerateArray())
                axes.Add(new ChartAxis
                {
                    Label = Str(a, "label"), Unit = Str(a, "unit"), Min = Num(a, "min"), Max = Num(a, "max"), Dec = Num(a, "dec") is double d ? (int)d : null,
                    MinSpan = Num(a, "minSpan"), MaxTicks = Num(a, "maxTicks") is double m ? (int)m : null, Color = Token(Str(a, "color")),
                });
        var refs = new List<ChartRefLine>();
        if (o.TryGetProperty("refLines", out var rs))
            foreach (var r in rs.EnumerateArray())
                refs.Add(new ChartRefLine((int)(Num(r, "axis") ?? 0), Num(r, "v") ?? double.NaN)
                {
                    Color = Token(Str(r, "color")), LabelColor = Token(Str(r, "labelColor")), Label = Str(r, "label"),
                    Dash = !(r.TryGetProperty("dash", out var dd) && dd.ValueKind == JsonValueKind.False),
                    Fit = !(r.TryGetProperty("fit", out var ff) && ff.ValueKind == JsonValueKind.False),
                });
        var marks = new List<ChartMarker>();
        if (o.TryGetProperty("markers", out var ms))
            foreach (var m in ms.EnumerateArray())
                marks.Add(new ChartMarker(T0Ms + m.GetProperty("t").GetDouble(), Str(m, "label")) { Color = Token(Str(m, "color")), LabelColor = Token(Str(m, "labelColor")) });
        return new ChartOptions
        {
            Series = series, YAxes = axes, RefLines = refs, Markers = marks,
            XMin = Num(o, "xMin") is double x0 ? T0Ms + x0 : null, XMax = Num(o, "xMax") is double x1 ? T0Ms + x1 : null,
            XLabel = Str(o, "xLabel"), EmptyText = Str(o, "emptyText"),
        };
    }

    /// <summary>反应釜曲线的版面：按场景重建演示数据，再用 ReactorChartOptions.FromDemo（与样稿 reactorChartOpts 同样的参数）。</summary>
    private static ChartOptions OptionsOfScene(JsonElement r)
    {
        var f = Fleet(r.GetProperty("q").GetString()!, r.GetProperty("ticks").GetInt32());
        var d = f.Device(r.GetProperty("k").GetInt32())!;
        return ReactorChartOptions.FromDemo(d, d.Channel(r.GetProperty("n").GetInt32())!, r.GetProperty("min").GetInt32());
    }

    private static readonly Dictionary<string, DemoFleet> Fleets = new();

    private static DemoFleet Fleet(string q, int ticks)
    {
        string key = q + "#" + ticks;
        lock (Fleets)
        {
            if (Fleets.TryGetValue(key, out var f)) return f;
            f = new DemoFleet(DemoParams.Parse(q));
            for (int i = 0; i < ticks; i++) f.Tick();
            Fleets[key] = f;
            return f;
        }
    }

    // ———————————————————— 反应釜曲线的选项 ————————————————————

    [Fact]
    public void ReactorChartOptions_MatchMock()
    {
        var meta = Root.GetProperty("reactorMeta");
        var bad = new List<string>();
        int rows = 0;
        foreach (var row in Root.GetProperty("reactor").EnumerateArray())
        {
            rows++;
            var f = Fleet(row.GetProperty("q").GetString()!, row.GetProperty("ticks").GetInt32());
            int k = row.GetProperty("k").GetInt32(), n = row.GetProperty("n").GetInt32(), min = row.GetProperty("min").GetInt32();
            var d = f.Device(k)!;
            var ch = d.Channel(n)!;
            var o = ReactorChartOptions.FromDemo(d, ch, min);
            var raw = ReactorChartOptions.FromDemo(d, ch, min, bands: false);
            string tag = $"{row.GetProperty("q").GetString()} +{row.GetProperty("ticks").GetInt32()} {k}/{n} {min} min";
            void Check(bool ok, string what) { if (!ok) bad.Add(tag + "：" + what); }

            var ms = meta.GetProperty("series").EnumerateArray().ToArray();
            Check(o.Series.Count == ms.Length, "系列数");
            for (int i = 0; i < Math.Min(o.Series.Count, ms.Length); i++)
            {
                var s = o.Series[i];
                var m = ms[i];
                Check(s.Name == m.GetProperty("name").GetString() && Hex(s.Color!) == m.GetProperty("color").GetString() && s.Axis == m.GetProperty("axis").GetInt32()
                    && s.Width == m.GetProperty("width").GetDouble() && s.Dash == (m.TryGetProperty("dash", out var dd) ? dd.GetString() : null)
                    && s.Unit == m.GetProperty("unit").GetString() && s.Dec == m.GetProperty("dec").GetInt32() && s.NoEnd == m.GetProperty("noEnd").GetBoolean(), "系列 " + i + " 的样式");
                var want = row.GetProperty("s")[i];
                var lines = s.Points.Select(PtLine).ToArray();
                Check(lines.Length == want[0].GetInt32(), $"系列 {s.Name} 点数 {lines.Length} ≠ {want[0].GetInt32()}");
                Check(FnvLines(lines) == want[1].GetString(), $"系列 {s.Name} 点的摘要不同（首 {lines.FirstOrDefault()} / {want[2]}，尾 {lines.LastOrDefault()} / {want[3]}）");
            }
            var axes = row.GetProperty("a");
            for (int i = 0; i < 2; i++)
            {
                var a = o.YAxes[i];
                var w = axes[i];
                Check(Eq(a.Min, N(w[0])) && Eq(a.Max, N(w[1])) && Eq(a.MaxTicks, N(w[2])) && Eq(a.Dec, N(w[3])) && Eq(a.MinSpan, N(w[4])),
                    $"轴 {i}：{a.Min}/{a.Max}/{a.MaxTicks}/{a.Dec}/{a.MinSpan} ≠ {w.GetRawText()}");
                Check(a.Label == meta.GetProperty("yAxes")[i].GetProperty("label").GetString() && a.Unit == meta.GetProperty("yAxes")[i].GetProperty("unit").GetString(), "轴标题");
                var rw = row.GetProperty("raw")[i];
                Check(Eq(raw.YAxes[i].Min, N(rw[0])) && Eq(raw.YAxes[i].Max, N(rw[1])) && Eq(raw.YAxes[i].MaxTicks, N(rw[2])), "不分带的轴 " + i);
            }
            var refs = row.GetProperty("ref").EnumerateArray().ToArray();
            Check(o.RefLines.Count == refs.Length, "上限线条数");
            for (int i = 0; i < Math.Min(refs.Length, o.RefLines.Count); i++)
            {
                var r = o.RefLines[i];
                Check(r.Axis == refs[i][0].GetInt32() && Eq(r.V, N(refs[i][1])) && Hex(r.Color!) == refs[i][2].GetString() && Hex(r.LabelColor!) == refs[i][3].GetString()
                    && r.Label == refs[i][4].GetString(), "上限线 " + r.Label);
            }
            var mks = row.GetProperty("mk").EnumerateArray().ToArray();
            Check(o.Markers.Count == mks.Length, "标记条数");
            for (int i = 0; i < Math.Min(mks.Length, o.Markers.Count); i++)
                Check(o.Markers[i].T - T0Ms == mks[i][0].GetDouble() && o.Markers[i].Label == mks[i][1].GetString() && Hex(o.Markers[i].Color!) == mks[i][2].GetString(), "标记 " + i);
            var x = row.GetProperty("x");
            Check(o.XMin - T0Ms == x[0].GetDouble() && o.XMax - T0Ms == x[1].GetDouble(), "横轴范围");
            Check(o.EmptyText == row.GetProperty("e").GetString(), "空文字");
        }
        Assert.True(rows > 300, "向量太少：" + rows);
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(25)));
    }

    private static string PtLine(ChartPoint p) => JsNumber.ToString(p.T - T0Ms) + "|" + (p.Ok ? JsNumber.ToString(p.V) : "-");

    private static string FnvLines(IEnumerable<string> lines)
    {
        uint h = 0x811c9dc5;
        foreach (var l in lines)
            foreach (char c in l + "\n") { h ^= c; h = unchecked(h * 0x01000193); }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    private static string R(double v) => JsNumber.ToString(v);

    // ———————————————————— 总览温度趋势（WebKit 量到的图例与 SVG） ————————————————————

    private static readonly Lazy<JsonDocument> Rv = new(() => JsonDocument.Parse(ReactorMockVectors.Json));

    public static IEnumerable<object[]> Trends() => Rv.Value.RootElement.GetProperty("trend").EnumerateArray().Select(v => new object[] { v.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(Trends))]
    public void OverviewTrend_MatchesMock(string name)
    {
        var v = Rv.Value.RootElement.GetProperty("trend").EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
        var p = v.GetProperty("probe");
        Assert.True(p.GetProperty("shown").GetBoolean(), name + "：样稿没有画趋势");
        string route = v.GetProperty("route").GetString()!;
        var f = new DemoFleet(DemoParams.Parse(route[(route.IndexOf('?') + 1)..]));
        int? sel = p.GetProperty("sel").ValueKind == JsonValueKind.Number ? p.GetProperty("sel").GetInt32() : null;
        var t = OverviewTrendOptions.FromDemo(f, sel);
        Assert.Equal(OverviewTrendOptions.Title, p.GetProperty("title").GetString());
        Assert.Equal(OverviewTrendOptions.Sub, p.GetProperty("sub").GetString());
        var groups = p.GetProperty("groups").EnumerateArray().ToArray();
        Assert.Single(groups);
        Assert.Equal(groups[0].GetProperty("alias").GetString(), t.Device!.Alias);
        var items = groups[0].GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(items.Select(i => i[0].GetString() + "|" + i[1].GetString() + "|" + (i[2].ValueKind == JsonValueKind.Null ? "-" : i[2].GetString())).ToArray(),
            t.Legend.Select(i => i.Label + "|" + Hex(i.Color) + "|" + (i.SampleDash ?? "-")).ToArray());
        var more = p.GetProperty("more");
        Assert.Equal(more.ValueKind == JsonValueKind.Null ? null : more[0].GetString(), t.MoreText);
        Assert.Equal(more.ValueKind == JsonValueKind.Null ? null : more[1].GetString(), t.MoreTip);
        var size = p.GetProperty("size");
        var g = ChartLayout.Compute(t.Options, size[0].GetDouble(), size[1].GetDouble(), ChartLayout.EstimateWidth);
        var mine = Svg(g, t.Options);
        var theirs = ParseSvg(p.GetProperty("svg").GetString()!);
        for (int i = 0; i < Math.Max(mine.Count, theirs.Count); i++)
            Assert.True(i < mine.Count && i < theirs.Count && mine[i] == theirs[i],
                $"{name} 第 {i} 个元素：\n  C#  {(i < mine.Count ? mine[i] : "（无）")}\n  样稿 {(i < theirs.Count ? theirs[i] : "（无）")}");
    }

    [Fact]
    public void OverviewTrend_RulesAndGalleryMulti()
    {
        Assert.True(OverviewTrendOptions.InTrend(true, true, null));
        Assert.True(OverviewTrendOptions.InTrend(true, false, Core.Programs.ProgramStatus.Done));
        Assert.False(OverviewTrendOptions.InTrend(true, false, Core.Programs.ProgramStatus.Aborted));
        Assert.False(OverviewTrendOptions.InTrend(false, true, null));
        Assert.Equal("4 2", OverviewTrendOptions.SampleDash("7 4"));
        Assert.Equal("1 1", OverviewTrendOptions.SampleDash("2 1"));
        Assert.Null(OverviewTrendOptions.SampleDash(null));
        var empty = OverviewTrendOptions.Build(Array.Empty<TrendDevice>(), null, DemoFleet.T0, (k, n) => Array.Empty<ChartPoint>());
        Assert.Empty(empty.Options.Series);
        Assert.Equal("没有运行中的反应釜", ChartLayout.Compute(empty.Options, 600, 200).EmptyText);
        Assert.Null(empty.MoreText);
        // 组件陈列的第三张图：与样稿同一组选项（版面在 layouts 的 gallery-multi 里核对过）
        var multi = OverviewTrendOptions.GalleryMulti(new DemoFleet(DemoParams.Parse("freeze=1")));
        Assert.Equal(new[] { "1号机 · RV1", "1号机 · RV2", "1号机 · RV3" }, multi.Series.Select(s => s.Name).ToArray());
        Assert.Equal(new string?[] { null, "7 4", "2 3" }, multi.Series.Select(s => s.Dash).ToArray());
    }

    // ———————————————————— 直接断言（可读的事实） ————————————————————

    [Fact]
    public void ReactorChart_NearLimit_AddsLimitLine_AndBandsIncludeIt()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        var d3 = f.Device(3)!;
        var o = ReactorChartOptions.FromDemo(d3, d3.Channel(11)!, 30);
        var lim = Assert.Single(o.RefLines);
        Assert.Equal(ChartTokens.Limit, lim.Color);
        Assert.Equal(ChartTokens.LimitInk, lim.LabelColor);
        Assert.Equal("上限 " + Fmt.Mpa(d3.Model.Limits.PressureMaxMPa, d3.Model.PressureDecimals) + " MPa", lim.Label);
        Assert.True(o.YAxes[1].Max >= d3.Model.Limits.PressureMaxMPa * 1.06 - 1e-12);
        // 设定值虚线在实线下面：画线顺序虚线在前
        var g = ChartLayout.Compute(o, 784, 330);
        Assert.Equal(new[] { "温度设定", "压力设定", "温度", "压力" }, g.DrawOrder.Select(i => g.Series[i].Name).ToArray());
        Assert.All(g.RightTicks.Concat(g.LeftTicks), t => Assert.False(string.IsNullOrEmpty(t.Text)));
    }

    [Fact]
    public void ReactorChart_EmptyText_OfflineVariant_AndLegendByWidth()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        var d = f.Device(1)!;
        Assert.Equal("暂无数据", ReactorChartOptions.FromDemo(d, d.Channel(1)!).EmptyText);
        Assert.Equal("离线 · 暂无数据", ReactorChartOptions.FromDemo(d, d.Channel(1)!, offline: true).EmptyText);
        Assert.Equal(new[] { "温度", "设定", "压力", "目标", "标记" }, ReactorChartOptions.Legend(800).Select(x => x.Label).ToArray());
        Assert.Equal(new[] { "温度", "设定", "压力", "目标" }, ReactorChartOptions.Legend(720).Select(x => x.Label).ToArray());
        Assert.Equal(new[] { "温度", "压力" }, ReactorChartOptions.Legend(640).Select(x => x.Label).ToArray());
        Assert.Empty(ReactorChartOptions.Legend(500));
        Assert.Equal(new[] { 10, 30, 60 }, ReactorChartOptions.Ranges);
        Assert.Equal("近 30 min", ReactorChartOptions.RangeTip(30));
        Assert.Equal(new[] { 4.0, 8.0 / 3 }, ChartStyle.DashInWidths("6 4", 1.5));
        Assert.Null(ChartStyle.DashInWidths(null, 2));
    }

    [Fact]
    public void NearestIndex_BinarySearch_TiesGoToEarlier()
    {
        var pts = new[] { new ChartPoint(0, 1), new ChartPoint(10, 2), new ChartPoint(20, 3), new ChartPoint(40, 4) };
        Assert.Equal(-1, ChartLayout.NearestIndex(Array.Empty<ChartPoint>(), 5));
        Assert.Equal(0, ChartLayout.NearestIndex(pts, -100));
        Assert.Equal(0, ChartLayout.NearestIndex(pts, 5));
        Assert.Equal(1, ChartLayout.NearestIndex(pts, 6));
        Assert.Equal(2, ChartLayout.NearestIndex(pts, 30));
        Assert.Equal(3, ChartLayout.NearestIndex(pts, 31));
        Assert.Equal(3, ChartLayout.NearestIndex(pts, 1000));
    }

    [Fact]
    public void ScreenPoints_BreakAtGaps_ReuseBuffers()
    {
        var pts = new[] { new ChartPoint(0, 1), new ChartPoint(10, double.NaN), new ChartPoint(20, 3), new ChartPoint(30, 4), new ChartPoint(40, double.NaN), new ChartPoint(50, double.NaN), new ChartPoint(60, 5) };
        var o = new ChartOptions { Series = new[] { new ChartSeries("x", pts) }, YAxes = new[] { new ChartAxis() }, XMin = 0, XMax = 60 };
        var g = ChartLayout.Compute(o, 400, 200);
        double[] xs = new double[2], ys = new double[2];
        int k = g.ScreenPoints(0, ref xs, ref ys);
        Assert.Equal(6, k);   // 1, NaN, 3, 4, NaN, 5（连续的缺失只断一次）
        Assert.True(double.IsNaN(xs[1]) && double.IsNaN(xs[4]));
        Assert.Equal(g.X(60), xs[5]);
        var keep = xs;
        g.ScreenPoints(0, ref xs, ref ys);
        Assert.Same(keep, xs);
    }
}
