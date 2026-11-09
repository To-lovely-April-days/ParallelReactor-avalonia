using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Guard：操作守卫（样稿 sim.js guard / why / ACTIONS，原因文字逐字相同，API.md §3.3）。事实（GuardFacts）在这里手工构造 ——
/// 真机的 App 也是这样填的；演示数据的事实另由 DemoFleetTests 的样稿向量逐条核对（每个场景每台设备每个操作）。
/// 重点：停止类永远不因控制权禁用；离线只剩 none 类；急停、未安装、已停用、程序运行中、超量程、共用 / 独立搅拌用错型号。
/// </summary>
public class GuardTests
{
    private static readonly ModelProfile Pr8 = ModelProfiles.Embedded.Get("PR8")!;
    private static readonly ModelProfile Pr84 = ModelProfiles.Embedded.Get("PR8-4")!;
    private static readonly ModelProfile Pr4 = ModelProfiles.Embedded.Get("PR4")!;

    /// <summary>1号机 · HT-PR8 · 现场：在线、远程控制中；RV1 反应中（程序运行中）、RV2 已启用、RV3 停用、RV4 已结束（程序已结束），RV5–8 未安装。</summary>
    private static GuardFacts F(LeaseStatus lease = LeaseStatus.Remote, ConnStatus conn = ConnStatus.Online, bool estop = false, ModelProfile? model = null,
        bool everOnline = true, bool stirOn = true, bool session = false, string? holder = null)
    {
        var m = model ?? Pr84;
        var chans = m.Channels.Select(c => c.N switch
        {
            1 => new ChannelFacts(1, c.Installed, StateKey.React, Heating: true, Program: ProgramStatus.Running, Sp: 120),
            2 => new ChannelFacts(2, c.Installed, StateKey.Ready),
            3 => new ChannelFacts(3, c.Installed, StateKey.Idle),
            4 => new ChannelFacts(4, c.Installed, StateKey.Done, Program: ProgramStatus.Done),
            _ => new ChannelFacts(c.N, c.Installed),
        }).ToArray();
        return new GuardFacts
        {
            Alias = "1号机", Conn = conn, EverOnline = everOnline, RetryS = 12, Lease = lease, LeaseRemainS = 45, LeaseGraceS = 18, LeaseHolder = holder,
            EstopActive = estop, Model = m, StirOn = stirOn, Channels = chans, ProgramIds = new[] { "h2-std", "purge-3" },
            SessionActive = session, SessionId = session ? "PT-20261108-0930" : null,
            Alarms = new[] { new AlarmFacts("a1", false), new AlarmFacts("a2", true) },
            Peers = new[] { new PeerDevice("2号机", "192.168.1.24") },
        };
    }

    private static readonly DeviceAction[] StopClass =
        { DeviceAction.Disable, DeviceAction.Stop, DeviceAction.HeatStop, DeviceAction.ProgAbort, DeviceAction.StopAll, DeviceAction.Estop, DeviceAction.StirOff, DeviceAction.SessionStart };

    // ———————————————————— 停止类永远不因控制权禁用 ————————————————————

