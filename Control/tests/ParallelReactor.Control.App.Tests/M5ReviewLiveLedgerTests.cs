using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Pages;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// M5 复核（程序台账接在实时数据源上，假连接 + 脚本化的 REST，同 LiveLedgerTests）：
/// <list type="bullet">
/// <item>c1-live-1：下发之后 HMI 回到本地控制（事件 control.released 等）→ 程序照样按台账显示、状态行写「触屏接管过控制」；「启动」禁用（守卫 + 数据源各挡一次），
///   「全部运行」没有对象、写明原因；检查器的「运行」照样能运行，提示里不写台账的程序名；重新下发之后照常。</item>
/// <item>c1-live-6：PUT 回来之前 WS 已经送到的事件对新条目补核对 —— 下一条实时事件不再看起来像跳号（不续取、不降级）。</item>
/// <item>c1-live-5：重新连上、续取还在路上时下发了 RV2，续取失败只作废还在核对的 RV1。</item>
/// <item>c1-live-4：台账文件坏了 / 读不了时提示一次（坏了改名 .bad；读不了的文件不动、不写盘）。</item>
/// <item>c1-live-8：会话标记的重取在请求失败时排着的那一遍不丢；换了设备地址时旧的标记清掉。</item>
/// </list>
/// </summary>
public class M5ReviewLiveLedgerTests
{
    private const string Me = "远程（PC-测试）";
    private const long PutSeq = 97;

