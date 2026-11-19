using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 融合版 F3 主页：运行表的行（预计结束、偏差标签、本步剩余、断线变灰）、报警弹窗的队列（稍后处理、翻页、确认）、断线横条、停止类对话框的文字、溶液体积的说明。
/// 数据 = 融合版演示数据（fusion=1：样稿 main-fusion 的 3 台设备，2026-11-15 14:30:07）。
/// </summary>
public class F3HomeTests
{
    private static ShellVm Shell(string query = "")
    {
        var p = DemoParams.Parse("fusion=1&freeze=1" + (query.Length > 0 ? "&" + query : ""));
        var s = new ShellVm(FleetVm.CreateDemo(p));
        s.Toasts.Frozen = true;
        s.Layout.Update(new Size(1920, 1080));
        s.ApplyDemoParams(DemoParams.Parse("page=main&dev=1&" + p));
        return s;
    }

    private static HomePageVm Home(ShellVm s) => Assert.IsType<HomePageVm>(s.Page);

    private static AlarmEntry Alarm(string id, AlarmLevel level, bool acked = false, string code = "P-HIGH", bool active = true) =>
        new(id, id, level, 4, code, "压力超上限 · 已关闭进气阀 · 程序暂停", new DateTime(2026, 11, 15, 14, 3, 18), acked, Active: active);

    // ———————————————————— 运行表 ————————————————————

    [Theory]
    [InlineData(StateKey.React, 145.2, 150.0, "−4.8")]
    [InlineData(StateKey.React, 153.1, 150.0, "+3.1")]
    [InlineData(StateKey.React, 148.0, 150.0, null)]     // |Δ| = 2：不出
    [InlineData(StateKey.Alarm, 158.4, 160.0, null)]
    [InlineData(StateKey.Alarm, 157.0, 160.0, "−3.0")]
    [InlineData(StateKey.Heat, 96.8, 120.0, null)]       // 升温中不出（样稿只在反应中 / 报警）
    public void DevChip_OnlyReactOrAlarm_AboveTwoDegrees(StateKey own, double t, double sp, string? want) =>
        Assert.Equal(want, RunRowText.DevChip(own, t, sp));

    [Fact]
    public void DevChip_NoReadingNoChip()
    {
        Assert.Null(RunRowText.DevChip(StateKey.React, null, 150));
        Assert.Null(RunRowText.DevChip(StateKey.React, 140, null));
        Assert.Equal("比设定值低 4.8 °C", RunRowText.DevTip(145.2, 150));
    }

    [Fact]
    public void Rows_EndTimes_LeftAndChips_MatchTheMock()
    {
        var s = Shell();
        var h = Home(s);
        Assert.Equal(8, h.Rows.Count);
        RunRowVm R(int n) => h.Rows.Single(r => r.N == n);
        Assert.Equal(("27", "15:19", "−4.8"), (R(1).LeftText, R(1).EndText, R(1).DevChip));
        Assert.Equal(("7", "15:47"), (R(2).LeftText, R(2).EndText));
        Assert.Equal(("21", "15:13"), (R(3).LeftText, R(3).EndText));
        Assert.Equal(("21", "15:13"), (R(6).LeftText, R(6).EndText));
        Assert.Equal(("9", "14:39", "降温", "4/4"), (R(8).LeftText, R(8).EndText, R(8).StepName, R(8).StepFrac));
        // 报警：已暂停、没有预计结束、红行、压力红字
        Assert.Equal((LeftKind.Paused, "已暂停", (string?)null, true, true), (R(4).Left, R(4).LeftText, R(4).EndText, R(4).IsErr, R(4).PressHot));
        Assert.Equal(("压力超限", "恒温反应", "3/4"), (R(4).Word, R(4).StepName, R(4).StepFrac));
        Assert.True(R(4).Segs.Single(x => x.State == SegmentState.Cur).Err);
        // 已就绪 / 停用
        Assert.Equal(("已就绪", false, "未运行 · 待启动", LeftKind.None), (R(5).Word, R(5).HasProg, R(5).NoProg, R(5).Left));
        Assert.Equal(("停用", true, "已停用", true, true), (R(7).Word, R(7).IsOff, R(7).NoProg, R(7).TempNa, R(7).PressNa));
        // 压力 2 位小数、吸气量 1 位、速率 2 位
        Assert.Equal(("2.35", "2.50", "18.4", "0.21"), (R(1).PressText, R(1).PSpText, R(1).GasText, R(1).RateText));
        Assert.True(R(4).RateNa);
        // 选中 RV1（默认）
        Assert.True(R(1).IsSelected);
        Assert.False(R(2).IsSelected);
        // 灰带
        Assert.Equal(("1号机", " · 8 路 · 运行 ", "6", "报警 1", " · 程序「平行合成程序 A」"), (h.BoardTitle, h.CtxA, h.CtxRun, h.CtxErr, h.CtxProg));
        s.Dispose();
    }

