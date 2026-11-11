using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using Xunit;
using static ParallelReactor.Control.Tests.PageVectorData;
using CoreKeep = ParallelReactor.Control.Core.Text.Keep;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 设备页（PORT-PLAN §2.2「设备页」、§5 M4；样稿 js/pages/device.js + css/device.css）的视图模型：
/// <list type="bullet">
/// <item>版面：演示数据 → DeviceVm 的在线 / 连上过 / 急停 → 页签规则 → Core 的 DeviceLayout，整条链路与样稿在 WebKit 里实际渲染的结果逐个场景对照
///   （MockVectors 的 dv-* / dx-*：宽度类、上排高度、说明条、卡片区滚动与高度、网格的取舍、块的顺序与身份、下排面板、窄工作区打开时的页签）。</item>
/// <item>报警与事件：td-* / tt-* 的当前报警卡片、事件筛选的三段与列表、页签上的红点 / 数字 / 提示（含先确认一条再看）。</item>
/// <item>卡片 / 面板的文字（逐字照样稿）、从未连上的设备、页签的自动选择（只有新的错误报警、用户没点过才切；确认从不换）、
///   同一条报警只有一个「确认」（页面拥有 → 检查器写「确认在「报警与事件」」）、反应釜面板放大时紧凑卡画完整釜体。</item>
/// </list>
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口；设备页的尺寸 = 外壳给工作区的尺寸（1920 → 1226 × 752，1600 → 936 × 592，1366 → 1002 × 460）。
/// 实时数据（报警簿 / 事件簿、模块通讯与设置的轮询节奏）见 LiveDeviceSourceTests.M4.cs。
/// </summary>
public class DeviceTests
{
    private static (ShellVm Shell, DevicePageVm Page) Open(int k, string query = "freeze=1", double width = 1226, double height = 752)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(p, Route.Device(k));
        var page = Assert.IsType<DevicePageVm>(s.Page);
        page.SetViewport(width, height);
        return (s, page);
    }

    private static DeviceCardVm Card(DevicePageVm p, int n) => p.Tiles.OfType<DeviceCardVm>().Single(c => c.N == n);

    private static string TileKey(int k, object t) => t switch
    {
        DeviceCardVm c => "dvc-" + k + "-" + c.N,
        DeviceUntileVm u => "dvu-" + k + "-" + u.From + "-" + u.To,
        _ => "?",
    };

    private static async Task AckAsync(ShellVm s, DevicePageVm p, string id)
    {
        var card = p.Log.Alarms.Single(a => a.Id == id);
        await ((IAsyncRelayCommand)card.AckCommand).ExecuteAsync(null);
        s.Fleet.RefreshNow();
    }

    // ———————————————————— 版面：与样稿逐个场景对照 ————————————————————

    public static IEnumerable<object[]> LayoutScenarios() => Names("device");

    [Theory]
    [MemberData(nameof(LayoutScenarios))]
    public void Layout_FromDemoData_MatchesMock(string name)
    {
        var v = Get("device", name);
        var pr = v.GetProperty("probe");
        var dev = pr.GetProperty("dev");
        int k = dev.GetProperty("k").GetInt32();
        double w = D(pr.GetProperty("root")[0]), h = D(pr.GetProperty("root")[1]);
        var (_, p) = Open(k, Query(v), w, h);
        var d = p.D!;
        var bad = new List<string>();
        void Check(bool ok, string what) { if (!ok) bad.Add(name + "：" + what); }

        // 演示数据给出的设备与样稿的同一台（型号、在线、连上过、急停）
        var (model, online, ever, estop) = Device(dev);
        Check(d.Model.Key == model.Key && !d.IsOffline == online && d.EverOnline == ever && d.EstopActive == estop,
            $"设备 {d.Model.Key} 在线 {!d.IsOffline} 连上过 {d.EverOnline} 急停 {d.EstopActive}");
        var L = p.Layout!;
        Check(pr.GetProperty("cls").GetString() == "device " + L.WidthClassName, "宽度类 " + L.WidthClassName);
        // 窄工作区：打开时的页签（样稿 syncTab 的第一次绘制）
        string? tab = S(pr.GetProperty("tab"));
        Check(p.Tabbed == (tab != null), "页签面板 " + p.Tabbed);
        if (tab != null)
        {
            Check(p.TabValue == tab, "打开时的页签 " + p.TabValue + " ≠ " + tab);
            Check(ReferenceEquals(p.TabsContent, tab == "log" ? p.Log : p.Info), "页签面板的内容");
            Check(p.InfoContent == null && p.LogContent == null, "页签模式下三列的内容为空");
        }
        else Check(ReferenceEquals(p.InfoContent, p.Info) && ReferenceEquals(p.LogContent, p.Log) && p.TabsContent == null, "三列的内容");
        var top = Regex.Match(S(pr.GetProperty("gridStyle")) ?? "", @"grid-template-rows:([\d.]+)px");
        Check(top.Success && p.TopRow is double t && Math.Abs(t - double.Parse(top.Groups[1].Value, CultureInfo.InvariantCulture)) < 0.01, "上排 " + p.TopRow);
        Check((p.HasOffBar ? 1 : 0) + (p.HasEstopBar ? 1 : 0) == pr.GetProperty("bars").GetArrayLength(), $"说明条 断线 {p.HasOffBar} 急停 {p.HasEstopBar}");
        Check(p.IsNever == pr.GetProperty("never").GetBoolean(), "从未连上 " + p.IsNever);

        // 卡片区：滚动与内容高度（滚动时卡片保持最小高度：内边距 + 行 × 最小高 + 行距）
        string cardsCls = S(pr.GetProperty("cardsCls")) ?? "", style = S(pr.GetProperty("cardsStyle")) ?? "";
        Check(cardsCls.Contains("is-scroll") == p.Scroll, "滚动 " + p.Scroll);
        var g = L.Grid;
        if (p.Scroll)
        {
            var mm = Regex.Match(style, @"minmax\(([\d.]+)px");
            double minH = mm.Success ? double.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture) : double.NaN;
            Check(Math.Abs(p.CardsHeight - (2 * g.Pad + g.Rows * minH + (g.Rows - 1) * g.Gap)) < 0.01, $"滚动时卡片区内容高 {p.CardsHeight}（{style}）");
        }
        else if (!p.IsNever) Check(Math.Abs(p.CardsHeight - D(pr.GetProperty("cardsBox")[1])) <= 1, "卡片区高 " + p.CardsHeight + " ≠ " + pr.GetProperty("cardsBox")[1]);

        // 块：顺序与身份（卡片按 RV 号、未安装段合并成窄列）；网格给卡片的取舍
        var items = pr.GetProperty("items").EnumerateArray().Select(it => it[it[0].GetString() == "C" ? 6 : 5].GetString()).ToArray();
        var mine = p.Tiles.Select(x => TileKey(k, x)).ToArray();
        Check(items.SequenceEqual(mine), "块 " + string.Join(",", mine) + " ≠ " + string.Join(",", items));
        // 每张卡片的设定值 / 目标（有没有：视图模型从演示数据得出的文字）与 fitSubs 的取舍（Core 的卡片版面 + 视图模型的「升压中 / 等待中先藏设定值」）
        var its = pr.GetProperty("items").EnumerateArray().ToArray();
        for (int i = 0; i < Math.Min(its.Length, Math.Min(p.Tiles.Count, L.Tiles.Count)); i++)
        {
            if (its[i][0].GetString() != "C" || p.Tiles[i] is not DeviceCardVm card) continue;
            var o = its[i][7];
            string tem = S(o.GetProperty("tem")) ?? "", pem = S(o.GetProperty("pem")) ?? "";
            Check((tem.Length > 0) == (card.TSub.Length > 0) && (pem.Length > 0) == (card.PSub.Length > 0),
                $"RV{card.N} 设定值「{card.TSub}」/ 目标「{card.PSub}」≠ 样稿 {tem} / {pem}");
            Check(card.PressFirst == (S(o.GetProperty("st")) is "press" or "wait"), $"RV{card.N} 先藏设定值 {card.PressFirst}");
            var cl = DeviceLayout.Card(g.Kind, L.Tiles[i].Width, L.Tiles[i].Height, g.Narrow, g.Tight, headHeight: D(o.GetProperty("head")));
            var (hideT, hideP) = cl.FitSubs(card.TSub.Length > 0, card.PSub.Length > 0, card.PressFirst);
            Check(o.GetProperty("sub").GetString() == (hideT ? "T" : "") + (hideP ? "P" : ""), $"RV{card.N} fitSubs {(hideT ? "T" : "")}{(hideP ? "P" : "")}");
        }
        foreach (var c in p.Tiles.OfType<DeviceCardVm>())
        {
            Check(c.Spec == new DvCardSpec(g.Kind, g.Narrow, g.Tight, false), $"RV{c.N} 网格取舍 {c.Spec}");
            Check(cardsCls.Contains("is-narrow") == c.Spec.Narrow && cardsCls.Contains("is-tight") == c.Spec.Tight, $"RV{c.N} 窄 / 紧");
            Check(c.IsCompact == cardsCls.Contains("device-cards--compact") && c.SimpleVessel == c.IsCompact, $"RV{c.N} 紧凑卡 {c.IsCompact}");
            Check(c.IsWide == cardsCls.Contains("device-cards--wide"), $"RV{c.N} 宽卡 {c.IsWide}");
            Check(c.IsOff == !online, $"RV{c.N} 断线样式 {c.IsOff}");
        }

        // 下排：面板、宽高，以及内容区的取舍（共用资源 ≤ 350 / 设备信息 ≤ 339 窄；报警与事件 ≤ 259 矮）
        var row = pr.GetProperty("row").EnumerateArray().ToArray();
        Check(row.Length == L.Bottom.Count, "下排面板数 " + L.Bottom.Count);
        for (int i = 0; i < Math.Min(row.Length, L.Bottom.Count); i++)
        {
            var b = L.Bottom[i];
            string exp = b.Panel switch { DevicePanel.Resources => "device-res", DevicePanel.Info => "device-info", DevicePanel.Log => "device-log", _ => "device-tabs" };
            Check(row[i][0].GetString() == exp, "下排第 " + (i + 1) + " 块 " + exp);
            Check(Math.Abs(D(row[i][1]) - b.Width) <= 0.51 && Math.Abs(D(row[i][2]) - b.Height) <= 1, $"下排 {exp} {b.Width}×{b.Height}");
            // 内容区的取舍交给面板（数值本身由 Core 的 DeviceLayoutTests 对照样稿）
            if (b.Panel == DevicePanel.Resources) Check(p.Res.Narrow == b.Narrow && p.ResNarrow == b.Narrow, "共用资源窄 " + p.Res.Narrow);
            if (b.Panel == DevicePanel.Info || b.Panel == DevicePanel.Tabs && tab == "info") Check(p.Info.Narrow == b.Narrow, "设备信息窄 " + p.Info.Narrow);
            if (b.Panel == DevicePanel.Log || b.Panel == DevicePanel.Tabs && tab == "log")
                Check(p.Log.LogShort == b.Short && Math.Abs(p.Log.BodyHeight - (b.Height - DeviceLayout.Head)) < 0.01, $"报警与事件 矮 {p.Log.LogShort} 内容高 {p.Log.BodyHeight}");
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(40)));
    }

    // ———————————————————— 报警与事件：与样稿逐个场景对照 ————————————————————

    public static IEnumerable<object[]> LogScenarios() => Names("texts").Where(o => ((string)o[0]).StartsWith("td-", StringComparison.Ordinal) || ((string)o[0]).StartsWith("tt-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(LogScenarios))]
    public async Task Log_FromDemoData_MatchesMock(string name)
    {
        var v = Get("texts", name);
        var pr = v.GetProperty("probe");
        int k = int.Parse(Regex.Match(v.GetProperty("route").GetString()!, @"/device/(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        bool narrow = v.GetProperty("w").GetInt32() == 1600;
        var (s, p) = Open(k, Query(v), narrow ? 936 : 1226, narrow ? 592 : 752);
        // 探针前的操作：确认这台设备的第一条报警（用页面上的「确认」）
        if ((v.GetProperty("pre").GetString() ?? "").Length > 0) await AckAsync(s, p, s.Fleet.DemoData!.Device(k)!.Alarms[0].Id);
        var bad = new List<string>();
        void Check(bool ok, string what) { if (!ok) bad.Add(name + "：" + what); }

        // 窄工作区的页签（探针先读页签，再点「报警与事件」）
        var tabs = pr.GetProperty("tabs").EnumerateArray().ToArray();
        Check(p.Tabbed == (tabs.Length == 2), "页签面板 " + p.Tabbed);
        if (tabs.Length == 2)
        {
            Check(p.TabValue == (tabs[1].GetProperty("on").GetBoolean() ? "log" : "info"), "打开时的页签 " + p.TabValue);
            Check(p.InfoTab.Label == Norm(tabs[0].GetProperty("text").GetString()), "页签 1 " + p.InfoTab.Label);
            Check(p.LogTab.Dot == tabs[1].GetProperty("dot").GetBoolean(), "页签红点 " + p.LogTab.Dot);
            Check((p.LogTab.Count ?? "") == (S(tabs[1].GetProperty("n")) ?? ""), "页签数字 " + p.LogTab.Count);
            Check((p.LogTab.Tip ?? "") == (S(tabs[1].GetProperty("tip")) ?? ""), "页签提示 " + p.LogTab.Tip);
            Check(p.LogTab.Label + (p.LogTab.Count ?? "") == Norm(tabs[1].GetProperty("text").GetString()), "页签 2 " + p.LogTab.Label);
            bool wasOn = p.TabValue == "log";
            p.TabValue = "log";
            Check(ReferenceEquals(p.TabsContent, p.Log) && p.TabRule.UserPicked == !wasOn, "点「报警与事件」");
        }
        var log = p.Log;
        Check(log.UnackedText == (S(pr.GetProperty("unackSub")) ?? ""), "标题栏副标题 " + log.UnackedText);
        // 页签面板里副标题前不加红点（页签上已经有）—— 视图只在三列的面板上画 UnackedDot
        if (!p.Tabbed) Check(log.UnackedDot == pr.GetProperty("unackDot").GetBoolean(), "副标题红点 " + log.UnackedDot);
        else Check(!pr.GetProperty("unackDot").GetBoolean(), "样稿的页签面板没有副标题红点");
        var head = pr.GetProperty("alarmHead");
        Check(log.Head == head[0].GetString() && log.HeadSub == (S(head[1]) ?? ""), $"当前报警标题 {log.Head} / {log.HeadSub}");

        var alarms = pr.GetProperty("alarms").EnumerateArray().ToArray();
        Check(log.Alarms.Count == alarms.Length && log.HasAlarms == (alarms.Length > 0), "报警卡片数 " + log.Alarms.Count);
        if (alarms.Length == 0) Check(S(pr.GetProperty("noAlarm")) == "当前没有报警", "没有报警时的一行");
        for (int i = 0; i < Math.Min(alarms.Length, log.Alarms.Count); i++)
        {
            var a = alarms[i];
            var vm = log.Alarms[i];
            string cls = a.GetProperty("cls").GetString()!, at = "第 " + (i + 1) + " 条报警";
            Check("dva-" + vm.Key == a.GetProperty("key").GetString(), at + " 身份 " + vm.Key);
            Check(cls.Contains("is-err") == vm.IsErr && cls.Contains("is-warn") == !vm.IsErr && cls.Contains("is-acked") == vm.Acked, at + " 样式 " + cls);
            Check(vm.Icon == (vm.IsErr ? "alert" : "warn"), at + " 图标 " + vm.Icon);
            Check(Norm(vm.Text) == Norm(a.GetProperty("b").GetString()) && vm.TextTip == a.GetProperty("bTitle").GetString(), at + " 文字 " + vm.Text);
            Check(Norm(vm.Meta) == Norm(a.GetProperty("meta").GetString()) && vm.MetaFull == a.GetProperty("full").GetString(), at + " 时间行 " + vm.Meta + " / " + vm.MetaFull);
            Check(!vm.Acked == a.GetProperty("ack").GetBoolean(), at + " 「确认」");
        }

        // 最近事件：逐个点三段筛选
        var segs = new[] { log.SegAll, log.SegAlarm, log.SegOp };
        foreach (var f in new[] { "all", "alarm", "op" })
        {
            log.FilterValue = f;
            var ev = pr.GetProperty("ev").GetProperty(f);
            var ps = ev.GetProperty("segs").EnumerateArray().ToArray();
            for (int i = 0; i < 3; i++)
            {
                Check((string?)segs[i].Value == ps[i][0].GetString() && segs[i].Label == Norm(ps[i][1].GetString()), f + "：分段 " + segs[i].Label);
                Check((log.FilterValue == (string?)segs[i].Value) == ps[i][2].GetBoolean(), f + "：选中的分段");
            }
            var evs = ev.GetProperty("evs").EnumerateArray().ToArray();
            Check(log.Events.Count == evs.Length && log.HasEvents == (evs.Length > 0), f + "：事件数 " + log.Events.Count + " ≠ " + evs.Length);
            for (int i = 0; i < Math.Min(evs.Length, log.Events.Count); i++)
            {
                var e = evs[i];
                var vm = log.Events[i];
                string cls = vm.IsErr ? "is-err" : vm.IsWarn ? "is-warn" : vm.HasIcon ? "is-op" : "is-info", at = f + "：第 " + (i + 1) + " 条事件";
                Check("dve-" + vm.Key == e[0].GetString() && cls == e[1].GetString(), at + " " + vm.Key + " " + cls);
                Check(vm.Time == e[2].GetString() && vm.Rv == e[3].GetString() && vm.IsDevice == e[4].GetBoolean(), at + $" {vm.Time} {vm.Rv}");
                Check(Norm(CoreKeep.Plain(vm.Text)) == Norm(e[5].GetString()) && vm.Tip == e[6].GetString(), at + " 文字 " + vm.Text);
            }
            if (evs.Length == 0) Check(log.EmptyText == S(ev.GetProperty("empty")), f + "：空列表 " + log.EmptyText);
        }
        Assert.True(bad.Count == 0, string.Join("\n", bad.Take(40)));
    }

    // ———————————————————— 卡片：块的身份与文字 ————————————————————

    [Fact]
    public void Tiles_PerModel_StableIdentity()
    {
        var (s, p) = Open(1);
        Assert.Equal(new[] { "dvc-1-1", "dvc-1-2", "dvc-1-3", "dvc-1-4", "dvu-1-5-8" }, p.Tiles.Select(t => TileKey(1, t)));
        var un = Assert.IsType<DeviceUntileVm>(p.Tiles[4]);
        Assert.Equal(("RV5–RV8", "1号机 · RV5–RV8 未安装（HT-PR8 · 现场）· 不参与运行"), (un.Label, un.Tip));
        var first = p.Tiles.ToArray();
        // 刷新、换尺寸、换不影响型号的参数：同一批实例（就地更新，R10）
        s.Fleet.RefreshNow();
        p.SetViewport(936, 592);
        s.ApplyDemoParams(DemoParams.Parse("freeze=1&alarm=1:1"), Route.Device(1));
        Assert.Same(p, s.Page);
        Assert.Equal(first, p.Tiles.ToArray());
        // 换了型号（8 路装满）：重排，8 张卡片、没有未安装列
        s.ApplyDemoParams(DemoParams.Parse("freeze=1&devs=PR8,PR4,PR16&offline=none"), Route.Device(1));
        var pp = Assert.IsType<DevicePageVm>(s.Page);
        Assert.Equal(Enumerable.Range(1, 8).Select(n => "dvc-1-" + n), pp.Tiles.Select(t => TileKey(1, t)));
        // HT-PR4：宽卡 4 张；HT-PR16：紧凑卡 16 张
        var (_, p2) = Open(2);
        Assert.Equal(4, p2.Tiles.Count);
        Assert.All(p2.Tiles.Cast<DeviceCardVm>(), c => Assert.True(c.IsWide && !c.IsCompact));
        var (_, p3) = Open(3);
        Assert.Equal(16, p3.Tiles.Count);
        Assert.All(p3.Tiles.Cast<DeviceCardVm>(), c => Assert.True(c.IsCompact && c.SimpleVessel));
    }

    /// <summary>
    /// 复核（中）：设备页卡片的程序没了 —— 段列表设成 null，步骤条（StepBar.Sync）清空：没有程序时这里照样可能画空轨（NoneBar），不能画成旧的分段。
    /// </summary>
    [Fact]
    public void Card_ProgramGone_StepBarSegmentsCleared()
    {
        var (s, p) = Open(1);
        var c = Card(p, 1);
        var d = s.Fleet.Device(1)!;
        var bar = new List<Controls.Pr.StepSeg>();
        Controls.Pr.StepBar.Sync(bar, c.Segments);
        Assert.Equal(4, bar.Count);
        var rd = new Services.ChannelReading(1, true, StateKey.Ready, "已启用", StateKey.Ready, "已启用", AlarmCause.None,
            24, null, 0.005, null, 0, 0, false, false, null);
        c.R.Apply(rd with { Hmi = new Services.HmiProgram(false, 3, 4, "completed", "4 步全部完成", null, false, null) }, d.Model, true, true, d.Alias, "", null);
        p.Refresh(s.Fleet);
        Controls.Pr.StepBar.Sync(bar, c.Segments);
        Assert.Equal(4, bar.Count);
        c.R.Apply(rd, d.Model, true, true, d.Alias, "", null);
        p.Refresh(s.Fleet);
        Assert.False(c.HasProgram);
        Assert.Null(c.Segments);
        Controls.Pr.StepBar.Sync(bar, c.Segments);
        Assert.Empty(bar);
    }

    /// <summary>
    /// 复核（低）：演示数据重建（截图的每个场景）后同一台设备的事件 Key（'e13'）会重复 ——「报警与事件」的最近事件、外框的事件列表都是这一份数据的，
    /// 不留上一份的文字（以前按 Key 只增不改，结果跟截图顺序有关）。
    /// </summary>
    [Fact]
    public void Events_AfterADemoRebuild_AreTheNewData()
    {
        var (s, p) = Open(1, "devs=PR16,PR4&sel=2&freeze=1");
        Assert.NotEmpty(p.Log.Events);
        var q = DemoParams.Parse("devs=PR8,PR4,PR16&freeze=1");
        s.Fleet.ResetDemo(q);
        s.ApplyDemoParams(q, Route.Device(1));
        var pp = Assert.IsType<DevicePageVm>(s.Page);
        var d = s.Fleet.Device(1)!;
        var book = d.Source.EventBook.Filter(pp.Log.Filter, Core.Events.EventBook.ListMax);
        Assert.Equal(book.Select(e => e.AtText + " " + e.Text), pp.Log.Events.Select(e => e.Time + " " + e.Tip));
        Assert.Equal(d.Source.Events.Select(e => e.Id + " " + e.Text), d.Events.Select(e => e.Id + " " + e.Text));
    }

    [Fact]
    public void Cards_RowCards_Texts_AsMock()
    {
        var (_, p) = Open(1);
        // 样稿 1号机：RV1 反应中「SP 120 ℃」、RV3 升温中「→ 120 ℃」、RV4 升压中「SP 120 ℃」+「→ 0.350 MPa」（先藏设定值）
        var c1 = Card(p, 1);
        Assert.Equal(("SP 120 ℃", "", false), (c1.TSub, c1.PSub, c1.PressFirst));
        Assert.Equal(("加氢-标准", "第 3/4 步", "剩 42 min", true), (c1.NameText, c1.KnText, c1.RemShown, c1.RemRun));
        Assert.Equal("1号机 · RV1 · 反应中 · 加氢-标准 第 3/4 步 · 剩 42 min\n双击打开反应釜页 · 右键更多操作", c1.Tip);
        Assert.Equal("加氢-标准 · 第 3/4 步 · 剩 42 min", c1.BarTip);
        Assert.Equal((5, 5), (c1.TempLen, c1.PressLen));
        var c3 = Card(p, 3);
        Assert.Equal(("→ 120 ℃", "", "第 1/4 步", "剩 9 min"), (c3.TSub, c3.PSub, c3.KnText, c3.RemText));
        var c4 = Card(p, 4);
        Assert.Equal(("SP 120 ℃", "→ 0.350 MPa", true), (c4.TSub, c4.PSub, c4.PressFirst));
        Assert.Equal(("第 2/4 步", "剩 3 min"), (c4.KnText, c4.RemText));
        Assert.All(p.Tiles.OfType<DeviceCardVm>(), c => Assert.True(c.HasProgram && !c.IsOff && !c.IsWide && !c.IsCompact));
    }

    [Fact]
    public void Cards_WideCards_KvRows_AsMock()
    {
        var (_, p) = Open(2);
        // 样稿 2号机（HT-PR4 宽卡）：设定值写在「加热」行（温度旁不写）；降温「冷却 → 40 ℃」；耗气没有读数「—」；停了的「本釜 · 停」
        var c1 = Card(p, 1);
        Assert.Equal("", c1.TSub);
        Assert.Equal(("800", "本釜", "45", "SP 150 ℃", "12.60"), (c1.StirValue, c1.StirNote, c1.HeatValue, c1.HeatNote, c1.GasValue));
        Assert.Equal(("搅拌 800 rpm · 本釜", "加热 45 % · SP 150 ℃", "耗气 12.60 mmol"), (c1.StirTip, c1.HeatTip, c1.GasTip));
        Assert.Equal((3, 2, 5), (c1.StirLen, c1.HeatLen, c1.GasLen));
        Assert.Equal(("高压加氢-150", "第 3/5 步", "剩 90 min"), (c1.NameText, c1.KnText, c1.RemText));
        var c2 = Card(p, 2);
        Assert.Equal(("500", "100", "→ 150 ℃", "—", true), (c2.StirValue, c2.HeatValue, c2.HeatNote, c2.GasValue, c2.GasNa));
        Assert.Equal("耗气 —", c2.GasTip);
        var c3 = Card(p, 3);
        Assert.Equal(("300", "0", "冷却 → 40 ℃", "9.80"), (c3.StirValue, c3.HeatValue, c3.HeatNote, c3.GasValue));
        var c4 = Card(p, 4);
        Assert.Equal(("0", "本釜 · 停", "0", "", "15.40"), (c4.StirValue, c4.StirNote, c4.HeatValue, c4.HeatNote, c4.GasValue));
        Assert.Equal(("已结束", "共 5 步", false), (c4.KnText, c4.RemText, c4.RemRun));
    }

    [Fact]
    public void Cards_Compact_FullVesselWhenRvPanelMaximized()
    {
        var (_, p) = Open(3, width: 936, height: 592);
        var cards = p.Tiles.OfType<DeviceCardVm>().ToArray();
        Assert.All(cards, c => Assert.True(c.SimpleVessel && c.Spec.Narrow));
        Assert.Equal((308, 117d, false), (p.TopRow, p.Layout!.CardHeight, p.Scroll));
        // 放大（PageHost 原地放大到整页）：卡片按整页的高度排，紧凑卡够高，画完整釜体
        p.SetPanel(DevicePanelKey.Rv, max: true, closed: false);
        Assert.All(cards, c => Assert.True(c.Spec.Maximized && !c.SimpleVessel && c.IsCompact));
        Assert.True(p.Layout!.CardHeight > 200, "放大后的卡片高 " + p.Layout.CardHeight);
        Assert.False(p.Scroll);
        p.SetPanel(DevicePanelKey.Rv, max: false, closed: false);
        Assert.All(cards, c => Assert.True(c.SimpleVessel && !c.Spec.Maximized));
        Assert.Equal((308, 117d), (p.TopRow, p.Layout!.CardHeight));
    }

    // ———————————————————— 标题、说明条、从未连上 ————————————————————

    [Fact]
    public void Head_And_Bars_Texts_AsMock()
    {
        var (_, p) = Open(1);
        Assert.Equal(("1号机 › 反应釜", "HT-PR8 · 现场 · 50 mL 釜"), (p.Crumb, p.Sub));
        Assert.Equal(("运行", "4", "/4"), (p.RunLabel, p.RunCount, p.RunTotal));
        Assert.Equal("运行 4 / 已安装 4（RV1–RV4，共 8 路）", p.RunTip);
        Assert.False(p.HasOffBar || p.HasEstopBar || p.IsNever);
        Assert.True(p.Online && p.EverOnline);

        // 断线（devs=4 的 4号机）：中性说明条 + 「立即重连」；运行数写「断线前运行」
        var (s4, p4) = Open(4, "freeze=1&devs=4");
        Assert.False(p4.Online);
        Assert.True(p4.HasOffBar);
        Assert.Equal(("wifioff", "下面是断线前的最后读数，仅供参考 · 恢复连接后自动刷新", "立即重连"), (p4.OffIcon, p4.OffText, p4.OffButton));
        Assert.Matches(@"^数据停在 \d\d:\d\d:\d\d$", p4.OffLead);
        Assert.Equal(s4.Fleet.Device(4)!.DataText, p4.OffLead);
        Assert.Equal("断线前运行", p4.RunLabel);
        Assert.All(p4.Tiles.OfType<DeviceCardVm>(), c => Assert.True(c.IsOff));
        Assert.StartsWith("4号机 · RV1 · 离线（断线前 ", Card(p4, 1).Tip);

        // 急停（estop=3）：红色说明条
        var (_, p3) = Open(3, "freeze=1&estop=3");
        Assert.True(p3.HasEstopBar);
        Assert.Equal("急停中", p3.EstopLead);
        Assert.Matches(@"^\d\d:\d\d:\d\d 触发 · 加热 / 进气 / 搅拌已切断 · 复位只能在3号机触屏上操作$", p3.EstopText);
        Assert.Equal(("急停中", true), (p3.Res.EstopWord, p3.Res.EstopOn));
        Assert.EndsWith(" 触发 · 复位只能在触屏", p3.Res.EstopCap);
    }

    [Fact]
    public void NeverConnected_OneNote_NoCards_UnknownEverywhere()
    {
        var (s, _) = Open(1);
        // 同样稿「添加设备」：5号机 HT-PR8 192.168.1.50:8080，正在连接（冻结时一直停在正在连接）
        var nd = s.Fleet.DemoData!.AddDevice(new DemoNewDevice(Host: "192.168.1.50", Port: 8080, Token: "x", Alias: "5号机", AutoConnect: true, ModelKey: "PR8"));
        s.Fleet.RefreshNow();
        s.Navigate(Route.Device(nd.K));
        var p = Assert.IsType<DevicePageVm>(s.Page);
        p.SetViewport(1226, 752);
        Assert.True(p.IsNever && !p.EverOnline);
        Assert.Equal(("正在连接 192.168.1.50:8080…", "尚未收到数据 · 连上后这里显示 RV1–RV8（HT-PR8）"), (p.NeverMain, p.NeverSub));
        Assert.Empty(p.Tiles);
        Assert.False(p.HasOffBar);                                   // 说明只写在一处
        Assert.Equal(42 + 110, p.TopRow);
        // 共用资源：全是未知
        Assert.Equal(("尚未收到数据", "未知 · 尚未连接", true), (p.Res.Sub, p.Res.EstopValue, p.Res.EstopMuted));
        Assert.True(p.Res.StirUnknown);
        Assert.Equal(("全釜共用 · 磁力搅拌", false), (p.Res.StirCap, p.Res.StirLink));
        Assert.All(p.Res.GasItems, gi => Assert.Equal(("—", false), (gi.Word, gi.IsOpen)));
        Assert.Equal("—", p.Res.SvText);
        // 设备信息：模块未连接
        Assert.Equal(("尚未收到数据", "192.168.1.50:8080"), (p.Info.CommSub, p.Info.Address));
        Assert.Equal(new[] { "温控 AI-8", "压力 8AI", "搅拌 DM2C", "阀门 IO16R" }, p.Info.Comm.Select(c => c.Name));
        Assert.All(p.Info.Comm, c => Assert.Equal(("未连接", "—", true, true), (c.State, c.Last, c.ShowDot, c.IsOff)));
        Assert.Equal("断线时无法得知各模块的状态", p.Info.StatTip);
        // 报警与事件：没有报警；事件只有「已添加」
        Assert.False(p.Log.HasAlarms);
        var ev = Assert.Single(p.Log.Events);
        Assert.Equal(("10:12:00", "整机", true, "已添加 192.168.1.50:8080 · 正在连接"), (ev.Time, ev.Rv, ev.IsDevice, ev.Text));
        Assert.Equal(("全部 1", "报警/提醒 0", "操作 0"), (p.Log.SegAll.Label, p.Log.SegAlarm.Label, p.Log.SegOp.Label));
    }

    // ———————————————————— 共用资源 / 设备信息的文字 ————————————————————

    [Fact]
    public void Resources_And_Info_Texts_AsMock()
    {
        var (_, p) = Open(1);
        var r = p.Res;
        Assert.Equal(("只读 · 设置在检查器", "未触发", false), (r.Sub, r.EstopValue, r.EstopMuted));
        Assert.True(r.SharedStir && !r.StirUnknown && r.StirOn && r.StirLink);
        Assert.Equal(("运行中", "300", StateKey.Stir), (r.StirWord, r.StirRpm, r.StirDot));
        Assert.Equal(("全釜共用 · 磁力搅拌", "全釜共用 RV1–RV8 · 磁力搅拌"), (r.StirCap, r.StirCapTip));
        Assert.Equal(new[] { ("惰性气体", "关", false), ("气体 A", "开", true), ("气体 B", "关", false), ("排空 / 泄压", "关", false) },
            r.GasItems.Select(gi => (gi.Name, gi.Word, gi.IsOpen)));
        Assert.Equal("进气 3 路 · 排空 / 泄压 · 阀门只能在设备触屏上操作", r.GasTip);
        // 进气阀：每项不断开（Core 的 Keep.NoBreak 插 U+2060，显示时换成当前字体的连接符），项与项之间可以换行
        Assert.Equal("SV1 开 · SV2 开 · SV3 关 · SV4 开", CoreKeep.Plain(r.SvText));
        Assert.Equal("SV1 开 · SV2 开 · SV3 关 · SV4 开", r.SvTip);
        Assert.StartsWith("S\u2060V\u20601\u2060\u00a0\u2060开\u00a0· S\u2060V\u20602", r.SvText);      // 分隔「不换行空格 · 空格」：只在点后面断

        var i = p.Info;
        Assert.Equal(("4", "/4", false, "全部模块通讯正常"), (i.StatCount, i.StatTotal, i.StatDot, i.StatTip));
        Assert.Equal(("HT-PR8 · 现场", true), (i.ModelText, i.Sample));
        Assert.Equal(("8 路 · 已装 RV1–RV4", "50 mL 釜 · 溶液 ≤ 20 mL", "≤ 200 ℃ · ≤ 4.14 MPa"), (i.ChannelsText, i.VesselText, i.RangeText));
        Assert.Equal(("192.168.1.20:8080", "PR8-2026-0001", "HMI 1.0.6 · 接口 v1.2"), (i.Address, i.Sn, i.VersionTip));
        Assert.Equal("每秒轮询", i.CommSub);
        Assert.Equal(4, i.Comm.Count);
        Assert.Equal(i.Comm.Count + 1, i.CommTable.Count);             // 表头 + 各模块（同一个表格面板定列宽）
        Assert.All(i.Comm, c => Assert.Equal(("× 1", "在线", false), (c.Count, c.State, c.ShowDot)));
        Assert.All(i.Comm, c => Assert.Matches(@"^\d\d:\d\d:\d\d$", c.Last));

        // HT-PR4：独立搅拌每釜一行（「设置」= 选中该釜并聚焦检查器里的转速框）
        var (s2, p2) = Open(2);
        Assert.False(p2.Res.SharedStir);
        Assert.Equal("各釜独立 · 顶置机械搅拌", p2.Res.StirHead);
        Assert.Equal(new[] { ("RV1", true, StateKey.Stir), ("RV2", true, StateKey.Stir), ("RV3", true, StateKey.Stir), ("RV4", false, StateKey.Stopped) },
            p2.Res.StirRows.Select(x => (x.Label, x.On, x.Dot)));
        Assert.Equal(new[] { "800", "500", "300" }, p2.Res.StirRows.Take(3).Select(x => x.Rpm));      // 停了的写「已停」（视图），不写转速
        Assert.All(p2.Res.StirRows, x => Assert.True(x.Link && !x.Unknown));
        p2.Res.StirRows[1].SetCommand.Execute(null);
        Assert.Equal((2, (int?)2), (s2.Selection.K, s2.Selection.N));
        Assert.Equal(("HT-PR4", "4 路 · 已装 RV1–RV4", "PR4-2026-0003"), (p2.Info.ModelText, p2.Info.ChannelsText, p2.Info.Sn));
    }

    // ———————————————————— 窄工作区的页签 ————————————————————

    [Fact]
    public async Task Tabs_OpenOnUnackedError_NewErrorSwitchesUntilUserPicks_AckNeverSwitches()
    {
        // 3号机：打开时有未确认的错误报警 → 「报警与事件」；确认之后页签不变（红点没了）
        var (s3, p3) = Open(3, width: 936, height: 592);
        Assert.True(p3.Tabbed);
        Assert.Equal(("log", true, true), (p3.TabValue, p3.TabLogOn, p3.LogTab.Dot));
        await AckAsync(s3, p3, p3.Log.Alarms[0].Id);
        Assert.Equal(("log", false, (string?)null), (p3.TabValue, p3.LogTab.Dot, p3.LogTab.Tip));
        Assert.True(p3.Log.Alarms[0].Acked);

        // 1号机：只有提醒 → 「设备信息」，页签上中性数字 1
        var (s, p) = Open(1, width: 936, height: 592);
        Assert.Equal(("info", false, "1", "1 条未确认"), (p.TabValue, p.LogTab.Dot, p.LogTab.Count, p.LogTab.Tip));
        Assert.Same(p.Info, p.TabsContent);
        int picked = 0;
        p.TabPicked += (_, _) => picked++;
        // 新的错误报警（数据变了，地址参数没变）、用户没点过页签：切到「报警与事件」（页签在前台：不再写数字）
        s.Fleet.DemoData!.Apply(DemoParams.Parse("freeze=1&alarm=1:1"));
        s.Fleet.RefreshNow();
        Assert.Equal(("log", true, (string?)null), (p.TabValue, p.LogTab.Dot, p.LogTab.Count));
        Assert.Same(p.Log, p.TabsContent);
        Assert.Equal(0, picked);                                     // 自动切换不算用户点的
        // 用户点「设备信息」：之后新的错误报警不再替用户换
        p.TabValue = "info";
        Assert.Equal(1, picked);
        Assert.True(p.TabRule.UserPicked);
        s.Fleet.DemoData!.Apply(DemoParams.Parse("freeze=1&alarm=1:0"));
        s.Fleet.DemoData!.Apply(DemoParams.Parse("freeze=1&alarm=1:1"));
        s.Fleet.RefreshNow();
        Assert.Equal(("info", true), (p.TabValue, p.LogTab.Dot));
        // 回到「报警与事件」确认：页签不变
        p.TabValue = "log";
        await AckAsync(s, p, p.Log.Alarms.First(a => a.IsErr).Id);
        Assert.Equal(("log", false), (p.TabValue, p.LogTab.Dot));
        Assert.Equal(2, picked);
    }

    // ———————————————————— 同一条报警只有一个「确认」 ————————————————————

    [Fact]
    public void Ownership_LogVisible_OwnsUnackedAlarms_InspectorPointsHere()
    {
        var (s, p) = Open(3);
        Assert.True(p.LogVisible);
        var own = Assert.Single(p.OwnedAlarms);
        Assert.Equal("「报警与事件」", own.Value);
        Assert.StartsWith("3:", own.Key);
        // 检查器（选中 RV11）：写「确认在「报警与事件」」，没有第二个「确认」
        s.Select(3, 11);
        var a = Assert.Single(Assert.IsType<ReactorInspectorVm>(s.Inspector.Body).TopAlarms);
        Assert.Equal(("确认在「报警与事件」", false), (a.WhereText, a.ShowAck));
        // 设备正文：报警 / 事件列表不在检查器里（页面上有，样稿 inspDevice 的 o.page === 'device'）
        s.Select(3);
        Assert.False(Assert.IsType<DeviceInspectorVm>(s.Inspector.Body).ShowLog);

        // 关掉「报警与事件」：页面不再拥有，检查器自己放「确认」
        s.Select(3, 11);
        p.SetPanel(DevicePanelKey.Log, max: false, closed: true);
        Assert.Empty(p.OwnedAlarms);
        Assert.True(((ReactorInspectorVm)s.Inspector.Body!).TopAlarms.Single().ShowAck);
        p.SetPanel(DevicePanelKey.Log, max: false, closed: false);
        Assert.Single(p.OwnedAlarms);
        Assert.False(((ReactorInspectorVm)s.Inspector.Body!).TopAlarms.Single().ShowAck);
        // 别的面板放大（盖住了「报警与事件」）：不拥有；「报警与事件」自己放大：拥有
        p.SetPanel(DevicePanelKey.Rv, max: true, closed: false);
        Assert.Empty(p.OwnedAlarms);
        p.SetPanel(DevicePanelKey.Rv, max: false, closed: false);
        p.SetPanel(DevicePanelKey.Log, max: true, closed: false);
        Assert.Single(p.OwnedAlarms);
        p.ResetPanels();
        Assert.Single(p.OwnedAlarms);

        // 窄工作区：页签在「设备信息」时不拥有（1号机的提醒），点到「报警与事件」才拥有
        var (s1, p1) = Open(1, width: 936, height: 592);
        Assert.Equal("info", p1.TabValue);
        Assert.Empty(p1.OwnedAlarms);
        p1.TabValue = "log";
        Assert.Equal("「报警与事件」", Assert.Single(p1.OwnedAlarms).Value);
        p1.SetPanel(DevicePanelKey.Tabs, max: false, closed: true);
        Assert.Empty(p1.OwnedAlarms);
        // 确认过的不再拥有
        var (s2, p2) = Open(3);
        s2.Fleet.DemoData!.Act(s2.Fleet.DemoData.Device(3)!, Core.Guard.DeviceAction.AlarmAck, new Core.Guard.ActionPayload(Id: p2.Log.Alarms[0].Id));
        s2.Fleet.RefreshNow();
        Assert.Empty(p2.OwnedAlarms);
    }

    [Fact]
    public void Panels_WidthClassChange_DropsBottomMax_KeepsOthers()
    {
        // 宽工作区放大「报警与事件」，窗口变窄（跨过 1100）：样稿换了一组面板（页签面板），放大跟着旧面板一起没了 ——
        // 视图模型撤掉放大并通知视图（PanelsUnmaximized），不再出现「放大的那块藏起来了、别的还被放大藏着」的空白页（M4 复核）
        var (_, p) = Open(3);
        int dropped = 0;
        p.PanelsUnmaximized += (_, _) => dropped++;
        p.SetPanel(DevicePanelKey.Log, max: true, closed: false);
        Assert.Single(p.OwnedAlarms);
        p.SetViewport(1002, 460);
        Assert.True(p.Tabbed);
        Assert.Equal(1, dropped);
        Assert.Equal("log", p.TabValue);            // 3号机有未确认的错误报警：页签面板在「报警与事件」
        Assert.True(p.LogVisible);                  // 没有别的面板放大盖住它
        Assert.Equal(p.Layout!.Bottom.Single(b => b.Panel == DevicePanel.Tabs).Height - DeviceLayout.Head, p.Log.BodyHeight, 3);   // 内容区按下排的面板高算，不按放大的整页
        // 反过来：窄工作区放大页签面板，窗口变宽 → 放大撤掉，「设备信息」「报警与事件」照常显示
        p.SetPanel(DevicePanelKey.Tabs, max: true, closed: false);
        p.SetViewport(1226, 752);
        Assert.False(p.Tabbed);
        Assert.Equal(2, dropped);
        Assert.True(p.LogVisible);
        Assert.Equal(DevicePanel.Log, p.Layout!.Bottom.Last().Panel);
        // 关闭不受影响（只撤放大）；反应釜面板的放大跨过宽度类照旧（样稿同一块面板，is-max 保留）
        p.SetPanel(DevicePanelKey.Log, max: false, closed: true);
        p.SetPanel(DevicePanelKey.Rv, max: true, closed: false);
        p.SetViewport(1002, 460);
        Assert.Equal(2, dropped);
        Assert.Empty(p.OwnedAlarms);                // 反应釜面板放大着：「报警与事件」页签被盖住
        p.SetViewport(1226, 752);
        Assert.False(p.LogVisible);                 // 宽工作区的「报警与事件」还关着
        // 宽度类没变时什么都不撤
        p.ResetPanels();
        p.SetPanel(DevicePanelKey.Info, max: true, closed: false);
        p.SetViewport(1300, 752);
        Assert.Equal(2, dropped);
    }

    [Fact]
    public void Options_ForSegAndTabs_AreTheViewModelInstances()
    {
        // 事件筛选与页签的选项是视图模型里的实例（SegControl / PTabs 的 OptionsSource 绑定：模板什么时候实例化都带着选项 —— M4 复核：
        // 自动切到「报警与事件」、跨过 1100 改窗口尺寸之后筛选是空的）
        var (_, p) = Open(1, width: 1002, height: 460);
        Assert.Equal(new[] { p.Log.SegAll, p.Log.SegAlarm, p.Log.SegOp }, p.Log.SegOptions);
        Assert.Equal(new object?[] { "all", "alarm", "op" }, p.Log.SegOptions.Select(o => o.Value));
        Assert.Equal(new[] { "全部 13", "报警/提醒 1", "操作 6" }, p.Log.SegOptions.Select(o => o.Label));
        Assert.Equal(new[] { p.InfoTab, p.LogTab }, p.TabOptions);
        Assert.Same(p.TabOptions, p.TabOptions);
        Assert.Same(p.Log.SegOptions, p.Log.SegOptions);
    }

    [Fact]
    public void RowSplit_ColumnsSnapToDevicePixels_OnePixelGap()
    {
        // 下排各列的边缘对齐到设备像素：列间 1 px line-strong 的缝在 1920 / 1600 / 1366（资源管理器展开 / 收起）和 125 % / 150 % / 175 % / 200 % 缩放下都至少 1 个设备像素
        // （以前按小数排，布局取整把前一列向上取整、正好盖住缝：1920 时「设备信息 | 报警与事件」、1366 时「共用资源 | 页签面板」之间没有线 —— M4 复核）
        var three = new[] { 1.0, 1.0, 1.2 };
        var two = new[] { 1.0, 1.3 };
        Assert.Equal(new[] { (0.0, 383.0), (384.0, 382.0), (767.0, 459.0) }, Views.Pages.Device.DvRowPanel.Split(1226, three, 1));   // 样稿 1920：线在 383、766
        Assert.Equal(new[] { (0.0, 435.0), (436.0, 566.0) }, Views.Pages.Device.DvRowPanel.Split(1002, two, 1));                       // 样稿 1366：线在 435
        foreach (double w in new double[] { 1226, 1526, 936, 1236, 1002, 1302, 1101, 1099, 777.5 })
            foreach (var fr in new[] { three, two, new[] { 1.0, 1.0 }, new[] { 1.0 } })
                foreach (double scale in new[] { 1, 1.25, 1.5, 1.75, 2 })
                {
                    var cols = Views.Pages.Device.DvRowPanel.Split(w, fr, scale);
                    Assert.Equal(fr.Length, cols.Length);
                    Assert.Equal(0, cols[0].X);
                    Assert.Equal(Math.Round(w * scale, MidpointRounding.AwayFromZero), Math.Round((cols[^1].X + cols[^1].W) * scale, 6));
                    for (int i = 0; i < cols.Length; i++)
                    {
                        // 边缘都在设备像素上
                        Assert.Equal(Math.Round(cols[i].X * scale), Math.Round(cols[i].X * scale, 6));
                        Assert.Equal(Math.Round(cols[i].W * scale), Math.Round(cols[i].W * scale, 6));
                        if (i == 0) continue;
                        double gap = (cols[i].X - (cols[i - 1].X + cols[i - 1].W)) * scale;
                        Assert.InRange(Math.Round(gap, 6), 1, Math.Ceiling(scale));
                    }
                }
    }

    [Fact]
    public void DemoSource_AlarmBook_ResyncsOnlyWhenTheAlarmsChange()
    {
        // 演示数据的报警簿：DemoDevice.Alarms 没变时读多少次都不重新同步（R10：每秒刷新里分区头、报警条、页面拥有的报警、检查器都要读 —— M4 复核）；
        // 确认 / 新报警 / 急停照样马上反映出来
        var (s, p) = Open(3);
        var src = Assert.IsType<Services.DemoDeviceSource>(s.Fleet.Device(3)!.Source);
        var book = src.AlarmBook;
        var cur = book.Current;
        int ver = book.Version;
        for (int i = 0; i < 5; i++)
        {
            Assert.Same(book, src.AlarmBook);
            Assert.Same(cur, src.AlarmBook.Current);
        }
        s.Fleet.RefreshNow();
        Assert.Same(cur, src.AlarmBook.Current);
        Assert.Equal(ver, src.AlarmBook.Version);
        var first = src.Device.Alarms[0];
        Assert.False(src.AlarmBook.Current.Single(a => a.Id == first.Id).Acked);
        s.Fleet.DemoData!.Act(src.Device, Core.Guard.DeviceAction.AlarmAck, new Core.Guard.ActionPayload(Id: first.Id));
        Assert.True(src.AlarmBook.Current.Single(a => a.Id == first.Id).Acked);   // 同一个 DemoAlarm 实例，只是确认状态变了
        Assert.NotEqual(ver, src.AlarmBook.Version);
        int n = src.AlarmBook.Count;
        s.Fleet.DemoData.Act(src.Device, Core.Guard.DeviceAction.Estop, null, quiet: true);
        Assert.Equal(n + 1, src.AlarmBook.Count);
        Assert.Equal("ESTOP", src.AlarmBook.Current[0].Code);
    }

    [Fact]
    public void Panels_Closed_TopRowFollows()
    {
        var (_, p) = Open(1);
        Assert.Equal(391, p.TopRow);
        // 下排全关：反应釜面板铺满
        p.SetPanel(DevicePanelKey.Res, false, true);
        p.SetPanel(DevicePanelKey.Info, false, true);
        p.SetPanel(DevicePanelKey.Log, false, true);
        Assert.Null(p.TopRow);
        Assert.Empty(p.Layout!.Bottom);
        p.ResetPanels();
        Assert.Equal(391, p.TopRow);
        // 反应釜面板关掉：下排铺满（BottomHeight = 整页）
        p.SetPanel(DevicePanelKey.Rv, false, true);
        Assert.Null(p.TopRow);
        Assert.Equal(752, p.Layout!.BottomHeight);
    }
}
