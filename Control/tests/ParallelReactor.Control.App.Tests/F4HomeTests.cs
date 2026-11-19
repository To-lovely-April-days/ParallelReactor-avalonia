using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Views.Pages.Home;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F4 主页其余：通道面板的气路图 / 卡片 / 棒图（共用的通道小块、气路、版面的容器查询）与总览卡片。
/// 数据 = 融合版演示数据（fusion=1：样稿 main-fusion 的 3 台设备，2026-11-15 14:30:07）。
/// </summary>
public class F4HomeTests
{
    private static ShellVm Shell(string query = "", int dev = 1, double w = 1920, double h = 1080)
    {
        var p = DemoParams.Parse("fusion=1&freeze=1" + (query.Length > 0 ? "&" + query : ""));
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        s.ApplyDemoParams(DemoParams.Parse("page=main&dev=" + dev + "&" + p));
        return s;
    }

    private static HomePageVm Home(ShellVm s) => Assert.IsType<HomePageVm>(s.Page);

    // ———————————————————— 纯函数 ————————————————————

    [Theory]
    [InlineData(StateKey.Alarm, false, "已暂停")]
    [InlineData(StateKey.Stopped, false, "已停止")]
    [InlineData(StateKey.Done, false, "已结束")]
    [InlineData(StateKey.Ready, false, "待启动")]
    [InlineData(StateKey.Idle, false, "已停用")]
    [InlineData(StateKey.React, false, "—")]          // 运行中但没有程序进度
    [InlineData(StateKey.React, true, "急停")]
    [InlineData(StateKey.Idle, true, "已停用")]       // 停用的不写急停
    public void Left_FollowsTheMock(StateKey own, bool estop, string want) => Assert.Equal(want, MonText.Left(own, estop, null));

    [Fact]
    public void StepLine_AndTip()
    {
        Assert.Equal("3/4 · 剩 27 min", MonText.StepLine(3, 4, "剩 27 min"));
        Assert.Equal("—/4 · 待启动", MonText.StepLine(null, 4, "待启动"));
        Assert.Equal("已停用", MonText.StepLine(null, null, "已停用"));
        Assert.Equal("RV1 · 反应中 · 步骤 3/4 · 剩 27 min · 搅拌 400 rpm · 加热 45 %", MonText.Tip("RV1", "反应中", 3, 4, "剩 27 min", 400, 45.2));
        Assert.Equal("RV7 · 停用 · 步骤 —/— · 已停用 · 搅拌 — rpm · 加热 — %", MonText.Tip("RV7", "停用", null, null, "已停用", null, null));
    }

    [Fact]
    public void Frac_ClampsAndNaN()
    {
        Assert.Equal(0.5, MonText.Frac(100, 200));
        Assert.Equal(1, MonText.Frac(300, 200));
        Assert.Equal(0, MonText.Frac(-1, 200));
        Assert.True(double.IsNaN(MonText.Frac(null, 200)));
        Assert.True(double.IsNaN(MonText.Frac(10, 0)));
    }

    [Fact]
    public void Strip_DoneCurNext()
    {
        var s = MonText.Strip(4, 3, 0.4, false, true);
        Assert.Equal(new[] { SegmentState.Done, SegmentState.Done, SegmentState.Cur, SegmentState.Next }, s.Select(x => x.State));
        Assert.Equal(0.4, s[2].Fill);
        Assert.True(s[2].Err);
        Assert.All(MonText.Strip(4, 4, 1, true, false), x => Assert.Equal(SegmentState.Done, x.State));
        Assert.All(MonText.Strip(4, null, 0, false, false), x => Assert.Equal(SegmentState.Next, x.State));
    }

    [Fact]
    public void Header_NamesTheLineOnly_NoGasSpecies()
    {
        Assert.Equal("共用气路总管 HDR-01 · 气体 A", MonText.Header("气体 A"));
        Assert.Equal("共用气路总管 HDR-01 · 气源全关", MonText.Header(null));
        Assert.EndsWith("点击关闭", MonText.ValveTip("进气阀 SV1", true));
        Assert.EndsWith("点击打开（要控制权）", MonText.ValveTip("进气阀 SV1", false));
        Assert.EndsWith("先申请控制权", MonText.ValveTip("进气阀 SV1", false, "先申请控制权"));
        Assert.StartsWith("进气阀 SV1 · 开 · ", MonText.ValveTip("进气阀 SV1", true));
    }

    // ———————————————————— 通道小块（1号机，8 路） ————————————————————

