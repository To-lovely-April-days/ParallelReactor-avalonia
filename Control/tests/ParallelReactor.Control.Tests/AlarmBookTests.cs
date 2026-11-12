using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.State;
using Xunit;
using static ParallelReactor.Control.Tests.PageVectorData;
using AlarmDto = ParallelReactor.Services.AlarmDto;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Alarms.AlarmBook / AlarmTexts / AlarmStrip：
/// 真机 —— Samples/live-alarms.json（PR_SIM 抓的 GET /alarms：info 混在里面、标题开头的「RVn」、模拟模式那条错误报警）+ WS alarm 的确认 / 解除 / 重启；
/// 演示 —— 与样稿逐字对照：MockVectors/DemoMockVectors.cs 每个场景每台设备的 counts / alarmText（样稿 JS 在 jsc 里算的），
/// MockVectors/PageMockVectors.cs 的 to-* / td-* / tt-*（样稿在 WebKit 里渲染的总览报警小块与提示、顶部报警条、设备页「当前报警」、页签上的标记）。
/// </summary>
public class AlarmBookTests
{
    private static AlarmsDto LiveAlarms() => ApiJson.Deserialize<AlarmsDto>(Samples.Read("live-alarms.json"))!;

    private static DateTime Utc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ———————————————————— 真机 ————————————————————

    [Fact]
    public void Live_Load_FiltersInfo_ParsesRv_NewestFirst()
    {
        var book = new AlarmBook("dev-1");
        Assert.True(book.Load(LiveAlarms(), "d8391e8a"));
        Assert.Equal("d8391e8a", book.BootId);
        // 12 条里 9 条 info（会话、标记、程序下发、程序启动）：剩 2 条提醒 + 1 条错误
        Assert.Equal(new[] { "12", "7", "2" }, book.Current.Select(a => a.Id).ToArray());
        var a12 = book.Current[0];
        Assert.Equal("d8391e8a/12", a12.Key);
        Assert.Equal(AlarmLevel.Warn, a12.Level);
        Assert.Equal(2, a12.Ch);                                   // rv「系统」、channel null：从标题「RV2 程序手动停止」取
        Assert.Equal("RV2 程序手动停止 · 加热已停，阀门全关", a12.Text);
        Assert.Equal(AlarmCodes.Program, a12.Code);
        Assert.False(a12.Acked);
        Assert.True(a12.Active);
        Assert.Equal("dev-1", a12.Device);
        Assert.Equal(Utc("2026-11-07T07:15:49.167385Z").ToLocalTime(), a12.At);
        Assert.Equal(DateTimeKind.Local, a12.At.Kind);
        var sim = book.Current[2];
        Assert.Equal(AlarmLevel.Err, sim.Level);
        Assert.Null(sim.Ch);
        Assert.Equal("整机", sim.Where);
        Assert.StartsWith("模拟模式 · PR_SIM=1", sim.Text, StringComparison.Ordinal);
        Assert.Equal(new AlarmTally(1, 2, 3), book.Counts(estopActive: false));
        Assert.Equal("报警 1 · 提醒 2", book.CountText(false));
        Assert.Equal(3, book.Unacked);
        Assert.True(book.HasUnackedErr);
        Assert.Equal(new[] { "2" }, book.UnackedErrIds());
        Assert.Same(book.Current, book.Current);
        Assert.False(book.Load(LiveAlarms(), "d8391e8a"));        // 同样的内容：没变
        Assert.Equal(1, book.Version);
    }

