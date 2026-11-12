using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.Models;
using Xunit;
using static ParallelReactor.Control.Tests.PageVectorData;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Layout.DeviceLayout 对照样稿：MockVectors/PageMockVectors.cs 的 dv-* / dx-* 场景（样稿在 WebKit 里实际渲染的设备页：1号机 HT-PR8 · 现场、
/// 2号机 HT-PR4、3号机 HT-PR16、断线的 4号机 HT-PR8、8 路装满的 PR8 × 1920 / 1600 / 1366 × 资源管理器停靠 / 收起 × 时间轴展开 / 收起 × stress=1、2 ×
/// 急停 + 断线），比较宽度类（下排三列 / 页签）、上排高度、卡片区滚动、网格（窄 / 紧 / 两排）、卡片位置与尺寸、卡片里的取舍
/// （釜体位置与尺寸、读数列宽、读数字号阶梯、设定值 / 目标的排法与 fitSubs、宽卡次要行、程序块）、下排面板宽高，以及窄工作区页签的自动选择。
/// </summary>
public class DeviceLayoutTests
{
    public static IEnumerable<object[]> Scenarios() => Names("device");

    private const double Px = 0.51;

    [Theory]
    [MemberData(nameof(Scenarios))]
    public void MatchesMock(string name)
    {
        var v = Get("device", name);
        var p = v.GetProperty("probe");
        var (model, online, ever, estop) = Device(p.GetProperty("dev"));
        double w = D(p.GetProperty("root")[0]), h = D(p.GetProperty("root")[1]);
        string tab = S(p.GetProperty("tab")) ?? "info";
        var page = DeviceLayout.Compute(new DeviceLayoutInput(w, h, model)
        {
            Online = online, EverOnline = ever, Estop = estop, LogTab = tab == "log" ? DeviceLogTab.Log : DeviceLogTab.Info,
        });
        var bad = new List<string>();
        void Check(bool ok, string what) { if (!ok) bad.Add(name + "：" + what); }

        Check(p.GetProperty("cls").GetString() == "device " + page.WidthClassName, "宽度类 " + page.WidthClassName);
        Check(page.Tabbed == (p.GetProperty("tab").ValueKind == JsonValueKind.String), "页签面板 " + page.Tabbed);
        string rows = S(p.GetProperty("gridStyle")) ?? "";
        var m = Regex.Match(rows, @"grid-template-rows:([\d.]+)px");
        Check(m.Success && page.TopRow is double top && Math.Abs(top - double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)) < 0.01, "上排 " + page.TopRow + " ≠ " + rows);
        Check(page.Bars == p.GetProperty("bars").GetArrayLength(), "说明条 " + page.Bars);
        string cardsCls = S(p.GetProperty("cardsCls")) ?? "";
        Check(cardsCls.Contains("device-cards--" + page.Grid.Kind.ToString().ToLowerInvariant()), "卡片种类 " + page.Grid.Kind + " ≠ " + cardsCls);
        Check(cardsCls.Contains("is-narrow") == page.Grid.Narrow, "is-narrow " + page.Grid.Narrow);
        Check(cardsCls.Contains("is-tight") == page.Grid.Tight, "is-tight " + page.Grid.Tight);
        Check(cardsCls.Contains("is-rows") == (page.Grid.Rows > 1), "is-rows " + page.Grid.Rows);
        Check(cardsCls.Contains("is-scroll") == page.Scroll, "is-scroll " + page.Scroll);
        string style = S(p.GetProperty("cardsStyle")) ?? "";
        Check(style.Contains("padding:" + page.Grid.Pad + "px") && style.Contains("gap:" + page.Grid.Gap + "px"), "内边距 / 间距 " + page.Grid.Pad + " / " + page.Grid.Gap);
        Check(Math.Abs(D(p.GetProperty("cardsBox")[1]) - page.CardsHeight) <= 1, "卡片区高 " + page.CardsHeight + " ≠ " + p.GetProperty("cardsBox")[1]);