    [Fact]
    public void Tiles_Dev1_MatchTheMock()
    {
        var s = Shell("mv=vessel");
        var h = Home(s);
        Assert.Equal(MonView.Vessel, h.MonView);
        Assert.Equal(8, h.Tiles.Count);
        Assert.Equal(8, h.TilesTop.Count);
        Assert.Empty(h.TilesLow);
        Assert.Equal((8, 8, false), (h.MonCols, h.BarCols, h.IsN16));
        MonTileVm T(int n) => h.Tiles.Single(t => t.N == n);
        Assert.Equal(("145.2", "目标 150.0 °C", "2.35", "400", "3/6 · 剩 27 min"), (T(1).TempText, T(1).SpLine, T(1).PressText, T(1).RpmText, T(1).StepLine));
        Assert.True(T(1).IsSelected);
        Assert.Equal(TileMark.None, T(1).Mark);
        Assert.Equal(RunDot.Run, T(1).Dot);
        // RV2 提醒（升温偏慢）、RV4 报警（压力超限：红底压力、已暂停、红色的当前段）
        Assert.Equal(TileMark.Warn, T(2).Mark);
        Assert.Equal((TileMark.Err, true, "压力超限", "3/6 · 已暂停"), (T(4).Mark, T(4).PressHot, T(4).Word, T(4).StepLine));
        Assert.True(T(4).Segs.Single(x => x.State == SegmentState.Cur).Err);
        Assert.Contains("未确认", T(4).MarkTip);
        // 已就绪 / 停用：照样稿写「—/N」（设备程序的步数；程序步骤 v0.8 的程序 A 是 6 步）
        Assert.Equal(("已就绪", "—/6 · 待启动", 6), (T(5).Word, T(5).StepLine, T(5).Segs.Count));
        Assert.Equal((true, "—/6 · 已停用", "—", "0.0"), (T(7).IsOff, T(7).StepLine, T(7).RpmText, T(7).GasText));
        Assert.Equal("4/6 · 剩 9 min", T(8).StepLine);
        // 釜图 / 棒图的比例：量程 200 ℃ / 4.14 MPa，报警上限 3.50
        Assert.Equal(145.2 / 200, T(1).TFrac, 3);
        Assert.Equal(0.75, T(1).SpFrac, 3);
        Assert.Equal(3.5 / 4.14, T(1).LimFrac, 3);
        Assert.True(T(1).Heat > 0);
        Assert.True(double.IsNaN(T(7).TFrac));
        s.Dispose();
    }

    [Fact]
    public void Tile_Click_SelectsAndOpensInspector()
    {
        var s = Shell("mv=bar");
        var h = Home(s);
        h.OpenTile(h.Tiles.Single(t => t.N == 6));
        Assert.Equal(6, s.Nav.ChannelOf(1));
        Assert.Equal(PaneMode.Docked, s.Layout.Mode(DockPaneId.Inspector));
        Assert.True(h.Tiles.Single(t => t.N == 6).IsSelected);
        Assert.False(h.Tiles.Single(t => t.N == 1).IsSelected);
        Assert.Equal(MonView.Bar, h.MonView);   // 视图不切回运行表
        s.Dispose();
    }

    // ———————————————————— 气路图 ————————————————————

    [Fact]
    public void Gas_Dev1_InletsValvesAndFlow()
    {
        var s = Shell("mv=gas");
        var h = Home(s);
        var g = h.Gas;
        Assert.Equal(new[] { "惰性气体", "气体 A", "气体 B" }, g.Inlets.Select(i => i.Name));
        Assert.Equal(new[] { false, true, false }, g.Inlets.Select(i => i.IsOn));
        Assert.True(g.SrcOn);
        Assert.True(g.HeaderFlow);
        Assert.False(g.VentOn);
        Assert.Equal("共用气路总管 HDR-01 · 气体 A", g.HeaderText);
        Assert.DoesNotContain("H₂", g.HeaderText + string.Concat(g.Inlets.Select(i => i.Tip)));
        MonTileVm T(int n) => h.Tiles.Single(t => t.N == n);
        // 样稿：SV1 / SV2 / SV3 / SV6 开（有气流），SV4（报警）/ SV5 / SV7 / SV8 关
        Assert.Equal(new[] { 1, 2, 3, 6 }, h.Tiles.Where(t => t.SvOpen).Select(t => t.N));
        Assert.Equal(new[] { 1, 2, 3, 6 }, h.Tiles.Where(t => t.Flow).Select(t => t.N));
        Assert.True(T(1).Bubbles);
        Assert.Equal("SV4", T(4).SvLabel);
        Assert.StartsWith("进气阀 SV4 · 关 · ", T(4).SvTip);            // RV4 报警中：写原因，不写「触屏操作」（HMI v0.7 起可以直接点）
        Assert.Contains("不能手动开阀", T(4).SvTip);
        Assert.Equal("进气阀 SV1 · 开 · 点击关闭", T(1).SvTip);
        s.Dispose();
    }

