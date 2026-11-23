using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using ParallelReactor.Control.Core.Activity;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Pages.Perf;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// v0.8.1 设备活动同步（ACT-SPEC §3）在外框里：浮动活动面板（方向 A）的各个状态、自动展开 / 收起 / 关闭 / 查看结果 / 中止（先确认），
/// 设备栏活动行、状态栏一项、共用资源的气路占用 / 手动阀控 / 搅拌故障、运行表的覆盖、急停分项的报警弹窗、性能测试页的阶段与进度。
/// 数据 = 融合版演示数据（fusion=1）+ 演示参数 act= 等。
/// </summary>
public class ActivityAppTests
{
    private static ShellVm Shell(string query = "", int dev = 1, double w = 1920, double h = 1080, string page = "main")
    {
        var p = DemoParams.Parse("fusion=1&freeze=1" + (query.Length > 0 ? "&" + query : ""));
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        s.ApplyDemoParams(DemoParams.Parse("page=" + page + "&dev=" + dev + "&" + p));
        return s;
    }

    [Fact]
    public void LeakRun_CardMatchesMock()
    {
        var s = Shell("apop=0&act=leak-run");
        var c = s.Activity.Card!;
        Assert.True(s.Activity.IsVisible);
        Assert.Empty(s.Activity.Pills);
        Assert.Equal("1号机 · 泄漏测试", c.Title);
        Assert.Equal("RV1–4 · 测试压力 0.34 MPa（50 psi） · 触屏发起", c.Sub);
        Assert.Equal(("剩余", "37:20"), (c.CenterSmall, c.CenterBig));
        Assert.Equal(("当前阶段 · 2 / 4", "平衡"), (c.CurSmall, c.CurBig));
        Assert.Equal("本阶段剩 05:20", c.Line1A + c.Line1Em + c.Line1B);
        Assert.Equal("约 15:07 结束 · 已完成 16 %", c.Line2);
        Assert.Equal(new[] { "充压", "平衡", "测量", "判读 · 泄压" }, c.Segs.Select(x => x.Text));
        Assert.True(c.Segs[0].Done && c.Segs[1].Cur);
        Assert.Equal(new[] { "RV1", "RV2", "RV3", "RV4" }, c.Chips.Select(x => x.Rv));
        Assert.Equal(("0.344", "MPa", "平衡中"), (c.Chips[0].Value, c.Chips[0].Unit, c.Chips[0].Word));
        Assert.Equal("RV5–8 不测", c.Off);
        Assert.Equal("14:22:47 开始 · 判据 ≤ 0.5 psi/h", c.Foot);
        Assert.True(c.CanAbort);
        Assert.False(c.CanClose || c.CanResults);
    }

    [Fact]
    public void LeakRun_GlobalMarkers()
    {
        var s = Shell("apop=0&act=leak-run");
        var d = s.Fleet.Device(1)!;
        // 设备栏活动行
        var line = Assert.Single(s.Devices.Rows.First(r => r.K == 1).Lines);
        Assert.Equal(("b", "泄漏测试 · 平衡 · 16 %"), (line.Tone, line.Text));
        // （运行表已从主页去掉：主页通道只显示气路图）
        var home = Assert.IsType<HomePageVm>(s.Page);
        // 共用资源 · 气路：先看 gas.owner，不再写「空闲」
        Assert.Equal(("泄漏测试占用 · RV1–4", true, "排队 RV5"), (home.Res.GasState, home.Res.GasBusy, home.Res.GasQueue));
        // 状态栏：面板展开着时没有一项；选中的通道写「泄漏测试」
        Assert.False(s.Status.HasActs);
        Assert.Equal("1号机 · RV1 · 泄漏测试", s.Status.SelText);
        Assert.Equal(ChannelActKind.Leak, d.Reactor(1)!.Act!.Kind);
    }

    [Fact]
    public void Collapsed_PillAndStatusItem_ReopenFromEach()
    {
        var s = Shell("apop=0&act=leak-run&actfold=1");
        Assert.Null(s.Activity.Card);
        var pill = Assert.Single(s.Activity.Pills);
        Assert.Equal(("1号机 · 泄漏测试", "平衡 · 剩余 37:20"), (pill.Title, pill.Meta));
        var st = Assert.Single(s.Status.Acts);
        Assert.Equal("1号机 · 泄漏测试 · 平衡 · 16 %", st.Text);
        st.Command.Execute(null);
        Assert.NotNull(s.Activity.Card);
        s.Activity.Card!.FoldCommand!.Execute(null);
        Assert.Null(s.Activity.Card);
        // 设备栏的活动行同样能重新打开
        s.Devices.Rows.First(r => r.K == 1).Lines[0].Command.Execute(null);
        Assert.NotNull(s.Activity.Card);
    }

