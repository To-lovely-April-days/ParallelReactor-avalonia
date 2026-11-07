using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>事件存储：seq 跨重启连续、JSONL 落盘、环形缓冲重同步、kind 推导（设计稿 §6.4 / §8.4 / §9.1 K1 K2）。</summary>
public class EventStoreTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "pr-events-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset T0 = new(2026, 11, 6, 16, 12, 30, 120, TimeSpan.FromHours(8));

    [Fact]
    public async Task Append_AssignsMonotonicSeq_WritesJsonl_AndRaisesAppended()
    {
        var dir = Tmp();
        var store = new EventStore(dir);
        EventRecord? seen = null;
        store.Appended += r => seen = r;
        var a = store.Append(T0, "err", "RV3", "超压 · 联锁动作", "压力 515 psi");
        var b = store.Append(T0.AddSeconds(1), "info", "系统", "程序启动", "数据目录 x");
        Assert.Equal(1, a.Seq);
        Assert.Equal(2, b.Seq);
        Assert.Equal(2, store.Head);
        Assert.Same(b, seen);
        Assert.Equal("alarm.overPressure", a.Kind);
        Assert.Equal(3, a.Channel);
        Assert.Equal("system.boot", b.Kind);
        Assert.Null(b.Channel);
        Assert.Equal(DateTimeKind.Utc, a.Ts.Kind);
        Assert.Equal(new DateTime(2026, 11, 6, 8, 12, 30, 120, DateTimeKind.Utc), a.Ts);
        Assert.Equal("2026-11-06T16:12:30.120+08:00", a.TsLocal);
        await store.StopAsync();

        var file = Path.Combine(dir, "events", "events-20261106.jsonl");
        Assert.True(File.Exists(file));
        var lines = File.ReadAllLines(file);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"seq\":1", lines[0]);
        Assert.Contains("超压", lines[0]);   // 中文不转义
        Assert.Equal("2", File.ReadAllText(Path.Combine(dir, "events", ".seq")).Trim());
        var parsed = EventStore.TryParse(lines[1]);
        Assert.NotNull(parsed);
        Assert.Equal(b with { }, parsed);
    }

    [Fact]
    public async Task Seq_ContinuesAcrossRestart_FromSeqFileOrTail()
    {
        var dir = Tmp();
        var s1 = new EventStore(dir);
        for (int i = 0; i < 5; i++) s1.Append(T0.AddSeconds(i), "info", "系统", "标记", "m" + i);
        await s1.StopAsync();

        var s2 = new EventStore(dir);
        Assert.Equal(5, s2.Head);
        Assert.Equal(6, s2.Append(T0.AddMinutes(1), "info", "系统", "标记", "again").Seq);
        await s2.StopAsync();

        // 删掉 .seq 后仍能从 JSONL 尾部恢复
        File.Delete(Path.Combine(dir, "events", ".seq"));
        var s3 = new EventStore(dir);
        Assert.Equal(6, s3.Head);
        await s3.StopAsync();
    }

    [Fact]
    public async Task CorruptJsonl_UsesMaxParsableSeqPlusOne()
    {
        var dir = Tmp();
        var ev = Path.Combine(dir, "events");
        Directory.CreateDirectory(ev);
        var good = new EventStore(dir);
        good.Append(T0, "info", "系统", "标记", "a");
        good.Append(T0, "info", "系统", "标记", "b");
        await good.StopAsync();
        var file = Path.Combine(ev, "events-20261106.jsonl");
        File.AppendAllText(file, "{\"seq\":99,\"garbage\n{\"seq\":7,\"seqEpoch\":1,\"ts\":\"2026-11-06T08:00:00Z\",\"tsLocal\":\"x\",\"level\":\"info\",\"kind\":\"k\",\"channel\":null,\"source\":\"system\",\"origin\":\"系统\",\"client\":null,\"opId\":null,\"title\":\"t\",\"message\":\"m\"}\n{half");
        File.WriteAllText(Path.Combine(ev, ".seq"), "not a number");
        var s = new EventStore(dir);
        Assert.Equal(7, s.Head);
        Assert.Equal(8, s.Append(T0, "info", "系统", "标记", "c").Seq);
        await s.StopAsync();
        // 半行被收尾后，新行独立成行
        var lines = File.ReadAllLines(file).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
        Assert.Contains(lines, l => l.Contains("\"seq\":8") && l.StartsWith("{\"seq\":8"));
    }

    [Fact]
    public async Task Append_Concurrent_KeepsSeqOrder_InRing_File_AndAppended()
    {
        // UI 线程与 Kestrel 线程并发 Append：Appended 顺序、环、JSONL 行序都必须严格按 seq（否则客户端会把后到的小 seq 当重复丢掉）
        var dir = Tmp();
        var store = new EventStore(dir);
        var seen = new List<long>();
        var gate = new object();
        store.Appended += r => { lock (gate) seen.Add(r.Seq); };
        const int N = 3000;
        Parallel.For(0, N, new ParallelOptions { MaxDegreeOfParallelism = 8 },
            i => store.Append(T0.AddMilliseconds(i), "info", i % 2 == 0 ? "系统" : "接口", "标记", "m" + i));
        Assert.Equal(N, store.Head);
        Assert.Equal(Enumerable.Range(1, N).Select(x => (long)x), seen);
        Assert.Equal(1, store.OldestBuffered);
        var r = store.Resync(0, N + 10);
        Assert.False(r.Gap);
        Assert.Equal(N, r.Items.Count);
        await store.StopAsync();
        var seqs = File.ReadAllLines(Path.Combine(dir, "events", "events-20261106.jsonl")).Select(l => EventStore.TryParse(l)!.Seq).ToList();
        Assert.Equal(Enumerable.Range(1, N).Select(x => (long)x), seqs);
    }

    [Fact]
    public async Task ReadFiles_SkipsFilesEntirelyBelowAfterSeq()
    {
        var dir = Tmp();
        var s = new EventStore(dir);
        for (int i = 0; i < 4; i++) s.Append(T0.AddDays(0), "info", "系统", "标记", "d0-" + i);        // seq 1..4 → events-20261106
        for (int i = 0; i < 3; i++) s.Append(T0.AddDays(1), "info", "系统", "标记", "d1-" + i);        // seq 5..7 → events-20261107
        for (int i = 0; i < 2; i++) s.Append(T0.AddDays(2), "info", "系统", "标记", "d2-" + i);        // seq 8..9 → events-20261108
        await s.StopAsync();
        var ev = Path.Combine(dir, "events");
        Assert.Equal(5, EventStore.FirstSeqIn(Path.Combine(ev, "events-20261107.jsonl")));
        Assert.Equal(0, EventStore.FirstSeqIn(Path.Combine(ev, "nope.jsonl")));
        Assert.Equal(new long[] { 6, 7, 8, 9 }, EventStore.ReadFiles(ev, 5, 100).Select(x => x.Seq));
        Assert.Equal(new long[] { 5, 6 }, EventStore.ReadFiles(ev, 4, 2).Select(x => x.Seq));     // 正好落在文件边界
        Assert.Equal(new long[] { 9 }, EventStore.ReadFiles(ev, 8, 100).Select(x => x.Seq));
        Assert.Empty(EventStore.ReadFiles(ev, 9, 100));
        Assert.Equal(9, EventStore.ReadFiles(ev, 0, 100).Count);
    }

    [Fact]
    public void Resync_HitAndGap()
    {
        var dir = Tmp();
        var s = new EventStore(dir);
        for (int i = 0; i < 20; i++) s.Append(T0, "info", "系统", "标记", "m" + i);
        var r = s.Resync(15, 100);
        Assert.False(r.Gap);
        Assert.Equal(new long[] { 16, 17, 18, 19, 20 }, r.Items.Select(x => x.Seq));
        Assert.Equal(20, r.Head);
        Assert.Equal(1, r.OldestBuffered);

        var none = s.Resync(20, 100);
        Assert.False(none.Gap);
        Assert.Empty(none.Items);

        var limited = s.Resync(0, 3);
        Assert.False(limited.Gap);
        Assert.Equal(new long[] { 1, 2, 3 }, limited.Items.Select(x => x.Seq));
        s.Stop();
    }

    [Fact]
    public async Task Resync_Gap_WhenRingWrappedOrAcrossRestart()
    {
        var dir = Tmp();
        var s = new EventStore(dir);
        for (int i = 0; i < EventStore.RingSize + 10; i++) s.Append(T0, "info", "系统", "标记", "m");
        Assert.Equal(11, s.OldestBuffered);
        Assert.True(s.Resync(5, 10).Gap);
        var ok = s.Resync(10, 10);
        Assert.False(ok.Gap);
        Assert.Equal(11, ok.Items[0].Seq);
        await s.StopAsync();

        // 重启后环是空的：afterSeq 早于本次运行的第一条 → gap；ReadAfter 回落到文件
        var s2 = new EventStore(dir);
        s2.Append(T0.AddDays(1), "info", "系统", "标记", "after-restart");
        Assert.True(s2.Resync(100, 10).Gap);
        await s2.StopAsync();
        var fromFiles = s2.ReadAfter(EventStore.RingSize + 8, 10);
        Assert.Equal(new long[] { EventStore.RingSize + 9, EventStore.RingSize + 10, EventStore.RingSize + 11 }, fromFiles.Select(x => x.Seq));
    }

    [Fact]
    public async Task ReadFiles_SpansDaysInOrder()
    {
        var dir = Tmp();
        var s = new EventStore(dir);
        s.Append(T0, "info", "系统", "标记", "day1");
        s.Append(T0.AddDays(1), "info", "系统", "标记", "day2");
        s.Append(T0.AddDays(2), "info", "系统", "标记", "day3");
        await s.StopAsync();
        Assert.Equal(3, Directory.GetFiles(Path.Combine(dir, "events"), "events-*.jsonl").Length);
        var all = EventStore.ReadFiles(Path.Combine(dir, "events"), 0, 10);
        Assert.Equal(new long[] { 1, 2, 3 }, all.Select(x => x.Seq));
        Assert.Equal(new long[] { 3 }, EventStore.ReadFiles(Path.Combine(dir, "events"), 2, 10).Select(x => x.Seq));
    }

    [Fact]
    public void AlarmFeed_PublishesSnapshotAndChange()
    {
        var s = new EventStore(Tmp());
        AlarmDto? changed = null;
        s.AlarmChanged += a => changed = a;
        var dto = new AlarmDto("1", "err", "RV2", 2, "超压", "x", DateTime.UtcNow, "active", null);
        s.PublishAlarms(new[] { dto }, dto);
        Assert.Single(s.Alarms);
        Assert.Same(dto, changed);
        s.Stop();
    }

    [Theory]
    [InlineData("RV1", "超温 · 切断加热", "alarm.overTemp", 1)]
    [InlineData("RV1", "超温 · 解除", "alarm.overTempCleared", 1)]
    [InlineData("RV2", "超温切断已确认", "alarm.overTempStopConfirmed", 2)]
    [InlineData("RV3", "超压 · 联锁动作", "alarm.overPressure", 3)]
    [InlineData("RV3", "超压 · 解除", "alarm.overPressureCleared", 3)]
    [InlineData("通讯", "温控 AI-8 离线", "alarm.commLost", null)]
    [InlineData("通讯", "温控 AI-8 通讯恢复 · 报警解除", "alarm.commRestored", null)]
    [InlineData("系统", "急停按下 · 停止指令下发", "estop.triggered", null)]
    [InlineData("系统", "急停已复位", "estop.reset", null)]
    [InlineData("系统", "上次急停未复位，开机保持锁定", "estop.restoredOnBoot", null)]
    [InlineData("系统", "急停 · 加热已确认", "estop.part", null)]
    [InlineData("系统", "程序启动", "system.boot", null)]
    [InlineData("系统", "程序退出", "system.shutdown", null)]
    [InlineData("系统", "测试会话开始", "session.begin", null)]
    [InlineData("系统", "测试会话结束", "session.end", null)]
    [InlineData("系统", "标记", "session.marker", null)]
    [InlineData("系统", "泄漏测试开始", "leak.started", null)]
    [InlineData("阀门", "阀门整帧下发", "valve.frame", null)]
    [InlineData("设置", "修改 · 超温报警", "system.settingsChanged", null)]
    [InlineData("接口", "接口客户端已连接", "system.clientConnected", null)]
    [InlineData("接口", "接口客户端已断开", "system.clientDisconnected", null)]
    [InlineData("接口", "接口已停止", "system.apiStopped", null)]
    [InlineData("接口", "接口 HTTPS 未实现 · 已忽略 httpsPort / certPath", "api.generic", null)]
    [InlineData("接口", "接口鉴权失败", "api.authFailed", null)]
    [InlineData("接口", "接口绑定失败 · 10 秒后重试", "system.apiBindFailed", null)]
    [InlineData("接口", "接口已启动", "system.apiStarted", null)]
    [InlineData("RV9", "随便什么", "event.generic", null)]
    [InlineData("RV12", "随便什么", "event.generic", null)]
    public void Derive_Kinds(string origin, string title, string kind, int? channel)
    {
        Assert.Equal(kind, EventKinds.Derive(origin, title));
        Assert.Equal(channel, EventKinds.ChannelOf(origin));
    }
}