    /// <summary>
    /// 阀门直接开关（HMI v0.7，用户：「我不要维护模式 我要远程控制可以控制一切操作」）：点开着的关、点关着的开；
    /// 报警中的一路不让开（写原因、什么都不发）；气源、排空同样。演示数据（1号机远程控制中）。
    /// </summary>
    [Fact]
    public void Valves_ToggleDirectly_NoMaintenanceMode()
    {
        var s = Shell("mv=gas");
        var h = Home(s);
        var d = s.Fleet.Device(1)!;
        void Refresh() { s.Fleet.RefreshNow(); }
        Assert.True(d.Reactor(1)!.Valve);
        h.ToggleSv(1, true);                                     // 关 SV1（停止类）
        Refresh();
        Assert.False(d.Reactor(1)!.Valve);
        Assert.Contains(s.Toasts.Items, t => t.Text == "1号机 · SV1 已关");
        h.ToggleSv(5, false);                                    // 开 SV5（控制类，1号机有控制权）
        Refresh();
        Assert.True(d.Reactor(5)!.Valve);
        h.ToggleSv(4, false);                                    // RV4 报警：不让开
        Refresh();
        Assert.False(d.Reactor(4)!.Valve);
        Assert.Contains(s.Toasts.Items, t => t.Text.Contains("不能手动开阀"));
        // 气源 / 排空
        var key = d.Model.GasInlets[0].Key;
        bool was = d.Source.GasOpen(key);
        h.ToggleGas(key, was);
        Refresh();
        Assert.NotEqual(was, d.Source.GasOpen(key));
        bool vent = d.Source.GasOpen(d.Model.Vent!.Key);
        h.ToggleVent();
        Refresh();
        Assert.NotEqual(vent, d.Source.GasOpen(d.Model.Vent.Key));
        s.Dispose();
    }

    [Fact]
    public void Dev3_SixteenChannels_TwoRowsAndUnits()
    {
        var s = Shell("mv=bar", dev: 3);
        var h = Home(s);
        Assert.Equal(16, h.Tiles.Count);
        Assert.Equal(Enumerable.Range(1, 8), h.TilesTop.Select(t => t.N));
        Assert.Equal(Enumerable.Range(9, 8), h.TilesLow.Select(t => t.N));
        Assert.Equal((8, 16, true), (h.MonCols, h.BarCols, h.IsN16));
        Assert.True(h.ShowBarUnits);
        Assert.All(h.Tiles, t => Assert.Equal("", t.BarTUnit));
        Assert.Equal(TileMark.Err, h.Tiles.Single(t => t.N == 11).Mark);
        h.MonKey = "gas";
        Assert.False(h.ShowBarUnits);
        s.Dispose();
    }

    [Fact]
    public void Dev2_FourChannels()
    {
        var s = Shell("mv=vessel", dev: 2);
        var h = Home(s);
        Assert.Equal((4, 4, 4, true), (h.Tiles.Count, h.MonCols, h.BarCols, h.IsN4));
        Assert.Equal("6/6 · 已结束", h.Tiles.Single(t => t.N == 4).StepLine);
        s.Dispose();
    }

    // ———————————————————— 显示方式 / 看板两行 ————————————————————

    [Fact]
    public void MonView_Switching_AndRow2OnShortScreens()
    {
        var s = Shell();
        var h = Home(s);
        Assert.True(h.IsRunView);
        Assert.False(h.IsMvX);
        h.MonKey = "gas";
        Assert.Equal((true, true, false, false), (h.IsMvX, h.IsGasView, h.IsVesselView, h.HideRow2));
        s.Dispose();

        var t = Shell("mv=vessel", w: 1366, h: 768);
        var ht = Home(t);
        Assert.True(ht.IsShort);
        Assert.True(ht.HideRow2);
        ht.MonKey = "run";
        Assert.False(ht.HideRow2);
        ht.MonKey = "bar";
        t.ResetLayout();
        Assert.Equal(MonView.Run, ht.MonView);   // 重置布局回到运行表
        t.Dispose();
    }