    [Theory]
    [InlineData(LeaseStatus.Local)]
    [InlineData(LeaseStatus.Requested)]
    [InlineData(LeaseStatus.Lost)]
    [InlineData(LeaseStatus.Other)]
    [InlineData(LeaseStatus.Remote)]
    public void StopClass_NeverDisabledByLease(LeaseStatus lease)
    {
        var f = F(lease, holder: "PC-2");
        foreach (var a in StopClass)
            Assert.Equal("", ActionGuard.Why(f, a, ActionPayload.Ch(1)));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.AlarmAck, new ActionPayload(Id: "a1")));
    }

    [Theory]
    [InlineData(DeviceAction.Stop, 2)]       // 已启用、没在运行：照样能发停止
    [InlineData(DeviceAction.Stop, 3)]       // 停用
    [InlineData(DeviceAction.HeatStop, 3)]
    [InlineData(DeviceAction.ProgAbort, 2)]  // 没有程序
    [InlineData(DeviceAction.Disable, 3)]    // 本来就停用
    public void StopClass_NeverDisabledBecauseNothingRuns(DeviceAction a, int n) =>
        Assert.Equal("", ActionGuard.Why(F(LeaseStatus.Local), a, ActionPayload.Ch(n)));

    [Fact]
    public void StopClass_StillSentDuringEstop() => Assert.Equal("", ActionGuard.Why(F(estop: true), DeviceAction.Stop, ActionPayload.Ch(1)));

    [Fact]
    public void KindTable_MatchesMock()
    {
        var expected = new Dictionary<string, string>
        {
            ["enable"] = "control", ["disable"] = "safe", ["start"] = "control", ["stop"] = "safe", ["heatStart"] = "control", ["heatStop"] = "safe", ["setSp"] = "control",
            ["progLoad"] = "control", ["progRun"] = "control", ["progAbort"] = "safe", ["stirOn"] = "control", ["stirOff"] = "safe", ["stirSet"] = "control",
            ["chStirOn"] = "control", ["chStirOff"] = "safe", ["chStirSet"] = "control", ["runAll"] = "control", ["stopAll"] = "safe", ["estop"] = "safe",
            ["sessionStart"] = "safe", ["sessionMark"] = "safe", ["sessionEnd"] = "safe", ["alarmAck"] = "safe", ["leaseRequest"] = "conn", ["leaseCancel"] = "none",
            ["leaseRelease"] = "none", ["disconnect"] = "none", ["reconnect"] = "none", ["rename"] = "none", ["remove"] = "none",
        };
        Assert.Equal(expected.Keys, DeviceActions.All.Select(s => s.Code));
        foreach (var s in DeviceActions.All)
        {
            Assert.Equal(expected[s.Code], DeviceActions.KindCode(ActionGuard.KindOf(s.Action)));
            Assert.Equal(s.Kind == GuardKind.Control, ActionGuard.NeedsLease(s.Action));
            Assert.True(DeviceActions.TryParse(s.Code, out var back) && back == s.Action);
        }
        Assert.Equal("启动加热", DeviceActions.Label(DeviceAction.HeatStart));
        Assert.True(DeviceActions.IsStopClass(DeviceAction.AlarmAck));
    }

    // ———————————————————— 离线只剩 none 类 ————————————————————

    [Theory]
    [InlineData(ConnStatus.Offline, true, "1号机连接中断 · 重连中 12 s")]
    [InlineData(ConnStatus.Offline, false, "1号机连接失败 · 12 s 后重试")]
    [InlineData(ConnStatus.Connecting, true, "1号机正在重连 · 稍候")]
    [InlineData(ConnStatus.Connecting, false, "1号机正在连接 · 稍候")]
    [InlineData(ConnStatus.Disconnected, true, "1号机已断开 · 先连接")]
    public void Offline_OnlyNoneClassLeft(ConnStatus conn, bool everOnline, string reason)
    {
        var f = F(conn: conn, everOnline: everOnline);
        foreach (var s in DeviceActions.All)
        {
            string why = ActionGuard.Why(f, s.Action, new ActionPayload(N: 1, Sp: 100, Rpm: 600, Id: "a1", Alias: "新名字"));
            if (s.Kind == GuardKind.None) Assert.DoesNotContain(reason, why);
            else Assert.Equal(reason, why);
        }
        Assert.Equal(reason, ActionGuard.GuardText(f, GuardKind.Safe));
        Assert.Equal("", ActionGuard.GuardText(f, GuardKind.None));
    }

    /// <summary>
    /// 真机：只是 WebSocket 断了在重连（REST 客户端还在、连上过，StopReachable）—— 停止类（急停、全部停止、停止、确认报警…）照样可用（尽力发送），
    /// 控制类和申请控制权照样是「正在重连 · 稍候」。旧界面也是只要有连接对象就能急停。
    /// </summary>
    [Fact]
    public void WsReconnecting_StopReachable_KeepsStopClassOnly()
    {
        var f = F(conn: ConnStatus.Connecting) with { StopReachable = true };
        foreach (var s in DeviceActions.All)
        {
            string why = ActionGuard.Why(f, s.Action, new ActionPayload(N: 1, Sp: 100, Rpm: 600, Id: "a1", Alias: "新名字"));
            if (s.Kind == GuardKind.Safe) Assert.DoesNotContain("正在重连", why);
            else if (s.Kind != GuardKind.None) Assert.Equal("1号机正在重连 · 稍候", why);
        }
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.Estop));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.StopAll));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.Stop, ActionPayload.Ch(1)));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.AlarmAck, new ActionPayload(Id: "a1")));
        Assert.Equal("RV6 未安装", ActionGuard.Why(f, DeviceAction.Stop, ActionPayload.Ch(6)));   // 纯逻辑检查照旧
        Assert.Equal("", ActionGuard.GuardText(f, GuardKind.Safe));
        Assert.Equal("1号机正在重连 · 稍候", ActionGuard.GuardText(f, GuardKind.Control));
        Assert.Equal("1号机正在重连 · 稍候", ActionGuard.Why(f, DeviceAction.LeaseRequest));
        // 没有 StopReachable（演示数据 / 样稿、真机已断开）：照旧只剩 none 类
        Assert.Equal("1号机正在重连 · 稍候", ActionGuard.Why(F(conn: ConnStatus.Connecting), DeviceAction.Estop));
        Assert.Equal("1号机已断开 · 先连接", ActionGuard.Why(F(conn: ConnStatus.Disconnected), DeviceAction.Estop));
    }

    [Fact]
    public void Offline_NoneClassChecksStillApply()
    {
        Assert.Equal("", ActionGuard.Why(F(conn: ConnStatus.Offline), DeviceAction.Reconnect));
        Assert.Equal("正在重连", ActionGuard.Why(F(conn: ConnStatus.Connecting), DeviceAction.Reconnect));
        Assert.Equal("已断开", ActionGuard.Why(F(conn: ConnStatus.Disconnected), DeviceAction.Disconnect));
        Assert.Equal("已连接", ActionGuard.Why(F(), DeviceAction.Reconnect));
        Assert.Equal("", ActionGuard.Why(F(LeaseStatus.Lost, ConnStatus.Offline), DeviceAction.LeaseRelease));   // 断线时也能释放
    }

    // ———————————————————— 控制权 / 急停 ————————————————————

    [Theory]
    [InlineData(LeaseStatus.Local, "1号机为本地只读 · 先申请控制权")]
    [InlineData(LeaseStatus.Requested, "1号机等待触屏确认 · 剩 45 s")]
    [InlineData(LeaseStatus.Lost, "1号机控制权心跳中断 · 宽限 18 s")]
    [InlineData(LeaseStatus.Other, "1号机远程控制中（其他客户端 PC-2）")]
    public void ControlClass_NeedsOwnLease(LeaseStatus lease, string reason)
    {
        var f = F(lease, holder: "PC-2");
        foreach (var s in DeviceActions.All.Where(s => s.Kind == GuardKind.Control))
            Assert.Equal(reason, ActionGuard.Why(f, s.Action, new ActionPayload(N: 2, Sp: 100, Rpm: 600)));
        Assert.Equal(reason, ActionGuard.GuardText(f, GuardKind.Control));
    }

    [Fact]
    public void Estop_BlocksControlClass_ButNotStopClass()
    {
        var f = F(estop: true);
        Assert.Equal("1号机急停中 · 需在触屏复位", ActionGuard.Why(f, DeviceAction.HeatStart, new ActionPayload(N: 2, Sp: 100)));
        Assert.Equal("1号机急停中 · 需在触屏复位", ActionGuard.Why(f, DeviceAction.RunAll));
        Assert.Equal("1号机已在急停 · 需在触屏复位", ActionGuard.Why(f, DeviceAction.Estop));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.StopAll));
        Assert.Equal("", ActionGuard.Why(F(LeaseStatus.Local, estop: true), DeviceAction.LeaseRequest));   // 申请控制权只要连接
    }

    // ———————————————————— 各操作自己的检查 ————————————————————

    [Theory]
    [InlineData(DeviceAction.Enable)]
    [InlineData(DeviceAction.Start)]
    [InlineData(DeviceAction.HeatStart)]
    [InlineData(DeviceAction.Stop)]
    [InlineData(DeviceAction.HeatStop)]
    [InlineData(DeviceAction.ProgAbort)]
    public void Uninstalled(DeviceAction a) => Assert.Equal("RV5 未安装", ActionGuard.Why(F(), a, new ActionPayload(N: 5, Sp: 100)));

    [Fact]
    public void NoSuchReactor()
    {
        Assert.Equal("没有这个反应釜", ActionGuard.Why(F(), DeviceAction.Stop, ActionPayload.Ch(9)));
        Assert.Equal("没有这个反应釜", ActionGuard.Why(F(), DeviceAction.Stop));          // 没给 n
        Assert.Equal("设备不存在", ActionGuard.Why(null, DeviceAction.Stop, ActionPayload.Ch(1)));
        Assert.Equal("设备不存在", ActionGuard.Why(F() with { Exists = false }, DeviceAction.Reconnect));
        Assert.Equal("未知操作 foo", ActionGuard.Why(F(), "foo"));
    }

    [Theory]
    [InlineData(DeviceAction.Start, 3, "RV3 已停用 · 先启用")]
    [InlineData(DeviceAction.HeatStart, 3, "RV3 已停用 · 先启用")]
    [InlineData(DeviceAction.ProgRun, 3, "RV3 已停用 · 先启用")]
    [InlineData(DeviceAction.Enable, 2, "RV2 已启用")]
    [InlineData(DeviceAction.Enable, 3, "")]
    [InlineData(DeviceAction.Start, 1, "RV1 已在运行")]
    [InlineData(DeviceAction.Start, 2, "")]
    [InlineData(DeviceAction.HeatStart, 1, "RV1 已在加热")]
    public void DisabledEnabledRunning(DeviceAction a, int n, string reason) => Assert.Equal(reason, ActionGuard.Why(F(), a, new ActionPayload(N: n, Sp: 100)));

    [Theory]
    [InlineData(DeviceAction.ProgLoad, 1, null, "RV1 程序运行中 · 先中止")]
    [InlineData(DeviceAction.ProgRun, 1, null, "RV1 程序运行中")]
    [InlineData(DeviceAction.ProgRun, 2, null, "RV2 没有程序 · 先下发")]
    [InlineData(DeviceAction.ProgRun, 4, null, "")]                 // 已结束的程序可以再运行
    [InlineData(DeviceAction.ProgLoad, 2, "h2-std", "")]
    [InlineData(DeviceAction.ProgLoad, 2, "nope", "程序不存在")]
    [InlineData(DeviceAction.ProgLoad, 4, null, "")]
    public void Programs(DeviceAction a, int n, string? id, string reason) => Assert.Equal(reason, ActionGuard.Why(F(), a, new ActionPayload(N: n, Id: id)));

    [Fact]
    public void ProgramIdsNull_SkipsExistenceCheck() =>
        Assert.Equal("", ActionGuard.Why(F() with { ProgramIds = null }, DeviceAction.ProgLoad, new ActionPayload(N: 2, Id: "nope")));

    [Theory]
    [InlineData(999.0, "超出量程 · HT-PR8 ≤ 200 ℃")]
    [InlineData(-1.0, "超出量程 · HT-PR8 ≤ 200 ℃")]
    [InlineData(200.0, "")]
    [InlineData(0.0, "")]
    [InlineData(null, "请输入设定温度")]
    [InlineData(double.NaN, "请输入设定温度")]
    public void SetSp_Range(double? sp, string reason) => Assert.Equal(reason, ActionGuard.Why(F(), DeviceAction.SetSp, new ActionPayload(N: 2, Sp: sp)));

    [Fact]
    public void Range_FollowsModel()
    {
        Assert.Equal("超出量程 · HT-PR4 ≤ 250 ℃", ActionGuard.Why(F(model: Pr4), DeviceAction.SetSp, new ActionPayload(N: 2, Sp: 251)));
        Assert.Equal("", ActionGuard.Why(F(model: Pr4), DeviceAction.SetSp, new ActionPayload(N: 2, Sp: 250)));
        Assert.Equal("超出量程 · 0–1500 rpm", ActionGuard.Why(F(), DeviceAction.StirSet, new ActionPayload(Rpm: 1501)));
        Assert.Equal("请输入转速", ActionGuard.Why(F(), DeviceAction.StirSet));
        Assert.Equal("", ActionGuard.Why(F(), DeviceAction.StirSet, new ActionPayload(Rpm: 1500)));
        // 启动加热不给设定值：用反应釜当前的设定值（没有 → 请输入设定温度）
        Assert.Equal("请输入设定温度", ActionGuard.Why(F(), DeviceAction.HeatStart, ActionPayload.Ch(2)));
        var withSp = F() with { Channels = F().Channels.Select(c => c.N == 2 ? c with { Sp = 999 } : c).ToArray() };
        Assert.Equal("超出量程 · HT-PR8 ≤ 200 ℃", ActionGuard.Why(withSp, DeviceAction.HeatStart, ActionPayload.Ch(2)));
    }

    [Fact]
    public void SharedStir_OnIndependentModel()
    {
        var f = F(model: Pr4);   // HT-PR4：每釜独立搅拌
        Assert.False(f.SharedStir);
        Assert.Equal("各釜独立搅拌 · 在反应釜上设置", ActionGuard.Why(f, DeviceAction.StirOn));
        Assert.Equal("各釜独立搅拌 · 在反应釜上设置", ActionGuard.Why(f, DeviceAction.StirOff));   // 停止类也查型号
        Assert.Equal("各釜独立搅拌 · 在反应釜上设置", ActionGuard.Why(f, DeviceAction.StirSet, new ActionPayload(Rpm: 600)));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.ChStirOn, ActionPayload.Ch(2)));
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.ChStirOff, ActionPayload.Ch(2)));
        Assert.Equal("超出量程 · 0–1500 rpm", ActionGuard.Why(f, DeviceAction.ChStirSet, new ActionPayload(N: 2, Rpm: 99999)));
        Assert.Equal("RV2 搅拌已在运行", ActionGuard.Why(f with { Channels = f.Channels.Select(c => c with { StirOn = true }).ToArray() }, DeviceAction.ChStirOn, ActionPayload.Ch(2)));
    }

    [Fact]
    public void IndependentStir_OnSharedModel()
    {
        var f = F(model: Pr8);
        Assert.True(f.SharedStir);
        Assert.Equal("全釜共用搅拌 · 在设备上设置", ActionGuard.Why(f, DeviceAction.ChStirOn, ActionPayload.Ch(2)));
        Assert.Equal("全釜共用搅拌 · 在设备上设置", ActionGuard.Why(f, DeviceAction.ChStirOff, ActionPayload.Ch(2)));
        Assert.Equal("全釜共用搅拌 · 在设备上设置", ActionGuard.Why(f, DeviceAction.ChStirSet, new ActionPayload(N: 2, Rpm: 600)));
        Assert.Equal("搅拌已在运行", ActionGuard.Why(f, DeviceAction.StirOn));
        Assert.Equal("", ActionGuard.Why(F(model: Pr8, stirOn: false), DeviceAction.StirOn));
        Assert.Equal("", ActionGuard.Why(f with { SharedStir = false }, DeviceAction.ChStirOn, ActionPayload.Ch(2)));   // 显式覆盖型号
    }

    [Fact]
    public void RunAll()
    {
        Assert.Equal("", ActionGuard.Why(F(), DeviceAction.RunAll));   // RV4 已结束 + 已启用
        var none = F() with { Channels = F().Channels.Select(c => c with { Program = c.N == 1 ? ProgramStatus.Running : null }).ToArray() };
        Assert.Equal("没有可运行的反应釜", ActionGuard.Why(none, DeviceAction.RunAll));
        var idleLoaded = F() with { Channels = new[] { new ChannelFacts(1, true, StateKey.Idle, Program: ProgramStatus.Loaded) } };
        Assert.Equal("没有可运行的反应釜", ActionGuard.Why(idleLoaded, DeviceAction.RunAll));   // 停用的不算
        Assert.True(ActionGuard.IsRerunnable(ProgramStatus.Failed));
        Assert.Equal(new[] { 4 }, ActionGuard.RunAllTargets(F()));
    }

    /// <summary>
    /// D6：真机的「全部运行」只运行本机这次下发的程序（ChannelFacts.Downloaded）—— 触屏放上去的程序（HMI 快照里已结束 / 已中止、本机没下发）不算，
    /// 守卫和要运行的清单用同一个判断。Downloaded = null（演示数据）照样稿。
    /// </summary>
    [Fact]
    public void RunAll_Live_OnlyProgramsThisPcDownloaded()
    {
        var chans = new[]
        {
            new ChannelFacts(1, true, StateKey.Done, Program: ProgramStatus.Done, Downloaded: false),      // 触屏的程序：不碰
            new ChannelFacts(2, true, StateKey.Ready, Program: ProgramStatus.Loaded, Downloaded: true),    // 本机下发、待运行
            new ChannelFacts(3, true, StateKey.React, Program: ProgramStatus.Running, Downloaded: true),   // 本机下发、在运行：跳过
            new ChannelFacts(4, true, StateKey.Stopped, Program: ProgramStatus.Aborted, Downloaded: true), // 本机下发、已中止：再跑
            new ChannelFacts(5, true, StateKey.Idle, Program: ProgramStatus.Loaded, Downloaded: true),     // 停用：不算
        };
        var f = F() with { Channels = chans };
        Assert.Equal("", ActionGuard.Why(f, DeviceAction.RunAll));
        Assert.Equal(new[] { 2, 4 }, ActionGuard.RunAllTargets(f));
        var onlyHmi = F() with { Channels = new[] { chans[0], new ChannelFacts(2, true, StateKey.Stopped, Program: ProgramStatus.Aborted, Downloaded: false) } };
        Assert.Equal("没有可运行的反应釜", ActionGuard.Why(onlyHmi, DeviceAction.RunAll));
        Assert.Empty(ActionGuard.RunAllTargets(onlyHmi));
        // 「运行程序」（单个反应釜）不受影响：照旧看 HMI 上的程序状态（旧界面的「运行程序」也是这样）
        Assert.Equal("", ActionGuard.Why(onlyHmi, DeviceAction.ProgRun, ActionPayload.Ch(1)));
    }

    [Fact]
    public void Sessions()
    {
        Assert.Equal("会话 PT-20261108-0930 记录中", ActionGuard.Why(F(session: true), DeviceAction.SessionStart));
        Assert.Equal("", ActionGuard.Why(F(session: true), DeviceAction.SessionMark, new ActionPayload(Text: "x")));
        Assert.Equal("1号机没有记录中的会话 · 先开始会话", ActionGuard.Why(F(), DeviceAction.SessionMark));
        Assert.Equal("没有记录中的会话", ActionGuard.Why(F(), DeviceAction.SessionEnd));
    }

    [Theory]
    [InlineData("a1", "")]
    [InlineData("a2", "已确认")]
    [InlineData("nope", "报警不存在")]
    [InlineData(null, "报警不存在")]
    public void AlarmAck(string? id, string reason) => Assert.Equal(reason, ActionGuard.Why(F(LeaseStatus.Local), DeviceAction.AlarmAck, new ActionPayload(Id: id)));

    [Theory]
    [InlineData(DeviceAction.LeaseRequest, LeaseStatus.Remote, "已是远程控制")]
    [InlineData(DeviceAction.LeaseRequest, LeaseStatus.Requested, "正在等待触屏确认")]
    [InlineData(DeviceAction.LeaseRequest, LeaseStatus.Local, "")]
    [InlineData(DeviceAction.LeaseCancel, LeaseStatus.Requested, "")]
    [InlineData(DeviceAction.LeaseCancel, LeaseStatus.Local, "没有进行中的申请")]
    [InlineData(DeviceAction.LeaseRelease, LeaseStatus.Remote, "")]
    [InlineData(DeviceAction.LeaseRelease, LeaseStatus.Lost, "")]
    [InlineData(DeviceAction.LeaseRelease, LeaseStatus.Local, "当前没有控制权")]
    [InlineData(DeviceAction.LeaseRelease, LeaseStatus.Other, "当前没有控制权")]
    public void LeaseActions(DeviceAction a, LeaseStatus lease, string reason) => Assert.Equal(reason, ActionGuard.Why(F(lease), a));

    [Fact]
    public void LeaseRequest_NeedsConnectionOnly() =>
        Assert.Equal("1号机连接中断 · 重连中 12 s", ActionGuard.Why(F(LeaseStatus.Local, ConnStatus.Offline), DeviceAction.LeaseRequest));

    [Theory]
    [InlineData("新名字", "")]
    [InlineData(" 新名字 ", "")]
    [InlineData("", "名称不能为空")]
    [InlineData("   ", "名称不能为空")]
    [InlineData(null, "名称不能为空")]
    [InlineData("一二三四五六七八", "")]                       // 8 个汉字 = 16
    [InlineData("一二三四五六七八九", "名称太长 · 最多 8 个汉字或 16 个字母")]
    [InlineData("ABCDEFGHIJKLMNOP", "")]
    [InlineData("ABCDEFGHIJKLMNOPQ", "名称太长 · 最多 8 个汉字或 16 个字母")]
    [InlineData("2号机", "名称已被其他设备使用（192.168.1.24）")]
    [InlineData(" 2号机 ", "名称已被其他设备使用（192.168.1.24）")]
    public void Rename(string? alias, string reason) => Assert.Equal(reason, ActionGuard.Why(F(), DeviceAction.Rename, new ActionPayload(Alias: alias)));

    [Fact]
    public void AliasUnits_CountCjkAsTwo()
    {
        Assert.Equal(16, ActionGuard.AliasUnits("高压加氢测试一号"));
        Assert.Equal(16, ActionGuard.AliasUnits("ReactorLab-PR8-A"));
        Assert.Equal(6, ActionGuard.AliasUnits("1号机" + "x"));
        Assert.Equal(2, ActionGuard.AliasUnits(char.ConvertFromUtf32(0x20000)));   // 辅助平面汉字也算 2
        Assert.Equal("a b", ActionGuard.JsTrim(((char)0xFEFF).ToString() + " a b" + (char)0x3000));
    }

    [Fact]
    public void LiveFacts_FromResolvedModel()
    {
        // 真机：型号由 /device + /settings 推出（量程覆盖表内假设值），守卫照样按型号查量程
        var info = ParallelReactor.Api.ApiJson.Deserialize<ParallelReactor.Api.DeviceInfoDto>(Samples.Read("live-device.json"))!;
        var m = ModelProfiles.Embedded.Resolve(info);
        var f = new GuardFacts { Alias = "现场机", Lease = LeaseStatus.Remote, Model = m, Channels = m.Channels.Select(c => new ChannelFacts(c.N, c.Installed, StateKey.Ready)).ToArray() };
        Assert.Equal("超出量程 · " + m.Name + " ≤ " + Core.Format.JsNumber.ToString(m.Limits.TempMaxC) + " ℃",
            ActionGuard.Why(f, DeviceAction.SetSp, new ActionPayload(N: m.Installed[0], Sp: m.Limits.TempMaxC + 1)));
        Assert.Equal("", ActionGuard.Why(f with { Lease = LeaseStatus.Local }, DeviceAction.Stop, ActionPayload.Ch(m.Installed[0])));
    }
}