    [Fact]
    public void Segs_WidthsFollowStepMinutes_DoneCurNext()
    {
        var s = Shell();
        var segs = Home(s).Rows.Single(r => r.N == 1).Segs;
        Assert.Equal(new[] { SegmentState.Done, SegmentState.Done, SegmentState.Cur, SegmentState.Next }, segs.Select(x => x.State));
        Assert.Equal(new[] { 50.7, 3.0, 45.0, 22.0 }, segs.Select(x => Math.Round(x.Weight, 1)));
        Assert.Equal(0.4, segs[2].Fill, 2);
        Assert.Equal("第 3 步 恒温反应 · 约 45 min", segs[2].Tip);
        s.Dispose();
    }

    [Fact]
    public void Row_Click_SelectsAndOpensInspector()
    {
        var s = Shell();
        var h = Home(s);
        h.OpenRow(h.Rows.Single(r => r.N == 4));
        Assert.Equal(4, s.Nav.ChannelOf(1));
        Assert.Equal(PaneMode.Docked, s.Layout.Mode(DockPaneId.Inspector));
        Assert.True(h.Rows.Single(r => r.N == 4).IsSelected);
        Assert.Equal("RV4", s.Ctl.Title);
        Assert.Equal("压力超限", s.Ctl.Word);
        s.Dispose();
    }

    [Fact]
    public void Stale_WhenTheDeviceIsLost_ReadingsKept()
    {
        var s = Shell("net=lost");
        var h = Home(s);
        Assert.True(h.IsStale);
        var r1 = h.Rows.Single(r => r.N == 1);
        Assert.True(r1.Stale);
        Assert.Equal(("145.2", "反应中"), (r1.TempText, r1.Word));   // 自己的状态、最后的读数
        Assert.True(h.Net.IsLost);
        Assert.Equal("与1号机的连接中断 1 min 26 s", h.Net.Title);
        Assert.Contains("下面的数据停在 14:28:41", h.Net.Body, StringComparison.Ordinal);
        Assert.Contains("已试 3 次", h.Net.Body, StringComparison.Ordinal);
        Assert.Equal("已连接 2/3 台 · 1号机重连中", s.Status.Conn.Text);
        s.Dispose();
    }

    [Fact]
    public void C16_Device_UsesCompactRows()
    {
        var s = Shell();
        s.SelectDevice(3);
        var h = Home(s);
        Assert.True(h.IsC16);
        Assert.Equal(16, h.Rows.Count);
        Assert.True(h.Curve.IsC16);
        Assert.Empty(h.Curve.Chips);
        Assert.Equal("RV9 升压中 · 占用", h.Res.GasState);
        s.Dispose();
    }

    // ———————————————————— 共用资源 / 曲线 ————————————————————