        var items = p.GetProperty("items").EnumerateArray().ToArray();
        Check(items.Length == page.Tiles.Count, "块数 " + page.Tiles.Count + " ≠ " + items.Length);
        double barsH = page.Bars * DeviceLayout.Bar;
        for (int t = 0; t < Math.Min(items.Length, page.Tiles.Count); t++)
        {
            var it = items[t];
            var tile = page.Tiles[t];
            string tt = "第 " + (t + 1) + " 块 " + tile.Label;
            bool isCard = it[0].GetString() == "C";
            Check(isCard == tile.IsCard, tt + "：卡片 / 未安装不符");
            Check(Math.Abs(D(it[1]) - tile.X) <= 1 && Math.Abs(D(it[2]) - (barsH + tile.Y)) <= 1, tt + $"：位置 ({tile.X}, {barsH + tile.Y}) ≠ ({it[1]}, {it[2]})");
            Check(Math.Abs(D(it[3]) - tile.Width) <= Px && Math.Abs(D(it[4]) - tile.Height) <= Px, tt + $"：尺寸 {tile.Width}×{tile.Height} ≠ {it[3]}×{it[4]}");
            if (!isCard) continue;
            var o = it[7];
            // 标题行量到的高度（状态字换到第二行时 48）—— 文字宽度是界面量的，计算器接受量到的值
            double head = D(o.GetProperty("head"));
            var c = DeviceLayout.Card(page.Grid.Kind, tile.Width, tile.Height, page.Grid.Narrow, page.Grid.Tight, headHeight: head);
            Check(Math.Abs(D(it[5][0]) - c.Width) <= 1 && Math.Abs(D(it[5][1]) - c.Height) <= 1, tt + $"：内容盒 {c.Width}×{c.Height} ≠ {it[5]}");
            Check(D(o.GetProperty("prog")) == c.ProgHeight, tt + $"：程序块高 {c.ProgHeight} ≠ {o.GetProperty("prog")}");
            Check(Math.Abs(D(o.GetProperty("body")[1]) - c.BodyHeight) <= Px, tt + $"：主体高 {c.BodyHeight} ≠ {o.GetProperty("body")[1]}");
            string dir = o.GetProperty("dir").GetString()!;
            var vs = o.GetProperty("v");
            string st = S(o.GetProperty("st")) ?? "";
            string tem = S(o.GetProperty("tem")) ?? "", pem = S(o.GetProperty("pem")) ?? "";
            if (vs.ValueKind != JsonValueKind.Array) Check(c.Vessel == VesselPlacement.Hidden, tt + "：釜体应不画（算出 " + c.Vessel + "）");
            else
            {
                Check(c.Vessel == (dir == "column" ? VesselPlacement.Above : VesselPlacement.Left), tt + $"：釜体位置 {c.Vessel} ≠ {dir}");
                double vh = c.VesselHeightWith(tem == "line", pem == "line");
                Check(Math.Abs(D(vs[0]) - c.VesselWidth) <= Px && Math.Abs(D(vs[1]) - vh) <= Px, tt + $"：釜体 {c.VesselWidth}×{vh} ≠ {vs}");
            }
            Check(Math.Abs(D(o.GetProperty("reads")[0]) - c.ReadsWidth) <= Px, tt + $"：读数列宽 {c.ReadsWidth} ≠ {o.GetProperty("reads")[0]}");
            var len = o.GetProperty("len");
            int tl = Len(len[0]) ?? 0, pl = Len(len[1]) ?? 0;
            var tf = c.Temp(tl);
            var pf = c.Pressure(pl);
            var ot = o.GetProperty("t");
            var op = o.GetProperty("p");
            Check(D(ot[0]) == tf.Size && D(ot[1]) == tf.LineHeight && ot[2].GetBoolean() == tf.Unit, tt + $"：温度 {tf} ≠ {ot}（len {tl}，读数列 {c.ReadsWidth}）");
            Check(D(op[0]) == pf.Size && D(op[1]) == pf.LineHeight && op[2].GetBoolean() == pf.Unit, tt + $"：压力 {pf} ≠ {op}（len {pl}，读数列 {c.ReadsWidth}）");

            // 设定值 / 目标：排法 + fitSubs
            var (hideT, hideP) = c.FitSubs(tem.Length > 0, pem.Length > 0, st is "press" or "wait");
            string sub = o.GetProperty("sub").GetString()!;
            Check(sub == (hideT ? "T" : "") + (hideP ? "P" : ""), tt + $"：fitSubs {(hideT ? "T" : "")}{(hideP ? "P" : "")} ≠ '{sub}'（{c.Subs}，主体 {c.BodyHeight}）");
            foreach (var (em, hidden) in new[] { (tem, hideT), (pem, hideP) })
            {
                if (em.Length == 0) continue;
                string exp = c.Subs == SubLine.Hidden || hidden ? "none" : c.Subs == SubLine.OwnLine ? "line" : "inline|clipped";
                Check(exp.Split('|').Contains(em), tt + $"：小字 {exp} ≠ {em}");
            }
            // 宽卡次要行
            foreach (var (key, show) in new[] { ("stir", c.StirRow), ("heat", c.HeatRow), ("gas", c.GasRow) })
            {
                var kv = o.GetProperty(key);
                if (kv.ValueKind != JsonValueKind.Array) { Check(c.Kind != CardKind.Wide, tt + "：宽卡应有 " + key); continue; }
                bool vis = D(kv[0]) > 0;
                Check(vis == show, tt + $"：{key} 行 {show} ≠ {vis}");
                if (!vis) continue;
                int kl = Len(kv[3]) ?? 0;
                var f = key == "stir" ? c.StirValue(kl) : key == "gas" ? c.GasValue(kl) : c.HeatValue(kl);
                Check(D(kv[0]) == f.Size, tt + $"：{key} 字号 {f.Size} ≠ {kv[0]}（len {kl}）");
                if (key != "heat") Check(kv[1].GetBoolean() == f.Unit || S(kv[3]) is null, tt + $"：{key} 单位 {f.Unit} ≠ {kv[1]}");
                if (key == "stir") Check(kv[2].GetBoolean() == c.KvNotes, tt + $"：搅拌行说明 {c.KvNotes} ≠ {kv[2]}");
                else if (kv[2].GetBoolean()) Check(c.KvNotes, tt + $"：{key} 行说明应不显示");
            }
            // 程序块
            bool none = o.GetProperty("none").GetBoolean(), barfit = o.GetProperty("barfit").ValueKind == JsonValueKind.True;
            Check(o.GetProperty("name").GetBoolean() == c.ProgNameLine, tt + $"：程序名行 {c.ProgNameLine}");
            Check(o.GetProperty("kn").GetBoolean() == (c.ProgNameLine && c.StepCount && !none), tt + $"：第 k/n 步 {c.StepCount}");
            Check(o.GetProperty("rem").GetBoolean() == (none ? c.NoneRemaining : !c.Narrow && !barfit), tt + $"：剩余时间 ≠ {o.GetProperty("rem")}");
            Check(o.GetProperty("bar").GetBoolean() == (!none || c.NoneBar), tt + $"：进度条 ≠ {o.GetProperty("bar")}");
            if (c.HeadColumn) Check(head == 38, tt + "：窄紧凑卡标题两行 38");
        }

