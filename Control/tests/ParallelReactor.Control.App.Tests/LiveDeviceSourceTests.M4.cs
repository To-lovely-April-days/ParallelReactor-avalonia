using System;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.Services;
using Xunit;
using AlarmDto = ParallelReactor.Services.AlarmDto;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M4 的实时数据（PORT-PLAN §5 M4「数据」）：报警簿 / 事件簿（Core 的 AlarmBook / EventBook）由 GET /alarms、GET /events 与 WS alarm / event 喂；
/// 确认成功后本地先标上确认人；在线时每 5 s GET /device/comm（设备信息面板的模块通讯），/settings 连上时取一次、之后每 60 s 一次；断线时不轮询；
/// 释放后不再轮询。两台进程内的假 HMI（Fakes/ScriptedHmi）经真 ReactorConnection 连接，时间用假时钟（FakeClock）推进。
/// </summary>
public partial class LiveDeviceSourceTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc);

    private static EventRecord Ev(long seq, string level, string kind, int? ch, string title, string message = "", int epoch = 1) =>
        new(seq, epoch, T0.AddSeconds(seq), "", level, kind, ch, "system", ch is int n ? "RV" + n : "系统", null, null, title, message);

    [Fact]
    public async Task Connect_LoadsAlarmAndEventBooks_ThenFollowsWs_AndAckMarksLocally()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.Alarms.Add(new AlarmDto("1", "warn", "RV3", null, "RV3 升温较慢", "已用 18 min", T0.AddMinutes(-2), "active", null));
        a.Alarms.Add(new AlarmDto("2", "err", "RV1", 1, "RV1 超压", "3.12 MPa ≥ 3.00 MPa · 已关闭 SV1", T0.AddMinutes(-1), "active", null));
        a.Alarms.Add(new AlarmDto("3", "info", "系统", null, "测试会话开始", "", T0, "done", null));
        a.Events.Add(Ev(8, "info", "program.step", 2, "RV2 进入第 3 步 反应"));
        a.Events.Add(Ev(9, "info", "op.program.run", 3, "RV3 程序「加氢-标准」开始"));
        a.Events.Add(Ev(10, "warn", "alarm.raised", 3, "RV3 升温较慢", "已用 18 min"));
        a.Http.On("POST", "/alarms/2/ack", call => ScriptedHttpHandler.Command(call, false, new { ok = true }));
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        await r.Until(() => r.S(1).AlarmBook.Count == 2 && r.S(1).EventBook.Items.Count == 3, "连上后取了 /alarms、/events");

        // 报警簿：info 过滤；身份 = bootId/id；新的在前；代码 / 反应釜号推断；DeviceVm.AlarmList 就是簿的 Current
        var cur = await r.On(() => r.S(1).AlarmBook.Current.ToArray());
        Assert.Equal(new[] { "boot-1/2", "boot-1/1" }, cur.Select(x => x.Key));
        Assert.Equal(("P-HIGH", 1, false), (cur[0].Code, cur[0].Ch!.Value, cur[0].Acked));
        Assert.Equal(("HEAT-SLOW", 3), (cur[1].Code, cur[1].Ch!.Value));
        Assert.Same(await r.On(() => r.S(1).AlarmBook.Current), await r.On(() => r.D(1).AlarmList));
        Assert.Equal(new[] { "2", "1" }, (await r.On(() => r.S(1).Alarms)).Select(x => x.Id));        // 外框 / 检查器用的投影，同样的顺序

        // 事件簿：最新在前；op. 开头算「操作」；反应釜号；筛选计数
        var ev = await r.On(() => r.S(1).EventBook.Items.ToArray());
        Assert.Equal(new[] { "1:10", "1:9", "1:8" }, ev.Select(x => x.Key));
        Assert.Equal((EventLevel.Warn, EventLevel.Op, EventLevel.Info), (ev[0].Level, ev[1].Level, ev[2].Level));
        Assert.Equal(new Core.Events.EventCounts(3, 1, 1), await r.On(() => r.S(1).EventBook.Counts));

        // WS：确认状态、新报警、新事件（重复的不重复记）
        a.PushAlarm(new AlarmDto("1", "warn", "RV3", null, "RV3 升温较慢", "已用 18 min", T0.AddMinutes(-2), "ack", null));
        await r.Until(() => r.S(1).AlarmBook.Find("1")?.Acked == true, "WS ack");
        // 没有条件的提醒（HMI 永远不解除）：确认了就退出当前报警（AlarmBook.Current），留在全部里
        Assert.Equal(new[] { "2" }, await r.On(() => r.S(1).AlarmBook.Current.Select(x => x.Id).ToArray()));
        Assert.Equal(2, await r.On(() => r.S(1).AlarmBook.All.Count));
        a.PushAlarm(new AlarmDto("4", "err", "RV4", 4, "RV4 超温", "", T0, "active", null));
        await r.Until(() => r.S(1).AlarmBook.Count == 2, "WS 新报警");
        a.PushEvent(Ev(11, "info", "op.alarm.ack", 3, "RV3 报警已确认"));
        a.PushEvent(Ev(11, "info", "op.alarm.ack", 3, "RV3 报警已确认"));
        await r.Until(() => r.S(1).EventBook.Items.Count == 4, "WS 新事件");
        await Task.Delay(50);
        Assert.Equal(4, await r.On(() => r.S(1).EventBook.Items.Count));
        Assert.Equal("1:11", await r.On(() => r.S(1).EventBook.Items[0].Key));

        // 确认：POST /alarms/2/ack 成功后本地先标上（确认人 = 本机客户端名），不等 WS 推来
        var o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: "2")));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · RV1 超压报警已确认", o.Message);
        var acked = await r.On(() => r.S(1).AlarmBook.Find("2"));
        Assert.Equal((true, Me), (acked!.Acked, acked.AckBy));
        Assert.Equal("已确认（" + Me + "）", acked.Meta[(acked.Meta.LastIndexOf(" · ", StringComparison.Ordinal) + 3)..]);
        // HMI 的 ack 推来时确认人保留本地这一份（HMI 没有确认人字段，A6）
        a.PushAlarm(new AlarmDto("2", "err", "RV1", 1, "RV1 超压", "3.12 MPa ≥ 3.00 MPa · 已关闭 SV1", T0.AddMinutes(-1), "ack", null));
        await Task.Delay(50);
        Assert.Equal(Me, (await r.On(() => r.S(1).AlarmBook.Find("2")))!.AckBy);
        // 「RV1 超压」是条件型（HMI 在条件消失时自己解除）：确认了照样列出
        Assert.Equal(new[] { "4", "2" }, await r.On(() => r.S(1).AlarmBook.Current.Select(x => x.Id).ToArray()));
        // 解除：不在当前报警里了
        a.PushAlarm(new AlarmDto("4", "err", "RV4", 4, "RV4 超温", "", T0, "done", T0.AddMinutes(1)));
        await r.Until(() => r.S(1).AlarmBook.Find("4") is { Active: false } && r.S(1).AlarmBook.Count == 1, "WS 解除");
        Assert.Equal(0, r.OffUi);
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task LogStyleAlarm_LeavesCurrentWhenAcked_ConditionAlarmStaysUntilCleared()
    {
        // HMI 用 Alarm.Log 记的报警（「RV2 程序手动停止」「急停触发」「模拟模式」…，HMI 里大多数报警）没有条件键，HMI 永远不会把它置为 cleared：确认后状态停在 ack。
        // 规则（M4 复核，说明 §8 第 23 条）：这类报警确认了就算处理完 —— 退出「当前报警」和「报警 / 提醒」计数（与触屏的「未处理」一致），
        // 留在报警簿的全部（与事件）里；条件型（超温 / 超压 / 模块离线：HMI 在条件消失时自己解除）同样稿，确认了照样列出、照样计数，直到 HMI 解除。
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        var stop = new AlarmDto("5", "warn", "系统", null, "RV2 程序手动停止", "加热已停，阀门全关", T0, "active", null);
        var op = new AlarmDto("6", "err", "RV3", 3, "超压 · 联锁动作", "3.12 MPa ≥ 3.00 MPa · 已关闭 SV3", T0.AddSeconds(1), "active", null);
        a.Alarms.Add(stop);
        a.Alarms.Add(op);
        a.Http.On("POST", "/alarms/5/ack", call => ScriptedHttpHandler.Command(call, false, new { ok = true }));
        a.Http.On("POST", "/alarms/6/ack", call => ScriptedHttpHandler.Command(call, false, new { ok = true }));
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        await r.Until(() => r.S(1).AlarmBook.Count == 2, "连上后取了 /alarms");
        Assert.Equal("报警 1 · 提醒 1", await r.On(() => Core.Alarms.AlarmTexts.CountText(r.D(1).AlarmList, estopActive: false)));

        // 确认没有条件的那条：本地标上就退出当前报警；HMI 的 ack 推来、之后很久没有 cleared / done，也不再回来
        var o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: "5")));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("报警 1", await r.On(() => Core.Alarms.AlarmTexts.CountText(r.D(1).AlarmList, estopActive: false)));
        a.PushAlarm(stop with { State = "ack" });
        await Task.Delay(50);
        await r.Advance(120, stepSec: 5);
        Assert.Equal(new[] { "6" }, await r.On(() => r.S(1).AlarmBook.Current.Select(x => x.Id).ToArray()));
        var handled = await r.On(() => r.S(1).AlarmBook.All.Single(x => x.Id == "5"));
        Assert.Equal((true, true, Me), (handled.Acked, handled.Active, handled.AckBy));
        Assert.DoesNotContain(await r.On(() => r.S(1).Alarms), x => x.Id == "5");          // 外框 / 检查器的投影也没有它

        // 确认条件型的那条：照样列出、照样计数，直到 HMI 解除（ack → done）
        o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: "6")));
        Assert.True(o.Ok, o.Message);
        a.PushAlarm(op with { State = "ack" });
        await Task.Delay(50);
        await r.Advance(30, stepSec: 5);
        var still = await r.On(() => r.S(1).AlarmBook.Current.Single());
        Assert.Equal(("6", true, Me), (still.Id, still.Acked, still.AckBy));
        Assert.Equal("报警 1", await r.On(() => Core.Alarms.AlarmTexts.CountText(r.D(1).AlarmList, estopActive: false)));
        a.PushAlarm(op with { State = "done", ClearedAt = T0.AddMinutes(3) });
        await r.Until(() => r.S(1).AlarmBook.Count == 0, "HMI 解除");
        Assert.Equal("", await r.On(() => Core.Alarms.AlarmTexts.CountText(r.D(1).AlarmList, estopActive: false)));

        // HMI 重启：新的启动号，报警表是空的 → 簿清空（全部里也没有了）
        a.Alarms.Clear();
        a.Restart("boot-2");
        await r.AdvanceUntil(() => OnlineState(r.D(1)) && r.S(1).Info?.BootId == "boot-2", "重启后重连");
        await r.Until(() => r.S(1).AlarmBook.All.Count == 0, "旧启动的报警作废");
        Assert.Empty(r.Ui.Errors);
    }

    [Theory]
    [InlineData("1.0", "v1.0")]          // HMI 的 ApiVersion 常量（Contracts ApiDtos）：写法同样稿「接口 v1.2」
    [InlineData("v1.2", "v1.2")]
    [InlineData(" 2.1 ", "v2.1")]
    [InlineData("beta", "beta")]
    [InlineData("", "—")]
    [InlineData(null, "—")]
    public void ApiVersion_WrittenLikeTheMock(string? hmi, string shown) => Assert.Equal(shown, LiveDeviceSource.ApiVersionText(hmi));

    [Fact]
    public async Task VersionRow_Live_ReadsLikeTheMock()
    {
        await using var r = new LiveRig();
        r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)) && r.S(1).Info != null, "上线");
        Assert.Equal(("1.0.0", "v1.0"), await r.On(() => (r.S(1).HmiVersion, r.S(1).ApiVersion)));   // live-device.json：「1.0.0+提交号」「1.0」
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task EstopAlarm_AckedStaysListedWhileEstopped_LeavesWhenFramesSayReset()
    {
        // 急停那一条（HMI 的 Alarm.Log「急停触发」，永远不解除）：急停中确认了照样列出（同样稿：急停那条要到复位才清掉；计数照旧不数它），
        // 遥测帧说急停已复位后退出当前报警。
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        var es = new AlarmDto("9", "err", "系统", null, "急停触发", "急停快速路径 · 来源 触屏", T0, "active", null);
        a.Alarms.Add(es);
        a.Http.On("POST", "/alarms/9/ack", call => ScriptedHttpHandler.Command(call, false, new { ok = true }));
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        var f = a.Frame();
        a.Open!.Push(ApiJson.Serialize(f with { Seq = 900, Estop = f.Estop with { Active = true, Since = T0 } }));
        await r.Until(() => r.S(1).Estop.Active && r.S(1).AlarmBook.EstopActive && r.S(1).AlarmBook.Count == 1, "急停中");
        Assert.Equal("", await r.On(() => Core.Alarms.AlarmTexts.CountText(r.D(1).AlarmList, estopActive: true)));

        var o = await r.Ui.InvokeAsync(() => r.D(1).Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: "9")));
        Assert.True(o.Ok, o.Message);
        a.PushAlarm(es with { State = "ack" });
        await Task.Delay(50);
        var listed = await r.On(() => r.S(1).AlarmBook.Current.Single());
        Assert.Equal(("9", true), (listed.Id, listed.Acked));
        Assert.Equal("报警 1", await r.On(() => Core.Alarms.AlarmTexts.InspectorSub(r.D(1).AlarmList)));   // 列出的卡片一一对应

        // 触屏复位：下一帧 Estop.Active = false → 退出当前报警（簿的全部里还在）
        a.Open!.Push(ApiJson.Serialize(f with { Seq = 901, Estop = f.Estop with { Active = false } }));
        await r.Until(() => !r.S(1).Estop.Active && r.S(1).AlarmBook.Count == 0, "复位");
        Assert.Single(await r.On(() => r.S(1).AlarmBook.All.ToArray()));
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task Polls_DeviceComm_Every5s_AndSettings_Every60s_OnlyWhileOnline_StopsOnDispose()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)), "上线");
        // 连上时：/settings 一次、/device/comm 一次（模块通讯面板马上有「最近成功」）
        await r.Until(() => a.Count("GET", "/settings") == 1 && a.Count("GET", "/device/comm") == 1 && r.S(1).Comm != null, "连上后的读取");
        var mods = await r.On(() => r.S(1).Modules);
        Assert.Equal(new[] { "temp", "press", "stir", "valve" }, mods.Select(m => m.Key));
        Assert.All(mods, m => Assert.True(m.Online && m.LastOk != null));     // 在线取最新一帧的 CommSnap，最近成功取 /device/comm
        Assert.Equal("温控 AI-8", mods[0].Name);

        // 在线：每 5 s 一次 /device/comm
        await r.AdvanceUntil(() => a.Count("GET", "/device/comm") == 2, "第 5 s", stepSec: 0.5, maxSteps: 20);
        await r.AdvanceUntil(() => a.Count("GET", "/device/comm") == 3, "第 10 s", stepSec: 0.5, maxSteps: 20);
        Assert.Equal(1, a.Count("GET", "/settings"));
        // 60 s：/settings 再取一次（触屏上改了量程，型号表跟着变）
        await r.AdvanceUntil(() => a.Count("GET", "/settings") == 2, "第 60 s", stepSec: 1, maxSteps: 80);
        int comm = a.Count("GET", "/device/comm");
        Assert.InRange(comm, 12, 14);                                           // 60 s / 5 s

        // 断线（WS 被拒，REST 还通）：不轮询
        a.WsFailStatus = 503;
        a.DropWs();
        await r.AdvanceUntil(() => r.S(1).Conn.State == ConnStatus.Offline, "断线", stepSec: 0.2, maxSteps: 50);
        int atDrop = a.Count("GET", "/device/comm");
        await r.Advance(20, stepSec: 1);
        Assert.Equal(atDrop, a.Count("GET", "/device/comm"));

        // 恢复：接着轮询
        a.WsFailStatus = null;
        await r.AdvanceUntil(() => OnlineState(r.D(1)), "恢复", stepSec: 0.5, maxSteps: 60);
        await r.AdvanceUntil(() => a.Count("GET", "/device/comm") > atDrop, "恢复后接着轮询", stepSec: 0.5, maxSteps: 30);

        // 释放：不再轮询
        await r.Ui.InvokeAsync(async () => await r.Fleet.DisposeAsync());
        int afterDispose = a.Count("GET", "/device/comm");
        await r.Advance(15, stepSec: 1);
        Assert.Equal(afterDispose, a.Count("GET", "/device/comm"));
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task LeaseTreeShort_OtherClient_WithoutHolder_OrphanKeepsItsOwnBadge()
    {
        // 资源管理器设备头第二行的徽标最窄（M3 复核：「其他客户端 PC-工位2」在 300 px 里只剩「其他客户端…」）：第 5 种控制权只写「其他客户端」，
        // 是谁写在悬停提示里；本机旧租约照样写「本机旧租约」（那是要用户处理的另一种情况）
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.ControlMode = "remote";
        a.Holder = "PC-工位2";
        var b = r.Hmi("192.168.1.21", "PR8-B");
        b.ControlMode = "remote";
        b.Holder = Me;
        await r.StartAsync(("192.168.1.20", "1号机"), ("192.168.1.21", "2号机"));
        await r.Until(() => OnlineState(r.D(1)) && OnlineState(r.D(2)), "上线");
        Assert.Equal(("其他客户端 PC-工位2", "其他客户端", "远程控制中（其他客户端 PC-工位2）"),
            await r.On(() => (r.D(1).LeaseShort, r.D(1).LeaseTreeShort, r.D(1).LeaseText)));
        Assert.Equal(("本机旧租约", "本机旧租约"), await r.On(() => (r.D(2).LeaseShort, r.D(2).LeaseTreeShort)));
        // 别人释放了：两处一样
        a.PushControl("released", "local", null, mode: "local");
        await r.Until(() => r.S(1).Lease.State == LeaseStatus.Local, "别人释放");
        Assert.Equal(("本地只读", "本地只读"), await r.On(() => { r.Fleet.RefreshNow(); return (r.D(1).LeaseShort, r.D(1).LeaseTreeShort); }));
        Assert.Empty(r.Ui.Errors);
    }

    [Fact]
    public async Task Modules_ShowOfflineModule_FromCommDto_WhenNoFrameYet_AndFrameWinsAfterwards()
    {
        await using var r = new LiveRig();
        var a = r.Hmi("192.168.1.20", "PR8-A");
        a.CommOnline = false;
        await r.StartAsync(("192.168.1.20", "1号机"));
        await r.Until(() => OnlineState(r.D(1)) && r.S(1).Comm != null, "上线并取到 /device/comm");
        var src = r.S(1);
        // 帧里的 CommSnap（live-snapshot.json：全部在线）比 /device/comm 新：在线取帧
        Assert.True(await r.On(() => src.Modules.All(m => m.Online)));
        Assert.False(await r.On(() => src.Comm!.Temp!.Online));
        // 最近成功：/device/comm 的 lastOkAt（本地时间）
        var last = await r.On(() => src.Modules[0].LastOk);
        Assert.Equal(DateTimeKind.Local, last!.Value.Kind);
        Assert.Empty(r.Ui.Errors);
    }
}