    [Fact]
    public void Resources_GasStir_Session_Events()
    {
        var s = Shell();
        var r = Home(s).Res;
        Assert.Equal("空闲 · 无排队", r.GasState);
        Assert.Equal(new[] { "惰性气", "气体 A", "气体 B", "排空" }, r.Valves.Select(v => v.Name));
        Assert.Equal(new[] { false, true, false, false }, r.Valves.Select(v => v.IsOn));
        // 只写气路名，不猜接的是什么气、也不写「未接」（接什么气由工艺决定，2026-11-18 用户确认）
        Assert.All(r.Valves, v => Assert.False(v.IsNa));
        Assert.All(r.Valves, v => Assert.Equal("", v.Gas));
        Assert.Equal("气体 A · 接什么气由工艺决定 · 开 · 点击关闭", r.Valves[1].Tip);   // HMI v0.7：点击直接开关
        Assert.Equal(("400", "1.2 A", true), (r.RpmText, r.AmpText, r.StirOn));
        var gasRow = s.Devices.Info.Single(x => x.Key == "气源");
        Assert.Equal(("惰性气体、气体 A、气体 B", "接什么气由工艺决定"), (gasRow.Value, gasRow.Sub));
        Assert.Equal("400 rpm · 全部釜共用", s.Devices.Info.Single(x => x.Key == "搅拌").Value);
        Assert.Equal("记录中 · PT-20261115-1305", r.SessText);
        Assert.Equal(("14:05", "控制权", "PC-测试工位1 获得远程控制"), (r.Events[0].Time, r.Events[0].Src, r.Events[0].Title));
        Assert.Equal(("RV4", "压力超上限", true), (r.Events[1].Src, r.Events[1].Title, r.Events[1].IsErr));
        Assert.Equal("RV2 升压中 · 占用 · 排队 RV3、RV5", ResPaneVm.GasStateText(2, new[] { 3, 5 }, "升压中"));
        s.Dispose();
    }

    [Fact]
    public void Overlay_Modes_LimitAndSelection()
    {
        var s = Shell();
        var c = Home(s).Curve;
        Assert.Equal(7, c.Data!.Lines.Count);                    // RV7 停用不画
        Assert.Equal("7 路叠加 · 13:00 – 现在", c.OptText);
        Assert.Null(c.Data.Limit);
        Assert.Equal(1, c.Data.Selected);
        c.ModeKey = "P";
        Assert.Equal((OverlayMode.P, 3.5), (c.Data!.Mode, c.Data.Limit));
        c.Chips.Single(x => x.N == 6).PickCommand.Execute(null);
        Assert.Equal(6, s.Nav.ChannelOf(1));
        Assert.Equal(6, c.Data!.Selected);
        Assert.Equal(new[] { 1.0, 2.0, 2.5, 5.0, 10.0 }, new[] { 0.9, 1.7, 2.3, 4.1, 7 }.Select(Controls.Home.OverlayChart.Nice));
        s.Dispose();
    }

    // ———————————————————— 报警弹窗 ————————————————————

    [Fact]
    public void PopQueue_OnlyUnackedErrors_SnoozeAndPaging()
    {
        var q = new PopQueue();
        q.Update(new (int, AlarmEntry)[]
        {
            (1, Alarm("a1", AlarmLevel.Err)),
            (1, Alarm("a2", AlarmLevel.Warn)),                    // 提醒不弹
            (1, Alarm("a3", AlarmLevel.Err, acked: true)),        // 已确认不弹
            (2, Alarm("a4", AlarmLevel.Err, active: false)),      // 已解除不弹
            (2, Alarm("a5", AlarmLevel.Err, code: AlarmCodes.Estop)),   // 急停那一条不弹
            (3, Alarm("a6", AlarmLevel.Err)),
        });
        Assert.Equal(new[] { "1:a1", "3:a6" }, q.List.Select(x => x.Key));
        Assert.Equal(("1:a1", 0), (q.Current!.Value.Key, q.Index));
        Assert.False(q.Prev());
        Assert.True(q.Next());
        Assert.Equal("3:a6", q.Current!.Value.Key);
        Assert.False(q.Next());
        q.Snooze();                                               // 稍后处理当前这一条
        Assert.Equal(new[] { "1:a1" }, q.List.Select(x => x.Key));
        // 再刷新：稍后处理的不回来；新来的照样弹
        q.Update(new (int, AlarmEntry)[] { (1, Alarm("a1", AlarmLevel.Err)), (3, Alarm("a6", AlarmLevel.Err)), (3, Alarm("a7", AlarmLevel.Err)) });
        Assert.Equal(new[] { "1:a1", "3:a7" }, q.List.Select(x => x.Key));
        q.Reset();
        Assert.Null(q.Current);
    }