        // 下排面板
        var row = p.GetProperty("row").EnumerateArray().ToArray();
        Check(row.Length == page.Bottom.Count, "下排面板数 " + page.Bottom.Count + " ≠ " + row.Length);
        for (int i = 0; i < Math.Min(row.Length, page.Bottom.Count); i++)
        {
            var b = page.Bottom[i];
            string cls = row[i][0].GetString()!;
            string exp = b.Panel switch { DevicePanel.Resources => "device-res", DevicePanel.Info => "device-info", DevicePanel.Log => "device-log", _ => "device-tabs" };
            Check(cls == exp, "下排第 " + (i + 1) + " 块 " + exp + " ≠ " + cls);
            Check(Math.Abs(D(row[i][1]) - b.Width) <= Px && Math.Abs(D(row[i][2]) - b.Height) <= 1, $"下排 {exp} {b.Width}×{b.Height} ≠ {row[i][1]}×{row[i][2]}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(40)));
    }

    [Fact]
    public void VectorsCoverRequiredScenarios()
    {
        var names = Names("device").Select(o => (string)o[0]).ToHashSet();
        foreach (int w in new[] { 1920, 1600, 1366 })
        {
            for (int k = 1; k <= 3; k++)
                foreach (int i in new[] { 0, 3, 4, 6 })   // 默认 / 资源管理器收起 / 时间轴收起 / 两者都收起
                    Assert.Contains("dv-" + i + "-" + k + "-" + w, names);
            Assert.Contains("dx-0-4-" + w, names);       // 4号机（断线）
        }
    }

    // ———————————————————— 设备 1–4 × 三种尺寸（工作区：1920 = 1226×752、1600 = 936×592、1366 = 1002×460） ————————————————————

    private static ModelProfile M(string key) => ModelProfiles.Embedded.Get(key)!;

    [Theory]
    // 型号、工作区 → 宽度类、上排、网格（列 × 行、窄 / 紧）、滚动、卡片外框
    [InlineData("PR8-4", 1226, 752, true, DeviceWidthClass.L, 391, 4, 1, false, false, false, 253.5, 317)]
    [InlineData("PR8-4", 936, 592, true, DeviceWidthClass.M, 308, 4, 1, false, false, false, 181, 234)]
    [InlineData("PR8-4", 1002, 460, true, DeviceWidthClass.M, 240, 4, 1, false, false, false, 197.5, 166)]
    [InlineData("PR4", 1226, 752, true, DeviceWidthClass.L, 391, 4, 1, false, false, false, 291, 317)]
    [InlineData("PR4", 936, 592, true, DeviceWidthClass.M, 308, 4, 1, false, false, false, 218.5, 234)]
    [InlineData("PR4", 1002, 460, true, DeviceWidthClass.M, 244, 4, 1, false, false, false, 235, 170)]
    [InlineData("PR16", 1226, 752, true, DeviceWidthClass.L, 391, 8, 2, false, false, false, 143.25, 158.5)]
    [InlineData("PR16", 936, 592, true, DeviceWidthClass.M, 308, 8, 2, true, false, false, 107, 117)]
    [InlineData("PR16", 1002, 460, true, DeviceWidthClass.M, 300, 8, 2, true, false, false, 115.25, 113)]
    [InlineData("PR8", 1226, 752, false, DeviceWidthClass.L, 391, 8, 1, false, true, false, 140.5, 272)]       // 4号机断线：说明条 45
    [InlineData("PR8", 936, 592, false, DeviceWidthClass.M, 436, 4, 2, false, false, true, 216.5, 164)]         // 折成两排、卡片区滚动（滚动条 8 px）
    [InlineData("PR8", 1002, 460, false, DeviceWidthClass.M, 304, 8, 1, false, true, false, 112.5, 185)]
    public void Devices_ThreeSizes(string key, double w, double h, bool online, DeviceWidthClass wc, double top, int cols, int rows, bool narrow, bool tight, bool scroll, double cardW, double cardH)
    {
        var p = DeviceLayout.Compute(new DeviceLayoutInput(w, h, M(key)) { Online = online });
        Assert.Equal(wc, p.W);
        Assert.Equal(wc == DeviceWidthClass.M, p.Tabbed);
        Assert.Equal(top, p.TopRow);
        Assert.Equal((cols, rows, narrow, tight), (p.Grid.Cols, p.Grid.Rows, p.Grid.Narrow, p.Grid.Tight));
        Assert.Equal(scroll, p.Scroll);
        Assert.Equal(cardW, p.CardWidth, 3);
        Assert.Equal(cardH, p.CardHeight, 3);
    }

    [Fact]
    public void ExplorerCollapsed_And_TimelineCollapsed()
    {
        // 1920 收起资源管理器：工作区 1526；1600 收起：1236（≥ 1100 → 下排三列）；时间轴收起：+180（1920 932、1600 752、1366 620）
        var a = DeviceLayout.Compute(new DeviceLayoutInput(1526, 752, M("PR8-4")));
        Assert.Equal(DeviceWidthClass.L, a.W);
        Assert.Equal(VesselPlacement.Above, a.Card.Vessel);                    // 一排卡够高够宽：釜体在上
        var b = DeviceLayout.Compute(new DeviceLayoutInput(1236, 592, M("PR16")));
        Assert.Equal(DeviceWidthClass.L, b.W);
        Assert.Equal(348, b.TopRow);                                          // 卡片放不下：上排加高
        var c = DeviceLayout.Compute(new DeviceLayoutInput(1226, 932, M("PR8-4")));
        Assert.Equal(485, c.TopRow);                                          // 52 %
        var d = DeviceLayout.Compute(new DeviceLayoutInput(1002, 620, M("PR4")));
        Assert.Equal(322, d.TopRow);
        Assert.Equal(VesselPlacement.Left, d.Card.Vessel);
        Assert.True(d.Card.HeatRow);
        Assert.False(d.Card.GasRow);                                          // 宽卡按高度：先去耗气
    }

    [Fact]
    public void Wide_KvRowsHideByHeight()
    {
        // HT-PR4 宽卡：内容高 ≤ 259 去耗气，≤ 199 去加热，≤ 139 去搅拌（device.css:125-127）
        double chrome = 2 + 12 + 13;
        var c260 = DeviceLayout.Card(CardKind.Wide, 300, 260 + chrome);
        Assert.True(c260.StirRow && c260.HeatRow && c260.GasRow);
        var c259 = DeviceLayout.Card(CardKind.Wide, 300, 259 + chrome);
        Assert.True(c259.StirRow && c259.HeatRow && !c259.GasRow);
        var c199 = DeviceLayout.Card(CardKind.Wide, 300, 199 + chrome);
        Assert.True(c199.StirRow && !c199.HeatRow && !c199.GasRow);
        var c139 = DeviceLayout.Card(CardKind.Wide, 300, 139 + chrome);
        Assert.True(!c139.StirRow && !c139.HeatRow && !c139.GasRow);
        Assert.Equal(VesselPlacement.Above, DeviceLayout.Card(CardKind.Wide, 300, 420 + chrome).Vessel);   // 面板放大：釜体在上
    }

    [Fact]
    public void Row_VesselPlacement()
    {
        double cx = 2 + 32, cy = 2 + 25;
        Assert.Equal(VesselPlacement.Above, DeviceLayout.Card(CardKind.Row, 190 + cx, 200 + cy).Vessel);
        Assert.Equal(VesselPlacement.Left, DeviceLayout.Card(CardKind.Row, 189 + cx, 200 + cy).Vessel);
        Assert.Equal(VesselPlacement.Above, DeviceLayout.Card(CardKind.Row, 129 + cx, 200 + cy).Vessel);   // 很窄但够高：釜体在上
        Assert.Equal(VesselPlacement.Left, DeviceLayout.Card(CardKind.Row, 190 + cx, 199 + cy).Vessel);
        // 紧卡（8 路一排每张 < 150）矮时不画釜体
        Assert.Equal(VesselPlacement.Hidden, DeviceLayout.Card(CardKind.Row, 113, 190, tight: true).Vessel);
        // 窄（130–189）横排：釜体最多占 26 %
        var c = DeviceLayout.Card(CardKind.Row, 181, 234);
        Assert.Equal(0.26 * 147, c.VesselWidth, 6);
        Assert.Equal(147 - 0.26 * 147 - 14, c.ReadsWidth, 6);
    }

    [Fact]
    public void Compact_NarrowAndMaximized()
    {
        var narrow = DeviceLayout.Card(CardKind.Compact, 107, 117, narrow: true);
        Assert.True(narrow.HeadColumn);
        Assert.Equal(VesselPlacement.Hidden, narrow.Vessel);
        Assert.Equal(SubLine.Hidden, narrow.Subs);
        Assert.Equal(new ReadingFont(24, 27), narrow.Temp(4));
        Assert.Equal(new ReadingFont(20, 27), narrow.Temp(5));               // 87 px 读数列：199.9 → 20
        var max = DeviceLayout.Card(CardKind.Compact, 150, 400, narrow: true, maximized: true);
        Assert.Equal(VesselPlacement.Above, max.Vessel);                      // 很高时釜体在上（窄卡也画）
        Assert.True(max.FullVessel);
        var p = DeviceLayout.Compute(new DeviceLayoutInput(1002, 460, M("PR16")) { RvMax = true });
        Assert.False(p.Scroll);
        Assert.True(p.Card.FullVessel);
        Assert.Equal((460 - 42 - 24 - 8) / 2.0, p.CardHeight, 6);           // 放大：卡片铺满整页
    }

    [Fact]
    public void Temp_LongReadingLadder_NonCompact()
    {
        // 4 位温度「199.9 ℃」：30 → 26（≤ 111.8）→ 24（≤ 100.5）→ 22（≤ 94.7）→ 20（≤ 88.9）→ 去单位（≤ 83.4）；行高跟宽度那一档
        ReadingFont T(double reads) => DeviceLayout.Card(CardKind.Row, reads + 34, 400).Temp(5);
        Assert.Equal(new ReadingFont(30, 36), DeviceLayout.Card(CardKind.Row, 220 + 34, 400).Temp(5));
        Assert.Equal(new ReadingFont(26, 36), T(110));
        Assert.Equal(new ReadingFont(24, 36), T(100));
        Assert.Equal(new ReadingFont(22, 32), T(94));
        Assert.Equal(new ReadingFont(20, 30), T(88));
        Assert.Equal(new ReadingFont(20, 28, false), T(80));
        Assert.Equal(new ReadingFont(30, 36), DeviceLayout.Card(CardKind.Row, 100 + 34, 400).Temp(4));   // 短读数：只跟宽度那一档
    }

    [Fact]
    public void FitSubs_HidesTargetFirst_SetpointFirstWhenPressing()
    {
        // 8 路一排窄卡（读数列 < 190）：设定值和目标各自单独一行；竖卡（釜体在上）主体只放得下一行小字时：先藏目标，升压中 / 等待中先藏设定值
        var c = DeviceLayout.Card(CardKind.Row, 113, 290, tight: true, headHeight: 48);
        Assert.Equal(SubLine.OwnLine, c.Subs);
        Assert.Equal(VesselPlacement.Above, c.Vessel);
        Assert.Equal((false, true), c.FitSubs(hasSetpoint: true, hasTarget: true, pressFirst: false));
        Assert.Equal((true, false), c.FitSubs(hasSetpoint: true, hasTarget: true, pressFirst: true));
        Assert.Equal((false, false), c.FitSubs(hasSetpoint: true, hasTarget: false, pressFirst: false));
        // 更矮：两行都藏
        var lower = DeviceLayout.Card(CardKind.Row, 113, 270, tight: true, headHeight: 48);
        Assert.Equal((true, true), lower.FitSubs(true, true, false));
        var roomy = DeviceLayout.Card(CardKind.Row, 254, 317);
        Assert.Equal(SubLine.Inline, roomy.Subs);
        Assert.Equal((false, false), roomy.FitSubs(true, true, false));
        Assert.Equal(SubLine.Hidden, DeviceLayout.Card(CardKind.Row, 254, 179 + 27).Subs);   // 内容矮于 180：不显示
    }

    [Fact]
    public void NeverConnected_And_ClosedPanels()
    {
        var never = DeviceLayout.Compute(new DeviceLayoutInput(1226, 752, M("PR4")) { EverOnline = false, Online = false });
        Assert.True(never.NeverConnected);
        Assert.Equal(42 + 110, never.TopRow);
        var rvClosed = DeviceLayout.Compute(new DeviceLayoutInput(1226, 752, M("PR4")) { RvClosed = true });
        Assert.Null(rvClosed.TopRow);
        Assert.Equal(752, rvClosed.BottomHeight);
        var bottomOff = DeviceLayout.Compute(new DeviceLayoutInput(1226, 752, M("PR4")) { ResClosed = true, InfoClosed = true, LogClosed = true });
        Assert.Null(bottomOff.TopRow);
        Assert.Empty(bottomOff.Bottom);
        var oneClosed = DeviceLayout.Compute(new DeviceLayoutInput(1226, 752, M("PR4")) { InfoClosed = true });
        Assert.Equal(2, oneClosed.Bottom.Count);
        Assert.Equal((1226 - 1) / 2.0, oneClosed.Bottom[0].Width, 6);         // 其余平分
    }

    [Fact]
    public void BottomPanels_Split()
    {
        var l = DeviceLayout.Compute(new DeviceLayoutInput(1226, 752, M("PR8-4")));
        Assert.Equal(new[] { DevicePanel.Resources, DevicePanel.Info, DevicePanel.Log }, l.Bottom.Select(b => b.Panel).ToArray());
        Assert.Equal(new[] { 1224 / 3.2, 1224 / 3.2, 1224 * 1.2 / 3.2 }, l.Bottom.Select(b => b.Width).ToArray());
        Assert.Equal(360, l.BottomHeight);
        var m = DeviceLayout.Compute(new DeviceLayoutInput(936, 592, M("PR8-4")));
        Assert.Equal(new[] { DevicePanel.Resources, DevicePanel.Tabs }, m.Bottom.Select(b => b.Panel).ToArray());
        Assert.Equal(935 / 2.3, m.Bottom[0].Width, 6);
        // 报警与事件内容高 ≤ 259：整块一起滚动
        Assert.True(DeviceLayout.Bottom(DevicePanel.Tabs, 528, 283, DeviceLogTab.Log).Short);
        Assert.False(DeviceLayout.Bottom(DevicePanel.Log, 459, 360).Short);
        Assert.True(DeviceLayout.Bottom(DevicePanel.Resources, 382, 283).Narrow);    // 共用资源内容 ≤ 350
        Assert.True(DeviceLayout.Bottom(DevicePanel.Info, 371, 283).Narrow);         // 设备信息内容 ≤ 339
    }

    // ———————————————————— 窄工作区页签的自动选择（device.js syncTab） ————————————————————

    public static IEnumerable<object[]> TabScenarios() => Names("texts").Where(o => ((string)o[0]).StartsWith("tt-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(TabScenarios))]
    public void LogTab_FirstPaint_MatchesMock(string name)
    {
        var v = Get("texts", name);
        var p = v.GetProperty("probe");
        int k = int.Parse(Regex.Match(v.GetProperty("route").GetString()!, @"/device/(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var f = Fleet(v);
        var d = f.Device(k)!;
        var book = new AlarmBook();
        book.Sync(d.Alarms);
        var rule = new DeviceLogTabRule();
        var tab = rule.Sync(book.UnackedErrIds());
        var tabs = p.GetProperty("tabs").EnumerateArray().ToArray();
        Assert.Equal(2, tabs.Length);
        bool logOn = tabs[1].GetProperty("on").GetBoolean();
        Assert.Equal(logOn ? DeviceLogTab.Log : DeviceLogTab.Info, tab);
        var badge = AlarmTexts.LogTab(book.Current, tab == DeviceLogTab.Log);
        Assert.Equal(tabs[1].GetProperty("dot").GetBoolean(), badge.Dot);
        Assert.Equal(S(tabs[1].GetProperty("n")) ?? "", badge.Count > 0 ? badge.Count.ToString(CultureInfo.InvariantCulture) : "");
        Assert.Equal(S(tabs[1].GetProperty("tip")) ?? "", badge.Tip);
        Assert.Equal("设备信息", Norm(tabs[0].GetProperty("text").GetString()));
        Assert.Equal("报警与事件" + (badge.Count > 0 ? badge.Count.ToString(CultureInfo.InvariantCulture) : ""), Norm(tabs[1].GetProperty("text").GetString()));
    }

    [Fact]
    public void LogTab_OnlyNewErrorsSwitch_AckNeverSwitches()
    {
        var rule = new DeviceLogTabRule();
        Assert.Equal(DeviceLogTab.Info, rule.Sync(Array.Empty<string>()));       // 第一次：没有未确认的错误报警 → 设备信息
        Assert.Equal(DeviceLogTab.Log, rule.Sync(new[] { "a1" }));               // 新的错误报警 → 报警与事件
        rule.Pick(DeviceLogTab.Info);                                            // 用户点回设备信息
        Assert.Equal(DeviceLogTab.Info, rule.Sync(new[] { "a1" }));
        Assert.Equal(DeviceLogTab.Info, rule.Sync(new[] { "a1", "a2" }));       // 用户点过：新的报警也不换
        var r2 = new DeviceLogTabRule();
        Assert.Equal(DeviceLogTab.Log, r2.Sync(new[] { "a1" }));                 // 打开时就有 → 报警与事件
        Assert.Equal(DeviceLogTab.Log, r2.Sync(Array.Empty<string>()));          // 确认了：页签不变
        var r3 = new DeviceLogTabRule();
        Assert.Equal(DeviceLogTab.Info, r3.Sync(Array.Empty<string>()));
        Assert.Equal(DeviceLogTab.Info, r3.Sync(Array.Empty<string>()));
        Assert.False(r3.UserPicked);
    }
}
