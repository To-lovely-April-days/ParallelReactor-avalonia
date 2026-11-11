using System;
using System.IO;
using System.Linq;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Models;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Programs.ProgramLedger（PORT-PLAN §3.2 第 4 条 / R5）：严格匹配的每一条规则（启动号、比下发新、步骤数、由谁启动）、
/// 对不上就作废且不再匹配、HMI 重启、别的客户端替换 / 复制（事件）、给页面的 ProgramRecord（与演示数据同一种：状态、当前步、步骤计时、剩余时间）、
/// 降级显示的文字、台账文件（原子写、读回、摘要校验、坏文件）。
/// </summary>
public class LedgerMatchTests
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

    private static ProgramLedger Ledger(out LedgerEntry e, TimeSpan? offset = null, ChannelProgramSnap? prior = null, bool priorKnown = true)
    {
        var l = new ProgramLedger("dev-1", Me);
        e = l.AddDownload(1, Doc(), "b1", D0, offset ?? TimeSpan.Zero, prior ?? Prior, priorKnown, opId: "op-1", opEventSeq: 77, libraryId: "h2-std");
        return l;
    }

    private static ChannelProgramSnap Run(int step, string state = "running", int count = 4, double startedAfterS = 30, string? by = ByMe, string message = "") =>
        Prog(step, count, state, D0.AddSeconds(startedAfterS), by, message);

    [Fact]
    public void NoEntry_WhenNothingDownloaded()
    {
        var l = new ProgramLedger("dev-1", Me);
        Assert.Equal(LedgerMatchKind.NoEntry, l.Match(1, Run(0), "b1").Kind);
        Assert.Null(l.ProgramOf(1, Frame(D0, Ch(1, program: Run(0))), "b1", Pr8));
    }

    [Fact]
    public void Entry_RecordsTheDownload()
    {
        var l = Ledger(out var e, offset: TimeSpan.FromSeconds(3));
        Assert.Equal(1, e.N);
        Assert.Equal("加氢测试-01", e.Name);
        Assert.Equal("h2-std", e.LibraryId);
        Assert.Equal(4, e.StepCount);
        Assert.Equal(Doc().ToJson(), e.DocJson);
        Assert.Equal(ProgramLedger.HashOf(e.DocJson), e.Hash);
        Assert.Equal(64, e.Hash.Length);
        Assert.Equal(D0, e.DownloadedAt);
        Assert.Equal(D0.AddSeconds(3), e.DownloadedAtDevice);
        Assert.Equal("b1", e.BootId);
        Assert.Equal(Me, e.Client);
        Assert.Equal("op-1", e.OpId);
        Assert.Equal(77, e.OpEventSeq);
        Assert.True(e.PriorKnown);
        Assert.Equal(D0.AddHours(-2), e.PriorStartedAt);
        Assert.True(e.Valid);
        Assert.Equal("加氢测试-01", e.Doc().Name);
        Assert.Equal(4, e.Doc().Steps.Count);
        Assert.False(l.Persistent);
    }

    [Fact]
    public void Loaded_UntilANewerRunStarts()
    {
        var l = Ledger(out _);
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, null, "b1").Kind);                 // HMI 这一路没有执行记录
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, Prior, "b1").Kind);                // 还是下发前的那一次
        Assert.True(l.CanRun(1, Prior, "b1"));
        var rec = l.ProgramOf(1, Frame(D0.AddSeconds(5), Ch(1, program: Prior)), "b1", Pr8)!;
        Assert.Equal(ProgramStatus.Loaded, rec.Status);
        Assert.Equal(ProgramSource.Ledger, rec.Source);
        Assert.Equal("加氢测试-01", rec.Name);
        Assert.Equal("h2-std", rec.Id);
        Assert.Equal(4, rec.Steps.Count);
        Assert.Null(rec.StartedAt);
        Assert.Equal("已下发 · 待运行", Progress.Text(Progress.Of(rec, new ProgressContext(D0.ToLocalTime(), Model: Pr8))));
    }

    /// <summary>
    /// 复核（R5）：连上到续取核对完之间（BeginCheck … EndCheck），此刻有效的条目先不认 —— Match = Checking（原因「正在核对离线期间的事件 · 稍候」）、
    /// 不能「启动」、不给页面程序记录；核对完照常。这次连接上新下发的条目不用等；作废的不算在核对；核对状态不写盘。
    /// </summary>
    [Fact]
    public void Checking_AfterReconnect_UntilTheCatchUpEnds()
    {
        var l = Ledger(out _);
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, null, "b1").Kind);
        int v = l.Version;
        Assert.True(l.BeginCheck());
        Assert.True(l.Version > v);
        Assert.True(l.IsChecking(1));
        Assert.True(l.Checking);
        Assert.False(l.IsChecking(2));
        var m = l.Match(1, null, "b1");
        Assert.Equal((LedgerMatchKind.Checking, ProgramLedger.CheckingReason, false), (m.Kind, m.Reason, m.Usable));
        Assert.Equal(LedgerMatchKind.Checking, l.Match(1, Run(0), "b1").Kind);          // 跑起来的那一次也先不认
        Assert.Equal(LedgerMatchKind.Mismatch, l.Match(1, null, "b2").Kind);            // 启动号不同照样是对不上
        Assert.False(l.CanRun(1, null, "b1"));
        Assert.Null(l.ProgramOf(1, Frame(D0.AddSeconds(5), Ch(1, program: Prior)), "b1", Pr8));
        Assert.False(l.BeginCheck());                                                   // 已经在核对：不重复
        v = l.Version;
        Assert.True(l.EndCheck());
        Assert.True(l.Version > v);
        Assert.False(l.Checking);
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, null, "b1").Kind);
        Assert.True(l.CanRun(1, null, "b1"));
        Assert.False(l.EndCheck());                                                     // 没有在核对的：什么都不做

        // 核对期间在这次连接上新下发的：不用等
        l.AddDownload(2, Doc("纯化-3"), "b1", D0.AddMinutes(1), TimeSpan.Zero, null, true, opEventSeq: 80);
        Assert.True(l.BeginCheck());
        l.AddDownload(1, Doc("加氢测试-02"), "b1", D0.AddMinutes(2), TimeSpan.Zero, null, true, opEventSeq: 81);
        Assert.False(l.IsChecking(1));
        Assert.True(l.IsChecking(2));
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, null, "b1").Kind);
        // 核对中作废的：不再算在核对（EndCheck 照样清掉）
        l.Supersede(2, "程序已被 PC-别人 替换");
        Assert.False(l.IsChecking(2));
        Assert.False(l.Checking);
        Assert.Equal(LedgerMatchKind.NoEntry, l.Match(2, null, "b1").Kind);
        Assert.False(l.EndCheck());

        // 不写盘：读回来的台账不在核对（下一次连上时 App 再 BeginCheck）
        Assert.True(l.BeginCheck());
        Assert.DoesNotContain("check", l.ToJson().Replace("checkedSeq", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
        var r = new ProgramLedger("dev-1", Me);
        r.LoadJson(l.ToJson());
        Assert.False(r.Checking);
        Assert.Equal(LedgerMatchKind.Loaded, r.Match(1, null, "b1").Kind);
        // 忘掉一路：不留核对状态
        l.Forget(1);
        Assert.False(l.Checking);
    }

    [Theory]
    [InlineData(ByMe, LedgerMatchKind.Matched, false)]
    [InlineData("远程（" + Me + " · 令牌 op）", LedgerMatchKind.Matched, false)]
    [InlineData("local", LedgerMatchKind.Matched, true)]
    [InlineData("远程（PC-测试工位2）", LedgerMatchKind.Mismatch, false)]
    [InlineData("远程（" + Me + "X）", LedgerMatchKind.Mismatch, false)]
    [InlineData(null, LedgerMatchKind.Mismatch, false)]
    public void StartedBy_MustBeThisPcOrTouch(string? by, LedgerMatchKind kind, bool touch)
    {
        var l = Ledger(out _);
        var m = l.Match(1, Run(0, by: by), "b1");
        Assert.Equal(kind, m.Kind);
        Assert.Equal(touch, m.StartedByTouch);
        if (kind == LedgerMatchKind.Mismatch) Assert.False(string.IsNullOrEmpty(m.Reason));
    }

    [Fact]
    public void StepCount_And_BootId_MustMatch()
    {
        var l = Ledger(out _);
        var m = l.Match(1, Run(0, count: 5), "b1");
        Assert.Equal(LedgerMatchKind.Mismatch, m.Kind);
        Assert.Equal("步骤数不同（设备 5 步 · 本机下发 4 步）", m.Reason);
        Assert.Equal(LedgerMatchKind.Mismatch, l.Match(1, Run(0), "b2").Kind);
        Assert.Equal(LedgerMatchKind.Mismatch, l.Match(1, Run(0), null).Kind);
        Assert.Equal(LedgerMatchKind.Matched, l.Match(1, Run(0), "b1").Kind);
        Assert.Equal(LedgerMatchKind.NoEntry, l.Match(2, Run(0), "b1").Kind);
    }

    [Fact]
    public void Newer_ByPriorRun_OrByDownloadTime_OrUnknown()
    {
        // 下发时没有看到这一路的帧：按下发时刻（设备时钟）比较，允许 2 s 误差
        var l = Ledger(out _, offset: TimeSpan.Zero, prior: null, priorKnown: false);
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, Run(0, startedAfterS: -10), "b1").Kind);
        Assert.Equal(LedgerMatchKind.Matched, l.Match(1, Run(0, startedAfterS: -1.5), "b1").Kind);
        Assert.Equal(LedgerMatchKind.Matched, l.Match(1, Run(0, startedAfterS: 60), "b1").Kind);
        // 两样都不知道：对不上（不猜）
        var u = new ProgramLedger("dev-1", Me);
        u.AddDownload(1, Doc(), "b1", D0, serverOffset: null, prior: null, priorKnown: false);
        Assert.Equal(LedgerMatchKind.Mismatch, u.Match(1, Run(0), "b1").Kind);
        // 看到过上一次：只认比它新的；知道下发时刻（设备时钟）时还要不早于下发（复核：下发时的帧可能是旧的）
        var p = Ledger(out _, offset: TimeSpan.Zero);
        Assert.Equal(LedgerMatchKind.Matched, p.Match(1, Run(0, startedAfterS: 30), "b1").Kind);
        Assert.Equal(LedgerMatchKind.Loaded, p.Match(1, Prog(2, 3, "completed", D0.AddHours(-3), "local"), "b1").Kind);   // 比上一次（−2 h）旧
        // 比上一次新、却比下发早：下发时的帧是旧的，那次执行（刚在触屏上开始又结束）不是这次下发的 → 待运行，不算对上
        Assert.Equal(LedgerMatchKind.Loaded, p.Match(1, Prog(3, 4, "completed", D0.AddMinutes(-10), "local"), "b1").Kind);
        Assert.Equal(LedgerMatchKind.Loaded, p.Match(1, Run(0, startedAfterS: -3600), "b1").Kind);
        Assert.Equal(LedgerMatchKind.Matched, p.Match(1, Run(0, startedAfterS: -1.5), "b1").Kind);                         // 时钟误差 2 s 以内
        // 不知道设备时钟的下发时刻（WS 还没给出时钟偏差）：只按上一次判断
        var q = new ProgramLedger("dev-1", Me);
        q.AddDownload(1, Doc(), "b1", D0, serverOffset: null, Prior, priorKnown: true);
        Assert.Equal(LedgerMatchKind.Matched, q.Match(1, Run(0, startedAfterS: -3600), "b1").Kind);   // 比上一次（−2 h）新
        // 下发时那一路从没执行过（prior = null 但看到了帧）：下发之后开始的都是新的
        var n = new ProgramLedger("dev-1", Me);
        n.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, prior: null, priorKnown: true);
        Assert.Equal(LedgerMatchKind.Matched, n.Match(1, Run(0, startedAfterS: 5), "b1").Kind);
        Assert.Equal(LedgerMatchKind.Loaded, n.Match(1, Run(0, startedAfterS: -100000), "b1").Kind);
        var n2 = new ProgramLedger("dev-1", Me);
        n2.AddDownload(1, Doc(), "b1", D0, serverOffset: null, prior: null, priorKnown: true);
        Assert.Equal(LedgerMatchKind.Matched, n2.Match(1, Run(0, startedAfterS: -100000), "b1").Kind);
    }

    [Fact]
    public void Mismatch_SupersedesTheEntry_ForGood()
    {
        var l = Ledger(out _);
        // 有人在触屏换了程序（5 步）并运行：作废，之后即使又是 4 步、由本机启动也不再按台账显示
        l.ObserveFrame(Frame(D0.AddSeconds(60), Ch(1, program: Run(0, count: 5, startedAfterS: 50, by: "local"))), "b1");
        var e = l.Entry(1)!;
        Assert.False(e.Valid);
        Assert.Equal("步骤数不同（设备 5 步 · 本机下发 4 步）", e.Superseded);
        Assert.Equal(LedgerMatchKind.NoEntry, l.Match(1, Run(0, startedAfterS: 120), "b1").Kind);
        Assert.Null(l.ProgramOf(1, Frame(D0.AddSeconds(130), Ch(1, program: Run(0, startedAfterS: 120))), "b1", Pr8));
        Assert.False(l.CanRun(1, null, "b1"));
        // 重新下发就好了
        l.AddDownload(1, Doc(), "b1", D0.AddSeconds(200), TimeSpan.Zero, Run(0, count: 5, startedAfterS: 50, by: "local"), true);
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, Run(0, count: 5, startedAfterS: 50, by: "local"), "b1").Kind);
    }

    [Fact]
    public void UnknownBootId_ObservesNothing()
    {
        var l = Ledger(out _);
        l.ObserveFrame(Frame(D0.AddSeconds(60), Ch(1, program: Run(0, count: 5))), null);   // hello 之前的帧：不判断、不作废
        Assert.True(l.Entry(1)!.Valid);
        Assert.Null(l.Tracker.Track(1));
        Assert.Null(l.ProgramOf(1, Frame(D0.AddSeconds(60), Ch(1, program: Run(0))), null, Pr8));
    }

    [Fact]
    public void Boot_SupersedesOtherBoots()
    {
        var l = Ledger(out _);
        l.AddDownload(2, Doc("b"), "b1", D0, TimeSpan.Zero, null, true);
        l.ObserveBoot("b1");
        Assert.True(l.Entry(1)!.Valid && l.Entry(2)!.Valid);
        l.ObserveBoot("b2");
        Assert.Equal("设备重启过（启动号不同）", l.Entry(1)!.Superseded);
        Assert.Equal("设备重启过（启动号不同）", l.Entry(2)!.Superseded);
        Assert.Equal("b2", l.BootId);
        Assert.Equal("b2", l.Tracker.BootId);
    }

    [Fact]
    public void Events_OtherClientsReplaceOrCopy()
    {
        var l = Ledger(out _);
        l.AddDownload(2, Doc("b"), "b1", D0, TimeSpan.Zero, null, true, opId: "op-2");
        l.AddDownload(3, Doc("c"), "b1", D0, TimeSpan.Zero, null, true, opId: "op-3");
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me, opId: "op-1");             // 本机自己的
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me);                             // 本机（客户端名）
        l.ObserveEvent("op.program.replace", "RV2 程序已更新（4 步）", "PC-2", level: "warn");          // 失败的命令
        l.ObserveEvent("op.program.run", "RV2 …", "PC-2");                                               // 不相关
        Assert.True(l.Entries.All(e => e.Valid));
        l.ObserveEvent("op.program.replace", "RV2 程序已更新（6 步）", "PC-2");
        Assert.Equal("程序已被 PC-2 替换", l.Entry(2)!.Superseded);
        Assert.True(l.Entry(1)!.Valid && l.Entry(3)!.Valid);
        l.ObserveEvent("op.program.copy", "已将 RV3 的 4 步程序应用到 2 个通道", "PC-2");               // 目标看不出来：除来源以外都作废
        Assert.False(l.Entry(1)!.Valid);
        Assert.True(l.Entry(3)!.Valid);
    }

    /// <summary>
    /// 事件的时刻（设备时钟）：重连后重新取回的历史（GET /events）里，别人在本机下发<b>之前</b>的替换不作废本机之后的下发（容差 2 s）；
    /// 之后的照样作废；不知道时刻、或条目没有设备时钟的下发时刻时照旧作废（宁可降级，R5）。
    /// </summary>
    [Fact]
    public void Events_OlderThanTheDownload_DoNotSupersede()
    {
        var l = Ledger(out var e, offset: TimeSpan.FromSeconds(3));                    // 设备时钟的下发时刻 = D0 + 3 s
        Assert.Equal(D0.AddSeconds(3), e.DownloadedAtDevice);
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（5 步）", "PC-2", "op-x", "info", D0.AddMinutes(-5));
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（5 步）", "PC-2", "op-y", "info", D0.AddSeconds(1.5));   // 容差内（≥ 下发 − 2 s）：算之后
        Assert.Equal("程序已被 PC-2 替换", l.Entry(1)!.Superseded);
        var l2 = Ledger(out _, offset: TimeSpan.FromSeconds(3));
        l2.ObserveEvent("op.program.copy", "已将 RV3 的 4 步程序应用到 2 个通道", "PC-2", "op-z", "info", D0.AddSeconds(-30));
        Assert.True(l2.Entry(1)!.Valid);
        l2.ObserveEvent("op.program.replace", "RV1 程序已更新（5 步）", "PC-2", "op-w", "info", null);
        Assert.False(l2.Entry(1)!.Valid);
        // 没有设备时钟的下发时刻（WS 还没给出时钟偏差）：按时刻判断不了 → 照旧作废
        var l3 = new ProgramLedger("dev-1", Me);
        l3.AddDownload(1, Doc(), "b1", D0, null, Prior, true);
        l3.ObserveEvent("op.program.replace", "RV1 程序已更新（5 步）", "PC-2", "op-v", "info", D0.AddMinutes(-5));
        Assert.False(l3.Entry(1)!.Valid);
    }

    // ———————————————————— 事件核对（复核 R5：断开期间别的客户端替换了程序） ————————————————————

    private static ParallelReactor.Services.EventRecord Ev(long seq, string kind = "op.channel.start", string? client = "PC-2", string message = "RV1 已启动",
        string? opId = null, int epoch = 1, string level = "info") =>
        new(seq, epoch, D0.AddSeconds(seq), "", level, kind, null, "remote", "RV1", client, opId, "远程命令", message);

    private static EventsPageDto Page(long after, long head, bool more, params ParallelReactor.Services.EventRecord[] items) =>
        new(1, head, after, items.Length, more, items);

    /// <summary>按序号核对：比下发早 / 核对过的不算；正好是下一条时核对位置往前推（不立即写盘）；跳号的照样核对但位置不动（Behind）；纪元不同作废。</summary>
    [Fact]
    public void Events_BySeq_CheckedPositionAdvances()
    {
        var l = Ledger(out var e);                                                      // 下发命令自己的事件序号 77
        Assert.Equal((77L, (int?)null), (e.CheckedSeq!.Value, e.EventEpoch));
        Assert.Equal(77, l.CatchUpAfter);
        // 序号比下发早的替换（别人在本机下发之前改的）不算 —— 不靠时钟
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（5 步）", "PC-2", "op-x", "info", D0.AddMinutes(5), seq: 76, epoch: 1);
        Assert.True(l.Entry(1)!.Valid);
        int v = l.Version;
        l.ObserveEvent("op.channel.start", "RV2 已启动", "PC-2", "op-y", "info", null, seq: 78, epoch: 1);
        Assert.Equal(78, l.Entry(1)!.CheckedSeq);
        Assert.True(l.CursorDirty);
        Assert.Equal(v, l.Version);                                                      // 只是往前推：不刷新页面、不写盘
        Assert.False(l.Behind);
        l.ObserveEvent("op.channel.start", "RV2 已启动", "PC-2", null, "info", null, seq: 80, epoch: 1);   // 79 还没收到
        Assert.Equal(78, l.Entry(1)!.CheckedSeq);
        Assert.True(l.Behind);
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", "PC-2", "op-z", "info", null, seq: 81, epoch: 1);   // 跳号的照样核对
        Assert.Equal("程序已被 PC-2 替换", l.Entry(1)!.Superseded);
        // 纪元不同（HMI 的事件记录重置过）：序号接不上 → 作废
        var k = new ProgramLedger("dev-1", Me);
        k.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opId: "op-1", opEventSeq: 10, eventEpoch: 1);
        k.ObserveEvent("op.channel.start", "RV2 已启动", "PC-2", null, "info", null, seq: 11, epoch: 2);
        Assert.Equal(ProgramLedger.EpochReason, k.Entry(1)!.Superseded);
        // 下发时没拿到命令的事件序号：从 WS 已经连续收到的位置起核对；两样都没有 → 重新连上时作废
        var u = new ProgramLedger("dev-1", Me);
        Assert.Equal(40, u.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, eventCursor: 40).CheckedSeq);
        u.AddDownload(2, Doc(), "b1", D0, TimeSpan.Zero, Prior, true);
        Assert.Equal(1, u.SupersedeUnchecked());
        Assert.Equal(ProgramLedger.UncheckedReason, u.Entry(2)!.Superseded);
        Assert.True(u.Entry(1)!.Valid);
    }

    /// <summary>
    /// 重新连上时续取（GET /events?afterSeq = 最早的核对位置，一页一页）：断开期间别人替换了 RV1 —— 隔着几百条别的事件也找得到（不再只看最近 200 条）；
    /// RV2 没被动过，核对到序号头、仍然有效。
    /// </summary>
    [Fact]
    public void CatchUp_Pages_FindTheReplacementWhileAway()
    {
        var l = Ledger(out _);                                                          // RV1：77
        l.AddDownload(2, Doc("b"), "b1", D0, TimeSpan.Zero, null, true, opId: "op-2", opEventSeq: 80);
        Assert.Equal(77, l.CatchUpAfter);
        var first = Enumerable.Range(78, 300).Select(i => Ev(i)).ToArray();            // 78–377：别的事件（含本机自己的 op-2 = 80）
        first[2] = Ev(80, "op.program.replace", ByMe, "RV2 程序已更新（4 步）", "op-2");
        Assert.True(l.ObservePage(Page(77, 700, true, first), 77));                      // 还有更多
        Assert.Equal((377L, 377L), (l.Entry(1)!.CheckedSeq!.Value, l.Entry(2)!.CheckedSeq!.Value));
        Assert.True(l.Entries.All(x => x.Valid));
        var second = Enumerable.Range(378, 323).Select(i => i == 650 ? Ev(i, "op.program.replace", "PC-2", "RV1 程序已更新（6 步）", "op-9") : Ev(i)).ToArray();
        Assert.False(l.ObservePage(Page(377, 700, false, second), 377));                 // 到序号头了
        Assert.Equal("程序已被 PC-2 替换", l.Entry(1)!.Superseded);
        Assert.True(l.Entry(2)!.Valid);
        Assert.Equal(700, l.Entry(2)!.CheckedSeq);
        Assert.False(l.Behind);
        // 读回台账文件的 JSON：核对位置与纪元都在
        var copy = new ProgramLedger("dev-1", Me);
        copy.LoadJson(l.ToJson());
        Assert.Equal(l.Entry(2), copy.Entry(2));
    }

    /// <summary>续取核对不全就作废：设备上缺了中间那段、序号纪元变了、什么也没取到而序号头在前面、请求失败；取到头了就照样有效。</summary>
    [Fact]
    public void CatchUp_Incomplete_Supersedes()
    {
        // 缺了 78–99（环形缓冲已经覆盖、落盘文件也没有）
        var gap = Ledger(out _);
        Assert.False(gap.ObservePage(Page(77, 120, false, Enumerable.Range(100, 21).Select(i => Ev(i)).ToArray()), 77));
        Assert.Equal(ProgramLedger.CatchUpFailedReason, gap.Entry(1)!.Superseded);
        // 序号纪元变了
        var ep = new ProgramLedger("dev-1", Me);
        ep.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opEventSeq: 77, eventEpoch: 1);
        Assert.False(ep.ObservePage(new EventsPageDto(2, 5, 77, 0, false, Array.Empty<ParallelReactor.Services.EventRecord>()), 77));
        Assert.Equal(ProgramLedger.EpochReason, ep.Entry(1)!.Superseded);
        // 一条也没取到，序号头却在前面
        var empty = Ledger(out _);
        Assert.False(empty.ObservePage(Page(77, 90, false), 77));
        Assert.Equal(ProgramLedger.CatchUpFailedReason, empty.Entry(1)!.Superseded);
        // 已经是序号头：没有要核对的，照样有效
        var head = Ledger(out _);
        Assert.False(head.ObservePage(Page(77, 77, false), 77));
        Assert.True(head.Entry(1)!.Valid);
        // 头在这一页之后又有了新事件：再取一页
        var more = Ledger(out _);
        Assert.True(more.ObservePage(Page(77, 82, false, Ev(78), Ev(79)), 77));
        Assert.Equal(79, more.Entry(1)!.CheckedSeq);
        // 请求失败 / 页数太多：还没核对完的（连上之后在核对中的）作废；这次连接上刚下发、一直连续核对着的不算（复核，见 M5ReviewLedgerTests）
        var fail = Ledger(out _);
        fail.BeginCheck();
        fail.FailCatchUp();
        Assert.Equal(ProgramLedger.CatchUpFailedReason, fail.Entry(1)!.Superseded);
        Assert.Null(fail.CatchUpAfter);
    }

    /// <summary>触屏启动本机下发的程序：照规则对上，程序记录带「触屏启动」，状态行写明（悬停写全「按本机下发的程序显示」）。</summary>
    [Fact]
    public void TouchStart_IsMarkedOnTheRecordAndStatusLine()
    {
        var l = Ledger(out _);
        var f = Frame(D0.AddSeconds(60), Ch(1, temp: 100, sp: 120, atMode: 0, program: Run(0, by: "local", message: "01/04 升温")));
        l.ObserveFrame(f, "b1");
        var rec = l.ProgramOf(1, f, "b1", Pr8)!;
        Assert.True(rec.StartedByTouch);
        var pr = Progress.Of(rec, new ProgressContext(D0.AddSeconds(60).ToLocalTime(), Model: Pr8, ChannelState: StateKey.Heat))!;
        var line = ProgramPanelText.Status(rec, pr, true, "", D0.AddSeconds(60).ToLocalTime())!;
        Assert.EndsWith(" · 触屏启动", line.Full, StringComparison.Ordinal);
        Assert.EndsWith(" · 触屏启动 · 按本机下发的程序显示", line.Title, StringComparison.Ordinal);
        Assert.Equal("运行中 · " + line.Full, line.Long);
        // 本机启动的没有这句
        var m = Ledger(out _);
        var g = Frame(D0.AddSeconds(60), Ch(1, temp: 100, sp: 120, atMode: 0, program: Run(0)));
        m.ObserveFrame(g, "b1");
        var mine = m.ProgramOf(1, g, "b1", Pr8)!;
        Assert.False(mine.StartedByTouch);
        Assert.DoesNotContain("触屏启动", ProgramPanelText.Status(mine, Progress.Of(mine, new ProgressContext(D0.AddSeconds(60).ToLocalTime(), Model: Pr8))!, true, "", D0.AddSeconds(60).ToLocalTime())!.Title,
            StringComparison.Ordinal);
    }

    /// <summary>WS 先报 HMI 重启（新的启动号还不知道）：有效条目全部作废、步骤计时清空；之后认新的启动号。</summary>
    [Fact]
    public void Restart_BeforeTheNewBootIsKnown()
    {
        var l = Ledger(out _);
        l.ObserveFrame(Frame(D0.AddSeconds(40), Ch(1, program: Run(0))), "b1");
        Assert.NotNull(l.Tracker.Track(1));
        int v = l.Version;
        l.ObserveRestart();
        Assert.Equal("设备重启过（启动号不同）", l.Entry(1)!.Superseded);
        Assert.Null(l.Tracker.Track(1));
        Assert.Null(l.BootId);
        Assert.True(l.Version > v);
        Assert.Equal(LedgerMatchKind.NoEntry, l.Match(1, Run(0), "b1").Kind);
        l.ObserveBoot("b2");
        Assert.Equal("b2", l.BootId);
        v = l.Version;
        l.ObserveRestart();
        l.ObserveRestart();
        Assert.True(l.Version >= v);
    }

    [Fact]
    public void ProgramOf_Running_WithStepTimesAndCountdown()
    {
        var l = Ledger(out _);
        var hist = new ChannelHistory();
        // 第 0 步（升温）从 D0+30 s 开始；PC 一直连着，每 1.2 s 一帧
        DateTime t = D0.AddSeconds(31);
        for (; t < D0.AddSeconds(900); t = t.AddSeconds(1.2))
            l.ObserveFrame(Frame(t, Ch(1, temp: 100, sp: 120, atMode: 0, program: Run(0, message: "01/04 升温 · 升温 100.0℃ → 120℃"))), "b1");
        var t1 = t;   // 换到第 1 步（充压）
        l.ObserveFrame(Frame(t1, Ch(1, temp: 120, psi: 10, atMode: 0, program: Run(1, message: "02/04 充压 · 充压 0.069 / 0.350 MPa"))), "b1");
        var t2 = t1.AddSeconds(240);
        for (var u = t1.AddSeconds(1.2); u < t2; u = u.AddSeconds(1.2))
            l.ObserveFrame(Frame(u, Ch(1, temp: 120, psi: 40, atMode: 0, program: Run(1))), "b1");
        var f = Frame(t2, Ch(1, temp: 120, psi: 50.8, pSp: 50.76, atMode: 0, program: Run(2, message: "03/04 反应 · 反应中 · 剩 71.9 分")));
        l.ObserveFrame(f, "b1");
        var rec = l.ProgramOf(1, f, "b1", Pr8, hist)!;
        Assert.Equal(ProgramStatus.Running, rec.Status);
        Assert.Equal(2, rec.StepIndex);
        Assert.Equal(D0.AddSeconds(30).ToLocalTime(), rec.StartedAt);
        Assert.Equal(2, rec.Log.Count);                                          // 第 0、1 步都看到了开始和结束
        Assert.Equal(D0.AddSeconds(30).ToLocalTime(), rec.Log[0].T0);
        Assert.Equal(rec.Log[0].T1, rec.Log[1].T0);
        Assert.True(Math.Abs((rec.Log[0].T1 - t1.ToLocalTime()).TotalSeconds) <= 1.2);
        var pr = Progress.Of(rec, new ProgressContext(t2.ToLocalTime(), Model: Pr8, ChannelState: StateKey.React))!;
        Assert.Equal(72, pr.StepRemainMin);                                      // 71.9 → 向上取整
        Assert.Equal("第 3/4 步 · 剩 72 min", Progress.Text(pr));
        Assert.Equal(SegmentState.Done, Progress.StepState(rec, 1));
        Assert.Equal("当前 · 剩 72 min", Progress.StepMark(rec, 2, pr));
        Assert.Equal(4, Progress.Segments(rec, new ProgressContext(t2.ToLocalTime(), Model: Pr8)).Count);   // 2 段已完成 + 当前 + 结束
    }

    [Fact]
    public void ProgramOf_MidRunConnect_Done_Aborted()
    {
        var l = Ledger(out _);
        // PC 中途连上：第 2 步（反应），开始时刻不知道 —— 按 HMI 倒计时反推
        var f = Frame(D0.AddMinutes(60), Ch(1, temp: 120, psi: 50.8, atMode: 0, program: Run(2, message: "03/04 反应 · 反应中 · 剩 30 分")));
        l.ObserveFrame(f, "b1");
        var rec = l.ProgramOf(1, f, "b1", Pr8)!;
        Assert.Empty(rec.Log);
        Assert.Equal(D0.AddMinutes(60).AddMinutes(-(72 - 30)).ToLocalTime(), rec.StepStartedAt);
        Assert.Equal(30, Progress.Of(rec, new ProgressContext(D0.AddMinutes(60).ToLocalTime(), Model: Pr8))!.StepRemainMin);
        // 结束
        var fd = Frame(D0.AddMinutes(200), Ch(1, temp: 48, program: Run(3, "completed")));
        l.ObserveFrame(fd, "b1");
        var done = l.ProgramOf(1, fd, "b1", Pr8)!;
        Assert.Equal(ProgramStatus.Done, done.Status);
        Assert.Equal(3, done.StepIndex);
        Assert.Equal("已结束 · 共 4 步", Progress.Text(Progress.Of(done, new ProgressContext(D0.AddMinutes(200).ToLocalTime(), Model: Pr8))));
        // 中止 / 报警中止
        var l2 = Ledger(out _);
        var fa = Frame(D0.AddMinutes(5), Ch(1, program: Run(1, "aborted")));
        l2.ObserveFrame(fa, "b1");
        Assert.Equal(ProgramStatus.Aborted, l2.ProgramOf(1, fa, "b1", Pr8)!.Status);
        Assert.Equal(ProgramStatus.Failed, ProgramLedger.StatusOf(Run(1, "failed")));
        Assert.Equal(ProgramStatus.Running, ProgramLedger.StatusOf(Run(1, "waitingGas")));
        Assert.Equal(ProgramStatus.Running, ProgramLedger.StatusOf(Run(-1, "preparing")));
        Assert.Equal(ProgramStatus.Running, ProgramLedger.StatusOf(Run(3, "finishing")));
    }

    [Fact]
    public void ProgramOf_UnknownStepType_Degrades()
    {
        var l = new ProgramLedger("dev-1", Me);
        var doc = Doc();
        doc.Steps.Insert(1, new ProgramStepDoc("leak"));      // 不认识的步骤类型：序号对不上，不按台账显示
        l.AddDownload(1, doc, "b1", D0, TimeSpan.Zero, null, true);
        var f = Frame(D0.AddSeconds(60), Ch(1, program: Run(0, count: 5)));
        Assert.Equal(LedgerMatchKind.Matched, l.Match(1, f.Channels[0].Program, "b1").Kind);
        Assert.Null(l.ProgramOf(1, f, "b1", Pr8));
    }

    [Fact]
    public void FallbackText()
    {
        Assert.Equal("RV3 程序", HmiProgramText.Name(3));
        Assert.Equal("第 2/5 步", HmiProgramText.Steps(Prog(1, 5)));
        Assert.Equal("第 1/5 步", HmiProgramText.Steps(Prog(-1, 5, "preparing")));
        Assert.Equal("第 2/5 步 · 等待气路", HmiProgramText.Steps(Prog(1, 5, "waitingGas")));
        Assert.Equal("已结束 · 共 5 步", HmiProgramText.Steps(Prog(4, 5, "completed")));
        Assert.Equal("已中止 · 第 3/5 步", HmiProgramText.Steps(Prog(2, 5, "aborted")));
        Assert.Equal("报警中止 · 第 3/5 步", HmiProgramText.Steps(Prog(2, 5, "failed")));
        Assert.Equal("RV3 程序 · 第 2/5 步 · 02/05 升压 · 充压 1.2 / 3.5 MPa", HmiProgramText.Full(3, Prog(1, 5, message: " 02/05 升压 · 充压 1.2 / 3.5 MPa ")));
        Assert.Equal("RV3 程序 · 已结束 · 共 5 步", HmiProgramText.Full(3, Prog(4, 5, "completed", message: "5 步全部完成")));
    }

    [Fact]
    public void File_AtomicWrite_RoundTrip_HashCheck_BadFile()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff");
            var l = ProgramLedger.Open(dir, id, Me);
            Assert.True(l.Persistent);
            Assert.Equal(Path.Combine(dir, "programs", id.ToString("D") + ".json"), l.FilePath);
            l.AddDownload(1, Doc(), "b1", D0, TimeSpan.FromSeconds(1), Prior, true, opId: "op-1", opEventSeq: 5);
            l.ObserveFrame(Frame(D0.AddSeconds(40), Ch(1, program: Run(0))), "b1");
            l.ObserveFrame(Frame(D0.AddSeconds(41.2), Ch(1, program: Run(1))), "b1");
            Assert.True(File.Exists(l.FilePath));
            Assert.Empty(Directory.GetFiles(Path.Combine(dir, "programs"), "*.tmp", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(Path.Combine(dir, "programs"), ".*.tmp")));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(l.FilePath!));

            var back = ProgramLedger.Open(dir, id, Me);
            var e = back.Entry(1)!;
            Assert.True(e.Valid);
            Assert.Equal(l.Entry(1), e);
            Assert.Equal(D0.AddSeconds(40.6), back.Tracker.Track(1)!.StartOf(1));
            Assert.Equal("b1", back.BootId);
            Assert.Equal(LedgerMatchKind.Matched, back.Match(1, Run(1), "b1").Kind);

            // 文件被改过（摘要对不上）：这一条作废
            var json = File.ReadAllText(l.FilePath!).Replace("\"time\": 72", "\"time\": 73");
            File.WriteAllText(l.FilePath!, json);
            var tampered = ProgramLedger.Open(dir, id, Me);
            Assert.Equal("台账内容与摘要不符", tampered.Entry(1)!.Superseded);

            // 坏文件：改名 .bad（原处不再有），从空的开始，原因在 LoadError；之后照常写新文件（读不了的情形见 M5ReviewLedgerTests）
            File.WriteAllText(l.FilePath!, "{ not json");
            var bad = ProgramLedger.Open(dir, id, Me);
            Assert.Empty(bad.Entries);
            Assert.Contains("已改名为 .bad", bad.LoadError, StringComparison.Ordinal);
            Assert.False(bad.LoadFailed);
            Assert.True(File.Exists(l.FilePath + ".bad"));
            Assert.False(File.Exists(l.FilePath));
            Assert.Equal("{ not json", File.ReadAllText(l.FilePath + ".bad"));
            Assert.True(bad.Save());
            Assert.True(File.Exists(l.FilePath));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Memory_LedgerNeverWrites()
    {
        var l = new ProgramLedger("dev-1", Me);
        l.AddDownload(1, Doc(), "b1", D0, null, null, true);
        Assert.False(l.Save());
        Assert.Null(l.FilePath);
        var copy = new ProgramLedger("dev-1", Me);
        copy.LoadJson(l.ToJson());
        Assert.Equal(l.Entry(1), copy.Entry(1));
    }
}