    [Fact]
    public void AlarmPop_OpensForUnackedErrors_PagesAndTexts()
    {
        var s = Shell();
        s.AlarmPop.Reset(false);
        var p = s.AlarmPop;
        Assert.True(p.IsOpen);
        Assert.Equal(("压力超上限", "1号机 · RV4 · 报警 · 14:03:18 发生 · 已持续 27 min"), (p.Title, p.Sub));
        Assert.Equal(("1 / 2", false, true), (p.PageText, p.CanPrev, p.CanNext));
        Assert.Equal(new[] { "当前压力 3.55 MPa", "报警上限 3.50 MPa", "设定压力 2.50 MPa" }, p.Tiles.Select(t => t.Label + " " + t.Value + " " + t.Unit));
        Assert.True(p.Tiles[0].Bad);
        Assert.Equal("已关闭进气阀 · 程序暂停", p.AutoText);
        Assert.StartsWith("检查是否过量进气", p.Tip, StringComparison.Ordinal);
        Assert.True(p.HasTrend);
        Assert.Equal(("定位到 RV4", "停止 RV4"), (p.LocateLabel, p.StopLabel));
        p.NextCommand.Execute(null);
        Assert.Equal("3号机 · RV11 · 报警 · 13:58:42 发生 · 已持续 31 min", p.Sub);
        // 对话框开着时不弹
        s.OpenDialog("estop");
        Assert.False(p.IsOpen);
        s.Dialogs.Close();
        Assert.True(p.IsOpen);
        // Esc = 稍后处理（这一条）
        Assert.True(s.Escape());
        Assert.Equal("1号机 · RV4 · 报警 · 14:03:18 发生 · 已持续 27 min", p.Sub);
        // 定位：主页选中 RV4、打开检查器，这一条不再弹
        p.LocateCommand.Execute(null);
        Assert.Equal(4, s.Nav.ChannelOf(1));
        Assert.False(p.IsOpen);
        s.Dispose();
    }

    [Fact]
    public void AlarmPop_Ack_AndOffParam()
    {
        var s = Shell();
        s.AlarmPop.Reset(false);
        s.AlarmPop.AckCommand.Execute(null);
        Assert.True(s.Fleet.Device(1)!.AlarmList.Single(a => a.Ch == 4).Acked);
        Assert.Equal("3号机 · RV11 · 报警 · 13:58:42 发生 · 已持续 31 min", s.AlarmPop.Sub);
        s.ApplyDemoParams(DemoParams.Parse("apop=0"));
        Assert.False(s.AlarmPop.IsOpen);
        s.Dispose();
    }

    [Fact]
    public void AlarmPop_Split_And_Lasting()
    {
        Assert.Equal(("压力超上限", "已关闭 SV11 · 程序暂停"), AlarmPopVm.Split("压力超上限 · 已关闭 SV11 · 程序暂停"));
        Assert.Equal(("急停已触发", ""), AlarmPopVm.Split("急停已触发"));
        Assert.True(AlarmPopVm.IsPressure("X", "压力超上限"));
        Assert.False(AlarmPopVm.IsPressure("T-HIGH", "超温 · 切断加热"));
        Assert.Equal("已持续 1 min", AlarmPopVm.Lasting(new DateTime(2026, 1, 1, 10, 0, 0), new DateTime(2026, 1, 1, 10, 0, 10)));
    }

    // ———————————————————— 断线横条 ————————————————————

