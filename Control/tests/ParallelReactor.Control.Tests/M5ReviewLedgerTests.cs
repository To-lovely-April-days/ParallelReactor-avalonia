using System;
using System.IO;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Models;
using Xunit;
using static ParallelReactor.Control.Tests.LiveFrames;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（程序台账，Core）：
/// <list type="bullet">
/// <item>c1-live-2：设备时钟的下发时刻取自下发命令自己的事件（实时、补核对、续取都认）—— 几天前 hello 量的时钟差差了 6 s 时，本机刚启动的执行不再被当成「待运行」。</item>
/// <item>c1-live-4：台账文件读不了（权限 / IO）不当成坏了：文件不动、这次运行不写盘（以前之后第一次写盘就用空台账盖掉了好文件）；内容坏了才改名 .bad；更新版本的不动。</item>
/// <item>c1-live-5：续取失败只作废还落在后面的条目：这次连接上刚下发的、已经核对到头的照样有效。</item>
/// <item>c1-live-6：PUT 回来之前 WS 已经送到的事件对新条目补核对 —— 下一条实时事件不再看起来像跳号；真跳号时只有落在后面的条目先不认。</item>
/// </list>
/// </summary>
public class M5ReviewLedgerTests
{
    private static readonly DateTime D0 = new(2026, 11, 8, 23, 0, 0, DateTimeKind.Utc);
    private static readonly ModelProfile Pr8 = ModelProfiles.Embedded.Get("PR8")!;
    private static readonly ChannelProgramSnap Prior = Prog(2, 3, "completed", D0.AddHours(-2), "local");

    private static ProgramDoc Doc(string name = "加氢测试-01") => new ProgramDoc { Name = name }
        .Add(StepType.Heat, new { temp = 120, wait = 10 })
        .Add(StepType.Press, new { gas = "A", pres = 50.76 })
        .Add(StepType.React, new { time = 72 })
        .Add(StepType.End, new { cool = 50, mode = "vent" });

    private static ParallelReactor.Services.EventRecord Ev(long seq, string kind = "op.channel.start", string? opId = null, DateTime? ts = null) =>
        new(seq, 1, ts ?? D0.AddSeconds(seq), "", "info", kind, null, "remote", "RV2", "PC-2", opId, "远程命令", "RV2 已启动");

    // ———————————————————— c1-live-2 ————————————————————