    [Fact]
    public void Live_WsAckClearAndRestart()
    {
        var book = new AlarmBook();
        book.Load(LiveAlarms(), "boot-1");
        var t = Utc("2026-11-07T07:20:00Z");
        // 新的超压（来源 RV3 → channel 3）
        Assert.True(book.Apply(new AlarmMsg("alarm", new AlarmDto("13", "err", "RV3", 3, "超压 · 联锁动作", "3.12 MPa ≥ 3.00 MPa · 已关闭 SV3", t, "active", null)), "boot-1"));
        var p = book.Current[0];
        Assert.Equal(("13", 3, AlarmCodes.PressureHigh, "超压 · 联锁动作 · 3.12 MPa ≥ 3.00 MPa · 已关闭 SV3"), (p.Id, p.Ch, p.Code, p.Text));
        Assert.Equal("报警：RV3 超压 · 联锁动作 · 3.12 MPa ≥ 3.00 MPa · 已关闭 SV3", p.TipPart);   // 文字不以 RV3 开头：补上
        // 本机确认成功：先在本地标上（带确认人），WS 的 ack 回来时确认人不丢
        Assert.True(book.MarkAcked("13", DemoFleet.Client));
        Assert.False(book.MarkAcked("13", DemoFleet.Client));
        Assert.True(book.Find("13")!.Acked);
        Assert.False(book.Apply(new AlarmDto("13", "err", "RV3", 3, "超压 · 联锁动作", "3.12 MPa ≥ 3.00 MPa · 已关闭 SV3", t, "ack", null), "boot-1"));   // 本地已标上：没变
        Assert.Equal(DemoFleet.Client, book.Find("13")!.AckBy);
        Assert.Equal("已确认（PC-测试工位1）", book.Find("13")!.Meta.Split(" · ")[2]);
        // 条件解除（ack → done，带 clearedAt）：不在当前报警里，留在全部里
        int v = book.Version;
        Assert.True(book.Apply(new AlarmDto("13", "err", "RV3", 3, "超压 · 联锁动作", "", t, "done", t.AddMinutes(2)), "boot-1"));
        Assert.Equal(v + 1, book.Version);
        Assert.DoesNotContain(book.Current, a => a.Id == "13");
        var done = book.All.Single(a => a.Id == "13");
        Assert.False(done.Active);
        Assert.Equal(t.AddMinutes(2).ToLocalTime(), done.ClearedAt);
        // info 级的推送忽略
        Assert.False(book.Apply(new AlarmDto("14", "info", "系统", null, "标记", "x", t, "done", null), "boot-1"));
        // 未确认就解除（cleared）：也不在当前报警里
        book.Apply(new AlarmDto("15", "warn", "RV1", 1, "仍超温 · 重新置报警", "", t, "active", null), "boot-1");
        Assert.Contains(book.Current, a => a.Id == "15");
        book.Apply(new AlarmDto("15", "warn", "RV1", 1, "仍超温 · 重新置报警", "", t, "cleared", t.AddSeconds(5)), "boot-1");
        Assert.DoesNotContain(book.Current, a => a.Id == "15");
        // HMI 重启（bootId 变了）：旧启动的报警全部作废，同号的新报警不会被当成旧的
        Assert.True(book.Apply(new AlarmDto("2", "warn", "RV2", 2, "RV2 升温较慢", "已用 18 min", t, "active", null), "boot-2"));
        Assert.Equal("boot-2", book.BootId);
        Assert.Single(book.All);
        Assert.Equal(("boot-2/2", AlarmCodes.HeatSlow, AlarmLevel.Warn), (book.Current[0].Key, book.Current[0].Code, book.Current[0].Level));
        Assert.True(book.Clear());
        Assert.Empty(book.All);
    }

    [Fact]
    public void Live_BootIdLearnedLater_NoDuplicates()
    {
        // 连上时 /device 还没回来（不知道启动号）就先取了 /alarms；之后 WS 带着启动号推来同一条：仍是一条
        var book = new AlarmBook();
        book.Load(LiveAlarms(), bootId: null);
        Assert.Equal("-/12", book.Current[0].Key);
        var acked = LiveAlarms().Items.First(x => x.Id == "12") with { State = "ack" };
        Assert.True(book.Apply(acked, "d8391e8a"));
        Assert.Equal(3, book.All.Count);                                            // 没有多出一条
        Assert.Equal("d8391e8a/12", book.All[0].Key);
        Assert.True(book.All[0].Acked);
        Assert.All(book.All, a => Assert.StartsWith("d8391e8a/", a.Key, StringComparison.Ordinal));
        // 「RV2 程序手动停止」没有条件（HMI 的 Alarm.Log，永远不会被解除）：确认了就退出当前报警
        Assert.Equal(new[] { "d8391e8a/7", "d8391e8a/2" }, book.Current.Select(a => a.Key).ToArray());
    }