    [Fact]
    public void ResPane_SeeGasSchematic_SwitchesView()
    {
        var s = Shell();
        var h = Home(s);
        h.Res.ShowGasCommand.Execute(null);
        Assert.Equal(MonView.Gas, h.MonView);
        Assert.Equal("6", h.RunCount);
        Assert.Equal("/8", h.RunOf);
        s.Dispose();
    }

    // ———————————————————— 版面（代替样稿的容器查询） ————————————————————

    [Fact]
    public void GasLayout_Modes()
    {
        var big = GasLayout.For(1532, 523, 8);
        Assert.Equal((true, false, false, 136.0, 74.0, 14.0, 30.0), (big.Big, big.Compact, big.Narrow, big.Gl, big.Gr, big.Co, big.BusH));
        Assert.Equal(220, GasLayout.PipeRow(513, 238, big));
        Assert.Equal(100, GasLayout.PipeRow(368, 238, big));
        var mid = GasLayout.For(1532, 400, 8);
        Assert.Equal((false, false, 26.0), (mid.Big, mid.Compact, mid.BusH));
        var cmp = GasLayout.For(1532, 240, 8);
        Assert.Equal((true, 88.0, 40.0, 8.0, 19.0, 10.0), (cmp.Compact, cmp.Gl, cmp.Gr, cmp.Co, cmp.Rh, cmp.BusH));
        var nar = GasLayout.For(800, 500, 8);
        Assert.Equal((true, 96.0, 46.0), (nar.Narrow, nar.Gl, nar.Gr));
        var mir = GasLayout.For(1532, 523, 16);
        Assert.True(mir.Mir);
        Assert.Equal(110, mir.PipeMax);
        Assert.Equal(110, GasLayout.PipeRow(513, 2 * 140, mir));
        Assert.False(GasLayout.For(1532, 523, 8).Tiny);
        Assert.True(GasLayout.For(700, 400, 8).Tiny);
    }

    [Fact]
    public void VesselLayout_WideAndCompact()
    {
        var n8 = VesselLayout.For(1532, 515, 8);
        Assert.Equal((false, false), (n8.Wide, n8.Compact));
        Assert.Equal(179.5, n8.CardW, 1);
        Assert.True(VesselLayout.For(1532, 515, 4).Wide);
        Assert.True(VesselLayout.For(1244, 553, 4).Wide);
        Assert.True(VesselLayout.For(1532, 515, 16).Compact);
        var low = VesselLayout.For(1532, 280, 8);
        Assert.Equal((true, false), (low.Compact, low.Wide));
    }

    // ———————————————————— 总览 ————————————————————

    [Fact]
    public void Overview_Cards_MatchTheMock()
    {
        var s = Shell(dev: 0);
        var h = Home(s);
        Assert.True(h.IsOverview);
        Assert.Equal("全部设备 · 3 台 · 28 路", h.OverviewSub);
        Assert.Equal(3, h.Cards.Count);
        var (c1, c2, c3) = (h.Cards[0], h.Cards[1], h.Cards[2]);
        Assert.Equal(("1号机", "6/8", "1", "86.6", RunDot.Err, true), (c1.Alias, c1.RunText, c1.ErrText, c1.GasText, c1.State, c1.Remote));
        Assert.Equal(("3/4", "0", "96.0", RunDot.Run, false, "本地只读"), (c2.RunText, c2.ErrText, c2.GasText, c2.State, c2.Remote, c2.LeaseShort));
        Assert.Equal(("11/16", "59.8", RunDot.Err), (c3.RunText, c3.GasText, c3.State));
        Assert.Equal(8, c1.Mats.Count);
        Assert.Equal(16, c3.Mats.Count);
        Assert.Equal(new[] { "4" }, c1.Mats.Where(m => m.IsErr).Select(m => m.Num));
        Assert.Equal(new[] { "11" }, c3.Mats.Where(m => m.IsErr).Select(m => m.Num));
        Assert.Equal(5, c1.Mats.Count(m => m.IsRun));   // 运行 6 里含报警的 RV4（红框）
        Assert.StartsWith("RV4 · 压力超限 · 158.4", c1.Mats[3].Tip);
        c2.OpenCommand.Execute(null);
        Assert.Equal(2, s.Nav.DeviceK);
        Assert.False(h.IsOverview);
        s.Dispose();
    }

    [Fact]
    public void DeviceInfo_ShowsThePressureAlarmLimit()
    {
        var s = Shell();
        Assert.Equal("3.50 MPa", s.Devices.Info.Single(x => x.Key == "压力报警").Value);
        s.Dispose();
    }
}