    [Fact]
    public void NetBanner_Texts()
    {
        Assert.Equal(("26 s", "2 min 26 s", "1 h 05 min"), (NetBannerText.Gap(TimeSpan.FromSeconds(26)), NetBannerText.Gap(TimeSpan.FromSeconds(146)), NetBannerText.Gap(TimeSpan.FromMinutes(65))));
        Assert.Equal("与1号机的连接中断 2 min 26 s", NetBannerText.LostTitle("1号机", TimeSpan.FromSeconds(146)));
        Assert.Equal("设备照常运行（断开时：保持运行）· 下面的数据停在 14:28:41 · 自动重连，已试 3 次 · 恢复后自动补拉这段数据，会话记录不断",
            NetBannerText.LostBody("keep", new DateTime(2026, 11, 15, 14, 28, 41), 3, backfill: true));
        Assert.EndsWith("恢复后从最新数据接着显示", NetBannerText.LostBody("stopHeat", null, 0, backfill: false), StringComparison.Ordinal);
        Assert.Contains("停止本机启动的加热", NetBannerText.LostBody("stopHeat", null, 0, false), StringComparison.Ordinal);
        Assert.StartsWith("设备按设置急停", NetBannerText.LostBody("estop", null, 0, false), StringComparison.Ordinal);
        var lost = new DateTime(2026, 11, 15, 14, 28, 41);
        Assert.Equal("断开 2 min 26 s（14:28:41 – 14:31:07）· 已补拉这段的 146 条数据 · 会话 PT-1 记录完整，期间没有新报警",
            NetBannerText.BackBody(lost, lost.AddSeconds(146), 146, "PT-1", 0));
        Assert.Equal("断开 26 s（14:28:41 – 14:29:07）· 这段数据没有补拉（曲线在这里断开）· 期间有 2 条新报警",
            NetBannerText.BackBody(lost, lost.AddSeconds(26), null, null, 2));
        Assert.Equal(NetBannerKind.Lost, NetBannerText.KindOf(offline: true, everOnline: true, hasBack: false));
        Assert.Equal(NetBannerKind.None, NetBannerText.KindOf(offline: true, everOnline: false, hasBack: false));   // 从未连上：不是「中断」
        Assert.Equal(NetBannerKind.Back, NetBannerText.KindOf(offline: false, everOnline: true, hasBack: true));
        Assert.Equal(NetBannerKind.None, NetBannerText.KindOf(offline: false, everOnline: true, hasBack: false));
    }

    [Fact]
    public void NetBanner_BackAfterReconnect_Dismiss()
    {
        var s = Shell("offline=1");
        var h = Home(s);
        Assert.True(h.Net.IsLost);
        s.ApplyDemoParams(DemoParams.Parse("fusion=1&freeze=1&offline=none"));   // 连回来：横条变成「已重新连接」
        Assert.True(h.Net.IsBack);
        Assert.Equal("已重新连接1号机", h.Net.Title);
        Assert.Contains("这段数据没有补拉", h.Net.Body, StringComparison.Ordinal);   // 补拉在 F8
        h.Net.DismissCommand.Execute(null);
        Assert.False(h.Net.IsVisible);
        s.Dispose();

        var b = Shell("net=back");
        Assert.True(Home(b).Net.IsBack);
        Assert.Contains("已补拉这段的 146 条数据 · 会话 PT-20261115-1305 记录完整", Home(b).Net.Body, StringComparison.Ordinal);
        b.Dispose();
    }

    // ———————————————————— 停止类对话框 ————————————————————

    [Fact]
    public void StopTexts_MatchTheMock()
    {
        string Flat(IEnumerable<StopRow> rows) => string.Join("｜", rows.Select(r => r.Key + "：" + string.Concat(r.Value.Select(v => v.Text))));
        Assert.Equal("程序：该路程序中止｜加热：该路停止加热｜搅拌：不变 · 电机全部釜共用，其它路还在用｜气路：关该路进气阀 SV；正在给该路升压的序列中止｜釜内压力：保留，不放空 · 温度自然下降｜之后：可以重新启动该路",
            Flat(StopTexts.Channel));
        Assert.Contains("搅拌：停止｜", Flat(StopTexts.All(true)), StringComparison.Ordinal);
        Assert.Contains("搅拌：不变｜", Flat(StopTexts.All(false)), StringComparison.Ordinal);
        Assert.Equal("复位：只能在设备触屏上", Flat(StopTexts.Estop).Split('｜').Last());
        Assert.Equal("三种停止都不会自动放空。要泄压：程序最后一步「结束方式」选「放空」，或降温后在触屏上手动放空。", string.Concat(StopTexts.Note.Select(x => x.Text)));
        Assert.Equal("不会自动放空", StopTexts.Note.Single(x => x.Bold).Text);
        Assert.Equal("这是经网络发出的软件停机请求，不能代替设备上的急停按钮。", StopTexts.LeadEstop.Single(x => x.Bold).Text);
    }