    [Theory]
    // HMI 按条件键登记、条件消失时自己解除的（AlarmViewModel.Raise，MainViewModel.Logging.cs）
    [InlineData("超温 · 切断加热", "RV2", true)]
    [InlineData("超温切断后输出被重新打开 · 重新切断", "RV2", true)]
    [InlineData("仍超温 · 重新置报警", "RV2", true)]
    [InlineData("超温切断未确认", "RV2", true)]
    [InlineData("超温切断多次未确认 · 请使用急停", "RV2", true)]
    [InlineData("超压 · 联锁动作", "RV3", true)]
    [InlineData("超压联锁后阀门被重新打开 · 重新关闭", "RV3", true)]
    [InlineData("超压联锁后加热输出被重新打开 · 重新切断", "RV3", true)]
    [InlineData("仍超压 · 重新置报警", "RV3", true)]
    [InlineData("超压联锁 · 关阀未确认", "RV3", true)]
    [InlineData("超压联锁 · 停止加热未确认", "RV3", true)]
    [InlineData("超压联锁多次未确认 · 请使用急停", "RV3", true)]
    [InlineData("压力 8AI 离线", "通讯", true)]
    [InlineData("温控 AI-8 离线", "通讯", true)]
    // Alarm.Log 记的（HMI 永远不解除）
    [InlineData("超压已解除 · 联锁动作仍未确认", "RV3", false)]
    [InlineData("超温切断已确认", "RV2", false)]
    [InlineData("温控 AI-8 通讯恢复 · 报警解除", "通讯", false)]
    [InlineData("RV2 程序手动停止", "系统", false)]
    [InlineData("RV2 程序中止", "系统", false)]
    [InlineData("急停触发", "系统", false)]
    [InlineData("急停已复位", "系统", false)]
    [InlineData("模拟模式", "系统", false)]
    [InlineData("停止加热未确认", "RV4", false)]
    [InlineData("放热超出控温能力", "RV4", false)]
    [InlineData("SV4 关闭失败", "RV4", false)]
    [InlineData("阀门全关写入失败", "系统", false)]
    [InlineData("RV3 升温较慢", "RV3", false)]
    [InlineData("", null, false)]
    public void Codes_IsCondition_SameKeysAsHmiRaise(string title, string? origin, bool condition) =>
        Assert.Equal(condition, AlarmCodes.IsCondition(title, origin));

    [Fact]
    public void Live_AckedLogAlarm_LeavesCurrent_ConditionAlarmStaysUntilCleared()
    {
        var t = Utc("2026-11-07T07:20:00Z");
        var book = new AlarmBook();
        // 连上时 GET /alarms：HMI 的表里已有确认过的（state ack）—— 没有条件的那条不再列出，条件型的照样列出
        book.Load(new[]
        {
            new AlarmDto("1", "warn", "系统", null, "RV2 程序手动停止", "加热已停，阀门全关", t.AddMinutes(-9), "ack", null),
            new AlarmDto("2", "err", "RV3", 3, "超压 · 联锁动作", "3.12 MPa ≥ 3.00 MPa · 已关闭 SV3", t.AddMinutes(-8), "ack", null),
            new AlarmDto("3", "err", "系统", null, "模拟模式", "PR_SIM=1", t.AddMinutes(-7), "active", null),
            new AlarmDto("4", "err", "通讯", null, "压力 8AI 离线", "连续 3 次通讯失败", t.AddMinutes(-6), "active", null),
        }, "b");
        Assert.Equal(new[] { "4", "3", "2" }, book.Current.Select(a => a.Id).ToArray());
        Assert.Equal(new[] { "4", "3", "2", "1" }, book.All.Select(a => a.Id).ToArray());
        Assert.Equal((false, true, true, true), (book.Find("1")!.Condition, book.Find("2")!.Condition, book.Find("4")!.Condition, book.Find("2")!.Acked));
        Assert.Equal("报警 3", book.CountText(estopActive: false));
        Assert.Equal(2, book.Unacked);

        // 本机确认「模拟模式」（没有条件）：本地标上就退出当前报警，留在全部里（确认人保留）；HMI 的 ack 推来时不再变
        int v = book.Version;
        Assert.True(book.MarkAcked("3", "PC-1"));
        Assert.Equal(v + 1, book.Version);
        Assert.DoesNotContain(book.Current, a => a.Id == "3");
        Assert.Equal((true, true, "PC-1"), (book.Find("3")!.Acked, book.Find("3")!.Active, book.Find("3")!.AckBy));
        Assert.False(book.Apply(new AlarmDto("3", "err", "系统", null, "模拟模式", "PR_SIM=1", t.AddMinutes(-7), "ack", null), "b"));
        Assert.Equal("报警 2", book.CountText(false));

        // 确认「压力 8AI 离线」（条件型）：照样列出、照样计数（同样稿），直到 HMI 解除（ack → done）
        Assert.True(book.Apply(new AlarmDto("4", "err", "通讯", null, "压力 8AI 离线", "连续 3 次通讯失败", t.AddMinutes(-6), "ack", null), "b"));
        Assert.Equal(new[] { "4", "2" }, book.Current.Select(a => a.Id).ToArray());
        Assert.True(book.Current[0].Acked);
        Assert.Equal(0, book.Unacked);
        Assert.False(book.HasUnackedErr);
        Assert.Equal("报警 2", book.CountText(false));
        Assert.True(book.Apply(new AlarmDto("4", "err", "通讯", null, "压力 8AI 离线", "", t.AddMinutes(-6), "done", t), "b"));
        Assert.Equal(new[] { "2" }, book.Current.Select(a => a.Id).ToArray());

        // 新的提醒（没有条件）：未确认时列出、计数；WS 推来已确认 → 退出
        Assert.True(book.Apply(new AlarmDto("5", "warn", "RV3", 3, "超压已解除 · 联锁动作仍未确认", "", t, "active", null), "b"));
        Assert.Equal("报警 1 · 提醒 1", book.CountText(false));
        Assert.True(book.Apply(new AlarmDto("5", "warn", "RV3", 3, "超压已解除 · 联锁动作仍未确认", "", t, "ack", null), "b"));
        Assert.Equal(new[] { "2" }, book.Current.Select(a => a.Id).ToArray());
        Assert.Equal(5, book.All.Count);
        Assert.Equal("报警 1", book.CountText(false));
    }