    [Fact]
    public void Collapsed_Narrow_StatusItemIsShort()
    {
        var s = Shell("apop=0&act=leak-run&actfold=1", w: 1366, h: 768);
        Assert.Equal("1号机 泄漏测试 16 %", Assert.Single(s.Status.Acts).Text);
        Assert.DoesNotContain("演示数据", s.Status.NoteText);
    }

    [Fact]
    public void Stack_WaitingCalibOpens_LeakIsPill()
    {
        var s = Shell("apop=0&act=stack");
        var c = s.Activity.Card!;
        Assert.Equal(2, c.K);
        Assert.Equal("2号机 · 容积标定 · RV4", c.Title);
        Assert.True(c.Amb);
        Assert.Equal(("已等", "03:12"), (c.CenterSmall, c.CenterBig));
        Assert.Equal(("等待触屏操作", "加水 5.00 mL"), (c.CurSmall, c.CurBig));
        Assert.Equal("往 RV4 加入 5.00 mL 水后，在触屏上点「继续」", c.Line1A + c.Line1Em + c.Line1B);
        Assert.True(c.Lock);
        Assert.False(c.CanAbort);
        Assert.Equal("标定只能在触屏操作 · 本机不能开始或中止", c.Foot);
        // 第一遍只得出比值（firstPassMl = null）：只写完成时刻
        Assert.Equal(("第一遍 · 空釜", "14:26 完成"), (c.Kv[0].Key, c.Kv[0].A));
        Assert.Equal("上次标定", c.Kv[1].Key);
        var pill = Assert.Single(s.Activity.Pills);
        Assert.Equal(1, pill.K);
        pill.Command.Execute(null);
        Assert.Equal(1, s.Activity.Card!.K);
        Assert.Equal(2, Assert.Single(s.Activity.Pills).K);
        // 2号机的设备栏行是琥珀的
        Assert.Equal("a", s.Devices.Rows.First(r => r.K == 2).Lines[0].Tone);
    }

    [Fact]
    public void LeakFail_ResultCard()
    {
        var s = Shell("apop=0&act=leak-fail");
        var c = s.Activity.Card!;
        Assert.Equal("RV1–4 · 14:28 完成 · 用时 44 min · 触屏发起", c.Sub);
        Assert.Equal(("2", "路不通过", "no"), (c.CenterBig, c.CenterSmall, c.CenterKind));
        Assert.Equal(("测试结果", "2 路不通过"), (c.CurSmall, c.CurBig));
        Assert.Equal(new[] { "ok", "no", "no", "amb" }, c.Chips.Select(x => x.Kind));
        Assert.Equal("釜内仍有测试压力，拆釜前请先泄压", c.WarnTitle);
        Assert.Contains("RV1、RV3、RV4", c.WarnBody);
        Assert.True(c.CanClose && c.CanResults && !c.CanAbort);
        Assert.False(c.HasSegs);
        // 查看结果：去性能测试 → 泄漏测试，面板收起
        c.ResultsCommand!.Execute(null);
        var pp = Assert.IsType<PerfPageVm>(s.Page);
        Assert.True(pp.IsLeak);
        Assert.Null(s.Activity.Card);
    }

    [Fact]
    public void LeakDone_Close_HidesUntilNextStart()
    {
        var s = Shell("apop=0&act=leak-done");
        var c = s.Activity.Card!;
        Assert.Equal(("4 路全部通过", "ok"), (c.CurBig, c.CurKind));
        Assert.Null(c.WarnTitle);
        Assert.Contains("本机发起", c.Sub);
        c.CloseCommand!.Execute(null);
        Assert.False(s.Activity.IsVisible);
        Assert.Empty(s.Devices.Rows.First(r => r.K == 1).Lines);
        // 下一次开始照常弹
        var demo = (DemoDeviceSource)s.Fleet.Device(1)!.Source;
        demo.Fleet.StartLeakDemo(demo.Device, new[] { 1 });
        s.Fleet.RefreshNow();
        Assert.Equal(ActPhase.Running, s.Fleet.Device(1)!.Act!.Phase);
        Assert.NotNull(s.Activity.Card);
    }

