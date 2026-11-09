using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Layout;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages.Overview;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 总览（PORT-PLAN §5 M4；样稿 js/pages/overview.js + css/overview.css）：分区（每台设备一个，身份稳定）、分区头的文字（控制权 / 连接徽标、运行 k/n、
/// 报警小块、急停、记录中）、说明条、卡片网格的块（宽卡 / 一排卡 / 紧凑卡 + 合并的未安装块）、卡片按内容宽的取舍（Core OverviewLayout）、读数与程序行的文字、
/// 顶部报警条（什么时候出现、文字、「定位」、页面拥有的报警 → 检查器只写「确认在顶部报警条」）、分区放大、放得下才显示的规则（Views/Pages/Overview/OvFit）。
/// 用真的外壳视图模型 + 演示数据（冻结），不创建窗口；总览的尺寸 = 外壳给工作区的尺寸（1920 → 1226 × 752，1600 → 936 × 592，1366 → 1002 × 460）。
/// </summary>
public class OverviewTests
{
    private static (ShellVm Shell, OverviewPageVm Page) Open(string query = "freeze=1", double width = 1226, double height = 752)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(p, Route.Overview);
        var page = Assert.IsType<OverviewPageVm>(s.Page);
        page.SetViewport(width, height);
        return (s, page);
    }

    private static OverviewSectionVm Sec(OverviewPageVm p, int k) => p.Sections.Single(s => s.K == k);

    private static OverviewCardVm Card(OverviewPageVm p, int k, int n) => Sec(p, k).Tiles.OfType<OverviewCardVm>().Single(c => c.N == n);

    // ———————————————————— 分区 ————————————————————

    [Fact]
    public void Sections_OnePerDevice_StableIdentity()
    {
        var (s, p) = Open();
        Assert.Equal(new[] { 1, 2, 3 }, p.Sections.Select(x => x.K));
        var first = p.Sections[0];
        s.Fleet.RefreshNow();
        Assert.Same(first, p.Sections[0]);
        s.ApplyDemoParams(DemoParams.Parse("devs=4&freeze=1"));
        Assert.Equal(new[] { 1, 2, 3, 4 }, p.Sections.Select(x => x.K));
        Assert.Same(first, p.Sections[0]);
        s.ApplyDemoParams(DemoParams.Parse("devs=1&freeze=1"));
        Assert.Single(p.Sections);
        Assert.False(p.IsEmpty);
    }

    [Fact]
    public void HiddenPages_SkipTickRefresh_CatchUpWhenShownAgain()
    {
        // R10：藏在别的页签后面的总览 / 设备页不跟着每秒的数据刷新；切回那个页签时先刷新再显示（同一个实例，身份不变）
        var (s, p) = Open();
        Assert.False(Sec(p, 2).Estop);
        s.Navigate(Route.Device(2));
        var dev = Assert.IsType<DevicePageVm>(s.Page);
        Assert.False(dev.HasEstopBar);
        var d2 = s.Fleet.DemoData!.Device(2)!;
        s.Fleet.DemoData.Act(d2, Core.Guard.DeviceAction.Estop, null, quiet: true);
        s.Fleet.RefreshNow();
        Assert.True(dev.HasEstopBar);                     // 看得见的设备页：马上跟上
        Assert.False(Sec(p, 2).Estop);                    // 藏着的总览：没有刷新
        s.Navigate(Route.Overview);
        Assert.Same(p, s.Page);
        Assert.True(Sec(p, 2).Estop);                     // 切回来：显示之前先刷新
        Assert.Equal("急停中", Sec(p, 2).EstopText);
        Assert.False(p.RefreshWhenHidden);
        Assert.False(dev.RefreshWhenHidden);
    }

    [Fact]
    public void Heads_Texts_AsMock()
    {
        var (_, p) = Open();
        var s1 = Sec(p, 1);
        Assert.Equal("1号机", s1.Alias);
        Assert.Equal("HT-PR8 · 现场", s1.Model);
        Assert.True(s1.Online);
        Assert.Equal("远程控制", s1.LeaseShort);
        Assert.Equal("远程控制中", s1.LeaseTip);
        Assert.Equal(("运行", "4", "/4"), (s1.RunWord, s1.RunCount, s1.RunTotal));
        Assert.Equal("运行 4 · 已安装 4", s1.RunTip);
        Assert.True(s1.HasChip && s1.ChipWarn && !s1.ChipErr);
        Assert.Equal("提醒 1", s1.ChipWarnText);
        Assert.True(s1.SessionShown);
        Assert.Equal("1号机 · HT-PR8 · 现场 · SN PR8-2026-0001 · 192.168.1.20:8080", s1.NameTip);
        Assert.Equal("打开1号机设备页", s1.OpenTip);
        Assert.Equal("1号机 · 更多操作", s1.MoreTip);
        Assert.False(s1.HasNote);

        var s2 = Sec(p, 2);
        Assert.Equal("本地只读", s2.LeaseShort);
        Assert.False(s2.HasChip);
        Assert.False(s2.SessionShown);

        var s3 = Sec(p, 3);
        Assert.True(s3.ChipErr && !s3.ChipWarn);
        Assert.Equal("RV11 超压报警", s3.ChipErrText);
        Assert.Equal(("运行", "11", "/16"), (s3.RunWord, s3.RunCount, s3.RunTotal));
        s3.ChipCommand.Execute(null);
        Assert.Equal((3, (int?)11), (p.Shell.Selection.K, p.Shell.Selection.N));   // 单击小块 = 选中那条报警所在的反应釜
    }

    [Fact]
    public void Estop_RedBadge_Note_AlarmNotCounted()
    {
        var (_, p) = Open("estop=3&freeze=1");
        var s3 = Sec(p, 3);
        Assert.True(s3.Estop);
        Assert.Equal("急停中", s3.EstopText);
        Assert.Equal("急停中 · 复位只能在3号机触屏上操作", s3.EstopTip);
        Assert.True(s3.HasNote);
        Assert.Equal("estop", s3.NoteIcon);
        Assert.Equal("加热、进气与搅拌已切断", s3.NoteMain);
        Assert.Equal("复位只能在3号机触屏上操作 · 本机不能复位", s3.NoteSub);
        Assert.Null(s3.NoteButton);
        // 急停那条报警不计入小块（各处已有红色「急停中」）：剩下 RV11 超压
        Assert.Equal("RV11 超压报警", s3.ChipErrText);
        Assert.Equal(("运行", "0"), (s3.RunWord, s3.RunCount));
        var c1 = Card(p, 3, 1);
        Assert.Equal("已中止", c1.ProgShort);
    }

    [Fact]
    public void Offline_ConnBadge_Note_GreyCards()
    {
        var (_, p) = Open("devs=4&freeze=1");
        var s4 = Sec(p, 4);
        Assert.False(s4.Online);
        Assert.True(s4.IsOff);
        Assert.Equal("连接中断", s4.ConnShort);
        Assert.Equal("断线前运行", s4.RunWord);
        Assert.True(s4.HasNote);
        Assert.Equal("wifioff", s4.NoteIcon);
        Assert.Equal("12 s 后自动重连", s4.NoteMain);
        Assert.Equal("下方为断线前的读数 · 数据停在 10:11:40", s4.NoteSub);
        Assert.Equal("立即重连", s4.NoteButton);
        Assert.Null(s4.NoteReason);   // 重连不需要连接（none 类）
        var c1 = Card(p, 4, 1);
        Assert.True(c1.IsOff);
        Assert.Equal("第 3/4 步", c1.ProgShort);
        Assert.False(c1.BarRunning);
        Assert.Equal("4号机 · RV1 · 离线（断线前 反应中） · 数据停在 10:11:40 · 单击选中 · 双击打开", c1.Tip);
    }

    // ———————————————————— 卡片网格 ————————————————————

    [Fact]
    public void Tiles_ByModel_MergedUninstalled()
    {
        var (_, p) = Open();
        var t1 = Sec(p, 1).Tiles;
        Assert.Equal(5, t1.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, t1.OfType<OverviewCardVm>().Select(c => c.N));
        var un = Assert.IsType<OverviewUntileVm>(t1[4]);
        Assert.Equal("RV5–RV8", un.Label);
        Assert.Equal("1号机 · RV5–RV8 未安装（HT-PR8 · 现场）· 不参与运行", un.Tip);
        Assert.Equal(CardKind.Row, Sec(p, 1).Kind);
        Assert.Equal(CardKind.Wide, Sec(p, 2).Kind);
        Assert.Equal(CardKind.Compact, Sec(p, 3).Kind);
        Assert.Equal(16, Sec(p, 3).Tiles.Count);
        // 8 路装满的 HT-PR8：紧凑卡（overview.js cardKind）
        var (_, q) = Open("devs=PR8,PR4,PR16&freeze=1");
        Assert.Equal(CardKind.Compact, Sec(q, 1).Kind);
        Assert.Equal(8, Sec(q, 1).Tiles.Count);
    }

    [Fact]
    public void Layout_1920_CardVariants()
    {
        var (_, p) = Open();
        Assert.NotNull(p.Layout);
        Assert.False(p.Strip.IsVisible);   // 三台在 1920 一屏放得下
        Assert.Empty(p.OwnedAlarms);
        Assert.Equal(752, p.ScrollHeight);
        // 一排卡（内容宽 234.5）：釜体 36 × 56；目标不带单位（≤ 239）；程序名照写
        var r1 = Card(p, 1, 1);
        Assert.Equal((36d, 56d), (r1.VesselWidth, r1.VesselHeight));
        Assert.Equal("SP 120", r1.TargetT);
        Assert.Equal("", r1.TargetP);
        Assert.True(r1.ShowProgName);
        Assert.Equal("→ 120", Card(p, 1, 3).TargetT);
        Assert.Equal("→ 0.350", Card(p, 1, 4).TargetP);
        Assert.Equal((26d, 32d, 20d, 26d), (r1.TempSize, r1.TempLineHeight, r1.PressSize, r1.PressLineHeight));
        // 宽卡：釜体 44 × 76，目标带单位；第三行 搅拌 / 加热
        var w1 = Card(p, 2, 1);
        Assert.Equal((44d, 76d), (w1.VesselWidth, w1.VesselHeight));
        Assert.Equal("SP 150 ℃", w1.TargetT);
        Assert.Equal(("800", true, "加热", "45", true), (w1.StirValue, w1.StirUnitShown, w1.HeatLabel, w1.HeatValue, w1.HeatUnit));
        Assert.Equal(18, w1.AuxSize);
        var w3 = Card(p, 2, 3);
        Assert.Equal(("冷却水", "开", false), (w3.HeatLabel, w3.HeatValue, w3.HeatUnit));
        var w4 = Card(p, 2, 4);
        Assert.Equal(("关", true, false), (w4.StirValue, w4.StirMuted, w4.StirUnitShown));
        Assert.Equal("共 5 步", w4.ProgShort);
        // 紧凑卡（内容宽 120.5）：釜体 26 × 44，不写目标、不写程序名；温度 24、压力 18 —— 5 位的「1.200」在 120–122.2 px 时 17（overview.css:213）
        var c1 = Card(p, 3, 1);
        Assert.Equal((26d, 44d), (c1.VesselWidth, c1.VesselHeight));
        Assert.Equal("", c1.TargetT);
        Assert.False(c1.ShowProgName);
        Assert.False(c1.HeadWrap);
        Assert.Equal("1.200", c1.R.PressText);
        Assert.Equal((24d, 28d, 17d, 22d), (c1.TempSize, c1.TempLineHeight, c1.PressSize, c1.PressLineHeight));
        Assert.Equal(24, Card(p, 3, 7).TempSize);   // 4 位「58.2」不缩
    }

    [Fact]
    public void Layout_1600_StripAndNarrowCards()
    {
        var (s, p) = Open(width: 936, height: 592);
        Assert.True(p.Strip.IsVisible);
        Assert.Equal(592 - 46, p.ScrollHeight);
        // 一排卡（内容宽 162）：小釜体 22 × 36，不写目标、不写程序名
        var r1 = Card(p, 1, 1);
        Assert.Equal((22d, 36d), (r1.VesselWidth, r1.VesselHeight));
        Assert.Equal("", r1.TargetT);
        Assert.False(r1.ShowProgName);
        // 宽卡（188.5）：不画釜体、不写程序名、第三行收紧（16 px）
        var w1 = Card(p, 2, 1);
        Assert.False(w1.ShowVessel);
        Assert.False(w1.ShowProgName);
        Assert.Equal("SP 150", w1.TargetT);
        Assert.Equal(16, w1.AuxSize);
        // 紧凑卡（84.25）：状态字换到第二行、不画釜体、进度条与文字分两行
        var c1 = Card(p, 3, 1);
        Assert.True(c1.HeadWrap);
        Assert.True(c1.ProgWrap);
        Assert.False(c1.ShowVessel);
        // 1366（1002 宽）：同样有报警条
        var (_, q) = Open(width: 1002, height: 460);
        Assert.True(q.Strip.IsVisible);
        Assert.False(q.HideSession);   // ≤ 900 才去掉「记录中」
        _ = s;
    }

    [Fact]
    public void Strip_Texts_Locate_OwnsTheOnlyAck()
    {
        var (s, p) = Open(width: 936, height: 592);
        var st = p.Strip;
        Assert.Equal("3号机 · RV11", st.Who);
        Assert.Equal("超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", st.Text);
        Assert.Equal("RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", st.Title);
        Assert.Equal("10:09:42", st.Time);
        Assert.Equal("", st.More);
        Assert.Null(st.AckReason);
        var key = Assert.Single(p.OwnedAlarms).Key;
        Assert.Equal(st.OwnedKey, key);
        Assert.Equal("顶部报警条", p.OwnedAlarms[key]);

        OverviewScrollRequest? scrolled = null;
        p.ScrollRequested += (_, r) => scrolled = r;
        st.LocateCommand.Execute(null);
        Assert.Equal(3, scrolled?.K);
        Assert.Equal((3, (int?)11), (s.Selection.K, s.Selection.N));
        // 检查器：这条报警「确认在顶部报警条」，没有第二个确认按钮
        var rv = Assert.IsType<ReactorInspectorVm>(s.Inspector.Body);
        var a = Assert.Single(rv.TopAlarms);
        Assert.Equal("确认在顶部报警条", a.WhereText);
        Assert.False(a.ShowAck);

        // 一屏放得下（1920）：没有报警条，检查器自己放「确认」
        p.SetViewport(1226, 752);
        Assert.False(p.Strip.IsVisible);
        Assert.Empty(p.OwnedAlarms);
        Assert.True(((ReactorInspectorVm)s.Inspector.Body!).TopAlarms.Single().ShowAck);
    }

    [Fact]
    public void Strip_OfflineDevice_AckHasReason()
    {
        var (_, p) = Open("offline=3&freeze=1", 1226, 752);
        Assert.True(p.Strip.IsVisible);   // 4 行说明条撑高了 3号机：超出一屏
        Assert.Equal("3号机连接中断 · 重连中 12 s", p.Strip.AckReason);
    }

    [Fact]
    public void Stress_Ladders_LongReadingsStepDown()
    {
        // 1366（1002 宽）：紧凑卡内容宽 92.5，5 位温度「199.9」→ 22 px（overview.css:204），压力「2.997」不变（18）
        var (_, p) = Open("stress=1&freeze=1", 1002, 460);
        var c = Card(p, 3, 1);
        Assert.Equal("199.9", c.R.TempText);
        Assert.Equal(22, c.TempSize);
        Assert.True(c.TempUnit);
        Assert.Equal(18, c.PressSize);
        // 1600（936 宽）：内容宽 84.25 → 温度 20 px（:205），压力 16 px（:211），单位都还在
        p.SetViewport(936, 592);
        Assert.Equal(20, c.TempSize);
        Assert.Equal(16, c.PressSize);
        Assert.True(c.PressUnit);
        // PR4 宽卡（188.5）：4 位转速 + 3 位加热 → 16 px、去掉「rpm」（:215-220）
        var w = Card(p, 2, 1);
        Assert.Equal("1500", w.StirValue);
        Assert.Equal(16, w.AuxSize);
        Assert.False(w.StirUnitShown);
        Assert.False(w.RpmUnitShown);
    }

    [Fact]
    public void Program_ShortTexts_AndTips()
    {
        var (_, p) = Open();
        var r1 = Card(p, 1, 1);
        Assert.True(r1.HasProgram);
        Assert.Equal("加氢-标准", r1.ProgName);
        Assert.Equal("剩 42 min", r1.ProgShort);
        Assert.True(r1.ProgRun);
        Assert.True(r1.BarRunning);
        Assert.Equal(4, r1.Segments?.Count);
        Assert.StartsWith("加氢-标准 · 第 3/4 步 · 剩 42 min · 全程剩 ", r1.ProgTip);
        var held = Card(p, 3, 11);
        Assert.Equal("报警暂停", held.ProgShort);
        Assert.True(held.ProgHeld);
        Assert.Equal("共 4 步", Card(p, 3, 12).ProgShort);
        var none = Card(p, 3, 13);
        Assert.False(none.HasProgram);
        Assert.Equal("无程序", none.NoneText);
        Assert.Equal("1号机 · RV1 · 反应中 · 单击选中 · 双击打开", r1.Tip);
    }

    [Fact]
    public void Program_LiveWithoutLedger_ShowsHmiStepsNotNone()
    {
        // 真机：本机台账（M5）对不上的程序只有 HMI 的第 k/n 步和消息（PORT-PLAN §3.1）—— 不能写「无程序」
        var (s, p) = Open();
        var c = Card(p, 1, 1);
        var d = c.D;
        var hmi = new Services.HmiProgram(true, 1, 5, "running", "升温", null, false, null);
        var rd = new Services.ChannelReading(1, true, Core.State.StateKey.Heat, "升温中", Core.State.StateKey.Heat, "升温中", Core.State.AlarmCause.None,
            80, 120, 0.1, null, 50, 300, true, false, hmi);
        c.R.Apply(rd, d.Model, online: true, everOnline: true, d.Alias, "", program: null);
        p.Refresh(s.Fleet);
        Assert.True(c.HasProgram);
        Assert.Equal("RV1 程序", c.ProgName);
        Assert.Equal("第 2/5 步", c.ProgShort);
        Assert.False(c.ProgRun);
        Assert.True(c.BarRunning);
        Assert.Equal(new[] { Controls.Pr.StepSegState.Done, Controls.Pr.StepSegState.Current, Controls.Pr.StepSegState.Pending, Controls.Pr.StepSegState.Pending, Controls.Pr.StepSegState.Pending },
            c.Segments!.Select(x => x.State));
        Assert.Equal("RV1 程序 · 第 2/5 步 · 升温", c.ProgTip);
        c.R.Apply(rd with { Hmi = hmi with { Running = false, State = "completed", StepIndex = 4 } }, d.Model, true, true, d.Alias, "", null);
        p.Refresh(s.Fleet);
        Assert.Equal("共 5 步", c.ProgShort);
        Assert.False(c.BarRunning);
        c.R.Apply(rd with { Hmi = hmi with { Running = false, State = "failed", StepIndex = 2 } }, d.Model, true, true, d.Alias, "", null);
        p.Refresh(s.Fleet);
        Assert.Equal("报警中止", c.ProgShort);
        Assert.True(c.ProgHeld);
        Assert.Equal(Controls.Pr.StepSegState.Aborted, c.Segments![2].State);
        c.R.Apply(rd with { Hmi = null }, d.Model, true, true, d.Alias, "", null);
        p.Refresh(s.Fleet);
        Assert.False(c.HasProgram);
        Assert.Equal("无程序", c.NoneText);
    }

    [Fact]
    public void Selection_FlagsFollowShell()
    {
        var (s, p) = Open();
        Assert.True(Sec(p, 1).IsSelected);   // 默认选中第一台设备
        s.Select(1, 2);
        Assert.False(Sec(p, 1).IsSelected);
        Assert.True(Card(p, 1, 2).IsSelected);
        s.Select(2);
        Assert.False(Card(p, 1, 2).IsSelected);
        Assert.True(Sec(p, 2).IsSelected);
    }

    [Fact]
    public void Maximize_HidesOthers_FillsScrollArea()
    {
        var (_, p) = Open(width: 1002, height: 460);
        p.SetMax(3, true);
        Assert.Equal(3, p.MaxK);
        Assert.True(Sec(p, 3).IsMax);
        Assert.Equal(p.ScrollHeight, Sec(p, 3).FillHeight);
        Assert.False(Sec(p, 1).IsShown);
        Assert.False(Sec(p, 2).IsShown);
        Assert.True(p.Strip.IsVisible);   // 报警条在滚动区外面，照常显示
        p.SetMax(3, false);
        Assert.Null(p.MaxK);
        Assert.All(p.Sections, x => Assert.True(x.IsShown));
        Assert.True(double.IsNaN(Sec(p, 3).FillHeight));
    }

    [Fact]
    public void MoreMenu_IsDeviceMenu_PlusDeviceInfo()
    {
        var (_, p) = Open();
        var s1 = Sec(p, 1);
        var labels = s1.MenuItems(() => { }).Select(e => e.Head != null ? "#" + e.Head : e.IsSeparator ? "-" : e.Label).ToList();
        Assert.Equal(new[] { "#1号机 · HT-PR8 · 现场", "打开设备页", "设备信息", "-", "全部停止" }, labels.Take(5));
        Assert.DoesNotContain("设备信息", s1.ContextItems().Select(e => e.Label));
        var rows = s1.InfoRows().ToDictionary(r => r.Key, r => r.Value);
        Assert.Equal("已连接", rows["连接"]);
        Assert.Equal("192.168.1.20:8080", rows["地址"]);
        Assert.Equal("PR8-2026-0001", rows["SN"]);
        Assert.Equal("8 路 · 已装 RV1–RV4", rows["通道"]);
        Assert.EndsWith(" · 记录中", rows["会话"]);
        Assert.False(rows.ContainsKey("数据"));
    }

    [Fact]
    public void Trend_Deferred_OnlyDecision()
    {
        var (_, p) = Open("devs=1&freeze=1");
        Assert.True(p.TrendDeferred);   // 样稿此刻补「温度趋势」面板（M5 的曲线控件）
        var (_, q) = Open();
        Assert.False(q.TrendDeferred);
    }

    [Fact]
    public void ScrollParam_End_ThenTop()
    {
        var (s, p) = Open();
        var got = new List<OverviewScrollRequest>();
        p.ScrollRequested += (_, r) => got.Add(r);
        s.ApplyDemoParams(DemoParams.Parse("devs=4&scroll=end&freeze=1"));
        Assert.Contains(got, r => r.ToEnd);
        got.Clear();
        s.ApplyDemoParams(DemoParams.Parse("devs=4&freeze=1"));
        Assert.Contains(got, r => r.ToTop);
    }

    [Fact]
    public void ScrollRequest_KeptUntilTheViewTakesIt()
    {
        // 截图顺序 reactor11 → overview4dev（样稿每张都是新开的页面）：换了设备组合，页签回到默认，总览页面重建 —— 新页面的第一次 Refresh
        // 在视图建好之前就发出了「滚到底」。请求记在 PendingScroll 里，视图排好版、显示着的时候取走（M4 复核：overview4dev 截在顶部）
        var (s, _) = Open();
        s.ResetView();
        s.ApplyDemoParams(DemoParams.Parse("freeze=1"), Route.Reactor(3, 11));
        Assert.IsType<ReactorPageVm>(s.Page);
        var p4 = DemoParams.Parse("devs=4&scroll=end&freeze=1");
        s.Fleet.ResetDemo(p4);
        s.ApplyDemoParams(p4);
        s.ResetView();
        s.ApplyDemoParams(p4, Route.Overview);
        var q = Assert.IsType<OverviewPageVm>(s.Page);
        Assert.Equal(OverviewScrollRequest.End, q.PendingScroll);
        Assert.Equal(OverviewScrollRequest.End, q.TakeScroll());
        Assert.Null(q.PendingScroll);
        Assert.Null(q.TakeScroll());
        // 「定位」：同样先记下，视图滚完取走；新的请求顶掉旧的
        q.SetViewport(936, 592);
        q.Strip.LocateCommand.Execute(null);
        Assert.Equal(OverviewScrollRequest.To(3), q.PendingScroll);
        s.ApplyDemoParams(DemoParams.Parse("devs=4&freeze=1"));
        Assert.Equal(OverviewScrollRequest.Top, q.PendingScroll);
    }

    [Fact]
    public void NoDevices_EmptyState()
    {
        var (s, p) = Open();
        foreach (var d in s.Fleet.DemoData!.Devices.ToList()) s.Fleet.DemoData.Act(d, Core.Guard.DeviceAction.Remove, null, quiet: true);
        s.Fleet.RefreshNow();
        Assert.True(p.IsEmpty);
        Assert.Empty(p.Sections);
        Assert.False(p.Strip.IsVisible);
    }

    // ———————————————————— 放得下才显示（OvFit） ————————————————————

    [Theory]
    // 宽度, 数值, 单位, 目标, 要单位, 有目标 → 单位, 目标
    [InlineData(200, 80, 20, 50, true, true, true, true)]      // 都放得下：80 + 3 + 20 + 3 + 8 + 50 = 164
    [InlineData(150, 80, 20, 50, true, true, true, false)]     // 目标整个让出去，单位还在
    [InlineData(100, 80, 20, 50, true, true, false, false)]    // 单位放不下（80 + 3 + 20 = 103）：换到第二行，目标跟着走
    [InlineData(150, 80, 20, 50, false, true, false, true)]    // 阶梯这一档不带单位：目标紧跟数值（80 + 3 + 8 + 50 = 141）
    [InlineData(103, 80, 20, 0, true, false, true, false)]     // 正好放得下单位
    public void Fit_ReadingRow(double w, double vw, double uw, double tw, bool unitWanted, bool hasTarget, bool unit, bool target)
    {
        Assert.Equal((unit, target), OvFit.Read(w, vw, uw, tw, unitWanted, hasTarget));
    }

    [Fact]
    public void Fit_ProgramRow_NameCap_BarMin_BarFit()
    {
        // 234.5 宽的一排卡：程序名 78（< 42 %）+ 10 + 步骤条 + 10 + 「剩 42 min」60 → 步骤条 76.5，4 段够宽
        var a = OvFit.ProgRow(234.5, 78, 60, 4, true, true, 10);
        Assert.Equal((78d, 76.5, true, false), (a.NameW, a.BarW, a.Text, a.BarFit));
        // 程序名很长：最多 42 %
        var b = OvFit.ProgRow(200, 150, 60, 4, true, true, 10);
        Assert.Equal(84, b.NameW, 3);
        // 步骤条不到 24 px：先压程序名（2 段的最小宽度 15，24 px 够）
        var c = OvFit.ProgRow(160, 60, 70, 2, true, true, 10);
        Assert.Equal(24, c.BarW, 3);
        Assert.Equal(160 - 24 - 70 - 20, c.NameW, 3);
        Assert.True(c.Text);
        // 同样宽度、4 段（最小 33）：压到 24 px 也不够 → 剩余时间让位，程序名回到自然宽度
        var c4 = OvFit.ProgRow(160, 60, 70, 4, true, true, 10);
        Assert.True(c4.BarFit);
        Assert.Equal((60d, 90d), (c4.NameW, c4.BarW));
        // 紧凑卡 + 6 段 +「剩 999 min」：每段会窄于 6 px → 剩余时间让位，步骤条占满
        var d = OvFit.ProgRow(100, 0, 70, 6, false, true, 8);
        Assert.True(d.BarFit);
        Assert.False(d.Text);
        Assert.Equal(100, d.BarW, 3);
        // 1 段的进度条永远不让位
        Assert.False(OvFit.ProgRow(60, 0, 50, 1, false, true, 8).BarFit);
    }

    [Fact]
    public void Fit_ModelOnlyIfWholeFits()
    {
        Assert.True(OvNamePanelFits(200, 45, 91));      // 45 + 8 + 91 = 144
        Assert.False(OvNamePanelFits(140, 45, 91));     // 型号整个不写（不挤成「H…」）
        Assert.True(OvNamePanelFits(double.PositiveInfinity, 400, 400));
        static bool OvNamePanelFits(double w, double a, double m) => OvFit.ModelFits(w, a, m);
        // 整行（别名 + 型号 + 徽标 + 间距的自然宽度）放不下：型号不写，徽标紧跟别名（fitHeads 的 is-nomodel）
        Assert.True(OvFit.HeadFits(700, 700));
        Assert.False(OvFit.HeadFits(700, 700.5));
        Assert.True(OvFit.HeadFits(double.PositiveInfinity, 5000));
    }
}