    /// <summary>报警弹窗的「停止 RVn」先弹停止确认框（2026-11-18 用户：停止一律先确认）；确认框开着时弹窗让开，确认后才停。</summary>
    [Fact]
    public void AlarmPop_Stop_OpensConfirm_NotStoppedYet()
    {
        var s = Shell();
        s.AlarmPop.Reset(false);
        var before = s.Fleet.Device(1)!.Reactor(4)!.Own;
        s.AlarmPop.StopCommand.Execute(null);
        var d = Assert.IsType<StopChannelDialogVm>(s.Dialogs.Current);
        Assert.Equal((1, 4, "停止 1号机 RV4"), (d.K, d.N, d.Title));
        Assert.False(s.AlarmPop.IsOpen);
        Assert.Equal(before, s.Fleet.Device(1)!.Reactor(4)!.Own);   // 还没停
        d.GoButton.Command.Execute(null);
        Assert.Null(s.Dialogs.Current);
        Assert.Equal(StateKey.Stopped, s.Fleet.Device(1)!.Reactor(4)!.Own);
        s.Dispose();
    }

    [Fact]
    public void StopChannelDialog_TitleButton_SendsStop()
    {
        var s = Shell();
        s.OpenDialog("stopch", "1:1");
        var d = Assert.IsType<StopChannelDialogVm>(s.Dialogs.Current);
        Assert.Equal(("停止 1号机 RV1", (string?)null, (string?)null, 518d), (d.Title, d.Sub, d.Icon, d.Width));
        Assert.Equal(("停止 RV1", FootKind.PrimaryStop, (string?)null), (d.GoButton.Label, d.GoButton.Kind, d.GoButton.Reason));
        d.GoButton.Command.Execute(null);
        Assert.Null(s.Dialogs.Current);
        Assert.Equal(StateKey.Stopped, s.Fleet.Device(1)!.Reactor(1)!.Own);
        s.Dispose();

        var t = Shell();
        t.OpenDialog("stopch", "1:99");      // 没有这一路：不打开
        Assert.Null(t.Dialogs.Current);
        t.Dispose();
    }

    [Fact]
    public void StopAll_FromChannelPane_PreselectsTheDevice()
    {
        var s = Shell();
        Home(s).StopDeviceCommand.Execute(null);
        var d = Assert.IsType<StopAllDialogVm>(s.Dialogs.Current);
        Assert.Equal(new[] { true, false, false }, d.Rows.Select(r => r.IsOn));
        Assert.Equal("已预选 1号机 · 可再勾选其它设备", d.PreText);
        Assert.Equal("全部停止 1 台", d.GoButton.Label);
        Assert.Equal(("1号机", "HT-PR8", "运行 6/8"), (d.Rows[0].Alias, d.Rows[0].Model, d.Rows[0].RunText));
        s.Dispose();
    }

    // ———————————————————— 溶液体积 ————————————————————

    [Fact]
    public void VolumeText_GasVolumeBreakdown()
    {
        var v = new VolumeInfo(10, 0.7, new double?[] { null, 10.3 }, new DateTime?[] { null, new DateTime(2026, 11, 10) });
        Assert.Equal("气相 5.7 mL = 釜 10 − 溶液 + 管路 0.7", VolumeText.GasLine(v, 1, 5));
        Assert.Equal("气相 5.3 mL = 有效容积 10.3 − 溶液", VolumeText.GasLine(v, 2, 5));
        Assert.Equal("用于计算吸气量", VolumeText.GasLine(v, 1, null));
        Assert.Equal("用于计算吸气量", VolumeText.GasLine(null, 1, 5));
        Assert.Equal(5.1, VolumeText.Step(5, +1, 10));
        Assert.Equal(4.9, VolumeText.Step(5, -1, 10));
        Assert.Equal(0, VolumeText.Step(0, -1, 10));
        Assert.Equal(10, VolumeText.Step(10, +1, 10));
        Assert.Equal(0.1, VolumeText.Step(null, +1, null));
    }