    [Fact]
    public async Task Abort_ConfirmsFirst_ThenEndsWithToast()
    {
        var s = Shell("apop=0&act=leak-run");
        s.Activity.Card!.AbortCommand!.Execute(null);
        var dlg = Assert.IsType<StopConfirmDialogVm>(s.Dialogs.Current);
        Assert.Equal("leakabort", dlg.Name);
        Assert.Equal(ActPhase.Running, s.Fleet.Device(1)!.Act!.Phase);   // 先确认：还没中止
        await ActivityActions.AbortAsync(s, s.Fleet.Device(1)!);
        s.Fleet.RefreshNow();
        var a = s.Fleet.Device(1)!.Act!;
        Assert.Equal(ActPhase.Aborted, a.Phase);
        var c = s.Activity.Card!;
        Assert.Equal(("已中止", "本次不出结果"), (c.CurBig, c.CurSmall));
        Assert.False(c.CanResults);
        Assert.Contains(s.Toasts.Items, t => t.Text.Contains("泄漏测试已中止"));
    }

    [Fact]
    public void FinishToast_PassIsAlsoAnnounced()
    {
        var pass = ActivityBuild.Leak(new ParallelReactor.Api.LeakSnap(false, 100, null, null, true, false, "done",
            Channels: new[] { new ParallelReactor.Api.LeakChannelSnap(1, "pass", 1, .1, ""), new ParallelReactor.Api.LeakChannelSnap(2, "pass", 1, .1, "") }))!;
        Assert.Equal("1号机 · 泄漏测试通过 · 2 路", ActivityPanelVm.FinishToast("1号机", pass));
    }

    [Fact]
    public void Badges_ManualValveAndStirFault()
    {
        var s = Shell("apop=0&stirfault=1&manual=1");
        var res = Assert.IsType<HomePageVm>(s.Page).Res;
        Assert.True(res.ManualValve);
        Assert.False(res.HasGasState);              // 手动阀控、没有别的占用：只画琥珀标签，不写「空闲」
        Assert.True(res.StirFault);
        Assert.Equal("电机故障 0x80", res.StirFaultTitle);
        Assert.Equal(" · 锁轴/缺相（堵转） · 在触屏清除 · 全部釜已停止搅拌", res.StirFaultRest);
        var lines = s.Devices.Rows.First(r => r.K == 1).Lines;
        Assert.Equal(new[] { ("r", "搅拌电机故障 · 在触屏清除"), ("a", "触屏手动阀控中") }, lines.Select(l => (l.Tone, l.Text)));
        Assert.All(lines, l => Assert.False(l.Clickable));
    }

    [Fact]
    public void Tune_DeviceLine()
    {
        var s = Shell("apop=0&act=leak-run&tune=1&actfold=1");
        Assert.Contains(s.Devices.Rows.First(r => r.K == 1).Lines, l => l.Text == "PID 自整定 RV5 · 150 °C");
    }

    [Fact]
    public void EstopPart_PopupShowsFourRows()
    {
        var s = Shell("estoppart=1");
        var p = s.AlarmPop;
        Assert.True(p.IsOpen);
        Assert.True(p.IsEstop);
        Assert.Equal("急停没有全部确认", p.Title);
        Assert.Equal("1号机 · 14:29:52 本机发出急停 · 3 / 4 项已确认", p.Sub);
        Assert.Equal(new[] { "程序", "加热", "搅拌", "阀门" }, p.EstopRows.Select(r => r.Part));
        Assert.Equal(new[] { false, false, false, true }, p.EstopRows.Select(r => r.Bad));
        Assert.Equal("阀门未确认关闭", p.EstopRows[3].Done);
        Assert.Equal("定位到1号机", p.LocateLabel);
        // 稍后处理之后，设备栏红行把它重新弹出来
        p.LaterCommand.Execute(null);
        Assert.False(p.IsOpen && p.IsEstop);
        var line = s.Devices.Rows.First(r => r.K == 1).Lines.First();
        Assert.Equal(("r", "阀门未确认关闭 · 到设备旁确认"), (line.Tone, line.Text));
        line.Command.Execute(null);
        Assert.True(p.IsOpen && p.IsEstop);
    }

    [Fact]
    public void Perf_LeakStageAndProgress_FromStageCode()
    {
        var s = Shell("apop=0&act=leak-run&ptm=leak", page: "test");
        var pp = Assert.IsType<PerfPageVm>(s.Page);
        Assert.True(pp.IsLeak);
        // 阶段按 stage 码（settle = 平衡），进度不乘 100
        Assert.Equal("正在测试 · 平衡 10 min · 16 %", pp.LeakState);
        Assert.True(pp.LeakPhases[0].Done);
        Assert.True(pp.LeakPhases[1].Cur);
    }
}