    [Fact]
    public void Live_AckedEstopAlarm_ListedWhileEstopped_LeavesOnReset()
    {
        var t = Utc("2026-11-07T07:20:00Z");
        var book = new AlarmBook();
        Assert.False(book.SetEstop(false));                                         // 没变
        book.Load(new[] { new AlarmDto("1", "err", "系统", null, "急停触发", "急停快速路径 · 来源 触屏", t, "active", null) }, "b");
        Assert.False(book.SetEstop(true));                                          // 未确认：本来就在当前报警里，内容没变
        Assert.True(book.EstopActive);
        Assert.Single(book.Current);
        Assert.True(book.MarkAcked("1", "PC-1"));
        Assert.True(book.Current.Single().Acked);                                  // 急停中：已确认的急停报警照样列出（样稿：复位才清掉）
        Assert.Equal("", book.CountText(estopActive: true));                        // 计数照旧不数它
        Assert.Equal("报警 1", book.CountText(estopActive: true, all: true));        // 列出的卡片一一对应
        int v = book.Version;
        Assert.True(book.SetEstop(false));                                          // 复位：退出当前报警
        Assert.Equal(v + 1, book.Version);
        Assert.Empty(book.Current);
        Assert.Single(book.All);
        Assert.True(book.SetEstop(true));                                           // 再次急停（同一条 HMI 不会再推）：又列出
        Assert.Single(book.Current);

        // 演示的报警簿不看急停状态（照 dev.alarms）
        var demo = new AlarmBook();
        demo.Sync(new[] { new AlarmEntry("a1", "a1", AlarmLevel.Err, null, AlarmCodes.Estop, "急停已触发", t, Acked: true) });
        Assert.False(demo.SetEstop(true));
        Assert.False(demo.SetEstop(false));
        Assert.Single(demo.Current);
    }

    [Fact]
    public void Live_EstopAlarmNotCountedWhileEstopped()
    {
        var t = Utc("2026-11-07T07:20:00Z");
        var book = new AlarmBook();
        book.Load(new[]
        {
            new AlarmDto("1", "err", "系统", null, "急停触发", "急停快速路径 · 来源 触屏", t, "active", null),
            new AlarmDto("2", "warn", "RV3", 3, "RV3 升温较慢", "已用 18 min", t.AddSeconds(-30), "active", null),
            new AlarmDto("3", "warn", "系统", null, "急停已复位", "触屏复位", t.AddSeconds(-60), "active", null),
        }, "b");
        Assert.Equal(AlarmCodes.Estop, book.Find("1")!.Code);
        Assert.Equal(AlarmCodes.EstopReset, book.Find("3")!.Code);    // 复位提醒不是「急停中」那一条
        Assert.Equal("提醒 2", book.CountText(estopActive: true));
        Assert.Equal("报警 1 · 提醒 2", book.CountText(estopActive: false));
        Assert.Equal("报警 1 · 提醒 2", book.CountText(estopActive: true, all: true));
        Assert.Null(AlarmStrip.Build(new[] { new AlarmStripSource(1, "1号机", book.Current) }).First);   // 报警条不列急停
        var chip = AlarmTexts.Chip(book.Current, estopActive: true)!;
        Assert.Equal(("", "提醒 2", false), (chip.ErrText, chip.WarnText, chip.HasErr));
    }

