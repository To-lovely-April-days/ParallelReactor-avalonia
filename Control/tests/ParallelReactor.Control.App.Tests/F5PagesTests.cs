using System;
using System.IO;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Alarms;
using ParallelReactor.Control.Pages.Curves;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F5：报警页（全部设备的报警与事件：表、默认选中、指标、筛选、详情、确认 / 定位 / 查看曲线 / 停止、导出）与
/// 曲线页（默认勾选、三张图的数据、光标读数表、分析条、时间范围、暂停、导出、地址参数、还原）。数据 = 融合版演示数据（fusion=1）。
/// </summary>
public class F5PagesTests
{
    private static ShellVm Shell(string query)
    {
        var p = DemoParams.Parse("fusion=1&freeze=1&apop=0");
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&apop=0&" + query));
        return s;
    }

    private static AlarmPageVm Alarms(ShellVm s) => Assert.IsType<AlarmPageVm>(s.Page);
    private static CurvePageVm Curves(ShellVm s) => Assert.IsType<CurvePageVm>(s.Page);

    // ———————————————————— 报警页 ————————————————————

    [Fact]
    public void Alarm_Device1_RowsKpisAndDefaultSelection_MatchTheMock()
    {
        var s = Shell("page=alarm&dev=1");
        var a = Alarms(s);
        Assert.Equal(new[] { "14:05:00", "14:03:18", "14:01:30", "13:58:00", "13:41:30", "13:05:00", "12:55:41", "11:22:10" }, a.Rows.Select(r => r.Time));
        Assert.Equal((1, 2, 1, 8, true), (a.KpiActive, a.KpiUnacked, a.KpiWarn, a.KpiToday, a.KpiActiveErr));
        Assert.Equal(("1号机", false), (a.ScopeText, a.IsAllDevices));
        // 默认选中最新的未确认报警（样稿 A0 = RV4 超压）
        var sel = Assert.Single(a.Rows, r => r.IsSelected);
        Assert.Equal(("RV4", "压力超上限", "3.55 MPa", "上限 3.50 MPa", "活动", "未确认", true, true), (sel.Src, sel.Title, sel.Val, sel.Lim, sel.StateText, sel.AckText, sel.ShowAck, sel.IsUa));
        var ev = a.Rows[0];
        Assert.Equal(("控制权", "PC-测试工位1 获得远程控制", "事件", true, false), (ev.Src, ev.Title, ev.StateText, ev.IsEvent, ev.ShowAck));
        var h2 = a.Rows.Last();
        Assert.Equal(("RV6", "超温 · 切断加热", "205.3 °C", "上限 200 °C", "已解除 11:24:52", "已确认 · PC-测试工位1", false), (h2.Src, h2.Title, h2.Val, h2.Lim, h2.StateText, h2.AckText, h2.IsUa));
        // 详情
        Assert.Equal(("1号机 · RV4", "压力超上限", "报警 · 1号机 HT-PR8 · RV4"), (a.DetailSub, a.DetailTitle, a.DetailMeta));
        Assert.Equal(new[] { "发生", "状态", "确认", "值 / 限值", "自动动作" }, a.DetailFields.Select(f => f.Label));
        Assert.Equal(("今天 14:03:18", "活动中", true), (a.DetailFields[0].Text, a.DetailFields[1].Text, a.DetailFields[1].Red));
        Assert.Equal(("3.55 MPa", " · 上限 3.50 MPa"), (a.DetailFields[3].Text, a.DetailFields[3].Tail));
        Assert.StartsWith("检查是否过量进气", a.DetailTip);
        var sp = Assert.IsType<AlarmSparkData>(a.Spark);
        Assert.Equal(("RV4 压力 (MPa) · 报警前后", true, 3.5), (sp.Title, sp.IsPressure, sp.Limit!.Value));
        Assert.Equal(new[] { "13:48", "14:03", "14:18" }, sp.Ticks);
        Assert.True((a.CanAck, a.HasCh, a.CanStop) == (true, true, true));
        Assert.Equal("停止 RV4", a.StopLabel);
        // 工具条
        var tools = a.Tools.SelectMany(g => g.Tools).ToList();
        Assert.Equal(new[] { "确认所选", "全部确认（3）", "导出 CSV" }, tools.Select(t => t.Label));
        Assert.All(tools, t => Assert.Null(t.Reason));
        s.Dispose();
    }