    /// <summary>
    /// 复核的探针：真实时钟差 0，hello 几天前量的 +6 s（HMI 的钟之后慢了 6 s）→ 估的下发时刻晚 6 s，下发后 3 s 本机启动的执行被当成下发之前的（「待运行」整段不变）。
    /// 看到下发命令自己的事件（设备时钟 D0）之后下发时刻改成 D0：对上、按台账显示运行中；写盘的也是改过的。
    /// </summary>
    [Fact]
    public void StaleOffset_OwnOpEvent_GivesTheDeviceClockDownloadTime()
    {
        var l = new ProgramLedger("dev-1", Me);
        l.AddDownload(1, Doc(), "b1", D0, TimeSpan.FromSeconds(6), Prior, true, opId: "op-1", opEventSeq: 100, eventEpoch: 1);
        Assert.Equal(D0.AddSeconds(6), l.Entry(1)!.DownloadedAtDevice);
        var run = Prog(0, 4, "running", D0.AddSeconds(3), ByMe, "剩 45 分");
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, run, "b1").Kind);               // 修之前一直停在这里
        int v = l.Version;
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me, "op-1", "info", D0, 100, 1);
        Assert.Equal(D0, l.Entry(1)!.DownloadedAtDevice);
        Assert.True(l.Version > v);
        Assert.Equal(LedgerMatchKind.Matched, l.Match(1, run, "b1").Kind);
        var f = Frame(D0.AddSeconds(4), Ch(1, 60, 120, program: run, state: "heating"));
        l.ObserveFrame(f, "b1");
        Assert.True(l.Entry(1)!.Valid);
        Assert.Equal(ProgramStatus.Running, l.ProgramOf(1, f, "b1", Pr8)?.Status);
        var copy = new ProgramLedger("dev-1", Me);
        copy.LoadJson(l.ToJson());
        Assert.Equal(D0, copy.Entry(1)!.DownloadedAtDevice);
    }

    /// <summary>自己的事件还可以：在 PUT 回来之前就到了（补核对时认）、只有序号对得上（没有 opId）、在续取的一页里；别人的、别的纪元的、没有时刻的不算。</summary>
    [Fact]
    public void OwnOpEvent_IsFoundEveryWay()
    {
        // PUT 回来之前 WS 就送到了
        var a = new ProgramLedger("dev-1", Me);
        a.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me, "op-1", "info", D0, 100, 1);
        a.AddDownload(1, Doc(), "b1", D0.AddSeconds(0.4), TimeSpan.FromSeconds(-9), Prior, true, opId: "op-1", opEventSeq: 100, eventEpoch: 1);
        Assert.Equal(D0, a.Entry(1)!.DownloadedAtDevice);
        // 只有序号（没有 opId 的旧 HMI）
        var b = new ProgramLedger("dev-1", Me);
        b.AddDownload(1, Doc(), "b1", D0, TimeSpan.FromSeconds(6), Prior, true, opEventSeq: 100, eventEpoch: 1);
        b.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me, null, "info", D0, 100, 1);
        Assert.Equal(D0, b.Entry(1)!.DownloadedAtDevice);
        // 续取的一页里（另一路的核对位置更早，页里有 RV1 下发命令的事件）
        var c = new ProgramLedger("dev-1", Me);
        c.AddDownload(2, Doc("b"), "b1", D0.AddMinutes(-10), TimeSpan.Zero, null, true, opId: "op-2", opEventSeq: 90, eventEpoch: 1);
        c.AddDownload(1, Doc(), "b1", D0, TimeSpan.FromSeconds(6), Prior, true, opId: "op-1", opEventSeq: 100, eventEpoch: 1);
        c.BeginCheck();
        var items = new ParallelReactor.Services.EventRecord[11];
        for (int i = 0; i < 11; i++) items[i] = Ev(91 + i);
        items[9] = Ev(100, "op.program.replace", "op-1", D0);
        Assert.False(c.ObservePage(new EventsPageDto(1, 101, 90, 11, false, items), 90));
        Assert.Equal(D0, c.Entry(1)!.DownloadedAtDevice);
        Assert.True(c.Entry(1)!.Valid && c.Entry(2)!.Valid);
        // 不算：别人的命令（opId、序号都不同）、没有时刻
        var d = new ProgramLedger("dev-1", Me);
        d.AddDownload(1, Doc(), "b1", D0, TimeSpan.FromSeconds(6), Prior, true, opId: "op-1", opEventSeq: 100, eventEpoch: 1);
        d.ObserveEvent("op.channel.start", "RV2 已启动", "PC-2", "op-9", "info", D0.AddSeconds(1), 101, 1);
        d.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me, "op-1", "info", null, 100, 1);
        Assert.Equal(D0.AddSeconds(6), d.Entry(1)!.DownloadedAtDevice);
        // 别的纪元的同一个序号：不是它（纪元变了，条目照旧作废）
        var g = new ProgramLedger("dev-1", Me);
        g.AddDownload(1, Doc(), "b1", D0, TimeSpan.FromSeconds(6), Prior, true, opEventSeq: 100, eventEpoch: 1);
        g.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me, null, "info", D0.AddSeconds(-50), 100, 2);
        Assert.Equal((D0.AddSeconds(6), ProgramLedger.EpochReason), (g.Entry(1)!.DownloadedAtDevice!.Value, g.Entry(1)!.Superseded));
    }

    // ———————————————————— c1-live-6 ————————————————————

    /// <summary>
    /// 复核的探针：UI 先处理了 WS 的 500（下发命令自己的事件）和 501（不相关），PUT 的续体才记下下发 —— 以前新条目从 500 起，下一条实时的 502 看起来像跳号，
    /// 全部条目进核对、续取一遍。现在补核对到 501（下发时刻取 500 的时刻），502 照常往前推，不落后、不进核对。
    /// </summary>
    [Fact]
    public void EventsBeforeThePutContinuation_AreReplayed()
    {
        var l = new ProgramLedger("dev-1", Me);
        l.ObserveEvent("op.program.replace", "RV1 程序已更新（4 步）", Me, "op-new", "info", D0, 500, 1);
        l.ObserveEvent("valve.frame", "阀门帧", null, null, "info", D0.AddSeconds(0.2), 501, 1);
        var e = l.AddDownload(1, Doc(), "b1", D0.AddSeconds(0.5), TimeSpan.Zero, Prior, true, opId: "op-new", opEventSeq: 500, eventEpoch: 1);
        Assert.Equal((501L, D0), (e.CheckedSeq!.Value, e.DownloadedAtDevice!.Value));
        Assert.False(l.Behind);
        l.ObserveEvent("op.channel.start", "RV2 已启动", "PC-别人", "op-y", "info", null, 502, 1);
        Assert.Equal(502, l.Entry(1)!.CheckedSeq);
        Assert.False(l.Behind);
        Assert.False(l.BeginCheckBehind());
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(1, Prior, "b1").Kind);
        Assert.True(l.CanRun(1, Prior, "b1"));
        // 补核对时别的客户端的替换照样作废新条目（本机持有控制权时不会有，这里只看规则一致）
        var x = new ProgramLedger("dev-1", Me);
        x.ObserveEvent("op.program.replace", "RV1 程序已更新（5 步）", "PC-别人", "op-x", "info", D0, 501, 1);
        x.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opId: "op-new", opEventSeq: 500, eventEpoch: 1);
        Assert.Equal("程序已被 PC-别人 替换", x.Entry(1)!.Superseded);
        // 旧纪元的缓冲不补
        var y = new ProgramLedger("dev-1", Me);
        y.ObserveEvent("op.channel.start", "RV2 已启动", "PC-2", null, "info", D0, 501, 1);
        y.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opEventSeq: 500, eventEpoch: 2);
        Assert.Equal((500L, true), (y.Entry(1)!.CheckedSeq!.Value, y.Entry(1)!.Valid));
    }

    /// <summary>真跳号时只有落在后面的条目先不认（以前全部有效条目都进核对）；续取补上之后照常；续取失败只作废落在后面的。</summary>
    [Fact]
    public void RealGap_OnlyTheEntryBehindIsHeldBack()
    {
        // RV2：从文件读回、核对到 100；之后连续收到 101–103
        var l = new ProgramLedger("dev-1", Me);
        l.AddDownload(2, Doc("b"), "b1", D0, TimeSpan.Zero, null, true, opId: "op-2", opEventSeq: 100, eventEpoch: 1);
        for (long s = 101; s <= 103; s++) l.ObserveEvent("op.channel.start", "RV3 已启动", "PC-2", null, "info", D0.AddSeconds(s), s, 1);
        // RV1：下发时没拿到命令的事件序号，从 WS 的位置 95 起 —— 补核对时 96–100 不在缓冲里：落在后面
        l.AddDownload(1, Doc(), "b1", D0.AddSeconds(104), TimeSpan.Zero, Prior, true, eventEpoch: 1, eventCursor: 95);
        Assert.True(l.Behind);
        Assert.True(l.BeginCheckBehind());
        Assert.True(l.IsChecking(1));
        Assert.False(l.IsChecking(2));
        Assert.Equal(LedgerMatchKind.Loaded, l.Match(2, null, "b1").Kind);
        Assert.Equal(LedgerMatchKind.Checking, l.Match(1, null, "b1").Kind);
        // 续取补上：照常
        var items = new ParallelReactor.Services.EventRecord[8];
        for (int i = 0; i < 8; i++) items[i] = Ev(96 + i);
        Assert.False(l.ObservePage(new EventsPageDto(1, 103, 95, 8, false, items), 95));
        Assert.True(l.EndCheck());
        Assert.False(l.Behind);
        Assert.Equal((103L, 103L), (l.Entry(1)!.CheckedSeq!.Value, l.Entry(2)!.CheckedSeq!.Value));
        // 同样落在后面、续取失败：只有它作废
        var k = new ProgramLedger("dev-1", Me);
        k.AddDownload(2, Doc("b"), "b1", D0, TimeSpan.Zero, null, true, opEventSeq: 100, eventEpoch: 1);
        for (long s = 101; s <= 103; s++) k.ObserveEvent("op.channel.start", "RV3 已启动", "PC-2", null, "info", null, s, 1);
        k.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, eventEpoch: 1, eventCursor: 95);
        k.BeginCheckBehind();
        k.FailCatchUp();
        Assert.Equal(ProgramLedger.CatchUpFailedReason, k.Entry(1)!.Superseded);
        Assert.True(k.Entry(2)!.Valid);
    }

    // ———————————————————— c1-live-5 ————————————————————

    /// <summary>
    /// 复核的探针：重新连上（RV1 在核对中）、续取还在路上时下发了 RV2 → 续取超时：只作废 RV1，RV2（这次连接上下发的）照样「已下发 · 待运行」、能「启动」。
    /// 页数超限时已经核对到这次续取取到过的序号头的条目同样不作废。
    /// </summary>
    [Fact]
    public void FailCatchUp_OnlyEntriesStillBehind()
    {
        var l = new ProgramLedger("dev-1", Me);
        l.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opId: "op-1", opEventSeq: 100, eventEpoch: 1);
        l.ObserveBoot("b1");
        l.BeginCheck();
        Assert.True(l.IsChecking(1));
        l.AddDownload(2, Doc("RV2 程序"), "b1", D0.AddHours(1), TimeSpan.Zero, null, true, opId: "op-2", opEventSeq: 900, eventEpoch: 1);
        Assert.Equal((false, LedgerMatchKind.Loaded), (l.IsChecking(2), l.Match(2, null, "b1").Kind));
        l.FailCatchUp();
        Assert.Equal(ProgramLedger.CatchUpFailedReason, l.Entry(1)!.Superseded);
        Assert.True(l.Entry(2)!.Valid);
        Assert.True(l.CanRun(2, null, "b1"));

        // 两条都在核对：取到第一页（头 = 500）后失败 —— 已经核对到 500 的 RV2 不算落在后面
        var h = new ProgramLedger("dev-1", Me);
        h.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opEventSeq: 100, eventEpoch: 1);
        h.AddDownload(2, Doc("b"), "b1", D0, TimeSpan.Zero, null, true, opEventSeq: 500, eventEpoch: 1);
        h.BeginCheck();
        var items = new ParallelReactor.Services.EventRecord[200];
        for (int i = 0; i < 200; i++) items[i] = Ev(101 + i);
        Assert.True(h.ObservePage(new EventsPageDto(1, 500, 100, 200, true, items), 100));
        Assert.Equal(300, h.Entry(1)!.CheckedSeq);
        h.FailCatchUp(head: 500);
        Assert.Equal(ProgramLedger.CatchUpFailedReason, h.Entry(1)!.Superseded);
        Assert.True(h.Entry(2)!.Valid);
        // 一页也没取到（不知道头）：在核对的都算落在后面
        var n = new ProgramLedger("dev-1", Me);
        n.AddDownload(2, Doc("b"), "b1", D0, TimeSpan.Zero, null, true, opEventSeq: 500, eventEpoch: 1);
        n.BeginCheck();
        n.FailCatchUp();
        Assert.False(n.Entry(2)!.Valid);
    }

    // ———————————————————— c1-live-4 ————————————————————

    /// <summary>
    /// 复核的探针：台账文件一时读不了（权限 000）—— 以前当成坏了：从空的开始，第一次连上（ObserveBoot 写盘）就用空台账盖掉了好文件，而且没有提示。
    /// 现在：不改名、不写盘（好文件原样留着），原因在 LoadError；内存里什么都没记时再读一次就回来了，之后照常写盘。
    /// </summary>
    [Fact]
    public void UnreadableFile_IsLeftAlone_NeverOverwritten()
    {
        if (OperatingSystem.IsWindows()) return;
        var dir = Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        string? path = null;
        try
        {
            var a = ProgramLedger.Open(dir, id, Me);
            a.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opId: "op-1", opEventSeq: 100, eventEpoch: 1);
            a.AddDownload(2, Doc("RV2 程序"), "b1", D0, TimeSpan.Zero, Prior, true, opId: "op-2", opEventSeq: 101, eventEpoch: 1);
            path = a.FilePath!;
            var bytes = File.ReadAllBytes(path);
            File.SetUnixFileMode(path, UnixFileMode.None);
            if (Readable(path)) return;                                                   // 以 root 运行：权限挡不住，没法测
            var b = ProgramLedger.Open(dir, id, Me);
            Assert.True(b.LoadFailed);
            Assert.StartsWith("程序台账读取失败：", b.LoadError, StringComparison.Ordinal);
            Assert.EndsWith(" · 本次不写台账文件", b.LoadError, StringComparison.Ordinal);
            Assert.Empty(b.Entries);
            Assert.False(File.Exists(path + ".bad"));
            b.ObserveBoot("b1");                                                          // 第一次连上：以前就在这里写盘
            Assert.False(b.Save());                                                       // 改名照样能盖掉 000 的文件：不写
            Assert.Null(b.SaveError);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            // 内存里已经记了东西：不再读（读回来会替换掉），照样不写
            var c = new ProgramLedger(id.ToString("D"), Me, path);
            File.SetUnixFileMode(path, UnixFileMode.None);
            c.Load();
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            c.AddDownload(3, Doc("RV3 程序"), "b1", D0, TimeSpan.Zero, null, true, opEventSeq: 102, eventEpoch: 1);
            Assert.False(c.RetryLoad());
            Assert.True(c.LoadFailed);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            // 什么都没记：再读一次就回来了，之后照常写盘
            Assert.True(b.RetryLoad());
            Assert.False(b.LoadFailed);
            Assert.Null(b.LoadError);
            Assert.Equal(2, b.Entries.Count);
            Assert.True(b.Entry(1)!.Valid);
            b.Supersede(2, "测试");
            Assert.Contains("\"superseded\": \"测试\"", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            if (path is not null && File.Exists(path)) try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>更新版本的软件写的台账：不认识，但也不是坏了 —— 不改名、不写盘；内容坏了才改名 .bad（见 LedgerMatchTests 的坏文件）。</summary>
    [Fact]
    public void NewerVersionFile_IsLeftAlone()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-tmp", Guid.NewGuid().ToString("N"));
        try
        {
            var id = Guid.NewGuid();
            var path = ProgramLedger.PathFor(dir, id);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string newer = "{ \"version\": 9, \"entries\": [] }";
            File.WriteAllText(path, newer);
            var l = ProgramLedger.Open(dir, id, Me);
            Assert.True(l.LoadFailed);
            Assert.Equal("程序台账是更新版本的软件写的（版本 9） · 本次不写台账文件", l.LoadError);
            l.AddDownload(1, Doc(), "b1", D0, TimeSpan.Zero, Prior, true, opEventSeq: 5);
            Assert.Equal(newer, File.ReadAllText(path));
            Assert.False(File.Exists(path + ".bad"));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static bool Readable(string path)
    {
        try { using var s = File.OpenRead(path); return true; }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
    }
}
