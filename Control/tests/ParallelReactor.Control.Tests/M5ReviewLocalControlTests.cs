using System;
using System.IO;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Models;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核 c1-live-1：本机下发之后 HMI 回到本地控制（触屏从此能改草稿，改了不记事件 —— HMI 源码 OperatorCommands.TryEditProgram 经仲裁 Control 类，
/// 远程控制中触屏一律被拒；这一路在执行程序 / 在运行时草稿也锁定）。回到本地的事件（Api/ControlService.OnTransition：control.released / takenBack / expired / mode）
/// 标在台账条目上（LocalControl，只留最早的、重新下发才清掉，实时与续取两条路都认，不看级别、不看是谁的租约）：
/// 之后开始的执行照样匹配但带说明（AfterLocalControl，状态行「触屏接管过控制」），之前开始的不受影响；「启动」禁用（LocalControlReason）、「全部运行」不算它。
/// </summary>
public class M5ReviewLocalControlTests
{
    private static readonly DateTime D0 = new(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc);
    private static readonly ModelProfile Pr8 = ModelProfiles.Embedded.Get("PR8")!;

    private static ProgramDoc Doc(string name = "加氢测试-01") => new ProgramDoc { Name = name }
        .Add(StepType.Heat, new { temp = 120, wait = 10 })
        .Add(StepType.Press, new { gas = "A", pres = 50.76 })
        .Add(StepType.React, new { time = 72 })
        .Add(StepType.End, new { cool = 50, mode = "vent" });

    /// <summary>下发之前 RV1 上次执行（2 h 前开始、3 步、已完成）。</summary>
    private static readonly ChannelProgramSnap Prior = Prog(2, 3, "completed", D0.AddHours(-2), "local");

