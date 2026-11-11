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
/// 程序台账接进实时数据源（PORT-PLAN §5 M5「数据」、§3.2 第 4–6 条、R5、D6；LiveDeviceSource + Core 的 ProgramLedger / StepTracker / EtaEstimator），
/// 用进程内的假连接（FakeReactorConnection：真 Client 的 REST / 控制权 + 脚本化的 HMI）和手造的遥测帧（LiveFrames）：
/// <list type="bullet">
/// <item>下发（PUT 成功之后记进台账）→「已下发 · 待运行」→「启动」运行本机下发的程序（program/run，绝不 POST /run，D6）→ 跑起来（本机启动）按台账显示：
///   名称、步骤、当前步；换步时记下每一步的开始（相邻两帧的中点）；剩余时间按 HMI 的倒计时（「剩 45 分」）→ 预计结束。</item>
/// <item>严格匹配（R5）：HMI 重启、步骤数不同、别的客户端启动、别的客户端替换了程序 —— 台账作废，页面降级成「RVn 程序 · 第 k/n 步 · 消息」，绝不把过期的程序当成正在运行的显示；
///   触屏启动本机下发的程序照样算。</item>
/// <item>持久化：&lt;配置目录&gt;/programs/&lt;设备 id&gt;.json（同一个启动号重连后还认得）；内存配置（截图 / 演示 / 测试）不写文件。</item>
/// </list>
/// </summary>
public class LiveLedgerTests
{
    private const string Me = "远程（PC-测试）";

    /// <summary>下发命令自己的事件序号（HMI 把 PUT 记成一条 op.program.replace 事件）：台账从这里起核对之后的事件。</summary>
    private const long PutSeq = 97;

    private static void ScriptWrites(ScriptedHttpHandler h)
    {
        FakeReactorConnection.ScriptGrantedLease(h);
        h.On("POST", @"/channels/\d+/start", c => ScriptedHttpHandler.Command(c, true, null));
        h.On("PUT", @"/channels/\d+/program", c => ScriptedHttpHandler.Command(c, false, null, opEventSeq: PutSeq));
        h.On("POST", @"/channels/\d+/program/run", c => ScriptedHttpHandler.Command(c, true, new ProgramRunResult(1, 4)));
        h.On("POST", "/run", c => ScriptedHttpHandler.Command(c, true, null));
    }