    [Fact]
    public void Codes_And_Channel_Inference()
    {
        Assert.Equal(AlarmCodes.TempHigh, AlarmCodes.Infer("超温 · 切断加热", "RV2"));
        Assert.Equal(AlarmCodes.Comm, AlarmCodes.Infer("压力 离线", "通讯"));
        Assert.Equal(AlarmCodes.Comm, AlarmCodes.Infer("温控 离线"));
        Assert.Equal(AlarmCodes.PressureHigh, AlarmCodes.Infer("超压联锁 · 关阀未确认"));
        Assert.Equal(AlarmCodes.HeatSlow, AlarmCodes.Infer("RV3 升温较慢 · 已用 18 min"));
        Assert.Equal(AlarmCodes.Leak, AlarmCodes.Infer("泄漏测试不通过"));
        Assert.Equal(AlarmCodes.Other, AlarmCodes.Infer("模拟模式"));
        Assert.Equal(3, AlarmCodes.ChannelOf(null, "x", "RV3"));
        Assert.Equal(11, AlarmCodes.ChannelOf(null, "RV11 超压", "系统"));
        Assert.Equal(5, AlarmCodes.ChannelOf(5, "RV11 超压", "RV2"));
        Assert.Null(AlarmCodes.ChannelOf(null, "RV 超压", "系统"));
        Assert.Null(AlarmCodes.ChannelOf(0, "", null));
    }

    [Fact]
    public void Capacity_DropsClearedFirst_ThenAcked()
    {
        var t = Utc("2026-11-07T07:00:00Z");
        var book = new AlarmBook(capacity: 3);
        book.Load(new[]
        {
            new AlarmDto("1", "warn", "RV1", 1, "a", "", t, "active", null),
            new AlarmDto("2", "warn", "RV1", 1, "b", "", t.AddSeconds(1), "ack", null),
            new AlarmDto("3", "warn", "RV1", 1, "c", "", t.AddSeconds(2), "cleared", t.AddSeconds(3)),
            new AlarmDto("4", "err", "RV1", 1, "d", "", t.AddSeconds(4), "active", null),
        }, "b");
        Assert.Equal(new[] { "4", "2", "1" }, book.All.Select(a => a.Id).ToArray());   // 已解除的先丢
        book.Apply(new AlarmDto("5", "err", "RV1", 1, "e", "", t.AddSeconds(5), "active", null), "b");
        Assert.Equal(new[] { "5", "4", "1" }, book.All.Select(a => a.Id).ToArray());   // 再丢已确认的
    }

    // ———————————————————— 演示：与样稿 JS（jsc）逐台对照计数 ————————————————————

    public static IEnumerable<object[]> DemoScenarios() =>
        JsonDocument.Parse(DemoMockVectors.Json).RootElement.GetProperty("scenarios").EnumerateArray().Select(s => new object[] { s.GetProperty("name").GetString()! });