    /// <summary>写命令的脚本（同 LiveLedgerTests，PUT 的事件序号可变）+ HMI 的事件库（GET /events，<paramref name="fail"/> 时 500）。</summary>
    private static void Script(ScriptedHttpHandler h, Func<long> putSeq, List<EventRecord> store, Func<bool>? fail = null)
    {
        FakeReactorConnection.ScriptGrantedLease(h);
        h.On("POST", @"/channels/\d+/start", c => ScriptedHttpHandler.Command(c, true, null));
        h.On("PUT", @"/channels/\d+/program", c => ScriptedHttpHandler.Command(c, false, null, opEventSeq: putSeq()));
        h.On("POST", @"/channels/\d+/program/run", c => ScriptedHttpHandler.Command(c, true, new ProgramRunResult(1, 4)));
        h.On("POST", "/run", c => ScriptedHttpHandler.Command(c, true, null));
        h.On("GET", @"/events\?.*", call =>
        {
            if (fail?.Invoke() == true) return ScriptedHttpHandler.Error("INTERNAL", "事件库读取失败");
            var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + call.Path).Query);
            long after = long.TryParse(q["afterSeq"], out var a) ? a : 0;
            int limit = int.TryParse(q["limit"], out var lm) ? lm : 500;
            lock (store)
            {
                var items = store.Where(e => e.Seq > after).OrderBy(e => e.Seq).ToList();
                var page = items.Take(limit).ToArray();
                long head = Math.Max(putSeq(), store.Count > 0 ? store.Max(e => e.Seq) : 0);
                return ScriptedHttpHandler.Ok(new EventsPageDto(1, head, after, page.Length, items.Count > page.Length, page));
            }
        });
    }

    private static async Task Until(Func<bool> done, int ms = 3000)
    {
        for (int i = 0; i < ms / 10 && !done(); i++) await Task.Delay(10);
    }

    private static async Task<(FleetVm F, DeviceVm D, FakeReactorConnection Fake, LiveDeviceSource Live)> Connected(List<EventRecord>? store = null, Action<ScriptedHttpHandler>? more = null,
        Func<long>? putSeq = null)
    {
        var s = store ?? new List<EventRecord>();
        var (f, d, fake) = await LiveFleet.OneAsync(h => { Script(h, putSeq ?? (() => PutSeq), s); more?.Invoke(h); });
        Assert.True((await d.Actions.RunAsync(DeviceAction.LeaseRequest)).Ok);
        for (int i = 0; i < 200 && fake.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
        f.RefreshNow();
        return (f, d, fake, (LiveDeviceSource)d.Source);
    }

    private static ProgramDoc Doc() => ProgramDraftStore.Template("加氢测试-01");

    private static TelemetrySnapshot Frame(DateTime ts, ChannelProgramSnap? p, double temp = 60, string state = "heating") =>
        LiveFrames.Frame(ts, LiveFrames.Ch(1, temp, 80, 30, 0, 50, state, program: p), LiveFrames.Ch(2), LiveFrames.Ch(3), LiveFrames.Ch(4));

    private static EventMsg Event(long seq, string kind, string? client, string title, DateTime ts, string level = "info", string? opId = null, string message = "") =>
        new("event", seq, 1, ts, "", level, kind, null, "remote", "系统", client, opId, title, message);

    private static ReactorProgramVm PanelOf(DeviceVm d)
    {
        var vm = new ReactorProgramVm(d.K, 1);
        vm.Refresh(d);
        return vm;
    }

    // ———————————————————— c1-live-1 ————————————————————

    [Fact]
    public async Task TouchTookControlAfterTheDownload_StartHeldBack_UntilRedownload()
    {
        long put = PutSeq;   // 下发命令的事件序号（HMI 每次下发记一条新的）
        var (f, d, fake, live) = await Connected(putSeq: () => put);
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        f.RefreshNow();
        Assert.Equal("", ActionGuard.Why(live.Facts(), DeviceAction.Start, ActionPayload.Ch(1)));

        // 本机释放控制权（HMI 回到本地：触屏从此能改草稿，改了不记事件）
        var now = DateTime.UtcNow;
        fake.RaiseEvent(Event(PutSeq + 1, "control.released", "PC-测试", "远程释放控制权 · 回到本地", now, message: "PC-测试 交还控制权 · 回到本地"));
        f.RefreshNow();
        var e = live.Ledger.Entry(1)!;
        Assert.Equal((true, true, (long?)(PutSeq + 1)), (e.Valid, e.LocalControl, e.LocalSinceSeq));
        // 已下发 · 待运行（照样按台账显示），状态行写明，悬停写全
        var p = Assert.IsType<ProgramRecord>(live.Program(1));
        Assert.Equal((ProgramStatus.Loaded, true), (p.Status, p.AfterLocalControl));
        var vm = PanelOf(d);
        Assert.Equal(("已下发", "待运行 · 触屏接管过控制"), (vm.StatusWord, vm.StatusFull));
        Assert.Equal("已下发 · 待运行 · 触屏端接管过控制 · 设备上的程序可能已修改 · 重新下发后才能从这里启动", vm.StatusTitle);
        Assert.False(vm.Degraded);

        // 「启动」：守卫禁用；数据源再挡一次 —— 既不运行它（program/run），也不改成启用 + 加热
        var ch = live.Facts().Channel(1)!;
        Assert.Equal((true, (bool?)true, false), (ch.LocalControl, ch.Downloaded, ch.Checking));
        Assert.Equal("RV1 触屏端接管过控制，设备上的程序可能已修改 · 请重新下发", ActionGuard.Why(live.Facts(), DeviceAction.Start, ActionPayload.Ch(1)));
        Assert.Equal("RV1 触屏端接管过控制，设备上的程序可能已修改 · 请重新下发", d.Actions.Why(DeviceAction.Start, ActionPayload.Ch(1)));
        var held = await live.ExecAsync(DeviceAction.Start, new ActionPayload(N: 1, Sp: 80), CancellationToken.None);
        Assert.False(held.Ok);
        Assert.Equal("1号机 · RV1 触屏端接管过控制，设备上的程序可能已修改 · 请重新下发", held.Message);
        Assert.Equal((0, 0, 0), (fake.Http.Count("POST", "/channels/1/program/run"), fake.Http.Count("POST", "/channels/1/start"), fake.Http.Count("POST", "/run")));
        // 「全部运行」：没有对象，写明原因
        Assert.Empty(live.RunAllTargets());
        Assert.Equal(ProgramLedger.LocalControlReason, ActionGuard.Why(live.Facts(), DeviceAction.RunAll));

        // 检查器的「运行」照 M4 的决定照样运行设备上的程序；提示里不写台账的程序名（设备上那份可能改过）
        var o = await d.Actions.RunAsync(DeviceAction.ProgRun, ActionPayload.Ch(1));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · RV1 程序开始运行", o.Message);
        // 跑起来了（接管之后由本机启动）：照样按台账显示，状态行带说明
        var t0 = now.AddSeconds(2);
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: Me, message: "升温至 80 ℃")));
        f.RefreshNow();
        p = Assert.IsType<ProgramRecord>(live.Program(1));
        Assert.Equal((ProgramStatus.Running, true, false), (p.Status, p.AfterLocalControl, p.StartedByTouch));
        vm = PanelOf(d);
        Assert.Matches(@"^第 1/4 步 · 预计 \d\d:\d\d 结束 · 触屏接管过控制$", vm.StatusFull);
        Assert.Matches(@"^运行中 · 第 1/4 步 · 预计 \d\d:\d\d 结束 · 触屏端接管过控制 · 按本机下发的程序显示，设备上的程序可能已修改$", vm.StatusTitle);

        // 跑完；重新下发：新的条目，说明没了，「启动」照常运行本机下发的程序
        fake.RaiseTelemetry(Frame(t0.AddSeconds(5), LiveFrames.Prog(3, 4, "completed", startedAt: t0, startedBy: Me, message: "4 步全部完成"), temp: 40, state: "done"));
        f.RefreshNow();
        put = PutSeq + 5;   // 重新下发（之前收到的「回到本地」98 比它早，补核对时不算）
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        f.RefreshNow();
        Assert.False(live.Ledger.Entry(1)!.LocalControl);
        Assert.False(live.Program(1)!.AfterLocalControl);
        Assert.False(live.Facts().Channel(1)!.LocalControl);
        Assert.Equal("", ActionGuard.Why(live.Facts(), DeviceAction.Start, ActionPayload.Ch(1)));
        Assert.DoesNotContain("接管", PanelOf(d).StatusTitle, StringComparison.Ordinal);
        var start = await live.ExecAsync(DeviceAction.Start, new ActionPayload(N: 1, Sp: 80), CancellationToken.None);
        Assert.True(start.Ok, start.Message);
        Assert.Equal("1号机 · RV1 已启动 · 程序「加氢测试-01」", start.Message);
        Assert.Equal(2, fake.Http.Count("POST", "/channels/1/program/run"));
        await f.DisposeAsync();
    }

    // ———————————————————— c1-live-6 ————————————————————

    /// <summary>
    /// PUT 还没回来时 WS 已经送到它自己的事件（97）和一条别的（98）：记下下发时补核对到 98（下发时刻取 97 的时刻），
    /// 下一条实时的 99 照常往前推 —— 不续取、不进核对（以前看起来像跳号：全部条目降级、「启动」禁用、多一次 GET /events）。
    /// </summary>
    [Fact]
    public async Task EventsBeforeThePutReturns_NoSpuriousCatchUp()
    {
        var (f, d, fake, live) = await Connected();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Gate = r => r.Method == System.Net.Http.HttpMethod.Put ? gate.Task : null;
        int gets = fake.Http.Count("GET", @"/events\?.*");
        var device = DateTime.UtcNow.AddSeconds(-40);   // 设备时钟（与本机差 40 s）
        var up = live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        fake.RaiseEvent(Event(PutSeq, "op.program.replace", "PC-测试", "远程命令 program.replace · ok", device, message: "RV1 程序已更新（4 步）"));
        fake.RaiseEvent(Event(PutSeq + 1, "op.channel.start", "PC-别人", "远程命令", device.AddSeconds(0.2), message: "RV2 已启动"));
        gate.SetResult();
        await up;
        fake.Gate = null;
        var e = live.Ledger.Entry(1)!;
        Assert.Equal((PutSeq + 1, device), (e.CheckedSeq!.Value, e.DownloadedAtDevice!.Value));
        Assert.False(live.Ledger.Behind);
        Assert.False(live.Ledger.Checking);
        fake.RaiseEvent(Event(PutSeq + 2, "op.channel.start", "PC-别人", "远程命令", device.AddSeconds(1), message: "RV3 已启动"));
        Assert.False(live.Ledger.Checking);
        Assert.Equal(PutSeq + 2, live.Ledger.Entry(1)!.CheckedSeq);
        await Task.Delay(50);
        Assert.Equal(gets, fake.Http.Count("GET", @"/events\?.*"));
        f.RefreshNow();
        Assert.Equal(ProgramStatus.Loaded, live.Program(1)?.Status);
        Assert.False(live.Facts().Channel(1)!.Checking);
        await f.DisposeAsync();
    }

    // ———————————————————— c1-live-5 ————————————————————

    /// <summary>
    /// 本机重启后重新连上：RV1（台账文件读回的）在核对、续取被拦在半路；这时下发了 RV2（这次连接上，从它自己的事件起连续核对）。
    /// 续取失败：只作废 RV1（离线期间的事件没核对完），RV2 照样「已下发 · 待运行」、「启动」运行它（以前两条一起作废）。
    /// </summary>
    [Fact]
    public async Task CatchUpFails_AfterADownloadOnThisConnection_OnlyTheOldEntryIsSuperseded()
    {
        using var dir = new TestDir();
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "tok");
        var store = new List<EventRecord>();
        long putSeq = PutSeq;
        bool failing = false;
        TaskCompletionSource? gate = null;
        FakeReactorConnection? fake = null;
        LiveDeviceSource Make() => new(cfg, "PC-测试", a => a(), o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
                Gate = r => r.RequestUri!.Query.Contains("afterSeq=" + PutSeq + "&limit=" + LiveDeviceSource.LedgerPageSize, StringComparison.Ordinal) ? gate?.Task : null,
            };
            Script(fake.Http, () => putSeq, store, () => failing);
            return fake;
        }) { LedgerDirectory = dir.Path };

        await using (var a = Make())
        {
            await a.ConnectAsync(CancellationToken.None);
            await a.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
            for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
            await a.UploadProgramAsync(1, Doc(), CancellationToken.None);
        }
        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var b = Make();
        await b.ConnectAsync(CancellationToken.None);
        Assert.True(b.Ledger.IsChecking(1));
        await b.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
        for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
        putSeq = 150;
        await b.UploadProgramAsync(2, Doc(), CancellationToken.None);
        Assert.False(b.Ledger.IsChecking(2));
        Assert.Equal(ProgramStatus.Loaded, b.Program(2)?.Status);
        failing = true;
        gate.SetResult();
        await Until(() => b.Ledger.Entry(1) is { Valid: false } && !b.Ledger.Checking);
        Assert.Equal(ProgramLedger.CatchUpFailedReason, b.LedgerReason(1));
        Assert.True(b.Ledger.Entry(2)!.Valid);
        Assert.Equal(ProgramStatus.Loaded, b.Program(2)?.Status);
        Assert.Equal("", ActionGuard.Why(b.Facts(), DeviceAction.Start, ActionPayload.Ch(2)));
        var start = await b.ExecAsync(DeviceAction.Start, new ActionPayload(N: 2, Sp: 80), CancellationToken.None);
        Assert.True(start.Ok, start.Message);
        Assert.Equal(1, fake!.Http.Count("POST", "/channels/2/program/run"));
    }

    // ———————————————————— c1-live-4 ————————————————————

    /// <summary>台账文件坏了：改名 .bad、从空的开始，提示一次（「1号机 · 程序台账内容有误…」，之后每秒的 Tick 不重复）。</summary>
    [Fact]
    public async Task CorruptLedgerFile_IsMovedAside_AndToldOnce()
    {
        using var dir = new TestDir();
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "tok");
        var path = ProgramLedger.PathFor(dir.Path, cfg.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var notices = new List<string>();
        await using var s = new LiveDeviceSource(cfg, "PC-测试", a => a(), o =>
        {
            var fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
            };
            Script(fake.Http, () => PutSeq, new List<EventRecord>());
            return fake;
        }) { LedgerDirectory = dir.Path };
        s.Notice += (t, _) => notices.Add(t);
        await s.ConnectAsync(CancellationToken.None);
        s.Tick();
        s.Tick();
        var told = Assert.Single(notices, n => n.Contains("程序台账", StringComparison.Ordinal));
        Assert.StartsWith("1号机 · 程序台账内容有误（", told, StringComparison.Ordinal);
        Assert.EndsWith("）· 已改名为 .bad 留存，从空台账开始", told, StringComparison.Ordinal);
        Assert.True(File.Exists(path + ".bad"));
        Assert.False(s.Ledger.LoadFailed);
    }

    /// <summary>
    /// 台账文件读不了（权限 000）：提示一次（「… · 本次不写台账文件」）；连上（以前这里就用空台账写盘）、下发之后文件原样不动。
    /// </summary>
    [Fact]
    public async Task UnreadableLedgerFile_IsNeverOverwritten_AndToldOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TestDir();
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "tok");
        var good = new ProgramLedger(cfg.Id.ToString("D"), "PC-测试", ProgramLedger.PathFor(dir.Path, cfg.Id));
        good.AddDownload(3, Doc(), "d8391e8a", DateTime.UtcNow, TimeSpan.Zero, null, true, opEventSeq: 40, eventEpoch: 1);
        var path = good.FilePath!;
        var bytes = File.ReadAllBytes(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            try { using var probe = File.OpenRead(path); return; }                        // 以 root 运行：权限挡不住，没法测
            catch (UnauthorizedAccessException) { }
            var notices = new List<string>();
            FakeReactorConnection? fake = null;
            await using var s = new LiveDeviceSource(cfg, "PC-测试", a => a(), o =>
            {
                fake = new FakeReactorConnection(o)
                {
                    StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                    StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
                };
                Script(fake.Http, () => PutSeq, new List<EventRecord>());
                return fake;
            }) { LedgerDirectory = dir.Path };
            s.Notice += (t, _) => notices.Add(t);
            await s.ConnectAsync(CancellationToken.None);
            await s.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
            for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
            await s.UploadProgramAsync(1, Doc(), CancellationToken.None);
            s.Tick();
            var told = Assert.Single(notices, n => n.Contains("程序台账", StringComparison.Ordinal));
            Assert.StartsWith("1号机 · 程序台账读取失败：", told, StringComparison.Ordinal);
            Assert.EndsWith(" · 本次不写台账文件", told, StringComparison.Ordinal);
            Assert.True(s.Ledger.Entry(1)!.Valid);                                       // 本次运行照样在内存里用
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal(bytes, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bad"));
        }
        finally
        {
            try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch (IOException) { }
        }
    }

    /// <summary>保存失败（台账目录不能写）：每一段失败提示一次（每秒的 Tick 不重复），保存成功一次之后再失败再提示。</summary>
    [Fact]
    public async Task SaveFailure_IsToldOncePerEpisode()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TestDir();
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "tok");
        var notices = new List<string>();
        FakeReactorConnection? fake = null;
        await using var s = new LiveDeviceSource(cfg, "PC-测试", a => a(), o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
            };
            Script(fake.Http, () => PutSeq, new List<EventRecord>());
            return fake;
        }) { LedgerDirectory = dir.Path };
        s.Notice += (t, _) => notices.Add(t);
        await s.ConnectAsync(CancellationToken.None);
        await s.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
        for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
        await s.UploadProgramAsync(1, Doc(), CancellationToken.None);
        var programs = Path.GetDirectoryName(s.Ledger.FilePath!)!;
        Assert.True(File.Exists(s.Ledger.FilePath));
        File.SetUnixFileMode(programs, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // 目录不能写：原子写的临时文件建不了
        try
        {
            await s.UploadProgramAsync(2, Doc(), CancellationToken.None);
            if (s.Ledger.SaveError is null) return;                                       // 以 root 运行：权限挡不住，没法测
            s.Tick();
            s.Tick();
            var told = Assert.Single(notices, n => n.Contains("程序台账", StringComparison.Ordinal));
            Assert.StartsWith("1号机 · 程序台账保存失败：", told, StringComparison.Ordinal);
            File.SetUnixFileMode(programs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await s.UploadProgramAsync(3, Doc(), CancellationToken.None);                  // 保存成功：这一段结束
            Assert.Null(s.Ledger.SaveError);
            File.SetUnixFileMode(programs, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await s.UploadProgramAsync(4, Doc(), CancellationToken.None);
            s.Tick();
            Assert.Equal(2, notices.Count(n => n.Contains("程序台账保存失败", StringComparison.Ordinal)));
        }
        finally
        {
            File.SetUnixFileMode(programs, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ———————————————————— c1-live-8 ————————————————————

    /// <summary>
    /// 会话标记的重取（GET /sessions/current）：请求在路上时又来了会话事件（排着再取一遍），这次请求失败了 —— 排着的那一遍照样取（以前丢掉，新标记一直不出来）。
    /// 换了设备地址：旧设备的标记立即清掉（新地址的请求失败了也不留着旧的）。
    /// </summary>
    [Fact]
    public async Task Marks_QueuedReloadSurvivesAFailedRequest_ClearedOnAddressChange()
    {
        var now = DateTime.UtcNow;
        var marks = new List<SessionMarker> { new(1, now.AddMinutes(-12), 40, "RV1 加料", "remote", "PC-测试") };
        int failNext = 0;   // 接下来几次 GET /sessions/current 回 500
        SessionDetailDto Current()
        {
            lock (marks)
                return new SessionDetailDto(true, "S1", "T1", "S1.csv", new SessionManifest("S1", "T1", null, now.AddHours(-1), null, SessionManifest.Active, "remote", "PC-测试",
                    true, 1, null, "S1.csv", marks.ToArray(), Array.Empty<SessionProgramRun>(), Array.Empty<SessionLeakTest>()), Array.Empty<string>());
        }
        var (f, d, fake, live) = await Connected(more: h => h.On("GET", "/sessions/current", _ => Interlocked.Decrement(ref failNext) >= 0
            ? ScriptedHttpHandler.Error("INTERNAL", "会话读取失败") : ScriptedHttpHandler.Ok(Current())));
        await Until(() => live.History.Marks.Count == 1);
        Assert.Single(live.History.Marks);

        // 一次重取被拦在路上（放开后回 500）；这时又打了一个标记（会话事件：排着再取一遍）。之后不再有事件
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Gate = r => r.RequestUri!.AbsolutePath.EndsWith("/sessions/current", StringComparison.Ordinal) ? gate.Task : null;
        int calls = fake.Http.Count("GET", "/sessions/current");
        fake.RaiseEvent(new EventMsg("event", 60, 1, now, "", "info", "session.marker", null, "system", "系统", null, null, "标记", "RV2 取样 · 会话 S1"));
        lock (marks) marks.Add(new SessionMarker(2, now, 60, "RV2 取样", "remote", "PC-测试"));
        fake.RaiseEvent(new EventMsg("event", 61, 1, now, "", "info", "session.marker", null, "system", "系统", null, null, "标记", "RV2 取样 · 会话 S1"));
        Interlocked.Exchange(ref failNext, 1);
        fake.Gate = null;
        gate.SetResult();
        // 失败的那一次之后，排着的那一遍照样取（一共两次请求）—— 新标记出来
        await Until(() => live.History.Marks.Count == 2);
        Assert.Equal(new[] { "RV1 加料", "RV2 取样" }, live.History.Marks.Select(m => m.Text).ToArray());
        Assert.Equal(calls + 2, fake.Http.Count("GET", "/sessions/current"));

        // 换了设备地址（新地址的会话请求一直失败）：旧设备的标记清掉，不留在曲线上
        Interlocked.Exchange(ref failNext, 1000);
        await live.UpdateConfigAsync(live.Config with { Host = "127.0.0.2" });
        Assert.Empty(live.History.Marks);
        await LiveFleet.WaitOnline(d);
        await Task.Delay(50);
        Assert.Empty(live.History.Marks);
        await f.DisposeAsync();
    }
}