    [Fact]
    public void Alarm_AllDevices_AddsDeviceColumnAndCounts()
    {
        var s = Shell("page=alarm&dev=0");
        var a = Alarms(s);
        Assert.Equal(("全部设备", true), (a.ScopeText, a.IsAllDevices));
        Assert.Equal((2, 3, 1, 13), (a.KpiActive, a.KpiUnacked, a.KpiWarn, a.KpiToday));
        Assert.Equal(13, a.Rows.Count);
        Assert.All(a.Rows, r => Assert.True(r.ShowDevice));
        var comm = a.Rows.Single(r => r.Src == "通讯");
        Assert.Equal(("2号机", "压力采集模块离线", "已确认 · 张工 · 触屏"), (comm.Device, comm.Title, comm.AckText));
        Assert.Equal("搅拌", a.Rows.Single(r => r.Title == "搅拌电机故障").Src);
        s.Dispose();
    }

    [Fact]
    public void Alarm_Filters_AndSearch()
    {
        var s = Shell("page=alarm&dev=1");
        var a = Alarms(s);
        a.StateKey = "ua";
        Assert.Equal(new[] { "压力超上限", "升温偏慢" }, a.Rows.Select(r => r.Title));
        a.StateKey = "clr";
        Assert.Equal(new[] { "超温 · 切断加热" }, a.Rows.Select(r => r.Title));
        a.StateKey = "all";
        a.LevelKey = "ev";
        Assert.Equal(5, a.Rows.Count);
        Assert.All(a.Rows, r => Assert.True(r.IsEvent));
        a.LevelKey = "all";
        a.RangeKey = "d7";
        Assert.Equal(9, a.Rows.Count);
        Assert.Equal("昨天 16:47:20", a.Rows.Last().Time);
        a.Query = "会话";
        Assert.Equal(new[] { "会话 PT-20261115-1305 开始" }, a.Rows.Select(r => r.Title));
        Assert.Equal(new[] { "会话 PT-20261115-1305 开始" }, new[] { a.DetailTitle });   // 选中跟着筛选结果走
        a.Query = "没有这个";
        Assert.True(a.IsEmpty);
        Assert.False(a.HasDetail);
        Assert.Equal("先选一条报警", a.Tools[0].Tools[0].Reason);
        // 指标只看设备，不看其余筛选
        Assert.Equal(8, a.KpiToday);
        s.Dispose();
    }

    [Fact]
    public void Alarm_SelectClearedAlarm_DetailAndParams()
    {
        var s = Shell("page=alarm&dev=1&alsel=h2");
        var a = Alarms(s);
        Assert.Equal("超温 · 切断加热", a.DetailTitle);
        Assert.Equal(("已解除 · 11:24:52", "已确认 · PC-测试工位1"), (a.DetailFields[1].Text, a.DetailFields[2].Text));
        Assert.Equal("确认热电偶读数；冷却后在控制抽屉里重新启动加热。", a.DetailTip);
        Assert.Null(a.Spark);                     // 11:22 比本机历史（13:00 起）早
        Assert.False(a.CanAck);
        Assert.False(a.CanStop);                  // 已解除：不放停止
        Assert.True(a.HasCh);
        Assert.Equal("已确认", a.Tools[0].Tools[0].Reason);
        // 事件：说明而不是自动动作，没有确认
        a.Select(a.Rows.First(r => r.IsEvent).Key);
        Assert.Equal(new[] { "发生" }, a.DetailFields.Select(f => f.Label));
        Assert.Equal("事件不需要确认", a.Tools[0].Tools[0].Reason);
        s.Dispose();
    }