    [Theory]
    [MemberData(nameof(DemoScenarios))]
    public void Demo_CountsMatchMockJs(string name)
    {
        var sc = JsonDocument.Parse(DemoMockVectors.Json).RootElement.GetProperty("scenarios").EnumerateArray().First(s => s.GetProperty("name").GetString() == name);
        var lines = sc.GetProperty("lines").EnumerateArray().Select(x => x.GetString()!).ToArray();
        DemoDump.Replay(sc, out var fleet);
        // 跑完后的最后一段摘要：每台设备一行「 counts … tally=e/w/t all=e/w/t text=… allText=…」，按设备顺序
        var counts = lines.Where(l => l.StartsWith(" counts ", StringComparison.Ordinal)).TakeLast(fleet.Devices.Count).ToArray();
        Assert.Equal(fleet.Devices.Count, counts.Length);
        for (int i = 0; i < counts.Length; i++)
        {
            var d = fleet.Devices[i];
            var book = new AlarmBook(d.Id);
            book.Sync(d.Alarms);
            var m = Regex.Match(counts[i], @"^ counts (\d+) (\d+) (\d+) (\d+) (\d+) (\d+) tally=(\d+)/(\d+)/(\d+) all=(\d+)/(\d+)/(\d+) text=(.*) allText=(.*)$");
            Assert.True(m.Success, counts[i]);
            int G(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
            Assert.Equal(G(4), book.Count);
            Assert.Equal(G(5), book.Unacked);
            Assert.Equal(new AlarmTally(G(7), G(8), G(9)), book.Counts(d.Estop.Active));
            Assert.Equal(new AlarmTally(G(10), G(11), G(12)), book.Counts(d.Estop.Active, all: true));
            Assert.Equal(m.Groups[13].Value, book.CountText(d.Estop.Active));
            Assert.Equal(m.Groups[14].Value, book.CountText(d.Estop.Active, all: true, none: "无"));
            Assert.Equal(d.Alarms.Select(a => a.Id), book.Current.Select(a => a.Id));   // 顺序 = 样稿 dev.alarms
        }
    }

    [Fact]
    public void Demo_SyncIsInPlace()
    {
        var f = new DemoFleet(DemoParams.Parse("freeze=1"));
        var d = f.Device(3)!;
        var book = new AlarmBook();
        Assert.True(book.Sync(d.Alarms));
        int v = book.Version;
        var cur = book.Current;
        Assert.False(book.Sync(d.Alarms));                // 没变：版本不变、同一个列表
        Assert.Equal(v, book.Version);
        Assert.Same(cur, book.Current);
        f.Act(d, Core.Guard.DeviceAction.AlarmAck, new Core.Guard.ActionPayload(Id: d.Alarms[0].Id));
        Assert.True(book.Sync(d.Alarms));
        Assert.True(book.Current[0].Acked);
        Assert.Equal(DemoFleet.Client, book.Current[0].AckBy);
    }

    // ———————————————————— 演示：与样稿在 WebKit 里渲染的文字对照 ————————————————————

    public static IEnumerable<object[]> OverviewTexts() => Names("texts").Where(o => ((string)o[0]).StartsWith("to-", StringComparison.Ordinal));

    public static IEnumerable<object[]> DeviceTexts() => Names("texts").Where(o => !((string)o[0]).StartsWith("to-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(OverviewTexts))]
    public void Overview_ChipAndStrip_MatchMock(string name)
    {
        var v = Get("texts", name);
        var p = v.GetProperty("probe");
        var f = Fleet(v);
        var books = f.Devices.ToDictionary(d => d.K, d => { var b = new AlarmBook(d.Id); b.Sync(d.Alarms); return b; });
        foreach (var c in p.GetProperty("chips").EnumerateArray())
        {
            int k = int.Parse(c.GetProperty("k").GetString()!, CultureInfo.InvariantCulture);
            var d = f.Device(k)!;
            var chip = AlarmTexts.Chip(books[k].Current, d.Estop.Active, n => d.StateText(d.Channel(n)));
            var exp = c.GetProperty("chip");
            if (exp.ValueKind == JsonValueKind.Null) { Assert.Null(chip); continue; }
            Assert.NotNull(chip);
            Assert.Equal(exp.GetProperty("text").GetString(), chip!.Text);
            Assert.Equal(exp.GetProperty("tip").GetString(), chip.Tip);
            Assert.Equal(exp.GetProperty("err").GetBoolean(), chip.HasErr);
            Assert.Equal(exp.GetProperty("sel").GetString(), chip.TargetCh is int ch ? k + "/" + ch : k.ToString(CultureInfo.InvariantCulture));
        }
        var strip = AlarmStrip.Build(f.Devices.Select(d => new AlarmStripSource(d.K, d.Alias, books[d.K].Current)));
        var bar = p.GetProperty("bar");
        if (bar.ValueKind == JsonValueKind.Null) { Assert.False(strip.Any); return; }
        Assert.Equal(bar.GetProperty("who").GetString(), strip.Who);
        Assert.Equal(bar.GetProperty("text").GetString(), strip.Text);
        Assert.Equal(bar.GetProperty("title").GetString(), strip.Title);
        Assert.Equal(bar.GetProperty("time").GetString(), strip.Time);
        Assert.Equal(S(bar.GetProperty("more")) ?? "", strip.More);
        var (lk, lch) = strip.Locate!.Value;
        Assert.Equal(bar.GetProperty("loc").GetString(), lk + (lch is int n2 ? "/" + n2 : ""));
        Assert.Equal(bar.GetProperty("ack").GetString(), strip.First!.Alarm.Id);
        Assert.Equal(lk + ":" + strip.First.Alarm.Id, strip.OwnedKey);
    }

    [Theory]
    [MemberData(nameof(DeviceTexts))]
    public void Device_CurrentAlarms_MatchMock(string name)
    {
        var v = Get("texts", name);
        var p = v.GetProperty("probe");
        int k = int.Parse(Regex.Match(v.GetProperty("route").GetString()!, @"/device/(\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        var d = Fleet(v).Device(k)!;
        var book = new AlarmBook(d.Id);
        book.Sync(d.Alarms);
        var cur = book.Current;
        var head = p.GetProperty("alarmHead");
        Assert.Equal(head[0].GetString(), AlarmTexts.LogHead(cur));
        Assert.Equal(S(head[1]) ?? "", AlarmTexts.LogSub(cur));
        var list = AlarmTexts.LogOrder(cur);
        var cards = p.GetProperty("alarms").EnumerateArray().ToArray();
        Assert.Equal(cards.Length, list.Count);
        for (int i = 0; i < cards.Length; i++)
        {
            var a = list[i];
            var c = cards[i];
            Assert.Equal(c.GetProperty("key").GetString(), "dva-" + a.Id);
            Assert.Equal(c.GetProperty("cls").GetString(), "device-alarm is-" + (a.IsErr ? "err" : "warn") + (a.Acked ? " is-acked" : ""));
            Assert.Equal(Norm(c.GetProperty("b").GetString()), a.Text);
            Assert.Equal(c.GetProperty("bTitle").GetString(), a.Text);
            Assert.Equal(c.GetProperty("meta").GetString(), a.Meta);
            Assert.Equal(c.GetProperty("full").GetString(), a.MetaFull);
            Assert.Equal(c.GetProperty("ack").GetBoolean(), !a.Acked);
        }
        Assert.Equal(S(p.GetProperty("noAlarm")), cur.Count == 0 ? AlarmTexts.NoAlarm : null);
        string unack = AlarmTexts.UnackedText(cur);
        Assert.Equal(S(p.GetProperty("unackSub")), unack.Length > 0 ? unack : null);
        bool tabbed = p.GetProperty("tabs").GetArrayLength() > 0;
        Assert.Equal(p.GetProperty("unackDot").GetBoolean(), !tabbed && AlarmTexts.UnackedDot(cur));   // 页签面板里不画（页签上有红点）
    }

    [Fact]
    public void Texts_Meta_TipPart_TextAfterRv()
    {
        var at = new DateTime(2026, 11, 8, 10, 9, 42, DateTimeKind.Local);
        var a = new AlarmEntry("a2", "a2", AlarmLevel.Err, 11, AlarmCodes.PressureHigh, "RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", at, false);
        Assert.Equal("10:09:42 · 报警 · 未确认", a.Meta);
        Assert.Equal("10:09:42 · 报警 · 未确认 · RV11 · 代码 P-HIGH", a.MetaFull);
        Assert.Equal("超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", a.TextAfterRv);
        Assert.Equal("报警：RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", a.TipPart);
        Assert.Equal("报警", a.InspectorMeta);
        var w = a with { Level = AlarmLevel.Warn, Ch = null, Acked = true, AckBy = "PC-2" };
        Assert.Equal("10:09:42 · 提醒 · 已确认（PC-2）", w.Meta);
        Assert.Equal("10:09:42 · 提醒 · 已确认（PC-2） · 整机 · 代码 P-HIGH", w.MetaFull);
        Assert.Equal("提醒 · 已确认", w.InspectorMeta);
        Assert.Equal("提醒：RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11（已确认）", w.TipPart);
        Assert.Equal(w.Text, w.TextAfterRv);
        Assert.Equal("报警 1 · 提醒 1", AlarmTexts.InspectorSub(new[] { a, w }));
        Assert.Equal("无", AlarmTexts.InspectorSub(Array.Empty<AlarmEntry>()));
        Assert.Equal("当前报警", AlarmTexts.LogHead(Array.Empty<AlarmEntry>()));
        Assert.Equal("", AlarmTexts.LogSub(Array.Empty<AlarmEntry>()));
        Assert.Equal("10:09:42", Fmt.Time(at));
    }
}