    /// <summary>
    /// HMI 的事件库（GET /events?afterSeq&amp;limit，同 HMI：序号 &gt; afterSeq 的按序号给，最多 limit 条，more = 还有）：<paramref name="store"/> 是设备上还在的事件，
    /// 序号头 = max(<see cref="PutSeq"/>, 最大序号)。<paramref name="fail"/> 时回 500。<paramref name="onRequest"/>(afterSeq, limit)：回答之前调用（测试在这里拦住续取）。
    /// </summary>
    private static void ScriptEvents(ScriptedHttpHandler h, List<EventRecord> store, int epoch = 1, Func<bool>? fail = null, Action<long, int>? onRequest = null)
    {
        h.On("GET", @"/events\?.*", call =>
        {
            if (fail?.Invoke() == true) return ScriptedHttpHandler.Error("INTERNAL", "事件库读取失败");
            var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + call.Path).Query);
            long after = long.TryParse(q["afterSeq"], out var a) ? a : 0;
            int limit = int.TryParse(q["limit"], out var lm) ? lm : 500;
            onRequest?.Invoke(after, limit);
            lock (store)
            {
                var items = store.Where(e => e.Seq > after).OrderBy(e => e.Seq).ToList();
                var page = items.Take(limit).ToArray();
                long head = Math.Max(PutSeq, store.Count > 0 ? store.Max(e => e.Seq) : 0);
                return ScriptedHttpHandler.Ok(new EventsPageDto(epoch, head, after, page.Length, items.Count > page.Length, page));
            }
        });
    }

    /// <summary>设备事件库里的一条（远程命令的事件：kind、客户端、opId、说明）。</summary>
    private static EventRecord Ev(long seq, string kind = "op.channel.start", string client = "PC-别人", string message = "RV2 已启动", string? opId = null) =>
        new(seq, 1, DateTime.UtcNow.AddMinutes(-5).AddSeconds(seq), "", "info", kind, null, "remote", "RV1", client, opId, "远程命令", message);

    /// <summary>等后台的续取做完（假连接的 REST 在线程池上回来）。</summary>
    private static async Task Until(Func<bool> done)
    {
        for (int i = 0; i < 300 && !done(); i++) await Task.Delay(10);
    }

    private static async Task<(FleetVm F, DeviceVm D, FakeReactorConnection Fake, LiveDeviceSource Live)> Connected(Action<ScriptedHttpHandler>? more = null)
    {
        var (f, d, fake) = await LiveFleet.OneAsync(h => { ScriptWrites(h); more?.Invoke(h); });
        Assert.True((await d.Actions.RunAsync(DeviceAction.LeaseRequest)).Ok);
        for (int i = 0; i < 200 && fake.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
        f.RefreshNow();
        return (f, d, fake, (LiveDeviceSource)d.Source);
    }

    private static ProgramDoc Doc() => ProgramDraftStore.Template("加氢测试-01");

    /// <summary>第 1 路的一帧（其它通道空闲）：温度 / 设定 / 程序。</summary>
    private static TelemetrySnapshot Frame(DateTime ts, ChannelProgramSnap? p, double temp = 60, double sp = 80, string state = "heating") =>
        LiveFrames.Frame(ts, LiveFrames.Ch(1, temp, sp, 30, 0, 50, state, program: p), LiveFrames.Ch(2), LiveFrames.Ch(3), LiveFrames.Ch(4));

    private static ReactorProgramVm PanelOf(DeviceVm d)
    {
        var vm = new ReactorProgramVm(d.K, 1);
        vm.Refresh(d);
        return vm;
    }

    [Fact]
    public async Task Download_Run_StepChanges_Eta()
    {
        var (f, d, fake, live) = await Connected();
        // 下发之前：设备上没有本机的程序 → 「启动」= 启用 + 加热（D6），程序面板是程序库
        Assert.Null(live.Program(1));
        Assert.True(PanelOf(d).HasNoProgram);

        var o = await live.UploadProgramAsync(1, Doc(), CancellationToken.None, libraryId: "draft-1");
        Assert.True(o.Ok);
        var e = Assert.IsType<LedgerEntry>(live.Ledger.Entry(1));
        Assert.Equal(("加氢测试-01", 4, "d8391e8a", "PC-测试", "draft-1"), (e.Name, e.StepCount, e.BootId, e.Client, e.LibraryId));
        Assert.True(e.Valid);
        f.RefreshNow();

        // 已下发 · 待运行（台账的程序：名称、步骤）；守卫：本机下发的、待运行
        var p = Assert.IsType<ProgramRecord>(live.Program(1));
        Assert.Equal((ProgramStatus.Loaded, "加氢测试-01", 4, ProgramSource.Ledger), (p.Status, p.Name, p.Steps.Count, p.Source));
        var ch = live.Facts().Channel(1)!;
        Assert.Equal((ProgramStatus.Loaded, (bool?)true), (ch.Program, ch.Downloaded));
        var vm = PanelOf(d);
        Assert.Equal(("1号机 › RV1 › 加氢测试-01", "已下发", "待运行"), (vm.CrumbTip, vm.StatusWord, vm.StatusFull));
        Assert.Equal(new[] { "1: 升温", "2: 充压", "3: 反应", "4: 结束" }, vm.Steps.Select(s => s.Title));
        Assert.Equal(("进气阀 SV1 :", "气体 A · 稳压保持", "目标压力", "1.034 MPa"),
            (vm.Steps[1].Sections[1].Dev, vm.Steps[1].Sections[1].Mode, vm.Steps[1].Sections[1].Label, vm.Steps[1].Sections[1].Value));

        // 「启动」运行本机下发的程序（program/run，带租约），绝不 POST /run
        var start = await d.Actions.RunAsync(DeviceAction.Start, ActionPayload.Ch(1));
        Assert.True(start.Ok, start.Message);
        Assert.Equal("1号机 · RV1 已启动 · 程序「加氢测试-01」", start.Message);
        Assert.Equal(1, fake.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/channels/1/start"));

        // 跑起来了（本机启动）：第 0 步从 startedAt 开始
        var t0 = DateTime.UtcNow.AddSeconds(-30);
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: Me, message: "升温至 80 ℃")));
        f.RefreshNow();
        p = Assert.IsType<ProgramRecord>(live.Program(1));
        Assert.Equal((ProgramStatus.Running, 0), (p.Status, p.StepIndex));
        Assert.Equal(t0.ToLocalTime(), p.StepStartedAt);
        Assert.Equal(t0.ToLocalTime(), p.StartedAt);
        vm = PanelOf(d);
        Assert.Equal("运行中", vm.StatusWord);
        Assert.StartsWith("第 1/4 步 · 预计 ", vm.StatusFull, StringComparison.Ordinal);
        Assert.Equal(0, vm.CurIndex);
        Assert.Equal(StepMarkKind.Cur, vm.Steps[0].MarkKind);

        // 换步（连续观察）：第 2 步的开始 = 相邻两帧的中点；第 1 步记进已完成
        fake.RaiseTelemetry(Frame(t0.AddSeconds(2), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: Me)));
        fake.RaiseTelemetry(Frame(t0.AddSeconds(3.2), LiveFrames.Prog(1, 4, startedAt: t0, startedBy: Me, message: "充压 0.2 / 1.0 MPa")));
        f.RefreshNow();
        p = Assert.IsType<ProgramRecord>(live.Program(1));
        Assert.Equal(1, p.StepIndex);
        Assert.Equal(t0.AddSeconds(2.6).ToLocalTime(), p.StepStartedAt);
        var log = Assert.Single(p.Log);
        Assert.Equal((0, t0.ToLocalTime(), t0.AddSeconds(2.6).ToLocalTime()), (log.I, log.T0, log.T1));
        vm = PanelOf(d);
        Assert.Equal(("已完成", "当前"), (vm.Steps[0].Mark[..3], vm.Steps[1].Mark[..2]));

        // 定时的步骤：剩余时间按 HMI 自己的倒计时（「剩 45 分」）→ 当前步剩 45 min、预计结束 = 现在 + 全程剩余
        fake.RaiseTelemetry(Frame(t0.AddSeconds(4.4), LiveFrames.Prog(2, 4, startedAt: t0, startedBy: Me, message: "反应中 · 剩 45 分"), temp: 80, state: "reacting"));
        f.RefreshNow();
        var r = d.Reactor(1)!;
        var pr = Assert.IsType<ProgressInfo>(r.Program!.Progress);
        Assert.Equal((3, 4), (pr.K, pr.N));
        Assert.InRange(pr.StepRemainMin, 44, 46);
        vm = PanelOf(d);
        Assert.Equal("当前 · 剩 " + pr.StepRemainMin + " min", vm.Steps[2].Mark);
        Assert.Matches(@"^第 3/4 步 · 预计 \d\d:\d\d 结束$", vm.StatusFull);
        Assert.Equal("1/1/draft-1/2/Running", vm.ScrollKey);
        // 时间轴分段也来自台账
        Assert.NotEmpty(d.Segments(1));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Restart_SupersedesAndDegrades()
    {
        var (f, d, fake, live) = await Connected();
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: Me, message: "升温至 80 ℃")));
        f.RefreshNow();
        Assert.NotNull(live.Program(1));
        // HMI 重启：台账作废（它手里的程序不一定还是本机下发的那份）、步骤计时清空；页面只写设备报告的进度
        fake.RaiseRestarted();
        f.RefreshNow();
        Assert.Null(live.Program(1));
        Assert.False(live.Ledger.Entry(1)!.Valid);
        Assert.Equal("设备重启过（启动号不同）", live.LedgerReason(1));
        Assert.Null(live.Downloaded(1));
        var vm = PanelOf(d);
        Assert.True(vm.Degraded);
        Assert.False(vm.ShowFlow);
        Assert.Equal(("RV1 程序", "1号机 › RV1 › RV1 程序"), (vm.ProgName, vm.CrumbTip));
        Assert.Equal(("运行中", "第 1/4 步 · 升温至 80 ℃"), (vm.StatusWord, vm.StatusFull));
        Assert.StartsWith("设备重启过（启动号不同） · ", vm.DegradedText, StringComparison.Ordinal);
        Assert.Empty(vm.Steps);
        // 降级显示没有步骤卡片：画布顶部没有工具（ShowFlow），也不写放大倍数（复核）
        vm.SetCanvasWidth(900);
        vm.ZoomInCommand.Execute(null);
        Assert.False(vm.ShowFlow);
        Assert.Null(vm.ZoomShown);
        // 「启动」回到启用 + 加热（D6），「全部运行」没有对象
        var ch = live.Facts().Channel(1)!;
        Assert.False(ch.Downloaded);
        await f.DisposeAsync();
    }

    [Theory]
    [InlineData(5, Me, "步骤数不同（设备 5 步 · 本机下发 4 步）")]
    [InlineData(4, "远程（PC-别人）", "由其他客户端启动（远程（PC-别人））")]
    public async Task Mismatch_NeverShowsTheStaleProgram(int count, string by, string reason)
    {
        var (f, d, fake, live) = await Connected();
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        var t0 = DateTime.UtcNow.AddSeconds(-10);
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1), LiveFrames.Prog(1, count, startedAt: t0, startedBy: by, message: "充压 0.2 / 1.0 MPa")));
        f.RefreshNow();
        Assert.Null(live.Program(1));
        Assert.Equal(reason, live.LedgerReason(1));
        var vm = PanelOf(d);
        Assert.True(vm.Degraded);
        Assert.Equal("第 2/" + count + " 步 · 充压 0.2 / 1.0 MPa", vm.StatusFull);
        Assert.Empty(vm.Steps);
        Assert.Equal(-1, vm.CurIndex);
        // 之后再也不匹配（要重新下发）
        fake.RaiseTelemetry(Frame(t0.AddSeconds(2), LiveFrames.Prog(1, 4, startedAt: t0, startedBy: Me)));
        f.RefreshNow();
        Assert.Null(live.Program(1));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task TouchStart_OfTheDownloadedProgram_Matches()
    {
        var (f, d, fake, live) = await Connected();
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        var t0 = DateTime.UtcNow.AddSeconds(-5);
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: "local")));
        f.RefreshNow();
        Assert.Equal(ProgramStatus.Running, live.Program(1)?.Status);
        Assert.True(live.Ledger.Match(1, LiveFrames.Prog(0, 4, startedAt: t0, startedBy: "local"), "d8391e8a").StartedByTouch);
        await f.DisposeAsync();
    }

    [Fact]
    public async Task OtherClientReplacedTheProgram_Supersedes()
    {
        var (f, d, fake, live) = await Connected();
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        var now = DateTime.UtcNow;
        // 本机自己的替换（同一个客户端名、没有 opId）不算
        fake.RaiseEvent(new EventMsg("event", 98, 1, now.AddSeconds(4), "", "info", "op.program.replace", 1, "remote", "RV1", "PC-测试", null,
            "远程命令 program.replace · ok", "RV1 程序已更新（4 步）"));
        f.RefreshNow();
        Assert.True(live.Ledger.Entry(1)!.Valid);
        // WS 推来的、本机下发之后的替换：作废（下发之前的历史事件不算，见 Core 的 LedgerMatchTests.Events_OlderThanTheDownload_DoNotSupersede）
        fake.RaiseEvent(new EventMsg("event", 99, 1, now.AddSeconds(5), "", "info", "op.program.replace", 1, "remote", "RV1", "PC-别人", "op-x",
            "远程命令 program.replace · ok", "RV1 程序已更新（5 步）"));
        f.RefreshNow();
        Assert.False(live.Ledger.Entry(1)!.Valid);
        Assert.Equal("程序已被 PC-别人 替换", live.LedgerReason(1));
        Assert.Null(live.Program(1));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task Persisted_UnderConfigDir_SameBootStillRecognised()
    {
        using var dir = new TestDir();
        var id = Guid.NewGuid();
        var cfg = new DeviceConfig(id, "1号机", "127.0.0.1", 8080, "tok");
        FakeReactorConnection? fake = null;
        LiveDeviceSource Make() => new(cfg, "PC-测试", a => a(), o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
            };
            ScriptWrites(fake.Http);
            ScriptEvents(fake.Http, new List<EventRecord>());   // 断开期间没有别的事件
            return fake;
        }) { LedgerDirectory = dir.Path };

        await using (var a = Make())
        {
            await a.ConnectAsync(CancellationToken.None);
            await a.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
            for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
            await a.UploadProgramAsync(2, Doc(), CancellationToken.None);
            Assert.True(a.Ledger.Persistent);
        }
        var file = ProgramLedger.PathFor(dir.Path, id);
        Assert.True(File.Exists(file));
        Assert.Contains("加氢测试-01", File.ReadAllText(file), StringComparison.Ordinal);

        // 本机重启（新的数据源，同一个配置目录）：同一个启动号、断开期间的事件核对过（没有别人替换）→ 还是「已下发 · 待运行」
        await using var b = Make();
        await b.ConnectAsync(CancellationToken.None);
        // 等续取核对完（连上之后到核对完之间条目先不认，见 Start_BeforeTheCatchUpCompletes_IsHeldBack）
        await Until(() => fake!.Http.Count("GET", @"/events\?.*") >= 2 && !b.Ledger.Checking);
        Assert.True(b.Ledger.Entry(2)!.Valid);
        Assert.Equal(PutSeq, b.Ledger.Entry(2)!.CheckedSeq);
        Assert.Equal(ProgramStatus.Loaded, b.Program(2)?.Status);
    }

    /// <summary>
    /// 复核（高）：本机断开期间（本机重启）有 1250 条事件，别的客户端替换 RV1 的程序（同样 4 步）是其中第 1100 条 —— 不能只看最近 200 条。
    /// 重新连上时台账从下发命令的事件序号续取，一页 1000 条：第一页（afterSeq = 97）没有，第二页（afterSeq = 1097）找到那次替换 → 作废：
    /// 不再显示「已下发 · 待运行」，「启动」回到启用 + 加热（不运行别人的程序，D6）。复核（c1-live-9）：以前替换就在第一页的第一条，测不到第二页。
    /// </summary>
    [Fact]
    public async Task ReplacedWhileAway_FoundByCatchUp_OnReconnect()
    {
        using var dir = new TestDir();
        var id = Guid.NewGuid();
        var cfg = new DeviceConfig(id, "1号机", "127.0.0.1", 8080, "tok");
        var store = new List<EventRecord>();
        FakeReactorConnection? fake = null;
        LiveDeviceSource Make() => new(cfg, "PC-测试", a => a(), o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
            };
            ScriptWrites(fake.Http);
            ScriptEvents(fake.Http, store);
            return fake;
        }) { LedgerDirectory = dir.Path };

        await using (var a = Make())
        {
            await a.ConnectAsync(CancellationToken.None);
            await a.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
            for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
            await a.UploadProgramAsync(1, Doc(), CancellationToken.None);
            Assert.Equal(PutSeq, a.Ledger.Entry(1)!.CheckedSeq);
        }
        // 本机关着的时候：1250 条事件（序号 98–1347），其中第 1100 条（1197）是 PC-别人 替换了 RV1（同样 4 步）—— 在续取的第二页
        lock (store)
        {
            for (long q = PutSeq + 1; q <= PutSeq + 1250; q++)
                store.Add(q == PutSeq + 1100 ? Ev(q, "op.program.replace", "PC-别人", "RV1 程序已更新（4 步）", "op-x") : Ev(q));
        }
        await using var b = Make();
        await b.ConnectAsync(CancellationToken.None);
        await Until(() => b.Ledger.Entry(1) is { Valid: false });
        Assert.Equal("程序已被 PC-别人 替换", b.LedgerReason(1));
        // 续取了两页：afterSeq = 97，再 afterSeq = 1097（每页 1000 条）
        var pages = fake!.Http.Of("GET", @"/events\?.*").Select(c => c.Path).Where(p => p.Contains("&limit=" + LiveDeviceSource.LedgerPageSize, StringComparison.Ordinal)).ToArray();
        Assert.Equal(new[] { "afterSeq=" + PutSeq, "afterSeq=" + (PutSeq + LiveDeviceSource.LedgerPageSize) },
            pages.Select(p => p[(p.IndexOf("afterSeq=", StringComparison.Ordinal))..p.IndexOf('&', p.IndexOf("afterSeq=", StringComparison.Ordinal))]).ToArray());
        Assert.Null(b.Program(1));
        Assert.Null(b.Downloaded(1));
        Assert.False(b.Facts().Channel(1)!.Downloaded);
        // 「启动」= 启用 + 加热（不运行设备上那份别人的程序）
        await b.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
        for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
        var start = await b.ExecAsync(DeviceAction.Start, new ActionPayload(N: 1, Sp: 80), CancellationToken.None);
        Assert.True(start.Ok, start.Message);
        Assert.Equal(1, fake!.Http.Count("POST", "/channels/1/start"));
        Assert.Equal(0, fake.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
    }

    /// <summary>
    /// 复核（c1-live-9）：断开期间的事件多得取不完（超过 <see cref="LiveDeviceSource.LedgerPagesMax"/> 页，每页之后都还有）—— 续取到上限就停（不一直取下去），
    /// 没核对完的条目作废（「离线期间的事件没有全部取回…」），不再按台账显示。假 HMI 每页只给 100 条（HMI 可以少给，more 照样为真），测得快一些。
    /// </summary>
    [Fact]
    public async Task CatchUp_MoreThanPagesMax_Supersedes()
    {
        using var dir = new TestDir();
        var cfg = new DeviceConfig(Guid.NewGuid(), "1号机", "127.0.0.1", 8080, "tok");
        FakeReactorConnection? fake = null;
        LiveDeviceSource Make() => new(cfg, "PC-测试", a => a(), o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
            };
            ScriptWrites(fake.Http);
            fake.Http.On("GET", @"/events\?.*", call =>
            {
                var q = System.Web.HttpUtility.ParseQueryString(new Uri("http://x" + call.Path).Query);
                long after = long.TryParse(q["afterSeq"], out var a) ? a : 0;
                var items = Enumerable.Range(1, 100).Select(i => Ev(after + i)).ToArray();
                return ScriptedHttpHandler.Ok(new EventsPageDto(1, PutSeq + 1_000_000, after, items.Length, true, items));
            });
            return fake;
        }) { LedgerDirectory = dir.Path };

        await using (var a = Make())
        {
            await a.ConnectAsync(CancellationToken.None);
            await a.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
            for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
            await a.UploadProgramAsync(1, Doc(), CancellationToken.None);
        }
        await using var b = Make();
        await b.ConnectAsync(CancellationToken.None);
        await Until(() => b.Ledger.Entry(1) is { Valid: false } && !b.Ledger.Checking);
        Assert.Equal(ProgramLedger.CatchUpFailedReason, b.LedgerReason(1));
        Assert.Null(b.Program(1));
        Assert.Equal(LiveDeviceSource.LedgerPagesMax, fake!.Http.Of("GET", @"/events\?.*").Count(c => c.Path.Contains("&limit=" + LiveDeviceSource.LedgerPageSize, StringComparison.Ordinal)));
        Assert.Equal(PutSeq + LiveDeviceSource.LedgerPagesMax * 100, b.Ledger.Entry(1)!.CheckedSeq);   // 核对到了第 50 页的末尾
    }

    /// <summary>
    /// 复核（低，R5）：重新连上（本机重启，台账从文件读回）到续取核对完之间，台账的条目先不认 —— 这时点「启动」既不运行台账里的程序（program/run），
    /// 也不改成启用 + 加热（channels/start）：守卫写「RV1 正在核对离线期间的事件 · 稍候」，数据源也挡一次；页面不按台账显示（降级说明写同一个原因）。
    /// 核对完照常：「已下发 · 待运行」，「启动」运行本机下发的程序。
    /// </summary>
    [Fact]
    public async Task Start_BeforeTheCatchUpCompletes_IsHeldBack()
    {
        using var dir = new TestDir();
        var id = Guid.NewGuid();
        var cfg = new DeviceConfig(id, "1号机", "127.0.0.1", 8080, "tok");
        var store = new List<EventRecord>();
        TaskCompletionSource? gate = null;   // null = 不拦
        string catchUp = "afterSeq=" + PutSeq + "&limit=" + LiveDeviceSource.LedgerPageSize;
        FakeReactorConnection? fake = null;
        LiveDeviceSource Make() => new(cfg, "PC-测试", a => a(), o =>
        {
            fake = new FakeReactorConnection(o)
            {
                StartDevice = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json")),
                StartSnapshot = ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json")),
                // 台账的续取（从下发命令的事件序号起、每页 1000）等闸门打开；连上时取最近事件的那一次不拦
                Gate = r => r.RequestUri!.Query.Contains(catchUp, StringComparison.Ordinal) ? gate?.Task : null,
            };
            ScriptWrites(fake.Http);
            ScriptEvents(fake.Http, store);
            return fake;
        }) { LedgerDirectory = dir.Path };

        await using (var a = Make())
        {
            await a.ConnectAsync(CancellationToken.None);
            await a.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
            for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
            await a.UploadProgramAsync(1, Doc(), CancellationToken.None);
            Assert.False(a.Ledger.Checking);                                      // 这次连接上刚下发的：不用等核对
            Assert.Equal(ProgramStatus.Loaded, a.Program(1)?.Status);
        }

        gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var b = Make();
        await b.ConnectAsync(CancellationToken.None);
        Assert.True(b.Ledger.IsChecking(1));                                    // 续取被拦在半路：条目还在核对
        Assert.True(b.Ledger.Entry(1)!.Valid);
        Assert.Null(b.Program(1));
        Assert.Equal(ProgramLedger.CheckingReason, b.LedgerReason(1));
        var ch = b.Facts().Channel(1)!;
        Assert.Equal((true, false), (ch.Checking, ch.Downloaded));
        await b.ExecAsync(DeviceAction.LeaseRequest, ActionPayload.Empty, CancellationToken.None);
        for (int i = 0; i < 200 && fake!.Lease.State != LeaseState.Holding; i++) await Task.Delay(10);
        Assert.Equal("RV1 正在核对离线期间的事件 · 稍候", ActionGuard.Why(b.Facts(), DeviceAction.Start, ActionPayload.Ch(1)));
        var held = await b.ExecAsync(DeviceAction.Start, new ActionPayload(N: 1, Sp: 80), CancellationToken.None);
        Assert.False(held.Ok);
        Assert.Equal("1号机 · RV1 正在核对离线期间的事件 · 稍候", held.Message);
        Assert.Equal(0, fake!.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/channels/1/start"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));

        // 核对完（断开期间没有别人替换）：照常
        gate.SetResult();
        await Until(() => !b.Ledger.Checking);
        Assert.Equal(1, fake.Http.Of("GET", @"/events\?.*").Count(c => c.Path.Contains(catchUp, StringComparison.Ordinal)));
        Assert.False(b.Ledger.Checking);
        Assert.Equal(ProgramStatus.Loaded, b.Program(1)?.Status);
        Assert.Null(b.LedgerReason(1));
        Assert.Equal((false, (bool?)true), (b.Facts().Channel(1)!.Checking, b.Facts().Channel(1)!.Downloaded));
        Assert.Equal("", ActionGuard.Why(b.Facts(), DeviceAction.Start, ActionPayload.Ch(1)));
        var start = await b.ExecAsync(DeviceAction.Start, new ActionPayload(N: 1, Sp: 80), CancellationToken.None);
        Assert.True(start.Ok, start.Message);
        Assert.Equal(1, fake.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/channels/1/start"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
    }

    /// <summary>
    /// 复核（低，R5）：连着的时候 WS 送来的事件跳号了（中间那条还没收到、可能就是别人的替换）—— 续取补上之前条目同样先不认，补上、核对过才照常。
    /// </summary>
    [Fact]
    public async Task LiveEventGap_HoldsTheEntryUntilTheGapIsRead()
    {
        var store = new List<EventRecord>();
        var (f, d, fake, live) = await Connected(h => ScriptEvents(h, store));
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        Assert.False(live.Ledger.Checking);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Gate = r => r.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal) ? gate.Task : null;
        lock (store) { store.Add(Ev(PutSeq + 1)); store.Add(Ev(PutSeq + 2)); }
        // WS 送来 99，98 没收到：跳号
        fake.RaiseEvent(new EventMsg("event", PutSeq + 2, 1, DateTime.UtcNow, "", "info", "op.channel.start", 2, "remote", "RV2", "PC-别人", null, "远程命令", "RV2 已启动"));
        Assert.True(live.Ledger.IsChecking(1));
        Assert.Null(live.Program(1));
        Assert.Equal(ProgramLedger.CheckingReason, live.LedgerReason(1));
        Assert.True(live.Facts().Channel(1)!.Checking);
        gate.SetResult();
        await Until(() => !live.Ledger.Checking);
        Assert.Equal(ProgramStatus.Loaded, live.Program(1)?.Status);
        Assert.Equal(PutSeq + 2, live.Ledger.Entry(1)!.CheckedSeq);
        Assert.False(live.Facts().Channel(1)!.Checking);
        await f.DisposeAsync();
    }

    /// <summary>
    /// 复核（低）：检查器的「运行」运行设备上那份不是本机下发的程序时，提示里没有程序名（以前是「RV1 程序「」开始运行」）：「1号机 · RV1 程序开始运行」；
    /// 本机下发、对得上时照写程序名。
    /// </summary>
    [Fact]
    public async Task ProgRun_ToastNamesOnlyTheLedgerProgram()
    {
        var (f, d, fake, live) = await Connected();
        var t0 = DateTime.UtcNow.AddMinutes(-50);
        fake.RaiseTelemetry(Frame(DateTime.UtcNow, LiveFrames.Prog(3, 4, "completed", startedAt: t0, startedBy: "远程（PC-别人）", message: "4 步全部完成"), temp: 40, state: "done"));
        f.RefreshNow();
        Assert.Null(live.Program(1));
        var o = await d.Actions.RunAsync(DeviceAction.ProgRun, ActionPayload.Ch(1));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · RV1 程序开始运行", o.Message);
        // 本机下发、待运行：写程序名
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        f.RefreshNow();
        o = await d.Actions.RunAsync(DeviceAction.ProgRun, ActionPayload.Ch(1));
        Assert.True(o.Ok, o.Message);
        Assert.Equal("1号机 · RV1 程序「加氢测试-01」开始运行", o.Message);
        Assert.Equal(2, fake.Http.Count("POST", "/channels/1/program/run"));
        Assert.Equal(0, fake.Http.Count("POST", "/run"));
        await f.DisposeAsync();
    }

    /// <summary>
    /// 复核（高）：续取不全就作废 —— 设备上已经没有断开期间的那段事件（环形缓冲覆盖、落盘也没有），或续取请求失败；
    /// 重新连上之后台账不再当成本机的程序（宁可降级，R5）。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CatchUpIncomplete_OnReconnect_Supersedes(bool requestFails)
    {
        var store = new List<EventRecord>();
        bool failing = false;
        var (f, d, fake, live) = await Connected(h => ScriptEvents(h, store, fail: () => failing));
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        Assert.True(live.Ledger.Entry(1)!.Valid);
        await live.DisconnectAsync();
        lock (store) for (long q = PutSeq + 60; q <= PutSeq + 400; q++) store.Add(Ev(q));   // 序号 98–156 已经不在设备上
        failing = requestFails;
        await live.ConnectAsync(CancellationToken.None);
        await Until(() => live.Ledger.Entry(1) is { Valid: false });
        Assert.Equal(ProgramLedger.CatchUpFailedReason, live.LedgerReason(1));
        Assert.Null(live.Program(1));
        await f.DisposeAsync();
    }

    /// <summary>
    /// WS 连上（hello）之后：hello 之前、上次核对之后的事件不会经 WS 送来（Client 从 hello 的序号头接着收）—— 再续取一遍，夹在中间的替换照样找到。
    /// </summary>
    [Fact]
    public async Task WsConnected_CatchesUpTheGapBeforeHello()
    {
        var store = new List<EventRecord>();
        var (f, d, fake, live) = await Connected(h => ScriptEvents(h, store));
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        lock (store) store.Add(Ev(PutSeq + 1, "op.program.replace", "PC-别人", "RV1 程序已更新（4 步）", "op-x"));
        fake.RaiseConnection(ConnectionState.Reconnecting);
        fake.RaiseConnection(ConnectionState.Connected);
        await Until(() => live.Ledger.Entry(1) is { Valid: false });
        Assert.Equal("程序已被 PC-别人 替换", live.LedgerReason(1));
        await f.DisposeAsync();
    }

    /// <summary>
    /// 复核（中）：真机的等待气路是程序排队等共用气路（别的反应釜正占用），不是样稿的「进气口关闭 · 需在触屏打开」（PORT-PLAN §3.5 / R6）——
    /// 反应釜页的状态条、检查器、时间轴的悬停都写「等待气路（RV3 占用中）」，没有「✋ 触屏操作」。
    /// </summary>
    [Fact]
    public async Task LiveWaitingGas_IsTheSharedGasLineQueue()
    {
        var (f, d, fake, live) = await Connected();
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        var t0 = DateTime.UtcNow.AddSeconds(-40);
        fake.RaiseTelemetry(Frame(t0.AddSeconds(1), LiveFrames.Prog(0, 4, startedAt: t0, startedBy: Me)));
        var fr = Frame(t0.AddSeconds(30), LiveFrames.Prog(1, 4, "waitingGas", startedAt: t0, startedBy: Me, message: "等待气路", waitingGas: true), state: "pressing");
        fake.RaiseTelemetry(fr with { Programs = fr.Programs with { GasHolder = 3, WaitingGas = new[] { 1 } } });
        f.RefreshNow();
        var r = d.Reactor(1)!;
        Assert.True(r.Program!.Progress!.WaitGas);
        var strip = Assert.Single(ReactorPageVm.AlertsOf(d, r), a => a.Key == "ra-wait");
        Assert.Equal(("等待气路（RV3 占用中）", "第 2/4 步「充压」暂停计时", false, "clock"), (strip.Title, strip.Text, strip.Touch, strip.Icon));
        // 检查器
        var shell = new Shell.ShellVm(f);
        shell.Select(d.K, 1);
        var insp = Assert.IsType<Shell.ReactorInspectorVm>(shell.Inspector.Body);
        Assert.Equal("等待气路（RV3 占用中） · 第 2/4 步「充压」暂停计时", insp.WaitText);
        Assert.Equal("clock", insp.WaitIcon);
        // 时间轴的悬停
        var segs = shell.Timeline.Groups.SelectMany(g => g.Rows.OfType<Shell.TlRowVm>()).SelectMany(x => x.Segments).ToList();
        Assert.NotEmpty(segs);
        Assert.DoesNotContain(segs, x => x.Tip.Contains("触屏开阀", StringComparison.Ordinal));
        Assert.Contains(segs, x => x.Tip.EndsWith(" · 等待气路（RV3 占用中）", StringComparison.Ordinal));
        await f.DisposeAsync();
    }

    /// <summary>
    /// 复核（中）：设备上跑着对不上台账的程序（不是本机下发的）—— 程序面板降级显示「RV1 程序 · 第 4/4 步」，检查器也要写这个程序（同总览卡片，§3.4），
    /// 不能写「没有程序 · 选择后下发」；不画步骤条，消息写在步骤条的位置。
    /// </summary>
    [Fact]
    public async Task Inspector_ShowsTheUnmatchedHmiProgram()
    {
        var (f, d, fake, live) = await Connected();
        var t0 = DateTime.UtcNow.AddMinutes(-50);
        fake.RaiseTelemetry(Frame(DateTime.UtcNow, LiveFrames.Prog(3, 4, startedAt: t0, startedBy: "远程（PC-别人）", message: "反应中 · 剩 30 分"), temp: 80, state: "reacting"));
        f.RefreshNow();
        Assert.Null(live.Program(1));
        var shell = new Shell.ShellVm(f);
        shell.Select(d.K, 1);
        var insp = Assert.IsType<Shell.ReactorInspectorVm>(shell.Inspector.Body);
        Assert.True(insp.HasProgram);
        Assert.Equal(("RV1 程序", "第 4/4 步", "运行中"), (insp.ProgName, insp.ProgText, insp.ProgMeta));
        Assert.False(insp.HasSteps);
        Assert.Null(insp.Steps);
        Assert.Equal("反应中 · 剩 30 分", insp.HmiMessage);
        Assert.Equal("RV1 程序 · 第 4/4 步 · 反应中 · 剩 30 分", insp.ProgTip);
        Assert.False(string.IsNullOrEmpty(insp.ProgLoad.Reason));   // 下发照样不能点（设备上的程序在跑）
        // 结束之后：「已结束 · 共 4 步」，没有消息
        fake.RaiseTelemetry(Frame(DateTime.UtcNow.AddSeconds(1), LiveFrames.Prog(3, 4, "completed", startedAt: t0, startedBy: "远程（PC-别人）", message: "4 步全部完成"), temp: 40, state: "done"));
        f.RefreshNow();
        shell.RefreshChrome();
        Assert.Equal(("已结束 · 共 4 步", "已结束"), (insp.ProgText, insp.ProgMeta));
        Assert.Null(insp.HmiMessage);
        await f.DisposeAsync();
    }

    /// <summary>复核（中，R10）：没有程序时的程序库每次刷新都要问 —— 草稿只读一次，同一份草稿返回同一个列表、同一批 ProgramDef（页面按身份比较选中项）。</summary>
    [Fact]
    public async Task Library_IsCached_NotReadEveryRefresh()
    {
        var (f, d, _, live) = await Connected();
        var a = live.Library(3);
        var b = live.Library(3);
        Assert.Same(a, b);
        Assert.Same(a[0], b[0]);
        Assert.Equal("draft-3", a[0].Id);
        Assert.Equal(a[0].Name, Assert.Single(live.ProgramChoices(3)).Name);
        var vm = new ReactorProgramVm(d.K, 3);          // RV3 空闲、没有程序：程序库
        vm.Refresh(d);
        var row = Assert.Single(vm.Library);
        vm.Refresh(d);
        Assert.True(vm.HasNoProgram);
        Assert.Same(row, Assert.Single(vm.Library));
        Assert.True(row.IsSelected);
        Assert.Same(a, live.Library(3));
        await f.DisposeAsync();
    }

    /// <summary>
    /// 复核（低）：真机曲线上的会话标记 —— 连上时取 GET /sessions/current 的标记（文字开头「RVn」的只画在 RVn 的曲线上，其余整机的每个反应釜都画），
    /// 打标记 / 会话开始 / 结束的事件之后重新取。
    /// </summary>
    [Fact]
    public async Task LiveMarks_FromTheCurrentSession()
    {
        var now = DateTime.UtcNow;
        var marks = new List<SessionMarker> { new(1, now.AddMinutes(-12), 40, "RV1 加料", "remote", "PC-测试"), new(2, now.AddMinutes(-6), 50, "整机检查", "local", null) };
        SessionDetailDto Current()
        {
            lock (marks)
                return new SessionDetailDto(true, "S1", "T1", "S1.csv", new SessionManifest("S1", "T1", null, now.AddHours(-1), null, SessionManifest.Active, "remote", "PC-测试",
                    true, 1, null, "S1.csv", marks.ToArray(), Array.Empty<SessionProgramRun>(), Array.Empty<SessionLeakTest>()), Array.Empty<string>());
        }
        var (f, d, fake, live) = await Connected(h => h.On("GET", "/sessions/current", _ => ScriptedHttpHandler.Ok(Current())));
        await Until(() => live.History.Marks.Count == 2);
        Assert.Equal(new int?[] { 1, null }, live.History.Marks.Select(m => m.N).ToArray());
        Assert.Equal("RV1 加料", live.History.Marks[0].Text);
        // RV1 的曲线：RV1 的与整机的；RV2 的：只有整机的
        f.RefreshNow();
        var c1 = new ReactorCurvesVm(d.K, 1);
        c1.Refresh(d);
        Assert.Equal(new[] { "RV1 加料", "整机检查" }, c1.Options!.Markers.Select(m => m.Label).ToArray());
        var c2 = new ReactorCurvesVm(d.K, 2);
        c2.Refresh(d);
        Assert.Equal(new[] { "整机检查" }, c2.Options!.Markers.Select(m => m.Label).ToArray());
        // 打标记之后重新取
        lock (marks) marks.Add(new SessionMarker(3, now, 60, "RV2 取样", "remote", "PC-测试"));
        fake.RaiseEvent(new EventMsg("event", 60, 1, now, "", "info", "session.marker", null, "system", "系统", null, null, "标记", "RV2 取样 · 会话 S1"));
        await Until(() => live.History.Marks.Count == 3);
        Assert.Equal(2, live.History.Marks[2].N);
        Assert.Null(LiveDeviceSource.MarkRv("整机 RV2"));
        Assert.Equal(12, LiveDeviceSource.MarkRv(" RV12 标记"));
        Assert.Null(LiveDeviceSource.MarkRv("RV123"));
        await f.DisposeAsync();
    }

    [Fact]
    public async Task InMemoryConfig_WritesNothing()
    {
        var (f, _, _, live) = await Connected();
        await live.UploadProgramAsync(1, Doc(), CancellationToken.None);
        Assert.False(live.Ledger.Persistent);
        Assert.Null(live.Ledger.FilePath);
        await f.DisposeAsync();
    }
}