    [Fact]
    public void Alarm_Ack_Locate_Curve_Stop()
    {
        var s = Shell("page=alarm&dev=1");
        var a = Alarms(s);
        var rv2 = a.Rows.Single(r => r.Src == "RV2" && !r.IsEvent);
        a.AckOneCommand.Execute(rv2);
        s.Page!.Refresh(s.Fleet);
        rv2 = a.Rows.Single(r => r.Src == "RV2" && !r.IsEvent);
        Assert.False(rv2.ShowAck);
        Assert.StartsWith("已确认", rv2.AckText);
        Assert.Equal(1, a.KpiUnacked);
        Assert.Equal("全部确认（2）", a.Tools[0].Tools[1].Label);

        // 停止 RV4：先弹停止通道的确认框
        a.StopCommand.Execute(null);
        var dlg = Assert.IsType<StopChannelDialogVm>(s.Dialogs.Current);
        s.Dialogs.Close();
        Assert.Null(s.Dialogs.Current);

        // 查看曲线：曲线页 · 1 h · 勾上 RV4
        a.CurveCommand.Execute(null);
        var c = Curves(s);
        Assert.Equal(("h1", 1), (c.RangeKey, s.Nav.DeviceK));
        Assert.Contains(4, c.Selected);
        Assert.Equal(4, s.Nav.ChannelOf(1));

        // 定位：主页选中 RV4、打开检查器
        s.Go(AppPage.Alarms);
        a.LocateCommand.Execute(null);
        Assert.Equal(AppPage.Home, s.Nav.Page);
        Assert.Equal(4, s.Nav.ChannelOf(1));
        _ = dlg;
        s.Dispose();
    }

