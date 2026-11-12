using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Control.Core.Layout;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Layout.StepFlowLayout 对照样稿：MockVectors/ReactorMockVectors.cs 的 flow 场景（样稿反应釜页在 WebKit 里实际渲染：1920 / 1600 / 1366、
/// 每种程序状态、5 步程序、没有程序的程序库预览、收起资源管理器 / 时间轴、放大 115 % / 130 %、断线、压力测试数据）。
/// 逐个比较：每行张数、卡片宽、放大倍数（含放不下时按钮不能点）、每一步的列 / 卡片 / 条件框、箭头、行分隔线、竖向连线、行、安全联锁框、画布宽高与水平居中、
/// 自动滚动后的 scrollTop、条件框的文字取舍（用样稿量到的字宽）。卡片与安全联锁框的高度取样稿量到的（字体决定，界面自己量）。
/// WebKit 对 CSS zoom 里的元素报的是「实际 ÷ 倍数」：放大场景按倍数换回设计 px 再比。
/// </summary>
public class StepFlowLayoutTests
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(ReactorMockVectors.Json));

    public static IEnumerable<object[]> Flows() => Doc.Value.RootElement.GetProperty("flow").EnumerateArray()
        .Where(v => v.GetProperty("probe").TryGetProperty("flow", out _)).Select(v => new object[] { v.GetProperty("name").GetString()! });

    private static JsonElement Get(string name) => Doc.Value.RootElement.GetProperty("flow").EnumerateArray().First(v => v.GetProperty("name").GetString() == name);

    private static FlowRect R(JsonElement a, double z) => new(a[0].GetDouble() * z, a[1].GetDouble() * z, a[2].GetDouble() * z, a[3].GetDouble() * z);

    [Theory]
    [MemberData(nameof(Flows))]
    public void MatchesMock(string name)
    {
        var v = Get(name);
        var p = v.GetProperty("probe");
        var cv = p.GetProperty("cv");
        var flow = p.GetProperty("flow");
        double ow = cv.GetProperty("ow").GetDouble(), cw = cv.GetProperty("cw").GetDouble(), ch = cv.GetProperty("ch").GetDouble();
        double z = flow.GetProperty("zoom").GetDouble();
        bool preview = flow.GetProperty("preview").GetBoolean();
        var cols = p.GetProperty("cols").EnumerateArray().ToArray();
        var bad = new List<string>();
        void Check(bool ok, string what) { if (!ok) bad.Add(what); }

        // 放大：探针点了几次「放大画布」（按钮不能点时点了也没用）
        string pre = v.GetProperty("pre").GetString() ?? "";
        int clicks = pre.Contains("__zoom = 2") ? 2 : pre.Contains("__zoom = 1") ? 1 : 0;
        int zoom = 0;
        for (int i = 0; i < clicks; i++) if (StepFlowLayout.ZoomInReason(ow, zoom) is null) zoom++;
        var fit = StepFlowLayout.Fit(ow, cols.Length, zoom);
        Check(fit.Per == flow.GetProperty("per").GetInt32(), $"每行 {fit.Per} ≠ {flow.GetProperty("per")}");
        Check(fit.StepW == flow.GetProperty("stepW").GetDouble(), $"卡宽 {fit.StepW} ≠ {flow.GetProperty("stepW")}");
        Check(Math.Abs(fit.Scale - z) < 1e-9, $"倍数 {fit.Scale} ≠ {z}");
        Check(fit.Column == flow.GetProperty("col").GetBoolean(), "每行一张");
        var zt = p.GetProperty("zoomText");
        Check(zt.ValueKind == JsonValueKind.Null ? zoom == 0 : zoom > 0 && StepFlowLayout.ZoomText(zoom) == zt.GetString(), "放大文字 " + zt);
        var tools = p.GetProperty("tools").EnumerateArray().ToDictionary(t => t[0].GetString()!, t => t[1].GetBoolean());
        if (tools.Count > 0)   // 没有程序（程序库预览）时没有画布工具
        {
            Check(tools["放大画布"] == (StepFlowLayout.ZoomInReason(ow, zoom) is not null), "放大按钮");
            Check(tools["缩小画布"] == (StepFlowLayout.ZoomOutReason(zoom) is not null), "缩小按钮");
        }
        else Check(preview, "画布工具");

        // 版面（设计 px）
        var cards = cols.Select(c => c.GetProperty("card")[3].GetDouble() * z).ToArray();
        double? tail = p.GetProperty("safe").ValueKind == JsonValueKind.Object ? p.GetProperty("safe").GetProperty("box")[3].GetDouble() * z : null;
        var g = StepFlowLayout.Arrange(fit, cards, tail, cw, preview);
        double tol = z == 1 ? 0.05 : 0.12;
        bool Near(FlowRect a, FlowRect b) => Math.Abs(a.X - b.X) <= tol && Math.Abs(a.Y - b.Y) <= tol && Math.Abs(a.W - b.W) <= tol && Math.Abs(a.H - b.H) <= tol;
        Check(g.Items.Count == cols.Length, "步数");
        for (int i = 0; i < Math.Min(g.Items.Count, cols.Length); i++)
        {
            var it = g.Items[i];
            Check(Near(it.Col, R(cols[i].GetProperty("col"), z)), $"第 {i + 1} 步 列 {it.Col} ≠ {R(cols[i].GetProperty("col"), z)}");
            Check(Near(it.Card, R(cols[i].GetProperty("card"), z)), $"第 {i + 1} 步 卡片 {it.Card} ≠ {R(cols[i].GetProperty("card"), z)}");
            Check(Near(it.Cond, R(cols[i].GetProperty("cond"), z)), $"第 {i + 1} 步 条件框 {it.Cond} ≠ {R(cols[i].GetProperty("cond"), z)}");
        }
        void List(string key, IReadOnlyList<FlowRect> mine)
        {
            var theirs = p.GetProperty(key).EnumerateArray().Select(a => R(a, z)).ToArray();
            Check(mine.Count == theirs.Length, $"{key} 个数 {mine.Count} ≠ {theirs.Length}");
            for (int i = 0; i < Math.Min(mine.Count, theirs.Length); i++) Check(Near(mine[i], theirs[i]), $"{key} {i}：{mine[i]} ≠ {theirs[i]}");
        }
        List("arrows", g.Arrows);
        List("dividers", g.Dividers);
        List("vlinks", g.VLinks);
        List("rows", g.Rows);
        if (tail is not null) Check(g.Tail is { } t && Near(t, R(p.GetProperty("safe").GetProperty("box"), z)), $"安全联锁框 {g.Tail}");
        var box = p.GetProperty("flowBox");
        // 画布自己的矩形：WebKit 报的就是设计 px（放大时 = 实际 ÷ 倍数）
        Check(Math.Abs(g.Width - box[2].GetDouble()) <= tol && Math.Abs(g.Height - box[3].GetDouble()) <= tol, $"画布 {g.Width}×{g.Height} ≠ {box[2]}×{box[3]}");
        if (z == 1 && !preview)
        {
            Check(Math.Abs(g.OffsetX - box[0].GetDouble()) <= tol, $"水平居中 {g.OffsetX} ≠ {box[0]}");
            Check(Math.Abs(g.ContentHeight - cv.GetProperty("sh").GetDouble()) <= 0.5, $"内容高 {g.ContentHeight} ≠ {cv.GetProperty("sh")}");
            // 自动滚动（打开页面时 scrollTop = 0，不强制）：当前步或中止的那一步
            int cur = Array.FindIndex(cols, c => c.GetProperty("state").GetString() is "cur" or "abort");
            var s = StepFlowLayout.ScrollTo(g, cur, ch, 0, force: false);
            double st = s.Top ?? 0;
            Check(Math.Abs(st - cv.GetProperty("st").GetDouble()) <= 0.5, $"scrollTop {st} ≠ {cv.GetProperty("st")}（当前 {cur}）");
        }
        else if (z != 1)
            Check(Math.Abs(g.ContentHeight - cv.GetProperty("sh").GetDouble()) <= 1, $"内容高 {g.ContentHeight} ≠ {cv.GetProperty("sh")}");

        // 条件框的文字取舍（样稿量到的字宽）
        foreach (var c in cols)
        {
            var fw = c.GetProperty("fw");
            string value = c.GetProperty("value").GetString()!;
            string label = value.StartsWith(">", StringComparison.Ordinal) ? "步骤时间" : "结束条件";
            double Measure(string text, double size, int weight) => text switch
            {
                "结束条件" => fw[0].GetDouble(),
                "条件" => fw[1].GetDouble(),
                "步骤时间" => fw[2].GetDouble(),
                _ when text == value && size == 18 => fw[3].GetDouble(),
                _ when text == value && size == 16 => fw[4].GetDouble(),
                _ => throw new InvalidOperationException("没量过：" + text + " " + size),
            };
            var (l, sm) = StepFlowLayout.CondFit(label, value, fit.StepW, Measure);
            Check(l == (c.GetProperty("label").GetString() ?? "") && sm == c.GetProperty("sm").GetBoolean(), $"条件框「{value}」：C# ({l}, {sm}) ≠ ({c.GetProperty("label")}, {c.GetProperty("sm")})");
        }
        Assert.True(bad.Count == 0, name + "：\n" + string.Join("\n", bad.Take(12)));
    }

    [Fact]
    public void Fit_ReadableFacts()
    {
        // 1920（程序栏外宽 564）：每行 2 张、223；1600（337）/ 1366（361）：每行 1 张，275 / 299；收起资源管理器（702）：2 张、280
        Assert.Equal((2, 223.0), (StepFlowLayout.Fit(564, 4).Per, StepFlowLayout.Fit(564, 4).StepW));
        Assert.Equal((1, 275.0), (StepFlowLayout.Fit(337, 4).Per, StepFlowLayout.Fit(337, 4).StepW));
        Assert.Equal((1, 299.0), (StepFlowLayout.Fit(361, 4).Per, StepFlowLayout.Fit(361, 4).StepW));
        Assert.Equal((2, 280.0), (StepFlowLayout.Fit(702, 4).Per, StepFlowLayout.Fit(702, 4).StepW));
        // 很宽：每行最多 4 张，5 步按 3 + 2 均衡；每行一张时最宽 300、最窄 214
        Assert.Equal(3, StepFlowLayout.Fit(1300, 5).Per);
        Assert.Equal(4, StepFlowLayout.Fit(1300, 8).Per);
        Assert.Equal(1, StepFlowLayout.Fit(1300, 1).Per);
        Assert.Equal(300, StepFlowLayout.Fit(1300, 1).StepW);
        Assert.Equal(214, StepFlowLayout.Fit(200, 3).StepW);
        // 放大按放大后的宽度重排：每行的卡片变少
        Assert.Equal(2, StepFlowLayout.Fit(702, 4, 1).Per);
        Assert.Equal(1, StepFlowLayout.Fit(702, 4, 2).Per);
        Assert.Equal(1.3, StepFlowLayout.Fit(702, 4, 2).Scale);
        // 放不下：放大按钮不能点；面板变窄时回到 100 %
        Assert.Equal("画布太窄 · 放大后一行放不下一张步骤卡", StepFlowLayout.ZoomInReason(337, 1));
        Assert.Equal("已是最大 130 %", StepFlowLayout.ZoomInReason(800, 2));
        Assert.Null(StepFlowLayout.ZoomInReason(800, 0));
        Assert.Equal("已是 100 %（字号不再缩小）", StepFlowLayout.ZoomOutReason(0));
        Assert.Null(StepFlowLayout.ZoomOutReason(1));
        Assert.Equal(0, StepFlowLayout.ClampZoom(337, 2));
        Assert.Equal(1, StepFlowLayout.ClampZoom(337, 1));
        Assert.Equal(0, StepFlowLayout.Fit(337, 4, 2).Zoom);
        Assert.Equal("115 %", StepFlowLayout.ZoomText(1));
        Assert.Equal("130 %", StepFlowLayout.ZoomText(2));
        // 组件陈列：1920 宽的分区每行 4 张以内
        Assert.Equal(4, StepFlowLayout.GalleryPerRow(1872, 4));
        Assert.Equal(3, StepFlowLayout.GalleryPerRow(1872, 5));
        Assert.Equal(2, StepFlowLayout.GalleryPerRow(700, 4));
    }

    [Fact]
    public void ScrollTo_ShowsConditionBox_WhenSlightlyTaller_AndClamps()
    {
        var fit = StepFlowLayout.Fit(337, 4);
        var g = StepFlowLayout.Arrange(fit, new double[] { 339, 339, 339, 339 }, 163, 329);
        // 可见区 390：整列 401 + 16 − 390 = 27 ≤ 28 → 条件框整个露出来（列底对齐可见区底）
        var s = StepFlowLayout.ScrollTo(g, 2, 390, 0, force: false);
        Assert.Equal(g.Items[2].Col.Bottom - 390, s.Top!.Value, 6);
        // 可见区 380：多出 37 > 28 → 卡片上方留 16
        Assert.Equal(g.Items[2].Col.Y - 16, StepFlowLayout.ScrollTo(g, 2, 380, 0, false).Top!.Value, 6);
        // 已经看得见：不动；强制（定位当前步）照样滚
        Assert.Null(StepFlowLayout.ScrollTo(g, 0, 600, 0, false).Top);
        Assert.Equal(4, StepFlowLayout.ScrollTo(g, 0, 600, 0, true).Top!.Value, 6);
        // 夹在内容高度里
        Assert.Equal(g.ContentHeight - 700, StepFlowLayout.ScrollTo(g, 3, 700, 0, false).Top!.Value, 6);
        // 放大：按实际 px
        var gz = StepFlowLayout.Arrange(StepFlowLayout.Fit(564, 4, 1), new double[] { 336, 336, 336, 336 }, 162, 556);
        Assert.Equal(1.15, gz.Scale);
        Assert.Equal(gz.Items[2].Col.Y * 1.15 - 16 * 1.15, StepFlowLayout.ScrollTo(gz, 2, 670, 0, false).Top!.Value, 6);
        Assert.True(gz.OffsetX > 0);
        // 没有当前步：强制时回到顶部
        Assert.Equal(0, StepFlowLayout.ScrollTo(g, -1, 600, 300, true).Top);
        Assert.Null(StepFlowLayout.ScrollTo(g, -1, 600, 300, false).Top);
        Assert.Equal("1/1/h2-std/2/Running/1/0", StepFlowLayout.ScrollKey(1, 1, "h2-std", 2, "Running", 1, 0));
        // 条件框上方的竖线、竖向连线的位置
        Assert.Equal((g.Items[0].Cond.X + 14, g.Items[0].Cond.Y - 19, g.Items[0].Cond.Y - 1), g.Items[0].Connector);
        Assert.Equal(StepFlowLayout.PadX + StepFlowLayout.VLinkLineX, g.Items[0].Cond.X + 14);
    }
}