    /// <summary>台账：RV1 在 D0 下发（下发命令的事件序号 77，纪元 1）。</summary>
    private static ProgramLedger Ledger()
    {
        var l = new ProgramLedger("dev-1", Me);
        l.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opId: "op-1", opEventSeq: 77, libraryId: "h2-std", eventEpoch: 1);
        return l;
    }

    private static ChannelProgramSnap Run(int step, double startedAfterS, string? by = ByMe, string state = "running", string message = "") =>
        Prog(step, 4, state, D0.AddSeconds(startedAfterS), by, message);

    /// <summary>本机释放控制权（回到本地）：kind / 级别 / 客户端同 HMI 的审计。</summary>
    private static void Released(ProgramLedger l, long seq, DateTime? at, string client = Me) =>
        l.ObserveEvent("control.released", "远程释放控制权 · 回到本地", client, null, "info", at, seq, 1);

    [Theory]
    [InlineData("control.released", "info", Me)]                 // 本机自己释放也算
    [InlineData("control.takenBack", "info", "PC-别人")]          // 触屏切回（不管是谁的租约）
    [InlineData("control.expired", "warn", Me)]                  // 宽限期满（warn：不能套「失败的命令不算」的级别过滤）
    [InlineData("control.mode", "warn", null)]                   // 被收回（急停 / 宽限期内重新申请），没有客户端
    public void EveryReturnToLocal_FlagsTheEntry_AnyLevelAnyHolder(string kind, string level, string? client)
    {
        var l = Ledger();
        l.ObserveEvent(kind, "回到本地", client, null, level, D0.AddMinutes(3), 78, 1);
        var e = l.Entry(1)!;
        Assert.True(e.Valid);
        Assert.Equal((true, (long?)78, (DateTime?)D0.AddMinutes(3)), (e.LocalControl, e.LocalSinceSeq, e.LocalSinceAt));
        Assert.Equal(78, e.CheckedSeq);                                                // 核对位置照常往前推
        Assert.False(l.CanRun(1, null, "b1"));
        Assert.True(l.IsLocalControl(1, null, "b1"));
    }

    [Theory]
    [InlineData("control.requested")]
    [InlineData("control.granted")]
    [InlineData("control.lost")]          // 心跳丢失进入宽限：仲裁器还在远程，触屏改不了
    [InlineData("control.resumed")]
    [InlineData("control.denied")]        // 只发生在申请中（本地）—— 本机持有控制权时别人申请不会登记；之前一定先有回到本地的事件
    [InlineData("control.requestExpired")]
    [InlineData("op.channel.start")]
    public void OtherEvents_DoNotFlag(string kind)
    {
        var l = Ledger();
        l.ObserveEvent(kind, "x", "PC-别人", null, "warn", D0.AddMinutes(3), 78, 1);
        Assert.False(l.Entry(1)!.LocalControl);
        Assert.True(l.CanRun(1, null, "b1"));
    }

    /// <summary>序号不比下发新（下发之前的释放）不算；只留最早的；只有重新下发才清掉；读回台账文件照样在。</summary>
    [Fact]
    public void OnlyAfterTheDownload_EarliestKept_ClearedByANewDownload()
    {
        var l = Ledger();
        Released(l, 76, D0.AddMinutes(-1));                                           // 比下发命令的事件（77）早
        Assert.False(l.Entry(1)!.LocalControl);
        l.ObserveEvent("op.channel.start", "RV2 已启动", "PC-别人", "op-y", "info", D0.AddMinutes(1), 78, 1);
        Released(l, 79, D0.AddMinutes(2));
        l.ObserveEvent("control.granted", "已授权远程控制", Me, null, "info", D0.AddMinutes(4), 80, 1);
        l.ObserveEvent("control.expired", "远程宽限期满", Me, null, "warn", D0.AddMinutes(9), 81, 1);
        var e = l.Entry(1)!;
        Assert.Equal((true, (long?)79, (DateTime?)D0.AddMinutes(2), (long?)81), (e.LocalControl, e.LocalSinceSeq, e.LocalSinceAt, e.CheckedSeq));
        // 比记下的更早的（续取补上来的、跳号时先收到后面的）：换成更早的那条
        var g = Ledger();
        Released(g, 80, D0.AddMinutes(5));                                             // 78、79 还没收到：跳号，照样核对
        Assert.Equal(80, g.Entry(1)!.LocalSinceSeq);
        Assert.True(g.Behind);
        Assert.False(g.ObservePage(new EventsPageDto(1, 80, 77, 3, false, new[]
        {
            Ev(78, "op.channel.start"), Ev(79, "control.takenBack", "warn", null), Ev(80, "control.released"),
        }), 77));
        Assert.Equal((79L, D0.AddSeconds(79)), (g.Entry(1)!.LocalSinceSeq!.Value, g.Entry(1)!.LocalSinceAt!.Value));
        Assert.False(g.Behind);
        // 读回台账文件：标记与起点都在
        var copy = new ProgramLedger("dev-1", Me);
        copy.LoadJson(l.ToJson());
        Assert.Equal(l.Entry(1), copy.Entry(1));
        Assert.False(copy.CanRun(1, null, "b1"));
        // 重新下发（新的条目）才清掉
        l.AddDownload(1, Doc(), "b1", D0.AddMinutes(20), TimeSpan.Zero, null, true, opId: "op-2", opEventSeq: 90, eventEpoch: 1);
        Assert.Equal((false, (long?)null, (DateTime?)null), (l.Entry(1)!.LocalControl, l.Entry(1)!.LocalSinceSeq, l.Entry(1)!.LocalSinceAt));
        Assert.True(l.CanRun(1, null, "b1"));
    }

    /// <summary>续取（GET /events）一页里的回到本地同样标上（warn 级别也算），核对位置照常推到页尾。</summary>
    [Fact]
    public void CatchUpPage_FlagsToo()
    {
        var l = Ledger();
        l.BeginCheck();
        Assert.False(l.ObservePage(new EventsPageDto(1, 81, 77, 4, false, new[]
        {
            Ev(78, "op.channel.start"), Ev(79, "control.expired", "warn"), Ev(80, "control.requested"), Ev(81, "control.granted"),
        }), 77));
        l.EndCheck();
        var e = l.Entry(1)!;
        Assert.Equal((true, (long?)79, (DateTime?)D0.AddSeconds(79), (long?)81), (e.LocalControl, e.LocalSinceSeq, e.LocalSinceAt, e.CheckedSeq));
        Assert.True(e.Valid);
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, null, "b1").Kind);
        Assert.False(l.CanRun(1, null, "b1"));
    }

    /// <summary>没有序号的事件按时刻：比下发早（容差 2 s）的不算；不知道时刻的算（宁可多标）。</summary>
    [Fact]
    public void WithoutSeq_ByTime_UnknownTimeCounts()
    {
        var l = Ledger();
        l.ObserveEvent("control.released", "回到本地", Me, null, "info", D0.AddMinutes(-5));
        Assert.False(l.Entry(1)!.LocalControl);
        l.ObserveEvent("control.released", "回到本地", Me, null, "info", D0.AddSeconds(-1.5));   // 容差内：算之后
        Assert.True(l.Entry(1)!.LocalControl);
        var u = Ledger();
        u.ObserveEvent("control.takenBack", "触屏切回", null, null, "info", null);
        Assert.Equal((true, (DateTime?)null), (u.Entry(1)!.LocalControl, u.Entry(1)!.LocalSinceAt));
    }

    /// <summary>
    /// 匹配：还没跑 / 跑的还是下发之前那次 → 待运行，带说明；接管之后开始的执行（本机或触屏启动）→ 对上，带说明；接管之前开始的 → 对上，不带（那时控制权还在本机）；
    /// 别的客户端启动的照旧对不上。不知道接管的时刻 → 比下发新的执行都带说明。
    /// </summary>
    [Fact]
    public void Match_CarriesTheCaveat_OnlyForRunsAfterTheWindow()
    {
        var l = Ledger();
        Released(l, 78, D0.AddMinutes(5));
        var loaded = l.Match(1, null, "b1");
        Assert.Equal((LedgerMatchKind.Loaded, true, true), (loaded.Kind, loaded.AfterLocalControl, loaded.Usable));
        Assert.True(l.Match(1, Prior, "b1").AfterLocalControl);
        var before = l.Match(1, Run(0, 60), "b1");                                     // 本机在 D0+1 min 启动、D0+5 min 才释放
        Assert.Equal((LedgerMatchKind.Matched, false), (before.Kind, before.AfterLocalControl));
        Assert.False(l.CanRun(1, Run(0, 60), "b1"));                                   // 条目接管过就不再从这里启动
        var after = l.Match(1, Run(0, 360), "b1");
        Assert.Equal((LedgerMatchKind.Matched, true, false), (after.Kind, after.AfterLocalControl, after.StartedByTouch));
        Assert.True(l.Match(1, Run(0, 300 - 1.5), "b1").AfterLocalControl);              // 时钟误差 2 s 以内算之后
        var touch = l.Match(1, Run(0, 360, by: "local"), "b1");
        Assert.Equal((LedgerMatchKind.Matched, true, true), (touch.Kind, touch.StartedByTouch, touch.AfterLocalControl));
        Assert.Equal(LedgerMatchKind.Mismatch, l.Match(1, Run(0, 360, by: "远程（PC-别人）"), "b1").Kind);
        // 不知道接管的时刻
        var u = Ledger();
        Released(u, 78, null);
        Assert.True(u.Match(1, Run(0, 30), "b1").AfterLocalControl);
        // 没接管过：照旧
        var n = Ledger();
        Assert.False(n.Match(1, Run(0, 30), "b1").AfterLocalControl);
        Assert.False(n.Match(1, null, "b1").AfterLocalControl);
        Assert.True(n.CanRun(1, null, "b1"));
        Assert.False(n.IsLocalControl(1, null, "b1"));
    }

    /// <summary>
    /// 程序记录与状态行（样稿没有这一种，复核）：接管之后的执行「… · 触屏接管过控制」，悬停写全「触屏端接管过控制 · 按本机下发的程序显示，设备上的程序可能已修改」；
    /// 已下发待运行「待运行 · 触屏接管过控制」，悬停「… · 重新下发后才能从这里启动」；触屏启动的照旧只写「触屏启动」（两样不同时写）；接管之前开始的不写。
    /// </summary>
    [Fact]
    public void RecordAndStatusLine_ShowTheCaveat()
    {
        var l = Ledger();
        Released(l, 78, D0.AddMinutes(5));
        // 已下发 · 待运行
        var f0 = Frame(D0.AddMinutes(6), Ch(1, program: Prior));
        var rec = l.ProgramOf(1, f0, "b1", Pr8)!;
        Assert.Equal((ProgramStatus.Loaded, true, false), (rec.Status, rec.AfterLocalControl, rec.StartedByTouch));
        var line = Line(rec, D0.AddMinutes(6));
        Assert.Equal(("已下发", "待运行 · 触屏接管过控制"), (line.Word, line.Full));
        Assert.Equal("已下发 · 待运行 · 触屏端接管过控制 · 设备上的程序可能已修改 · 重新下发后才能从这里启动", line.Title);
        Assert.Equal("已下发 · 待运行", line.Short);                                     // 窄画布不写（同「触屏启动」）

        // 接管之后由本机启动（检查器的「运行」照样能跑）
        var t = D0.AddMinutes(8);
        var f1 = Frame(t, Ch(1, temp: 100, sp: 120, atMode: 0, program: Run(0, 7 * 60, message: "01/04 升温")));
        l.ObserveFrame(f1, "b1");
        rec = l.ProgramOf(1, f1, "b1", Pr8)!;
        Assert.Equal((ProgramStatus.Running, true, false), (rec.Status, rec.AfterLocalControl, rec.StartedByTouch));
        line = Line(rec, t, StateKey.Heat);
        Assert.Matches(@"^第 1/4 步 · 预计 \d\d:\d\d 结束 · 触屏接管过控制$", line.Full);
        Assert.Equal(line.Long[..^" · 触屏接管过控制".Length] + " · 触屏端接管过控制 · 按本机下发的程序显示，设备上的程序可能已修改", line.Title);
        Assert.Equal("运行中", line.Word);
        Assert.DoesNotContain("触屏", line.Short, StringComparison.Ordinal);

        // 结束之后照样写
        var f2 = Frame(D0.AddMinutes(200), Ch(1, temp: 48, program: Run(3, 7 * 60, state: "completed")));
        l.ObserveFrame(f2, "b1");
        rec = l.ProgramOf(1, f2, "b1", Pr8)!;
        line = Line(rec, D0.AddMinutes(200));
        Assert.Equal(ProgramStatus.Done, rec.Status);
        Assert.EndsWith(" · 触屏接管过控制", line.Full, StringComparison.Ordinal);
        Assert.EndsWith(" · 触屏端接管过控制 · 按本机下发的程序显示，设备上的程序可能已修改", line.Title, StringComparison.Ordinal);

        // 触屏启动的：只写「触屏启动」
        var k = Ledger();
        Released(k, 78, D0.AddMinutes(5));
        var f3 = Frame(t, Ch(1, temp: 100, sp: 120, atMode: 0, program: Run(0, 7 * 60, by: "local")));
        k.ObserveFrame(f3, "b1");
        rec = k.ProgramOf(1, f3, "b1", Pr8)!;
        Assert.True(rec.StartedByTouch && rec.AfterLocalControl);
        line = Line(rec, t, StateKey.Heat);
        Assert.EndsWith(" · 触屏启动", line.Full, StringComparison.Ordinal);
        Assert.EndsWith(" · 触屏启动 · 按本机下发的程序显示", line.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("接管", line.Title, StringComparison.Ordinal);

        // 接管之前开始的：不写
        var b = Ledger();
        var f4 = Frame(D0.AddMinutes(2), Ch(1, temp: 100, sp: 120, atMode: 0, program: Run(0, 60)));
        b.ObserveFrame(f4, "b1");
        Released(b, 78, D0.AddMinutes(5));
        rec = b.ProgramOf(1, Frame(D0.AddMinutes(6), Ch(1, temp: 100, sp: 120, atMode: 0, program: Run(0, 60))), "b1", Pr8)!;
        Assert.False(rec.AfterLocalControl);
        line = Line(rec, D0.AddMinutes(6), StateKey.Heat);
        Assert.DoesNotContain("接管", line.Title, StringComparison.Ordinal);
        Assert.Null(line.TitleLong);
    }

    /// <summary>
    /// 守卫（复核）：接管过控制的反应釜「启动」禁用并写明（停用 / 在运行 / 还在核对的先写那个），「全部运行」不算它 ——
    /// 没有别的对象时写同一个原因（本来就运行不了的照旧「没有可运行的反应釜」）。重新下发之后照常。
    /// </summary>
    [Fact]
    public void Guard_StartAndRunAll()
    {
        var chans = new[]
        {
            new ChannelFacts(1, true, StateKey.Ready, Program: ProgramStatus.Loaded, Downloaded: true, LocalControl: true),
            new ChannelFacts(2, true, StateKey.Idle, Program: ProgramStatus.Loaded, Downloaded: true, LocalControl: true),
            new ChannelFacts(3, true, StateKey.React, Program: ProgramStatus.Running, Downloaded: true, LocalControl: true),
            new ChannelFacts(4, true, StateKey.Ready, Program: ProgramStatus.Loaded, Downloaded: true, Checking: true, LocalControl: true),
        };
        var f = F(chans);
        Assert.Equal("RV1 触屏端接管过控制，设备上的程序可能已修改 · 请重新下发", ActionGuard.Why(f, DeviceAction.Start, ActionPayload.Ch(1)));
        Assert.Equal("RV2 已停用 · 先启用", ActionGuard.Why(f, DeviceAction.Start, ActionPayload.Ch(2)));
        Assert.Equal("RV3 已在运行", ActionGuard.Why(f, DeviceAction.Start, ActionPayload.Ch(3)));
        Assert.Equal("RV4 " + ProgramLedger.CheckingReason, ActionGuard.Why(f, DeviceAction.Start, ActionPayload.Ch(4)));
        Assert.False(ActionGuard.IsRunAllTarget(chans[0]));
        Assert.Empty(ActionGuard.RunAllTargets(f));
        Assert.Equal(ProgramLedger.CheckingReason, ActionGuard.Why(f, DeviceAction.RunAll));                 // 还在核对的先写（核对完可能就能运行）
        var g = F(chans.Take(3).ToArray());
        Assert.Equal(ProgramLedger.LocalControlReason, ActionGuard.Why(g, DeviceAction.RunAll));
        Assert.Equal("没有可运行的反应釜", ActionGuard.Why(F(chans.Skip(1).Take(2).ToArray()), DeviceAction.RunAll));   // 停用的、在运行的本来就不能运行
        // 别的反应釜能运行：照常，接管过的不在对象里
        var h = F(chans.Append(new ChannelFacts(5, true, StateKey.Stopped, Program: ProgramStatus.Aborted, Downloaded: true)).ToArray());
        Assert.Equal("", ActionGuard.Why(h, DeviceAction.RunAll));
        Assert.Equal(new[] { 5 }, ActionGuard.RunAllTargets(h));
        // 重新下发之后（没有接管）：照常
        var again = F(new[] { chans[0] with { LocalControl = false } });
        Assert.Equal("", ActionGuard.Why(again, DeviceAction.Start, ActionPayload.Ch(1)));
        Assert.Equal(new[] { 1 }, ActionGuard.RunAllTargets(again));
    }

    /// <summary>M5 写的台账文件（没有接管控制的字段）照样读：条目有效、没有标记；文件版本不变。</summary>
    [Fact]
    public void OldLedgerFile_WithoutTheNewFields_StillLoads()
    {
        string doc = Doc().ToJson();
        string json = "{\n  \"version\": 1,\n  \"deviceId\": \"dev-1\",\n  \"client\": \"" + Me + "\",\n  \"bootId\": \"b1\",\n  \"entries\": [\n    {\n      \"n\": 1,\n" +
            "      \"name\": \"加氢测试-01\",\n      \"libraryId\": \"h2-std\",\n      \"doc\": " + doc + ",\n      \"hash\": \"" + ProgramLedger.HashOf(doc) + "\",\n" +
            "      \"stepCount\": 4,\n      \"downloadedAt\": \"2026-11-08T23:00:00Z\",\n      \"downloadedAtDevice\": \"2026-11-08T23:00:00Z\",\n      \"bootId\": \"b1\",\n" +
            "      \"client\": \"" + Me + "\",\n      \"opId\": \"op-1\",\n      \"opEventSeq\": 77,\n      \"priorKnown\": true,\n      \"priorStartedAt\": \"2026-11-08T21:00:00Z\",\n" +
            "      \"eventEpoch\": 1,\n      \"checkedSeq\": 80\n    }\n  ],\n  \"runs\": []\n}\n";
        var dir = Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            var path = ProgramLedger.PathFor(dir, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            var l = ProgramLedger.Open(dir, id, Me);
            Assert.Null(l.LoadError);
            var e = l.Entry(1)!;
            Assert.True(e.Valid);
            Assert.Equal((false, (long?)null, (DateTime?)null, (long?)80), (e.LocalControl, e.LocalSinceSeq, e.LocalSinceAt, e.CheckedSeq));
            Assert.True(l.CanRun(1, null, "b1"));
            Assert.Equal(1, ProgramLedger.FileVersion);
            // 标上之后写盘、读回
            Released(l, 81, D0.AddMinutes(3));
            Assert.Contains("\"localControl\": true", File.ReadAllText(path), StringComparison.Ordinal);
            var back = ProgramLedger.Open(dir, id, Me);
            Assert.Equal(l.Entry(1), back.Entry(1));
            Assert.Equal((true, (long?)81), (back.Entry(1)!.LocalControl, back.Entry(1)!.LocalSinceSeq));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ProgramStatusLine Line(ProgramRecord rec, DateTime nowUtc, StateKey state = StateKey.Ready)
    {
        var pr = Progress.Of(rec, new ProgressContext(nowUtc.ToLocalTime(), Model: Pr8, ChannelState: state))!;
        return ProgramPanelText.Status(rec, pr, true, "", nowUtc.ToLocalTime())!;
    }

    private static ParallelReactor.Services.EventRecord Ev(long seq, string kind, string level = "info", string? client = "PC-2") =>
        new(seq, 1, D0.AddSeconds(seq), "", level, kind, null, "remote", "系统", client, null, "控制权", "x");

    private static GuardFacts F(ChannelFacts[] chans) => new()
    {
        Exists = true, Alias = "1号机", Conn = ConnStatus.Online, EverOnline = true, StopReachable = true, Lease = LeaseStatus.Remote,
        Model = Pr8, Channels = chans, Alarms = Array.Empty<AlarmFacts>(),
    };
}