    [Fact]
    public void Inspector_Volume_StepsAndSends()
    {
        var s = Shell();
        s.OpenChannel(1, 1);
        Assert.Equal(("RV1", "反应中", "气相 5.7 mL = 有效容积 10.7 − 溶液", "5.0"), (s.Ctl.Title, s.Ctl.Word, s.Ctl.VolHint, s.Ctl.VolText));
        Assert.Equal("第 3/4 步 恒温反应 · 剩 27 min", s.Ctl.Sub2);
        Assert.Null(s.Ctl.Bar);                                   // 1号机远程控制中
        Assert.Equal("RV1 程序运行中", s.Ctl.VolReason);              // 程序运行中设备不让改（HMI CHANNEL_PROGRAM_OWNED）
        s.OpenChannel(1, 5);                                          // RV5 已就绪
        Assert.Null(s.Ctl.VolReason);
        s.Ctl.VolUpCommand.Execute(null);
        s.Fleet.RefreshNow();
        Assert.Equal("5.1", s.Ctl.VolText);
        Assert.Equal("气相 5.3 mL = 有效容积 10.4 − 溶液", s.Ctl.VolHint);   // RV5：有效容积 = 10 + DEADV 0.4（样稿）
        s.SelectDevice(3);
        s.OpenChannel(3, 1);
        Assert.Equal("本地只读 · 在工具条「申请控制」后才能操作", s.Ctl.Bar);
        Assert.NotNull(s.Ctl.VolReason);
        s.Dispose();
    }

    // ———————————————————— 胶囊 / 状态栏 / 折叠 ————————————————————

    [Fact]
    public void Capsule_Start_RunsTheSelectedChannelOrExplains()
    {
        var s = Shell();
        Assert.Null(s.Capsule.StartReason);                       // 1号机远程控制中、没急停
        s.SelectChannel(1, 5);                                    // RV5 已就绪
        s.RefreshChrome();
        Assert.Equal("启动所选通道的程序（RV5）", s.Capsule.StartTip);
        s.Capsule.StartCommand.Execute(null);
        Assert.True(StateKeys.IsRunning(s.Fleet.Device(1)!.Reactor(5)!.Own));
        s.SelectDevice(3);
        Assert.Equal("3号机为本地只读 · 先申请控制权", s.Capsule.StartReason);
        s.Dispose();
    }

    [Fact]
    public void StatusBar_SleepBlock_WhenAnythingRuns()
    {
        var s = Shell();
        Assert.Contains(s.Status.Extra, x => x.Text == "运行中 · 已阻止睡眠");
        Assert.Equal("1号机 · RV1 · 反应中", s.Status.SelText);
        s.Prefs.Set(s.Prefs.Current with { PreventSleep = false });   // 融合版 F8：设置页「运行中阻止电脑睡眠」（改了马上生效）
        Assert.DoesNotContain(s.Status.Extra, x => x.Text == "运行中 · 已阻止睡眠");
        s.Dispose();
    }

    [Fact]
    public void Panes_CollapseAndReset()
    {
        var s = Shell();
        var h = Home(s);
        h.ToggleCurveCommand.Execute(null);
        h.ToggleResCommand.Execute(null);
        h.ToggleMonCommand.Execute(null);
        Assert.Equal((PaneMode.Collapsed, PaneMode.Collapsed, PaneMode.Collapsed), (h.MonMode, h.CurveMode, h.ResMode));
        h.MonKey = "gas";
        Assert.False(h.IsRunView);
        s.ResetLayout();
        Assert.Equal((PaneMode.Docked, PaneMode.Docked, PaneMode.Docked, true), (h.MonMode, h.CurveMode, h.ResMode, h.IsRunView));
        s.Dispose();
    }
}
