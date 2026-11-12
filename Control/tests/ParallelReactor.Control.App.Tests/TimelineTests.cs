using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 时间轴（PORT-PLAN §5 M2「TimelineVm：组 / 行 / 段 / 时间刻度」；样稿 shell.js renderTimeline / tlRow / tlBar / tlFollow）与
/// 设备 / 反应釜菜单（样稿 devMenuItems / ctxMenu 'rv'，同名、同顺序）。演示数据冻结在 2026-11-08 10:12:00。
/// </summary>
public class TimelineTests
{
    private static ShellVm Shell(string query = "freeze=1", double w = 1920, double h = 1080)
    {
        var p = DemoParams.Parse(query);
        if (!p.Freeze) p = p with { Freeze = true };
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(w, h));
        s.RefreshChrome();
        return s;
    }

    [Fact]
    public void Groups_PerDevice_DefaultOpenUpTo8Channels()
    {
        var s = Shell();
        var tl = s.Timeline;
        Assert.Equal(new[] { 1, 2, 3 }, tl.Groups.Select(g => g.K));
        Assert.True(tl.Groups[0].IsOpen);
        Assert.True(tl.Groups[1].IsOpen);
        Assert.False(tl.Groups[2].IsOpen);   // 16 路默认收起
        Assert.Equal(5, tl.Groups[0].Rows.Count);
        Assert.Equal(new[] { 1, 2, 3, 4 }, tl.Groups[0].Rows.OfType<TlRowVm>().Select(r => r.R.N));
        Assert.Equal("RV5–RV8 未安装", Assert.IsType<TlUnRowVm>(tl.Groups[0].Rows[4]).Text);
        Assert.Empty(tl.Groups[2].Rows);
        Assert.Equal("本地 · 只读 · 16 路已折叠", tl.Groups[2].LeaseText);
        Assert.Equal("运行 4/4", tl.Groups[0].RunText);
        Assert.Equal("提醒 1", tl.Groups[0].WarnText);
        Assert.Null(tl.Groups[0].ErrText);
        Assert.Equal("报警 1", tl.Groups[2].ErrText);
        Assert.Equal("远程控制中", tl.Groups[0].LeaseText);
        Assert.Equal("1号机 · HT-PR8 · 现场 · 点击收起", tl.Groups[0].LabelTip);
        tl.ToggleGroup(3);
        Assert.True(tl.Groups[2].IsOpen);
        Assert.Equal(16, tl.Groups[2].Rows.Count);
        var rows = tl.Groups[2].Rows.ToList();
        s.Fleet.RefreshNow();
        Assert.Equal(rows, tl.Groups[2].Rows);   // 行的身份不变（R10）
    }

    [Fact]
    public void Header_TotalsOnlyCountOnlineDevices()
    {
        var s = Shell();
        Assert.Equal("运行 18/24", s.Timeline.RunText);
        Assert.Null(s.Timeline.OffText);
        s = Shell("devs=4&freeze=1");
        Assert.Equal("运行 18/24", s.Timeline.RunText);
        Assert.Equal("· 断线 1", s.Timeline.OffText);
        Assert.Equal("已连接设备：运行 18/24 · 另有 1 台断线（状态停在断线时刻，不计入）", s.Timeline.RunTip);
        var g4 = s.Timeline.Groups.Single(g => g.K == 4);
        Assert.True(g4.IsOff);
        Assert.StartsWith("连接中断 · 重连中 ", g4.OffText);
        Assert.Contains(" · 数据停在 ", g4.OffText);
    }

    [Fact]
    public void Window_TicksAndNowLabel()
    {
        var s = Shell();
        var tl = s.Timeline;
        Assert.Equal(new DateTime(2026, 11, 8, 10, 12, 0), tl.Now);
        Assert.Equal(tl.Now.AddMinutes(-54), tl.X0);   // 现在在左 45 %（2 h × 45 % = 54 min）
        Assert.Equal(tl.X0.AddMinutes(120), tl.X1);
        Assert.Equal("现在 10:12", tl.NowText);
        Assert.Equal(new[] { "09:30", "09:45", "10:00", "10:30", "10:45", "11:00", "11:15" }, tl.Ticks.Select(t => t.Text));
        Assert.Equal(8, tl.Lines.Count);   // 10:15 离「现在」太近：只有竖线，没有字
        Assert.Equal("2 h", tl.SpanText);
        Assert.Equal(300, tl.LabelWidth);
        tl.ZoomCommand.Execute("-1");
        Assert.Equal(60, tl.SpanMin);
        Assert.Equal("已是最短 1 h", tl.ZoomInReason);
        tl.ZoomCommand.Execute("1");
        tl.ZoomCommand.Execute("1");
        tl.ZoomCommand.Execute("1");
        Assert.Equal(480, tl.SpanMin);
        Assert.Equal("已是最长 8 h", tl.ZoomOutReason);
        s.ResetLayout();
        Assert.Equal(120, tl.SpanMin);
    }

    [Fact]
    public void LabelColumn_FollowsLayout()
    {
        Assert.Equal(300, Shell().Timeline.LabelWidth);
        var m = Shell(w: 1600, h: 900);
        Assert.Equal(300, m.Timeline.LabelWidth);
        Assert.False(m.Timeline.Narrow);
        var n = Shell(w: 1366, h: 768);
        Assert.Equal(220, n.Timeline.LabelWidth);
        Assert.True(n.Timeline.Narrow);   // 窄于 1600：组头不画设备图标、行不写程序名
        n.ToggleExplorer();
        Assert.Equal(220, n.Timeline.LabelWidth);
        var x0 = Shell("explorer=0&freeze=1");
        x0.ApplyDemoParams(DemoParams.Parse("explorer=0&freeze=1"));
        Assert.Equal(248, x0.Timeline.LabelWidth);
    }

    [Fact]
    public void Segments_OnePerStep_CurrentTag_AndEta()
    {
        var s = Shell();
        var rows = s.Timeline.Groups[0].Rows.OfType<TlRowVm>().ToList();
        var rv1 = rows[0];
        Assert.Equal(new[] { SegmentState.Done, SegmentState.Done, SegmentState.Cur, SegmentState.Next }, rv1.Segments.Select(x => x.State));
        Assert.Equal(new[] { "升温", "充压", "反应", "结束" }, rv1.Segments.Select(x => x.Name));
        Assert.Equal("反应 · 当前", rv1.Segments[2].Full);
        Assert.True(rv1.Segments[2].Run);
        Assert.StartsWith("第 3 步 反应 · ", rv1.Segments[2].Tip);
        Assert.Equal("加氢-标准", rv1.ProgName);
        Assert.Null(rv1.MoreText);                 // RV1 在窗口里结束
        Assert.Equal("11:54", rows[2].MoreText);   // RV3 在窗口右边之外结束：「11:54 ›」
        Assert.Equal("预计 11:54 结束", rows[2].MoreTip);
        Assert.Equal("11:42", rows[3].MoreText);
    }

    [Fact]
    public void HeldAndOfflineTags()
    {
        var s = Shell();
        s.Timeline.ToggleGroup(3);
        var rv11 = s.Timeline.Groups[2].Rows.OfType<TlRowVm>().Single(r => r.R.N == 11);
        var cur = rv11.Segments.Single(x => x.State == SegmentState.Cur);
        Assert.Equal("充压 · 暂停", cur.Full);
        Assert.True(cur.Held);
        Assert.False(cur.Run);

        var off = Shell("devs=4&freeze=1");
        var rv1 = off.Timeline.Groups.Single(g => g.K == 4).Rows.OfType<TlRowVm>().First(r => r.Segments.Any(x => x.State == SegmentState.Cur));
        var c4 = rv1.Segments.Single(x => x.State == SegmentState.Cur);
        Assert.EndsWith(" · 离线", c4.Full);
        Assert.False(c4.Run);
        Assert.True(rv1.Offline);
    }

    [Fact]
    public void RowActions_Reasons_StopNeverLockedByLease()
    {
        var s = Shell();
        var r21 = s.Timeline.Groups[1].Rows.OfType<TlRowVm>().First();   // 2号机 本地只读
        Assert.Equal("启动 2号机 · RV1", r21.Run.Tip);
        Assert.NotNull(r21.Run.Reason);
        Assert.Null(r21.Stop.Reason);
        Assert.Equal("停止 2号机 · RV1", r21.Stop.Tip);
        Assert.Equal("打开 2号机 · RV1", r21.OpenTip);
        Assert.True(r21.IsRunning);
        var done = s.Timeline.Groups[1].Rows.OfType<TlRowVm>().Single(r => r.R.N == 4);   // 已结束的程序：「运行程序」
        Assert.Equal("运行程序 2号机 · RV4", done.Run.Tip);
        Assert.Null(s.Timeline.Groups[1].StopAll.Reason);
        Assert.Equal("全部停止 2号机", s.Timeline.Groups[1].StopAll.Tip);
    }

    [Fact]
    public void Selection_SyncsRows_AndFollowExpandsTheOpenDevice()
    {
        var s = Shell();
        s.Select(1, 2);
        var rows = s.Timeline.Groups[0].Rows.OfType<TlRowVm>().ToList();
        Assert.Equal(new[] { false, true, false, false }, rows.Select(r => r.IsSelected));
        rows[2].SelectCommand.Execute(null);
        Assert.Equal(3, s.Selection.N);
        Assert.True(rows[2].IsSelected);

        var follows = new List<TlFollow>();
        s.Timeline.FollowRequested += (_, f) => follows.Add(f);
        s.Navigate(Route.Reactor(3, 11));
        Assert.True(s.Timeline.Groups[2].IsOpen);   // 16 路的 3号机 展开
        var last = follows.Last();
        Assert.Equal(3, last.K);
        Assert.Equal(11, last.N);
        Assert.True(last.ScrollGroupToTop);
        Assert.True(s.Timeline.Groups[2].Rows.OfType<TlRowVm>().Single(r => r.R.N == 11).IsSelected);

        // 在 3号机 · RV11 页签上选中别的设备的反应釜：那一组也展开、那一行也要可见
        s.Timeline.ToggleGroup(2);
        follows.Clear();
        s.Select(2, 3);
        Assert.True(s.Timeline.Groups[1].IsOpen);
        Assert.Equal((2, 3), follows.Last().Other);
    }

    [Fact]
    public void LiveTicks_UpdateInPlace_NoRebuild()
    {
        // 不冻结：每秒推进（UiTicker → FleetVm.OnTick），检查器与时间轴就地更新、身份不变（R10）
        var s = new ShellVm(FleetVm.CreateDemo(DemoParams.Parse("")));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.Select(1, 1);
        var insp = (ReactorInspectorVm)s.Inspector.Body!;
        var groups = s.Timeline.Groups.ToList();
        var rows = s.Timeline.Groups[0].Rows.ToList();
        var now0 = s.Timeline.Now;
        var text0 = insp.ProgText;
        for (int i = 0; i < 125; i++) s.Fleet.OnTick(advance: true);
        Assert.True(s.Timeline.Now > now0.AddMinutes(2));
        Assert.Equal(groups, s.Timeline.Groups);
        Assert.Equal(rows, s.Timeline.Groups[0].Rows);
        Assert.Same(insp, s.Inspector.Body);
        Assert.NotEqual(text0, insp.ProgText);   // 剩余时间在走
        Assert.Equal("现在 " + s.Timeline.Now.ToString("HH:mm"), s.Timeline.NowText);
    }

    [Fact]
    public void DeviceMenu_OneMenu_SameLabelsAndOrder()
    {
        var s = Shell();
        string L(MenuEntry e) => e.IsSeparator ? "-" : e.Head != null ? "#" + e.Head : e.Label + (e.Hint != null ? "（" + e.Hint + "）" : "") + (e.Reason != null ? "⊘" : "");
        var d1 = MenuModel.DeviceItems(s, s.Fleet.Device(1)!).Select(L).ToArray();
        Assert.Equal(new[] { "#1号机 · HT-PR8 · 现场", "打开设备页", "-", "全部停止（不需要控制权）", "-", "释放控制权", "断开连接…", "-", "重命名…", "编辑设置…", "移除…", "-", "急停 1号机…" }, d1);
        var d2 = MenuModel.DeviceItems(s, s.Fleet.Device(2)!).Select(L).ToArray();
        Assert.Equal("申请控制权…", d2[5]);
        Assert.True(MenuModel.DeviceItems(s, s.Fleet.Device(1)!).Last().Danger);

        var req = Shell("lease=1:requested&freeze=1");
        Assert.Equal("取消申请控制权", MenuModel.DeviceItems(req, req.Fleet.Device(1)!)[5].Label);
        var off = Shell("devs=4&freeze=1");
        var d4 = MenuModel.DeviceItems(off, off.Fleet.Device(4)!);
        Assert.Equal("立即重连", d4[6].Label);
        Assert.NotNull(d4[3].Reason);    // 断线：全部停止也发不出去（要连接）

        // 「急停 1号机…」先打开只预选这一台的急停对话框（M3）
        MenuModel.DeviceItems(s, s.Fleet.Device(1)!).Last().Run!();
        var dlg = Assert.IsType<ParallelReactor.Control.Shell.Dialogs.EstopDialogVm>(s.Dialogs.Current);
        Assert.Equal(("estop", "1", "已预选 1号机 · 可再勾选其它设备"), (dlg.Name, dlg.Arg, dlg.Caption));
    }

    [Fact]
    public void ReactorMenu_LikeMock()
    {
        var s = Shell();
        var d = s.Fleet.Device(1)!;
        string L(MenuEntry e) => e.IsSeparator ? "-" : e.Head != null ? "#" + e.Head : e.Label + (e.Reason != null ? "⊘" : "");
        var m = MenuModel.ReactorItems(s, d, d.Reactor(1)!).Select(L).ToArray();
        Assert.Equal(new[] { "#1号机 · RV1 · 反应中", "打开", "-", "启动⊘", "停止", "停止加热", "中止程序", "-", "打标记" }, m);
        var d2 = s.Fleet.Device(2)!;
        var m4 = MenuModel.ReactorItems(s, d2, d2.Reactor(4)!).Select(L).ToArray();
        Assert.Equal("运行程序⊘", m4[3]);   // 已结束的程序 → 运行程序（2号机 本地只读：带原因）
        Assert.Equal("停止", m4[4]);        // 停止类：不看控制权
    }
}