    [Fact]
    public void Alarm_ExportCsv_WritesFilteredRows()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-f5-" + Guid.NewGuid().ToString("N"));
        var old = AlarmPageVm.ExportDir;
        AlarmPageVm.ExportDir = () => dir;
        try
        {
            var s = Shell("page=alarm&dev=1&als=ua");
            Alarms(s).Tools[1].Tools[0].Command.Execute(null);
            var f = Assert.Single(Directory.GetFiles(dir, "alarms-*.csv"));
            var lines = File.ReadAllLines(f);
            Assert.Equal(3, lines.Length);
            Assert.Contains("压力超上限", lines[1]);
            s.Dispose();
        }
        finally
        {
            AlarmPageVm.ExportDir = old;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Alarm_ResetView_RestoresFilters()
    {
        var s = Shell("page=alarm&dev=1&als=clr&alv=hi&alq=超温");
        var a = Alarms(s);
        Assert.Single(a.Rows);
        s.ResetView();
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&apop=0&page=alarm&dev=1"));
        Assert.Equal(("day", "all", "all", ""), (a.RangeKey, a.LevelKey, a.StateKey, a.Query));
        Assert.Equal(8, a.Rows.Count);
        s.Dispose();
    }

    [Fact]
    public void AlarmLog_ReadingFallback_UsesCurrentReadings()
    {
        var s = Shell("page=alarm&dev=1");
        var d = s.Fleet.Device(1)!;
        var heat = new AlarmEntry("x", "x", AlarmLevel.Warn, 2, AlarmCodes.HeatSlow, "升温偏慢", DemoFleet.FusionT0, false);
        Assert.Equal(new AlarmReading("96.8 °C", "设定 120.0 °C"), AlarmPageVm.ReadingOf(d, heat));
        Assert.Null(AlarmPageVm.ReadingOf(d, heat with { Active = false }));
        Assert.Null(AlarmPageVm.ReadingOf(d, heat with { Ch = null }));
        s.Dispose();
    }

    // ———————————————————— 曲线页 ————————————————————

    [Fact]
    public void Curve_Defaults_MatchTheMock()
    {
        var s = Shell("page=curve&dev=1");
        var c = Curves(s);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8 }, c.Selected);
        Assert.Equal(("1号机 · 8 路", "已选 6", "1号机 · 13:00 – 现在"), (c.ChSub, c.SelText, c.TrendSub));
        Assert.Equal(8, c.Channels.Count);
        var rv7 = c.Channels.Single(x => x.N == 7);
        Assert.Equal((false, false, "—", "—"), (rv7.IsEnabled, rv7.IsOn, rv7.Temp, rv7.Press));
        var rv1 = c.Channels.Single(x => x.N == 1);
        Assert.Equal(("145.2", "2.35", true), (rv1.Temp, rv1.Press, rv1.IsOn));
        // 两张图：温度 0–200 每 50、压力 0–4 每 1 + 报警上限 3.50；三个标记（RV4 超压红）
        var t = Assert.IsType<CurvePanelData>(c.TData);
        var p = Assert.IsType<CurvePanelData>(c.PData);
        Assert.Null(c.GData);
        Assert.Equal((new CurveAxis(0, 200, 50), true, false, 6), (t.Axis, t.First, t.Last, t.Lines.Count));
        Assert.Equal((new CurveAxis(0, 4, 1), 3.5, false, true), (p.Axis, p.Limit!.Value, p.First, p.Last));
        Assert.Equal(new[] { ("RV3 加料", false), ("换气源 · 气体 A", false), ("RV4 超压", true) }, t.Marks.Select(m => (m.Text, m.Err)));
        Assert.Equal(15, t.TickMinutes);
        Assert.False(t.EndLabels);
        // 读数表 + 分析
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8 }, c.Rows.Select(r => r.N));
        var r1 = c.Rows[0];
        Assert.Equal(("反应中", "145.2", "2.35", "18.4", true), (r1.Word, r1.T, r1.P, r1.G, r1.IsSelected));
        Assert.True(c.Rows.Single(r => r.N == 4).PHot);
        Assert.Equal(("46.3", "4.8", "按已选 6 路"), (c.RsdText, c.DevText, c.CountText));
        Assert.StartsWith("最新 14:30 · 区间 13:00 – 14:30", c.ReadoutSub);
        // 工具条
        Assert.Equal(new[] { "暂停滚动", "回到最新", "打标记", "导出 CSV", "复制图片" }, c.Tools.SelectMany(g => g.Tools).Select(x => x.Label));
        s.Dispose();
    }

    [Fact]
    public void Curve_Cursor_ReadsAllChannelsAtOneTime()
    {
        var s = Shell("page=curve&dev=1&cvcur=13:53");
        var c = Curves(s);
        var tip = Assert.IsType<CurveTipData>(c.Tip);
        Assert.Equal(("13:53", 6), (tip.Head, tip.Rows.Count));
        Assert.Equal(new[] { "°C", "MPa" }, tip.Units);
        Assert.StartsWith("光标 13:53", c.ReadoutSub);
        Assert.Equal(c.Rows[0].T, tip.Rows[0].Values[0]);
        Assert.Equal(c.Rows[0].P, tip.Rows[0].Values[1]);
        // 离开图：回到最新
        c.SetCursor(null);
        Assert.Null(c.Tip);
        Assert.StartsWith("最新 14:30", c.ReadoutSub);
        // 光标取整到 10 s，夹在窗口里
        c.SetCursor(ChartTime.Ms(new DateTime(2026, 11, 15, 13, 53, 4, DateTimeKind.Local)));
        Assert.Equal(ChartTime.Ms(new DateTime(2026, 11, 15, 13, 53, 0, DateTimeKind.Local)), c.Cursor);
        c.SetCursor(ChartTime.Ms(new DateTime(2026, 11, 15, 9, 0, 0, DateTimeKind.Local)));
        Assert.Equal(ChartTime.Ms(new DateTime(2026, 11, 15, 13, 0, 0, DateTimeKind.Local)), c.Cursor);
        s.Dispose();
    }

    [Fact]
    public void Curve_VarsRangeSelectionAndSp()
    {
        var s = Shell("page=curve&dev=1&cvr=h1&cvv=TPG&cvsel=1,2,3,4&cvsp=1");
        var c = Curves(s);
        Assert.Equal(new[] { 1, 2, 3, 4 }, c.Selected);
        Assert.NotNull(c.GData);
        Assert.True(c.TData!.First && !c.TData.Last && c.GData!.Last);
        Assert.True(c.TData.EndLabels);
        Assert.Equal(10, c.TData.TickMinutes);
        Assert.Equal(4, c.TData.SpLines.Count);                  // 4 路的温度设定
        Assert.Equal(4, c.PData!.SpLines.Count);                 // 4 路都有目标压力（样稿 psp 2.50 / 2.50 / 1.40 / 2.50）
        Assert.Equal("1号机 · 13:30 – 现在", c.TrendSub);
        c.ShowT = c.ShowP = c.ShowG = false;
        Assert.True(c.NoVars);
        Assert.Null(c.TData);
        // 清空 / 运行中 / 全选
        c.NoneCommand.Execute(null);
        Assert.Empty(c.Selected);
        Assert.True(c.NoRows);
        c.RunningCommand.Execute(null);
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8 }, c.Selected);
        c.AllCommand.Execute(null);
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 8 }, c.Selected);  // RV7 停用不能勾
        c.Channels.Single(x => x.N == 5).IsOn = false;
        Assert.DoesNotContain(5, c.Selected);
        s.Dispose();
    }

    [Fact]
    public void Curve_PauseAndLatest()
    {
        var s = Shell("page=curve&dev=1&cvpause=1");
        var c = Curves(s);
        Assert.False(c.Follow);
        Assert.Equal("1号机 · 13:00 – 14:30", c.TrendSub);
        var follow = c.Tools[0].Tools[0];
        Assert.Equal(("实时跟随", "f-play"), (follow.Label, follow.Icon));
        c.Tools[0].Tools[1].Command.Execute(null);   // 回到最新
        Assert.True(c.Follow);
        Assert.Equal("暂停滚动", follow.Label);
        follow.Command.Execute(null);
        Assert.False(c.Follow);
        s.Dispose();
    }

    [Fact]
    public void Curve_ExportCsv()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pr-f5-" + Guid.NewGuid().ToString("N"));
        var old = CurvePageVm.ExportDir;
        CurvePageVm.ExportDir = () => dir;
        try
        {
            var s = Shell("page=curve&dev=1&cvsel=1,4&cvr=m5");
            Curves(s).Tools[2].Tools[0].Command.Execute(null);
            var f = Assert.Single(Directory.GetFiles(dir, "curves-*.csv"));
            var lines = File.ReadAllLines(f);
            Assert.Equal("时间,RV1 温度 °C,RV1 压力 MPa,RV4 温度 °C,RV4 压力 MPa", lines[0].TrimStart('﻿'));
            Assert.InRange(lines.Length, 30, 32);   // 5 min × 6 格
            s.Dispose();
        }
        finally
        {
            CurvePageVm.ExportDir = old;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Curve_ResetView_AndPickRow()
    {
        var s = Shell("page=curve&dev=1&cvr=m15&cvsel=2");
        var c = Curves(s);
        Assert.Equal(new[] { 2 }, c.Selected);
        c.PickRowCommand.Execute(c.Rows[0]);
        Assert.Equal(2, s.Nav.ChannelOf(1));
        Assert.True(c.Rows[0].IsSelected);
        s.ResetView();
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&apop=0&page=curve&dev=1"));
        Assert.Equal(("run", true, true, false), (c.RangeKey, c.ShowT, c.ShowP, c.ShowG));
        Assert.Equal(new[] { 1, 2, 3, 4, 6, 8 }, c.Selected);
        s.Dispose();
    }

    [Fact]
    public void ShotPlan_IncludesStageF5()
    {
        foreach (var n in new[] { "page-alarm-all", "page-alarm-sel", "page-alarm-ua", "page-curve-h1", "page-curve-sel", "page-curve-cur" })
            Assert.NotNull(Shot.ShotPlan.Find(n));
        Assert.NotNull(Shot.ShotPlan.Find("page-alarm"));
    }
}
