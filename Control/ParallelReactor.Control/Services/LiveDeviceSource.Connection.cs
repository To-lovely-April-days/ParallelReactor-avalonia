using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ClientLease = ParallelReactor.Client.LeaseState;
using FleetActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Services;

/// <summary>
/// LiveDeviceSource 的连接与控制权（M3「LiveDeviceSource 加监督循环」，PORT-PLAN §5 M3 / §3.1 conn / R7 / R8 / R9 / R19）。
/// <para><b>连接的四种状态</b>（样稿 conn.state，文字见 Core 的 DeviceTexts.Conn）：</para>
/// <list type="bullet">
/// <item><b>已断开</b>（Disconnected）：没开始连（登记了、没勾自动连接 / 没有令牌）或用户断开；按钮「连接」。</item>
/// <item><b>正在连接 / 正在重连</b>（Connecting）：一次连接尝试正在进行 —— REST 起步（GET /device + /control + /snapshot），或 REST 已通、WS 在握手 / 等 hello。</item>
/// <item><b>已连接</b>（Online）：WS 收到 hello（实时数据在流）。<see cref="_everOnline"/> 从此为 true（样稿 everOnline，会话内不清）。</item>
/// <item><b>断线</b>（Offline）：在等下一次尝试，带倒计时 —— 从未连上「连接失败 · 5 s 后重试」，连上过「连接中断 · 重连中 5 s」。两种等待：
///   ① 首次连接（REST 起步）失败：本类自己的重试等待（<see cref="RetryDelay"/>：2 s 起翻倍、最长 10 s，经 <see cref="IClock.Delay"/>）；
///   ② 连接对象已建好、WS 断了或握手被拒：Client 的退避（0.5 → 10 s），倒计时取 TelemetryWsClient.NextRetryAt。</item>
/// </list>
/// 「立即重连」打断任一种等待（① 叫醒自己的等待，② Client 的 ReconnectNow）。失败原因（REST 的 Describe / WS 的 WsConnectException 文字）记在 <see cref="LastError"/>，
/// 同一轮里每种原因只提示一次（「1号机 · 连接失败：…」；连上过的设备断线、网络层的原因不提示 —— 徽标已经写着「连接中断」）。
/// </summary>
public sealed partial class LiveDeviceSource
{
    // ———————————————————— 监督：首次连接失败后的倒计时重试 ————————————————————

    /// <summary>首次连接（REST 起步）失败后的重试间隔：<see cref="RetryMin"/> 起每次翻倍，最长 <see cref="RetryMax"/>（同 Client 的 WS 退避上限 ReconnectMax）。</summary>
    public static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(2), RetryMax = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 断开 / 移除时，已取消的控制权申请还在等触屏（R7）：WS 先停（不再收数据），REST 留给 Client 的后台守卫最多这么久（它自己的截止 =
    /// 申请上限 70 s + 心跳超时 + 宽限，默认 ≈ 140 s），之后再释放连接。
    /// </summary>
    public static readonly TimeSpan GuardWait = TimeSpan.FromMinutes(3);

    /// <summary>第 <paramref name="failures"/> 次连续失败之后等多久（1 → 2 s，2 → 4 s，3 → 8 s，之后 10 s）。</summary>
    public static TimeSpan RetryDelay(int failures)
    {
        if (failures <= 1) return RetryMin;
        double s = RetryMin.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 16));
        return s >= RetryMax.TotalSeconds ? RetryMax : TimeSpan.FromSeconds(s);
    }

    /// <summary>非空 = 在监督（要连着）：用户断开、改地址、释放时取消。</summary>
    private CancellationTokenSource? _sup;
    /// <summary>这一轮监督的第一次连接尝试（StartAsync 等它：成功或失败都算完成）。</summary>
    private TaskCompletionSource? _firstAttempt;
    /// <summary>首次连接的重试等待中：「立即重连」完成它。</summary>
    private TaskCompletionSource<bool>? _wake;
    /// <summary>首次连接的下一次尝试时刻（<see cref="IClock.UtcNow"/>）；不在等待为 null。</summary>
    private DateTime? _retryAt;
    /// <summary>这一轮连续失败的连接尝试（REST 起步阶段；连上清零）。</summary>
    private int _failures;
    /// <summary>这一轮已经提示过的失败原因（同一原因只提示一次；连上清空）。</summary>
    private string? _reported;
    /// <summary>当前连接对象的 WS 状态（ConnectionStateChanged 事件）。</summary>
    private ConnectionState _ws = ConnectionState.Disconnected;
    /// <summary>当前连接对象的 StartAsync（REST 起步）已经完成。</summary>
    private bool _restReady;
    /// <summary>事件处理器挂在哪个连接对象上（摘的时候只摘它的）。</summary>
    private IReactorConnection? _attachedTo;
    /// <summary>上次连上时设备的 bootId（重新连上时变了 = HMI 在断线期间重启过）。</summary>
    private string? _bootId;
    /// <summary>本机发出控制权申请的时刻（Client 时钟）：POST 还没回来、不知道服务端的到期时刻时，倒计时按它算。</summary>
    private DateTime? _requestAt;

    // ———— M3 复核：停止类能不能送达要有证据（StopReachable）；本机旧租约；申请超时后的冷却 ————

    /// <summary>
    /// REST 通的正面证据（<see cref="StopReachable"/>）：REST 起步完成、WS hello、WS 握手被 HTTP 拒绝、探测 GET /health 有响应、命令送达 → true；
    /// 最近一次是网络层失败（WS 连不上、探测失败、命令网络错误 / 超时）→ false。换连接时清零。
    /// </summary>
    private bool _restOk;
    /// <summary>已经按它记过证据的 WS 连接失败（Client 的 LastError；同一个失败只记一次，之后由探测更新）。</summary>
    private Exception? _wsErrorSeen;
    /// <summary>WS 没连上时的 REST 探测（GET /health，见 <see cref="ProbeLoopAsync"/>）；非空 = 在探测。</summary>
    private CancellationTokenSource? _probe;
    /// <summary>这条连接上本机申请过 / 持有过控制权：之后「HMI 说持有者是本机、本机却没持有」只是自己刚释放的过渡，不算本机旧租约。</summary>
    private bool _leaseUsed;
    /// <summary>本机的申请超时后 HMI 的冷却到哪一刻（本机时钟）：HMI 超时同样冷却 30 s，Client 只在拒绝 / 429 时记 DenyUntil，这一份 App 自己记。</summary>
    private DateTime? _cooldownUntil;
    /// <summary>Client 的 DenyUntil 来自 429 RATE_LIMITED（原因不确定：可能是之前的拒绝或超时），不是确知的触屏拒绝。</summary>
    private bool _denyNeutral;
    /// <summary>申请中时 HMI 的到期时刻（本机时钟，最近一次算出的）：申请超时后的冷却从它起算。</summary>
    private DateTime? _requestDue;

    /// <summary>WS 没连上（断线后重连中、握手被拒在退避）时探测 REST 的间隔与单次时限：拔线 / 主机不回包时不必等 WS 的连接超时（系统的 TCP 超时可达 20–75 s）。</summary>
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(3), ProbeTimeout = TimeSpan.FromSeconds(3);

    /// <summary>HMI 在触屏拒绝 / 申请超时后对同一令牌的冷却（ControlLease.DenyCooldown 默认 30 s；Client 同样按 30 s 记 DenyUntil）。</summary>
    public static readonly TimeSpan DenyCooldown = TimeSpan.FromSeconds(30);

    /// <summary>在监督中（开始连接之后、用户断开 / 释放之前）。</summary>
    public bool IsSupervising => _sup != null;

    /// <summary>
    /// 开始「要连着」（首次连接的入口：DeviceRegistry 启动时对勾了自动连接且存了令牌的设备并行调用、「添加并连接」之后、设备管理 / 菜单的「连接」）。
    /// 返回的任务在<b>第一次连接尝试</b>有结果（连上或失败）时完成，不抛；失败后在后台倒计时重试，直到连上、用户断开或释放。已在监督时 = 立即重连。
    /// 没有令牌时不连接（「已断开」，<see cref="LastError"/>「请输入访问令牌」）。
    /// </summary>
    public Task StartAsync()
    {
        if (_disposed) return Task.CompletedTask;
        if (_sup != null)
        {
            Wake();
            return _firstAttempt?.Task ?? Task.CompletedTask;
        }
        if (string.IsNullOrWhiteSpace(Config.Token))
        {
            LastError = "请输入访问令牌";
            SetConn(ConnStatus.Disconnected);
            return Task.CompletedTask;
        }
        var sup = new CancellationTokenSource();
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _sup = sup;
        _firstAttempt = first;
        _failures = 0;
        _reported = null;
        _ = SuperviseAsync(sup, first);
        return first.Task;
    }

    /// <summary>
    /// 监督循环（从 UI 线程开始，续体回到 UI 线程）：连一次（REST 起步）；失败 → 倒计时（<see cref="RetryDelay"/>）→ 再连，直到连上（之后 WS 的重连归 Client）、
    /// 被取消（断开 / 改地址 / 释放）。
    /// </summary>
    private async Task SuperviseAsync(CancellationTokenSource sup, TaskCompletionSource first)
    {
        var ct = sup.Token;
        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                _retryAt = null;
                SetConn(ConnStatus.Connecting);
                Exception? error = null;
                try { await ConnectCoreAsync(ct).ConfigureAwait(true); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { error = ex; }
                if (!ReferenceEquals(_sup, sup) || _disposed) return;   // 已被断开 / 改地址接手
                if (error == null) { _failures = 0; return; }           // 连上了（REST 起步完成）：之后 WS 的断线重连由 Client 自己做
                _failures++;
                ReportFailure(FleetActions.Describe(error), toast: true);
                first.TrySetResult();
                // 倒计时等下一次（「立即重连」可打断）
                var wait = RetryDelay(_failures);
                var wake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _wake = wake;
                _retryAt = _clock.UtcNow + wait;
                SetConn(ConnStatus.Offline);
                MarkDirty(SourceChange.Conn);
                using (var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    var delay = _clock.Delay(wait, waitCts.Token);
                    await Task.WhenAny(delay, wake.Task).ConfigureAwait(true);
                    waitCts.Cancel();   // 被叫醒时撤掉还挂着的等待（假时钟随即移除它）
                }
                if (ReferenceEquals(_wake, wake)) _wake = null;
                _retryAt = null;
            }
        }
        finally { first.TrySetResult(); }
    }

    /// <summary>打断首次连接的重试等待（立即再试）。</summary>
    private bool Wake()
    {
        var w = _wake;
        return w != null && w.TrySetResult(true);
    }

    /// <summary>停止监督（取消重试等待与正在进行的连接尝试）。</summary>
    private void StopSupervision()
    {
        var sup = _sup;
        _sup = null;
        _wake = null;
        _retryAt = null;
        if (sup != null) { try { sup.Cancel(); } catch { } }
        _firstAttempt?.TrySetResult();
    }

    private ClientOptions NewOptions() => new(Config.BaseUrl, Config.Token ?? "", _clientName)
    {
        UiContext = _uiContext,
        ClientVersion = "pc-" + (typeof(LiveDeviceSource).Assembly.GetName().Version?.ToString(3) ?? "1.0"),
        // 样稿：宽限期满「控制权已交回触屏」，要不要再申请由用户决定 —— 不让 Client 自动重新申请（重连 / HMI 重启后回到本地时同样）
        AutoReacquireLease = false,
        // 没有人按顺序读 Messages 队列（界面与性能测试都订阅事件）：多台同时连接时省掉每台最多 2000 条消息的内存（ClientOptions 的说明）
        MessageQueueCapacity = 0,
    };

    /// <summary>
    /// 一次连接尝试（REST 起步）：新建连接、挂上全部事件、StartAsync（GET /device + /control + /snapshot，再启动 WS）。失败时摘掉事件、释放连接再抛出。
    /// 成功后状态由 WS 决定（hello → 已连接；之前 = 正在连接，握手被拒 → 断线倒计时）。
    /// </summary>
    private async Task ConnectCoreAsync(CancellationToken ct)
    {
        var c = _factory(NewOptions());
        _ws = ConnectionState.Disconnected;
        _restReady = false;
        _restOk = false;
        _wsErrorSeen = null;
        _leaseUsed = false;
        StopProbe();
        StopPoll();
        Connection = c;
        Attach(c);
        try
        {
            await c.StartAsync(ct).ConfigureAwait(true);
            if (!ReferenceEquals(Connection, c)) throw new OperationCanceledException("连接已被接手");
        }
        catch
        {
            if (ReferenceEquals(Connection, c)) Connection = null;
            Detach(c);
            try { await c.DisposeAsync().ConfigureAwait(true); } catch { }
            throw;
        }
        _restReady = true;
        _restOk = true;   // REST 起步（GET /device + /control + /snapshot）刚完成：主机通 —— WS 还没连上 / 被拒时停止类照样能发
        Info = c.Device;
        _model = Info != null ? _models.Resolve(Info, Settings) : null;
        // 断线期间 HMI 重启过（新的连接对象：WS 的 ServerRestarted 不会报）：旧启动的报警、本机下发记录都作废
        bool restarted = _bootId != null && Info?.BootId is { } boot && boot != _bootId;
        if (Info?.BootId is { } b) _bootId = b;
        if (restarted) OnRestartedCore(refetch: false);
        // 台账文件上次读不了（IO / 权限，文件没动、本次不写盘）而内存里还什么都没记：再读一次（复核）
        Ledger.RetryLoad();
        // 程序台账：认这次的启动号（别的启动号的条目作废 —— 包括本机重启后从台账文件读回来的、HMI 期间重启过的）
        if (_bootId is { Length: > 0 } nb) Ledger.ObserveBoot(nb);
        // 离线期间的事件核对完（LoadExtrasAsync → 续取）之前，台账的条目先不认：不按台账显示、「启动」先禁用（复核 R5：以前这段时间里还能运行没核对过的程序）
        Ledger.BeginCheck();
        ReportLedgerError();
        // 程序库（本机的草稿）：每次连上重读一次 drafts.json（平时缓存着，见 Library；别的进程改过草稿时这里跟上）
        _drafts = null;
        MarkDirty(SourceChange.All);
        UpdateConnFromWs();
        _ = LoadExtrasAsync(c, refetchDevice: false);
        StartPoll(c);
    }

    /// <summary>
    /// 连接（一次，不重试；测试与旧调用方用）：已有连接时先断开（释放控制权），连不上时摘掉事件、释放连接、回到「已断开」再抛出。
    /// 界面的连接走 <see cref="StartAsync"/>（带监督）。
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LiveDeviceSource));
        StopSupervision();
        if (Connection != null) await CloseAsync(releaseLease: true).ConfigureAwait(true);
        SetConn(ConnStatus.Connecting);
        try
        {
            await ConnectCoreAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LastError = FleetActions.Describe(ex);
            SetConn(ConnStatus.Disconnected);
            throw;
        }
    }

    /// <summary>连上之后的读取（尽力而为）：/settings（型号的量程）、/alarms（当前报警，整个替换）、最近的事件；HMI 重启后先重取 /device。</summary>
    private async Task LoadExtrasAsync(IReactorConnection c, bool refetchDevice)
    {
        if (refetchDevice)
        {
            try
            {
                var info = await c.Rest.DeviceAsync(CancellationToken.None).ConfigureAwait(true);
                if (!ReferenceEquals(c, Connection)) return;
                Info = info;
                _bootId = info.BootId;
                _model = _models.Resolve(info, Settings);
                if (!string.IsNullOrEmpty(info.BootId)) Ledger.ObserveBoot(info.BootId);
                MarkDirty(SourceChange.Info);
            }
            catch { }
        }
        _settingsAt = _clock.UtcNow;
        try
        {
            var s = await c.Rest.SettingsAsync(CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(c, Connection)) return;
            Settings = s;
            if (Info != null) _model = _models.Resolve(Info, s);
            MarkDirty(SourceChange.Info);
        }
        catch { }
        try
        {
            var a = await c.Rest.AlarmsAsync(null, CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(c, Connection)) return;
            ReplaceAlarms(a.Items);
        }
        catch { }
        try
        {
            // 最近 200 条（PORT-PLAN §3.2 第 8 条：afterSeq = max(0, EventSeqHead − 200)）；还有更多时用簿的续取序号接着取（最多几页，之后跟随 WS）
            long after = Core.Events.EventBook.InitialAfterSeq(Info?.EventSeqHead ?? 0);
            for (int page = 0; page < EventPagesMax; page++)
            {
                var p = await c.Rest.EventsAsync(after, Core.Events.EventBook.InitialBack, CancellationToken.None).ConfigureAwait(true);
                if (!ReferenceEquals(c, Connection)) return;
                if (_eventBook.Load(p)) MarkDirty(SourceChange.Events);
                if (!_eventBook.More || _eventBook.NextAfterSeq <= after) break;
                after = _eventBook.NextAfterSeq;
            }
        }
        catch { }
        if (!ReferenceEquals(c, Connection)) return;
        // 程序台账：断开期间的事件从每个条目核对到的位置续取（不是只看最近 200 条 —— 复核 R5，见 LedgerCatchUpAsync）
        RequestLedgerCatchUp(c);
        await LoadMarksAsync(c).ConfigureAwait(true);
        await PollCommAsync(c).ConfigureAwait(true);
    }

    // ———————————————————— M5 复核：程序台账的事件核对（断开期间别的客户端替换了程序） ————————————————————

    /// <summary>续取事件每页多少条（HMI 的 GET /events 上限 1000）。</summary>
    public const int LedgerPageSize = 1000;
    /// <summary>一次续取最多几页（5 万条：断开期间的事件再多就不核对了，作废、要重新下发）。</summary>
    public const int LedgerPagesMax = 50;

    /// <summary>正在续取 / 续取时又有人要求（跑完再来一遍）。</summary>
    private bool _catchUpRunning, _catchUpAgain;
    /// <summary>上一次把台账的核对位置写盘的时刻（<see cref="IClock"/>）。</summary>
    private DateTime _cursorFlushAt;

    /// <summary>台账核对位置定时写盘的间隔（落后的位置只会让下次多核对几条，不会漏）。</summary>
    public static readonly TimeSpan CursorFlushInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// 要求核对一次（REST 起步之后、WS 每次连上（hello）、实时事件跳号时）：有效条目从各自核对到的序号续取 GET /events 直到序号头（ProgramLedger.ObservePage）。
    /// 没有要核对的有效条目时什么都不做（不发请求）。一次只跑一个，跑的时候又要求了就再跑一遍（WS hello 的序号头可能在第一遍之后）。
    /// </summary>
    private void RequestLedgerCatchUp(IReactorConnection c)
    {
        if (_disposed || !ReferenceEquals(c, Connection)) return;
        var l = Ledger;
        if (l.CatchUpAfter is null && !l.Entries.Any(e => e.Valid && e.CheckedSeq is null))
        {
            EndLedgerCheck();   // 没有要核对的有效条目
            return;
        }
        _ = LedgerCatchUpAsync(c);
    }

    private async Task LedgerCatchUpAsync(IReactorConnection c)
    {
        if (_catchUpRunning) { _catchUpAgain = true; return; }
        _catchUpRunning = true;
        try
        {
            do
            {
                _catchUpAgain = false;
                await LedgerCatchUpOnceAsync(c).ConfigureAwait(true);
            } while (_catchUpAgain && ReferenceEquals(c, Connection) && !_disposed);
        }
        finally { _catchUpRunning = false; }
        if (_disposed) return;
        // 这次连接的续取做完了（核对到序号头，或核对不了的已经作废）：条目照常匹配。
        // 跑的时候连接换了：新连接在这期间要求的那一遍还没跑（被合并进了这一轮）—— 给新连接再跑
        if (ReferenceEquals(c, Connection)) EndLedgerCheck();
        else if (_catchUpAgain && _restReady && Connection is { } now) RequestLedgerCatchUp(now);
    }

    /// <summary>台账结束核对窗口（<see cref="ProgramLedger.EndCheck"/>）：有条目从「核对中」变回来时重算程序、刷新界面。</summary>
    private void EndLedgerCheck()
    {
        if (!Ledger.EndCheck()) return;
        _progCache.Clear();
        MarkDirty(SourceChange.Programs);
    }

    /// <summary>
    /// 续取一遍：不知道核对位置的条目作废；从最早的核对位置起一页一页取（每页 <see cref="LedgerPageSize"/>，最多 <see cref="LedgerPagesMax"/> 页）交给台账核对，
    /// 直到序号头。设备上已经没有中间那段事件、序号纪元变了、请求失败、页数超限 → 还落在后面的有效条目作废（宁可降级，R5；这次连接上刚下发、
    /// 一直连续核对着的照样有效，复核，见 ProgramLedger.FailCatchUp）；连接换了就不管了。
    /// </summary>
    private async Task LedgerCatchUpOnceAsync(IReactorConnection c)
    {
        var l = Ledger;
        int v = l.Version;
        l.SupersedeUnchecked();
        long? head = null;   // 这一遍取到过的序号头（最后一页的）：失败时核对位置已经到了它的条目不算落在后面
        try
        {
            for (int page = 0; ; page++)
            {
                if (l.CatchUpAfter is not long after) break;
                if (page >= LedgerPagesMax) { l.FailCatchUp(head: head); break; }
                var p = await c.Rest.EventsAsync(after, LedgerPageSize, CancellationToken.None).ConfigureAwait(true);
                if (!ReferenceEquals(c, Connection) || _disposed) return;
                head = p.EventSeqHead;
                if (!l.ObservePage(p, after)) break;
            }
        }
        catch (Exception) when (ReferenceEquals(c, Connection) && !_disposed)
        {
            l.FailCatchUp(head: head);
        }
        catch { return; }
        if (l.CursorDirty) { l.FlushCursors(); _cursorFlushAt = _clock.UtcNow; }
        if (l.Version != v) { _progCache.Clear(); MarkDirty(SourceChange.Programs); }
    }

    // ———————————————————— M5 复核：曲线上的会话标记（真机） ————————————————————

    /// <summary>当前会话的标记（GET /sessions/current 的 manifest.markers）；没有会话 / 还没取到为空。</summary>
    private IReadOnlyList<Core.Chart.SessionMark> _marks = Array.Empty<Core.Chart.SessionMark>();
    private bool _marksLoading, _marksAgain;

    /// <summary>
    /// 取当前会话的标记（连上时、会话开始 / 结束 / 打标记的事件之后）：manifest 的每个标记 → SessionMark(时刻（UTC，设备时钟）, 文字, 文字开头「RVn」的反应釜号，
    /// 没有为 null = 整机的，每个反应釜的曲线都画；A11 之前 HMI 的标记没有反应釜号）。没有会话 → 空。失败不提示（下一次事件再取）。
    /// 正在取的时候又要求了（会话事件、新连接连上）就再取一遍；这一遍失败了或连接换了，排着的那一遍照样给当前连接取（同 LedgerCatchUpAsync，复核：以前丢掉了）。
    /// </summary>
    private async Task LoadMarksAsync(IReactorConnection c)
    {
        if (_marksLoading) { _marksAgain = true; return; }
        _marksLoading = true;
        try
        {
            do
            {
                _marksAgain = false;
                SessionDetailDto? s;
                try { s = await c.Rest.SessionCurrentAsync(CancellationToken.None).ConfigureAwait(true); }
                catch { return; }
                if (!ReferenceEquals(c, Connection) || _disposed) return;
                var next = MarksOf(s);
                if (!SameMarks(next, _marks))
                {
                    _marks = next;
                    MarkDirty(SourceChange.Frame);
                }
            } while (_marksAgain && ReferenceEquals(c, Connection) && !_disposed);
        }
        finally
        {
            _marksLoading = false;
            // 这一遍失败了 / 连接换了而还有排着的：给当前连接再取（失败时没有排着的就等下一次事件，不重试成死循环）
            if (_marksAgain && !_disposed && _restReady && Connection is { } now) _ = LoadMarksAsync(now);
        }
    }

    /// <summary>会话详情 → 曲线的标记（没有活动会话、没有 manifest 为空）。</summary>
    public static IReadOnlyList<Core.Chart.SessionMark> MarksOf(SessionDetailDto? s)
    {
        if (s is not { Active: true, Manifest: { } m } || m.Markers is not { Length: > 0 } list) return Array.Empty<Core.Chart.SessionMark>();
        var a = new Core.Chart.SessionMark[list.Length];
        for (int i = 0; i < list.Length; i++)
        {
            var x = list[i];
            var at = x.Ts.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(x.Ts, DateTimeKind.Utc) : x.Ts.ToUniversalTime();
            a[i] = new Core.Chart.SessionMark(at, x.Text ?? "", MarkRv(x.Text));
        }
        return a;
    }

    private static readonly System.Text.RegularExpressions.Regex MarkRvRe = new(@"^\s*RV(\d{1,2})(?!\d)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>标记文字开头的「RVn」→ n（「RV3 标记」→ 3）；没有为 null（整机的标记）。</summary>
    public static int? MarkRv(string? text)
    {
        var m = MarkRvRe.Match(text ?? "");
        return m.Success && int.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) ? n : null;
    }

    private static bool SameMarks(IReadOnlyList<Core.Chart.SessionMark> a, IReadOnlyList<Core.Chart.SessionMark> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return false;
        return true;
    }

    // ———————————————————— M4：/device/comm 与 /settings 轮询 ————————————————————

    /// <summary>
    /// 在线时 GET /device/comm 的间隔（PORT-PLAN §5 M4「数据」：每 5–10 s；设备信息面板「模块通讯」的在线 / 最近成功）。
    /// 模块是否在线另外每帧都有（CommSnap），这里主要是「最近成功」的时刻。
    /// </summary>
    public static readonly TimeSpan CommPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>GET /settings 的间隔（连上时取一次，之后每 60 s 一次：量程、溶液上限、搅拌上限在触屏上改了之后型号表跟着变）。</summary>
    public static readonly TimeSpan SettingsPollInterval = TimeSpan.FromSeconds(60);

    /// <summary>连上时最多续取几页事件（每页 200 条）。</summary>
    private const int EventPagesMax = 5;

    /// <summary>最近一次取 /settings 的时刻（<see cref="IClock"/>）；轮询按它算下一次。</summary>
    private DateTime _settingsAt;

    /// <summary>非空 = 在轮询（每条连接一个循环，换连接 / 断开 / 释放时取消）。</summary>
    private CancellationTokenSource? _poll;

    /// <summary>开始轮询这条连接（已经在轮询、连接换了或已释放时什么都不做）。</summary>
    private void StartPoll(IReactorConnection c)
    {
        if (_poll != null || _disposed || !ReferenceEquals(c, Connection)) return;
        var cts = new CancellationTokenSource();
        _poll = cts;
        _ = PollLoopAsync(c, cts);
    }

    private void StopPoll()
    {
        var p = _poll;
        _poll = null;
        if (p != null) { try { p.Cancel(); } catch { } }
    }

    /// <summary>
    /// 轮询循环（UI 线程上开始，续体回到 UI 线程；时间按 <see cref="IClock"/>，测试用假时钟）：每 <see cref="CommPollInterval"/> 一次，在线（WS hello）时
    /// GET /device/comm；距上次取 /settings 满 <see cref="SettingsPollInterval"/> 时再取 /settings（型号的量程跟着重新解析）。断线时只等、不请求。
    /// 换连接、断开、释放时停。请求失败不提示（下一轮再试；连接状态另有监督）。
    /// </summary>
    private async Task PollLoopAsync(IReactorConnection c, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.Delay(CommPollInterval, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || !ReferenceEquals(c, Connection) || _disposed) return;
                if (_conn != ConnStatus.Online) continue;
                await PollCommAsync(c).ConfigureAwait(true);
                if (_clock.UtcNow - _settingsAt >= SettingsPollInterval - TimeSpan.FromMilliseconds(1)) await PollSettingsAsync(c).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_poll, cts)) _poll = null;
            cts.Dispose();
        }
    }

    /// <summary>GET /device/comm 一次（失败不提示）。</summary>
    private async Task PollCommAsync(IReactorConnection c)
    {
        try
        {
            var comm = await c.Rest.CommAsync(CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(c, Connection) || _disposed) return;
            Comm = comm;
            MarkDirty(SourceChange.Info);
        }
        catch { }
    }

    /// <summary>GET /settings 一次：型号的量程按它重新解析（失败不提示）。</summary>
    private async Task PollSettingsAsync(IReactorConnection c)
    {
        _settingsAt = _clock.UtcNow;
        try
        {
            var s = await c.Rest.SettingsAsync(CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(c, Connection) || _disposed) return;
            Settings = s;
            if (Info != null) _model = _models.Resolve(Info, s);
            MarkDirty(SourceChange.Info);
        }
        catch { }
    }

    /// <summary>
    /// 立即重连（样稿 reconnect）：已断开 / 还没开始连 → 开始监督（等第一次尝试的结果）；首次连接的重试等待中 → 立即再试；
    /// WS 在退避等待 → Client 的 ReconnectNow（立刻发起下一次握手）。正在连接 / 已连接时什么也不做。
    /// </summary>
    public async Task ReconnectNowAsync()
    {
        if (_disposed) return;
        if (_sup == null && Connection == null) { await StartAsync().ConfigureAwait(true); return; }
        if (Wake()) return;
        if (Connection is { } c)
        {
            try { c.ReconnectNow(); } catch { }
            UpdateConnFromWs();
        }
    }

    /// <summary>
    /// 改了设备设置（编辑设置 / 重命名）：别名立即生效；地址、端口或令牌变了且正连着（或正在连 / 在重试等待）时先释放控制权（≤ 3 s）、取消申请、断开，
    /// 再按新设置连接（样稿 editDevice：地址改变 → 重新连接）。换了令牌时学到的令牌权限作废。
    /// 换了地址 / 端口（后面可能是另一台设备）：曲线先按旧设备的时钟差断开，再忘掉旧设备的时钟差与会话标记（复核）。
    /// </summary>
    public async Task UpdateConfigAsync(DeviceConfig config)
    {
        if (config.Id != Config.Id) throw new ArgumentException("不是这台设备的配置", nameof(config));
        var old = Config;
        Config = config;
        bool tokenChanged = !string.Equals(old.Token ?? "", config.Token ?? "", StringComparison.Ordinal);
        if (tokenChanged) _role = TokenRole.Unknown;
        bool hostChanged = !string.Equals(old.Host, config.Host, StringComparison.OrdinalIgnoreCase) || old.Port != config.Port;
        bool moved = hostChanged || tokenChanged;
        MarkDirty(SourceChange.Info);
        if (!moved || _disposed || (_sup == null && Connection == null))
        {
            if (hostChanged && !_disposed) ForgetTarget();
            return;
        }
        StopSupervision();
        await CloseAsync(releaseLease: true).ConfigureAwait(true);
        if (hostChanged)
        {
            SetConn(ConnStatus.Connecting);   // 离开「已连接」：断开标记按记下的（旧设备的）时钟差插，之后才忘掉它
            ForgetTarget();
        }
        await StartAsync().ConfigureAwait(true);
    }

    /// <summary>换了连接目标（地址 / 端口）：旧设备的时钟差、会话标记不再算数。</summary>
    private void ForgetTarget()
    {
        ForgetClock();
        if (_marks.Count > 0)
        {
            _marks = Array.Empty<Core.Chart.SessionMark>();
            MarkDirty(SourceChange.Frame);
        }
    }

    /// <summary>
    /// 用户断开：停止监督；持有控制权时先释放（≤ 3 s）、正在申请时先取消（R7：见 <see cref="CloseAsync"/>），摘掉事件，释放连接；
    /// 设备留在列表里（「已断开」，可以再连）。
    /// </summary>
    public async Task DisconnectAsync()
    {
        StopSupervision();
        await CloseAsync(releaseLease: true).ConfigureAwait(true);
        SetConn(ConnStatus.Disconnected);
        MarkDirty(SourceChange.All);
    }

    /// <summary>
    /// 只释放控制权、不断开（退出前 DeviceRegistry 对全部设备并行调用，每台 ≤ <paramref name="timeout"/>）：本机持有（远程 / 宽限）时 POST /control/release；
    /// 正在申请时先取消，再在时限内等 Client 的后台守卫（R7：触屏在这期间批准就立即释放）。不抛。
    /// </summary>
    public async Task ReleaseLeaseQuietAsync(TimeSpan timeout)
    {
        var c = Connection;
        if (c == null) return;
        _releaseTried = true;
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            if (c.Lease.State == ClientLease.Requesting) await c.Lease.CancelRequestAsync().ConfigureAwait(false);
            if (c.Lease.State is ClientLease.Holding or ClientLease.Lost) await c.Lease.ReleaseAsync(cts.Token).ConfigureAwait(false);
            if (c.Lease.CancelledRequestPending) await c.Lease.WaitCancelledRequestsAsync(cts.Token).ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>
    /// 关掉当前连接：摘掉事件（程序台账不动：断开不清，重新连上时从核对位置续取，§4.8「程序台账」）；正在申请 → 先取消（Client 的 CancelRequestAsync）；持有 → 释放（≤ 3 s）；
    /// 已取消的申请还在等触屏（R7）→ 先停掉 WS，REST 留给后台守卫（最多 <see cref="GuardWait"/>），守卫结束再释放连接；否则立即释放。
    /// </summary>
    private async Task CloseAsync(bool releaseLease)
    {
        var c = Connection;
        if (c == null) return;
        Detach(c);
        Connection = null;
        StopProbe();
        StopPoll();
        _restReady = false;
        _restOk = false;
        _wsErrorSeen = null;
        _ws = ConnectionState.Disconnected;
        _requestAt = null;
        _requestDue = null;
        try
        {
            if (c.Lease.State == ClientLease.Requesting) await c.Lease.CancelRequestAsync().ConfigureAwait(false);
            if (releaseLease && c.Lease.State is ClientLease.Holding or ClientLease.Lost)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await c.Lease.ReleaseAsync(cts.Token).ConfigureAwait(false); } catch { }
            }
            if (c.Lease.CancelledRequestPending)
            {
                try { await c.Ws.StopAsync().ConfigureAwait(false); } catch { }
                _ = DisposeAfterGuardsAsync(c);
                return;
            }
            await c.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }

    private static async Task DisposeAfterGuardsAsync(IReactorConnection c)
    {
        try
        {
            using var cts = new CancellationTokenSource(GuardWait);
            await c.Lease.WaitCancelledRequestsAsync(cts.Token).ConfigureAwait(false);
        }
        catch { }
        try { await c.DisposeAsync().ConfigureAwait(false); } catch { }
    }

    /// <summary>释放：停止监督、摘掉全部事件、释放控制权（≤ 3 s）/ 取消申请、释放连接。可在任意线程调用。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopSupervision();
        try { _ledger?.FlushCursors(); } catch { }
        await CloseAsync(releaseLease: !_releaseTried).ConfigureAwait(false);
        Changed = null;
        Notice = null;
    }

    // ———————————————————— 事件 ————————————————————

    private void Attach(IReactorConnection c)
    {
        if (_attachedTo != null) Detach(_attachedTo);
        c.TelemetryReceived += OnTelemetry;
        c.ConnectionStateChanged += OnConnState;
        c.LeaseTransitioned += OnLease;
        c.ControlChanged += OnControl;
        c.AlarmChanged += OnAlarm;
        c.EventReceived += OnEvent;
        c.Error += OnError;
        c.ServerRestarted += OnRestarted;
        // M3：重连倒计时、HMI 报告的控制权现状（R8）、403（R9）、已取消申请的结局（R7）
        c.ReconnectScheduled += OnReconnectScheduled;
        c.LeaseServerViewChanged += OnServerView;
        c.ForbiddenRole += OnForbidden;
        c.LeaseCancelledRequestSettled += OnCancelSettled;
        _attachedTo = c;
    }

    /// <summary>摘掉 <see cref="Attach"/> 挂上的每一个处理器（一一对应；只摘挂在 <paramref name="c"/> 上的）。</summary>
    private void Detach(IReactorConnection c)
    {
        if (!ReferenceEquals(_attachedTo, c)) return;
        c.TelemetryReceived -= OnTelemetry;
        c.ConnectionStateChanged -= OnConnState;
        c.LeaseTransitioned -= OnLease;
        c.ControlChanged -= OnControl;
        c.AlarmChanged -= OnAlarm;
        c.EventReceived -= OnEvent;
        c.Error -= OnError;
        c.ServerRestarted -= OnRestarted;
        c.ReconnectScheduled -= OnReconnectScheduled;
        c.LeaseServerViewChanged -= OnServerView;
        c.ForbiddenRole -= OnForbidden;
        c.LeaseCancelledRequestSettled -= OnCancelSettled;
        _attachedTo = null;
    }

    private void OnTelemetry(TelemetrySnapshot f)
    {
        _pending = f;
        // 设备时钟：每一帧一个时钟差样本（下发时刻按它换到设备时钟），顺手记下 hello 的时钟差（断开之后曲线用，复核）
        NoteClock(f);
        // 曲线：每一帧都进环形缓冲（10 s 一格留最后一帧；刷新会合并几帧，曲线不丢格）
        _history.Data.AppendFrame(f);
        // 程序台账：每一帧都看（步骤计时取相邻两帧的中点，合并刷新之前就要记）；跑起来的不是本机下发的程序时条目作废（R5）
        if (BootIdNow is { Length: > 0 } boot) Ledger.ObserveFrame(f, boot);
        MarkDirty(SourceChange.Frame);
    }

    private void OnConnState(ConnectionState s)
    {
        // WS 连上（hello 之后的事件由 WS 连续送来）：hello 之前、上次核对之后的事件再续取一遍（REST 起步时的续取可能在 hello 之前）；
        // 续取做完之前台账的条目先不认（复核 R5）—— 在状态变成「已连接」之前就标上，界面不会有一刻按没核对过的条目显示
        bool check = s == ConnectionState.Connected && _restReady && Connection is not null;
        if (check && Ledger.BeginCheck()) { _progCache.Clear(); MarkDirty(SourceChange.Programs); }
        _ws = s;
        UpdateConnFromWs();
        if (check && Connection is { } c) RequestLedgerCatchUp(c);
    }

    private void OnReconnectScheduled(DateTime? at) => UpdateConnFromWs();

    /// <summary>
    /// 按当前连接对象的 WS 定连接状态（REST 起步完成之后）：hello 之后 → 已连接；在退避等待（NextRetryAt）→ 断线倒计时；否则 → 正在连接 / 重连。
    /// 等待中有失败原因时记下（握手被拒：每种原因提示一次；网络层的：从未连上才提示）。
    /// 同时记「REST 通不通」的证据（<see cref="StopReachable"/>）：每个 WS 连接失败记一次 —— 握手被 HTTP 拒绝（503 / 401…）= 主机答话了，
    /// 网络层失败（拒绝连接 / 找不到主机 / 超时）= 主机不通；WS 断了（连上过）或已经在退避时另外每 <see cref="ProbeInterval"/> 探测一次 GET /health。
    /// </summary>
    private void UpdateConnFromWs()
    {
        var c = Connection;
        if (c == null || !_restReady || _disposed) return;
        if (_ws is ConnectionState.Connected or ConnectionState.Resyncing)
        {
            StopProbe();
            NoteRest(true);
            SetConn(ConnStatus.Online);
            return;
        }
        bool waiting = NextRetry(c) != null;
        var e = WsError(c);
        if (e is WsConnectException w && !ReferenceEquals(w, _wsErrorSeen))
        {
            _wsErrorSeen = w;
            NoteRest(!w.IsNetwork);
        }
        if (_everOnline || waiting) StartProbe(c);
        if (waiting && e != null)
        {
            var msg = e is WsConnectException we ? we.Message : FleetActions.Describe(e);
            ReportFailure(msg, toast: e is WsConnectException { IsNetwork: false } || !_everOnline);
        }
        SetConn(waiting ? ConnStatus.Offline : ConnStatus.Connecting);
        if (waiting) MarkDirty(SourceChange.Conn);
    }

    /// <summary>记一条「REST 通不通」的证据（变了才刷新）。</summary>
    private void NoteRest(bool ok)
    {
        if (_restOk == ok) return;
        _restOk = ok;
        MarkDirty(SourceChange.Conn);
    }

    /// <summary>命令的结局（<see cref="ExecAsync"/>）：HMI 回了 HTTP（成功、4xx、5xx）→ REST 通；网络层失败 / HttpClient 超时 → 不通；调用方取消、其它异常不算证据。</summary>
    private void NoteRestOutcome(IReactorConnection c, Exception? ex, CancellationToken ct)
    {
        if (!ReferenceEquals(c, Connection) || _disposed) return;
        if (ex is null or ApiException) NoteRest(true);
        else if (ex is System.Net.Http.HttpRequestException or TimeoutException || ex is TaskCanceledException && !ct.IsCancellationRequested) NoteRest(false);
    }

    /// <summary>开始探测（已经在探测、连接换了或已释放时什么都不做）。</summary>
    private void StartProbe(IReactorConnection c)
    {
        if (_probe != null || _disposed || !ReferenceEquals(c, Connection)) return;
        var cts = new CancellationTokenSource();
        _probe = cts;
        _ = ProbeLoopAsync(c, cts);
    }

    private void StopProbe()
    {
        var p = _probe;
        _probe = null;
        if (p != null) { try { p.Cancel(); } catch { } }
    }

    /// <summary>
    /// WS 没连上时的 REST 探测（UI 线程上开始、续体回到 UI 线程）：每 <see cref="ProbeInterval"/>（<see cref="IClock"/>）一次 GET /health，单次 ≤ <see cref="ProbeTimeout"/>；
    /// 结果记成证据。WS 连上（hello）、换连接、断开 / 释放时停。
    /// </summary>
    private async Task ProbeLoopAsync(IReactorConnection c, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.Delay(ProbeInterval, ct).ConfigureAwait(true);
                var ok = await ProbeOnceAsync(c, ct).ConfigureAwait(true);
                if (ct.IsCancellationRequested || !ReferenceEquals(c, Connection) || _disposed) return;
                if (ok is bool b) NoteRest(b);
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (ReferenceEquals(_probe, cts)) _probe = null;
            cts.Dispose();
        }
    }

    /// <summary>一次探测：HMI 回了 HTTP（成功或任何状态码）→ true；网络层失败 / 超时 → false；探测本身被取消 → null。</summary>
    private static async Task<bool?> ProbeOnceAsync(IReactorConnection c, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(ProbeTimeout);
        try
        {
            await c.Rest.HealthAsync(t.Token).ConfigureAwait(false);
            return true;
        }
        catch (ApiException) { return true; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch { return false; }
    }

    private static DateTime? NextRetry(IReactorConnection c)
    {
        try { return c.Ws?.NextRetryAt; } catch { return null; }
    }

    private static Exception? WsError(IReactorConnection c)
    {
        try { return c.Ws?.LastError; } catch { return null; }
    }

    private static int WsFailures(IReactorConnection c)
    {
        try { return c.Ws?.FailedAttempts ?? 0; } catch { return 0; }
    }

    /// <summary>记下失败原因；<paramref name="toast"/> 时提示「1号机 · 连接失败：…」（同一轮里同一原因只提示一次）。</summary>
    private void ReportFailure(string reason, bool toast)
    {
        LastError = reason;
        if (!toast || _reported == reason) return;
        _reported = reason;
        Notice?.Invoke(Config.Alias + " · 连接失败：" + reason, ToastKind.Err);
    }

    private void SetConn(ConnStatus st)
    {
        if (st == _conn) return;
        if (st == ConnStatus.Online)
        {
            _since = _now();
            _lostAt = null;
            _everOnline = true;
            LastError = null;
            _reported = null;
            _failures = 0;
        }
        else if (_conn == ConnStatus.Online)
        {
            _lostAt = _now();
            // 曲线在断线处断开（重连后的第一帧不和断线前的最后一帧连成一条直线）。时刻按设备时钟：用户断开、改地址时连接对象已经没了，
            // 用记下的时钟差（复核：以前退回到本机时钟，HMI 的钟慢几个小时时这一个标记就把 4 h 的曲线全裁掉了）
            _history.Data.MarkGap(_clock.UtcNow + HistoryClockOffset);
        }
        _conn = st;
        MarkDirty(SourceChange.Conn);
    }

    /// <summary>断线倒计时（秒，向上取整）：首次连接的重试等待，或 WS 的退避（Client 的 NextRetryAt）；不在等待为 0。</summary>
    private int RetrySeconds()
    {
        DateTime? due = _retryAt ?? (Connection is { } c ? NextRetry(c) : null);
        return due is { } d ? CeilSeconds(d - _clock.UtcNow) : 0;
    }

    /// <summary>样稿 conn.attempts（见 <see cref="ConnInfo.Attempts"/>）。</summary>
    private int Attempts()
    {
        if (_conn == ConnStatus.Online) return 0;
        if (Connection is { } c && _restReady)
        {
            int f = WsFailures(c);
            return _everOnline ? 1 + f : f;
        }
        return _failures;
    }

    private static int CeilSeconds(TimeSpan t) => t <= TimeSpan.Zero ? 0 : (int)Math.Ceiling(t.TotalSeconds - 1e-9);

    /// <summary>每秒（UiTicker）：断线倒计时、申请 / 宽限 / 拒绝冷却的倒计时文字要变。</summary>
    public void Tick()
    {
        if (_disposed) return;
        if (_conn == ConnStatus.Offline) MarkDirty(SourceChange.Conn);
        // 程序台账的核对位置：每条事件只在内存里往前推，这里定时写盘（落后的位置只会让下次多核对几条）
        if (_ledger is { CursorDirty: true } led && _clock.UtcNow - _cursorFlushAt >= CursorFlushInterval)
        {
            _cursorFlushAt = _clock.UtcNow;
            led.FlushCursors();
        }
        // 台账读 / 写失败：提示一次（任何一次写盘都可能失败，这里每秒查一下）
        ReportLedgerError();
        var c = Connection;
        if (c != null && (c.Lease.State is ClientLease.Requesting or ClientLease.Lost || DenySeconds(c) > 0)) MarkDirty(SourceChange.Lease);
    }

    private void OnControl(ControlMsg m) => MarkDirty(SourceChange.Lease);

    private void OnServerView() => MarkDirty(SourceChange.Lease);

    private void OnAlarm(AlarmMsg a)
    {
        if (_alarmBook.Apply(a, _bootId)) MarkDirty(SourceChange.Alarms);
        if (a.Alarm.Level is "err" or "error" || a.Alarm.State == "active" && a.Alarm.Level == "warn")
            Notice?.Invoke("报警 " + a.Alarm.Rv + "：" + a.Alarm.Title, a.Alarm.Level is "err" or "error" ? ToastKind.Err : ToastKind.Warn);
    }

    private void OnEvent(EventMsg e)
    {
        if (_eventBook.Add(e)) MarkDirty(SourceChange.Events);
        // 程序台账：别的客户端替换 / 复制了程序 → 本机的条目作废（之后降级显示，R5）；HMI 回到本地控制 → 条目标上「触屏接管过控制」（复核）；
        // 按序号核对，跳号了（中间的还没核对）再续取一次
        var l = Ledger;
        int v = l.Version;
        l.ObserveEvent(e.Kind, ProgramLedger.EventText(e.Title, e.Message), e.Client, e.OpId, e.Level, e.Ts, e.Seq, e.SeqEpoch);
        if (l.Behind && Connection is { } c)
        {
            // 跳号了（中间的事件还没核对）：续取补上之前，落在后面的条目先不认（同连上时，复核 R5）—— 只是落在后面的那几条
            // （复核：以前全部有效条目都进核对；刚下发的条目也不会因为 PUT 回来之前收到的事件而落在后面，见 ProgramLedger.AddDownload）
            l.BeginCheckBehind();
            _progCache.Clear();
            MarkDirty(SourceChange.Programs);
            RequestLedgerCatchUp(c);
        }
        else if (l.Version != v) MarkDirty(SourceChange.Programs);
        // 曲线的会话标记：会话开始 / 结束 / 打标记之后重新取（标记的文字与时刻以 manifest 为准）
        if (e.Kind is { } k && (k.StartsWith("session.", StringComparison.Ordinal) || k.StartsWith("op.session.", StringComparison.Ordinal)) && Connection is { } sc)
            _ = LoadMarksAsync(sc);
    }

    private void OnError(Exception ex) => MarkDirty(SourceChange.Lease);   // 心跳异常时控制权的说明要跟着变

    /// <summary>HMI 重启（同一个连接上 WS 的 bootId 变了）：清掉旧启动的报警与本机下发记录，提示，重取 /device、/settings、当前报警与事件。</summary>
    private void OnRestarted()
    {
        OnRestartedCore(refetch: true);
    }

    private void OnRestartedCore(bool refetch)
    {
        _alarmBook.Clear();
        // 曲线缓冲：旧启动的数据作废（提示里写「曲线缓冲已清空」）
        _history.Data.Clear();
        // 令牌的角色在 HMI 的配置里，改了要重启 HMI 才生效：重启后之前学到的权限（R9）作废，下一个 403 再学（M3 复核）
        _role = TokenRole.Unknown;
        // HMI 重启后它手里的程序不一定还是本机下发的那份：程序台账的条目全部作废（「启动」回到启用 + 加热，D6；「全部运行」没有对象，要先重新下发），
        // 步骤计时清空；新的启动号（WS hello 已经带来时）立即记下
        Ledger.ObserveRestart();
        _progCache.Clear();
        // 会话标记：旧启动的会话不算（重取 /sessions/current 时再按新的来）
        _marks = Array.Empty<Core.Chart.SessionMark>();
        // 按帧估的时钟差：HMI 的钟可能在启动时校过，旧样本不算
        ForgetFrameClock();
        if (Connection?.Ws?.BootId is { Length: > 0 } wb) { _bootId = wb; Ledger.ObserveBoot(wb); }
        Notice?.Invoke(Config.Alias + " · 设备 HMI 已重启 · 曲线缓冲已清空", ToastKind.Warn);
        MarkDirty(SourceChange.All);
        if (refetch && Connection is { } c) _ = LoadExtrasAsync(c, refetchDevice: true);
    }

    /// <summary>某个请求被 403 FORBIDDEN_ROLE 拒绝（含 Client 内部吞掉的心跳、申请轮询）：按请求推出是哪一类操作，记下令牌权限。只要 admin 的操作被拒不学（见 <see cref="AdminOnly"/>）。</summary>
    private void OnForbidden(ForbiddenNotice n)
    {
        if (AdminOnly(n.Message)) return;
        if (ForbiddenAction(n.Method, n.Path) is { } a) LearnForbidden(a);
    }

    /// <summary>
    /// 这个 403 是「该操作需要 admin 角色」（HMI Api/Endpoints.Write.cs：PUT channels/{id}/pid/{band}）：operator 令牌照样能做别的写命令 ——
    /// 不能当成只读令牌学（M3 复核）。
    /// </summary>
    public static bool AdminOnly(string? message) => message != null && message.Contains("admin 角色", StringComparison.Ordinal);

    /// <summary>
    /// 被 403 拒绝的请求对应哪一类操作（TokenRole.PermissionOf 的类别：控制 / 记录 / 停止 / 急停）。PUT stir 既可能是开 / 设定（控制类）也可能是停止（停止类），
    /// 看不出来时返回 null（经 DeviceActions 执行的操作照样按操作学到）。读请求（GET）不会因为角色被拒（viewer 就能读），只要 admin 的 PID 写入（channels/n/pid/…）
    /// 被拒说明不了 operator 的权限 —— 这两种都返回 null、不学（M3 复核）。
    /// </summary>
    public static DeviceAction? ForbiddenAction(string? method, string? path)
    {
        var p = (path ?? "").Split('?')[0].Trim('/');
        var m = (method ?? "").ToUpperInvariant();
        if (m is "GET" or "HEAD") return null;
        if (p.Contains("/pid/", StringComparison.Ordinal) || p.EndsWith("/pid", StringComparison.Ordinal)) return null;
        if (p == "control" || p.StartsWith("control/", StringComparison.Ordinal)) return DeviceAction.LeaseRequest;   // 申请 / 心跳 / 释放：记录类
        if (p == "estop") return DeviceAction.Estop;
        if (p == "stop-all") return DeviceAction.StopAll;
        if (p.StartsWith("alarms/", StringComparison.Ordinal) || p == "sessions" || p.StartsWith("sessions/", StringComparison.Ordinal)) return DeviceAction.AlarmAck;
        if (p.StartsWith("channels/", StringComparison.Ordinal))
        {
            if (p.EndsWith("/stop", StringComparison.Ordinal) || p.EndsWith("/disable", StringComparison.Ordinal)) return DeviceAction.Stop;
            if (m == "DELETE") return DeviceAction.ProgAbort;   // DELETE program/run、autotune：停止类
            return DeviceAction.Enable;                          // 其余通道写命令：控制类
        }
        if (p == "stir") return null;
        if (m is "POST" or "PUT" or "DELETE") return DeviceAction.Enable;   // run、copy、settings…：控制类
        return null;
    }

    // ———————————————————— 控制权 ————————————————————

    /// <summary>
    /// 控制权（5 种状态，PORT-PLAN §3.2 第 9 条）：本地只读、申请中（倒计时 = HMI 给的到期时刻按时钟偏差换算；POST 还没回来时按本机发出时刻 + HMI 的申请超时）、
    /// 本机远程控制中（连续心跳失败 = <see cref="LeaseView.HeartbeatDegraded"/>）、宽限（Client 的 LostAt + 宽限秒数）、被其他客户端持有（R8：HMI 报告的持有者不是本机名）。
    /// </summary>
    public LeaseView Lease
    {
        get
        {
            var c = Connection;
            var st = LeaseStatus.Local;
            string? other = null;
            int remain = 0, grace = 0, wait = 60;
            bool degraded = false;
            var orphan = OrphanLease.None;
            if (c != null && _conn != ConnStatus.Disconnected)
            {
                var L = c.Lease;
                wait = RequestTimeoutSeconds(c);
                switch (L.State)
                {
                    case ClientLease.Holding:
                        st = LeaseStatus.Remote;
                        degraded = L.HeartbeatDegraded;
                        break;
                    case ClientLease.Lost:
                        st = LeaseStatus.Lost;
                        grace = GraceSeconds(L);
                        break;
                    case ClientLease.Requesting:
                        st = LeaseStatus.Requested;
                        remain = RemainSeconds(c);
                        break;
                    default:
                        orphan = OrphanOf(c);
                        if (orphan != OrphanLease.None) st = LeaseStatus.Other;
                        else
                        {
                            other = OtherHolder(c);
                            if (other != null) st = LeaseStatus.Other;
                        }
                        break;
                }
            }
            var client = LeaseClientName;
            var d = orphan != OrphanLease.None ? DeviceTexts.Orphan(client) : DeviceTexts.Lease(st, remain, grace, client, other);
            return new LeaseView(st, remain, grace, d.Holder, d, degraded, wait);
        }
    }

    /// <summary>HMI 的申请超时（GET /control 的 requestTimeoutSec，默认 60 s）：申请控制权对话框倒计时环的满格。</summary>
    private static int RequestTimeoutSeconds(IReactorConnection c) => c.ControlState?.RequestTimeoutSec is int s && s > 0 ? s : 60;

    /// <summary>申请的剩余秒数；顺手记下到期时刻（本机时钟，<see cref="_requestDue"/>：申请超时后的冷却从它起算）。</summary>
    private int RemainSeconds(IReactorConnection c)
    {
        var L = c.Lease;
        DateTime due;
        if (L.RequestExpiresAt is { } exp) due = exp - (L.ServerClockOffset ?? TimeSpan.Zero);
        else due = (_requestAt ?? _clock.UtcNow) + TimeSpan.FromSeconds(RequestTimeoutSeconds(c));
        _requestDue = due;
        return CeilSeconds(due - _clock.UtcNow);
    }

    private int GraceSeconds(ControlLeaseClient L) =>
        L.LostAt is { } at ? CeilSeconds(at + TimeSpan.FromSeconds(L.LeaseGraceSec) - _clock.UtcNow) : L.LeaseGraceSec;

    /// <summary>
    /// 申请控制权的冷却还剩几秒（本机时钟）：Client 的 DenyUntil（触屏拒绝、429）与 App 自己记的申请超时冷却（<see cref="_cooldownUntil"/>）取大的；不在冷却为 0。
    /// </summary>
    private int DenySeconds(IReactorConnection c) => Math.Max(ClientDenySeconds(c), _cooldownUntil is { } cu ? CeilSeconds(cu - _clock.UtcNow) : 0);

    private int ClientDenySeconds(IReactorConnection c) => c.Lease.DenyUntil is { } du ? CeilSeconds(du - _clock.UtcNow) : 0;

    /// <summary>冷却的原因不确定（申请超时的冷却比 Client 的 DenyUntil 长，或 DenyUntil 来自 429）：守卫写中性的 ActionGuard.CooldownText。</summary>
    private bool DenyIsNeutral(IReactorConnection c)
    {
        int client = ClientDenySeconds(c);
        int own = _cooldownUntil is { } cu ? CeilSeconds(cu - _clock.UtcNow) : 0;
        return own > client || client > 0 && _denyNeutral;
    }

    /// <summary>
    /// 本机旧租约（R8 的边角，GuardFacts.Orphan）：HMI 报告控制权在本机客户端名手里（Client 的 HolderIsMe），本机这条连接却没有持有、也没申请过 ——
    /// 上次运行崩溃 / 强退后留下、心跳还没超时（或另一台 PC 用了同一个客户端名）。HMI 进入 remoteLost 后同一令牌 + 客户端名可以重新申请（旧租约作废）。
    /// 本机取消的申请还在等结果（触屏刚批准、后台守卫正要释放）时不算。
    /// </summary>
    private OrphanLease OrphanOf(IReactorConnection c)
    {
        var L = c.Lease;
        if (_leaseUsed || L.State != ClientLease.None || L.CancelledRequestPending || L.Server is not { } s || !L.HolderIsMe) return OrphanLease.None;
        if (s.State == ControlModes.RemoteLost) return OrphanLease.Lost;
        return s.State == ControlModes.Remote || s.Mode == ControlModes.Remote ? OrphanLease.Remote : OrphanLease.None;
    }

    /// <summary>本机的申请（或已取消、还在等结果的申请）还在触屏上：退出前要问一下（App 的退出确认，M3 复核）。</summary>
    public bool LeaseRequestPending => Connection is { } c && !_disposed && (c.Lease.State == ClientLease.Requesting || c.Lease.CancelledRequestPending);

    /// <summary>
    /// 本机这条申请在 HMI 上的到期时刻（本机时钟，<see cref="IClock.UtcNow"/>）：申请中按 HMI 给的到期时刻，已取消的按取消前最后算出的；不知道时 null。
    /// 退出确认里「等结果再退出」最多等到这时（再加几秒让后台守卫查到结局）。
    /// </summary>
    public DateTime? LeaseRequestDue
    {
        get
        {
            if (!LeaseRequestPending) return null;
            if (Connection is { } c && c.Lease.State == ClientLease.Requesting) RemainSeconds(c);   // 顺手刷新 _requestDue
            return _requestDue;
        }
    }

    /// <summary>倒计时与重试等待用的时钟的「现在」（退出确认的倒计时与它比）。</summary>
    public DateTime ClockNow => _clock.UtcNow;

    /// <summary>R8：控制权被其他客户端持有时是谁（HMI 报告的合并现状；还没有现状时（假连接）看 GET /control 的结果）。</summary>
    private static string? OtherHolder(IReactorConnection c)
    {
        var L = c.Lease;
        if (L.Server != null) return L.HeldByOther ? L.Holder : null;
        var cs = c.ControlState;
        return cs != null && cs.Mode is ControlModes.Remote or ControlModes.RemoteLost && !string.IsNullOrEmpty(cs.Holder)
            && !string.Equals(cs.Holder, c.Options.ClientName, StringComparison.Ordinal) ? cs.Holder : null;
    }

    /// <summary>
    /// 另一个客户端的控制权申请正在等触屏处理（R8，Client 的 PendingByOther：GET /control 与 WS control 合并）：返回那个客户端名，否则 null。
    /// 本机自己的申请（包括本地已取消、HMI 那边还挂着的）不算。名字只从 WS 消息得知又解析不出时写「其他客户端」（同第 5 种状态的短写法）。
    /// </summary>
    private string? OtherRequest()
    {
        var c = Connection;
        if (c == null || _conn == ConnStatus.Disconnected) return null;
        var L = c.Lease;
        if (L.Server != null) return L.PendingByOther ? L.PendingRequest?.ClientName ?? DeviceTexts.OtherShort(null) : null;
        var cs = c.ControlState;
        if (cs == null || cs.State != ControlModes.Requested || cs.Pending is not { } p) return null;
        return string.Equals(p.ClientName, c.Options.ClientName, StringComparison.Ordinal) ? null : p.ClientName;
    }

    /// <summary>申请控制权：后台等触屏确认（倒计时见 <see cref="Lease"/>），结果用 <see cref="Notice"/> 提示（样稿的写法，见 <see cref="LeaseNotice"/>）。</summary>
    public Task RequestLeaseAsync()
    {
        var c = Conn0;
        _requestAt = _clock.UtcNow;
        MarkDirty(SourceChange.Lease);
        _ = AcquireAsync(c);
        return Task.CompletedTask;
    }

    private async Task AcquireAsync(IReactorConnection c)
    {
        try { await c.Lease.AcquireAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex)
        {
            // POST /control/request 被拒（409 别人持有 / 别人的申请在等、403 只读令牌、网络…）：结局在这里提示（迁移的原因 request:* 不再提示）
            if (ex is ApiException { Code: ParallelReactor.Commands.OpCodes.ForbiddenRole } fe && !AdminOnly(fe.Message)) LearnForbidden(DeviceAction.LeaseRequest);
            if (ReferenceEquals(c, Connection)) Notice?.Invoke(Config.Alias + " · " + LeaseRequestError(ex), ToastKind.Err);
        }
        finally { MarkDirty(SourceChange.Lease); }
    }

    /// <summary>
    /// 申请被拒的说明：409 NOT_CONTROL_HOLDER 在申请时的意思是「控制权已经在别人（或本机旧租约）手里」—— 用 HMI 自己的话「当前控制权属于「X」」，
    /// 不用写命令那句「409 非控制权持有者：请先申请远程控制」（申请本身就是在申请，M3 复核）；其它照 Fleet/DeviceActions.Describe。
    /// </summary>
    public static string LeaseRequestError(Exception ex) =>
        ex is ApiException { Code: ParallelReactor.Commands.OpCodes.NotControlHolder } a && a.Message.Length > 0 ? a.Message : FleetActions.Describe(ex);

    /// <summary>
    /// 取消申请（HMI 没有取消端点，A5 / R7）：Client 的 CancelRequestAsync —— 界面立即回到「本地只读」，后台守卫继续查这条申请，触屏之后批准就立即释放
    /// （结局见 <see cref="OnCancelSettled"/>）。
    /// </summary>
    public async Task CancelLeaseRequestAsync()
    {
        if (Connection is { } c) await c.Lease.CancelRequestAsync().ConfigureAwait(true);
        _requestAt = null;
        MarkDirty(SourceChange.Lease);
    }

    public async Task ReleaseLeaseAsync()
    {
        var c = Conn0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await c.Lease.ReleaseAsync(cts.Token).ConfigureAwait(true);
        MarkDirty(SourceChange.Lease);
    }

    private void OnLease(LeaseTransition t)
    {
        if (t.To != ClientLease.Requesting) _requestAt = null;
        if (t.To is ClientLease.Requesting or ClientLease.Holding or ClientLease.Lost) _leaseUsed = true;
        var r = t.Reason ?? "";
        // 冷却的原因（守卫的写法）：触屏拒绝 = 确知；429 = 不确定（HMI：「触屏刚拒绝了该令牌的申请（或申请超时）」）
        if (r == "denied") _denyNeutral = false;
        else if (r == "request:" + ParallelReactor.Commands.OpCodes.RateLimited) _denyNeutral = true;
        // 申请超时：HMI 同样冷却 30 s（ControlLease 的 RequestExpired → CooldownLocked），Client 只在拒绝 / 429 时记 —— 这一份自己记（M3 复核）
        if (t.From == ClientLease.Requesting && t.To == ClientLease.None && r is "requestExpired" or "request:timeout") StartCooldown(_requestDue);
        if (t.To == ClientLease.Holding) _cooldownUntil = null;
        MarkDirty(SourceChange.Lease);
        if (LeaseNotice(t, out var kind) is { } msg) Notice?.Invoke(msg, kind);
    }

    /// <summary>申请超时后的冷却：从 HMI 的到期时刻起 <see cref="DenyCooldown"/>（不知道到期时刻时从现在起），最长从现在起 30 s。</summary>
    private void StartCooldown(DateTime? expiredAt)
    {
        var now = _clock.UtcNow;
        var until = (expiredAt is { } at && at < now ? at : now) + DenyCooldown;
        if (until > now + DenyCooldown) until = now + DenyCooldown;
        if (until > now && (_cooldownUntil is not { } cu || until > cu)) _cooldownUntil = until;
    }

    /// <summary>
    /// 控制权迁移的提示。样稿有的照样稿（sim.js tick：「1号机触屏已确认 · 远程控制中」「1号机控制权申请超时 · 触屏未确认」「1号机宽限期结束 · 控制权已交回触屏」）；
    /// 样稿没有的沿用旧界面的写法（丢失 / 恢复 / 收回 / 急停失效 / 心跳或写命令被拒），触屏拒绝用守卫同一句（<see cref="ActionGuard.DenyText"/>）。
    /// 本机自己的取消、释放和 POST 被拒（request:*，由 AcquireAsync 提示）不提示。
    /// </summary>
    public string? LeaseNotice(LeaseTransition t, out ToastKind kind)
    {
        string a = Config.Alias;
        kind = ToastKind.Warn;
        if (t.From == ClientLease.Requesting && t.To == ClientLease.Holding)
        {
            kind = ToastKind.Ok;
            // HMI 设了自动授权（GET /control 的 autoGrant）时没有人在触屏上确认：不写「触屏已确认」（样稿没有这一种，M3 复核）
            return Connection?.ControlState?.AutoGrant == true ? a + "已获得控制权 · 远程控制中" : a + "触屏已确认 · 远程控制中";
        }
        if (t.To == ClientLease.Lost) return a + " · 控制权连接丢失 · 宽限期内自动恢复";
        if (t.From == ClientLease.Lost && t.To == ClientLease.Holding) { kind = ToastKind.Info; return a + " · 控制权已恢复"; }
        if (t.To != ClientLease.None) return null;
        var r = t.Reason ?? "";
        if (r is "cancelled" or "released" || r.StartsWith("request:", StringComparison.Ordinal) && r != "request:timeout" && r != "request:noId") return null;
        if (r == "denied")
        {
            int left = Connection is { } c ? DenySeconds(c) : 0;
            return ActionGuard.DenyText(a, left > 0 ? left : 30);
        }
        if (r is "requestExpired" or "request:timeout") return a + "控制权申请超时 · 触屏未确认";
        if (r == "expired") return a + "宽限期结束 · 控制权已交回触屏";
        if (r == "takenBack") return a + " · 触屏已收回控制权";
        if (r == "invalidated") return a + " · 急停：控制权已失效";
        if (t.From is ClientLease.Holding or ClientLease.Lost)
        {
            // 重连 / HMI 重启后对齐 GET /control：设备回到了本地（或已被别人持有）—— 不自动重新申请（AutoReacquireLease = false）
            if (r == "state:local") return a + " · 重连后设备已回到本地模式 · 控制权失效";
            if (r.StartsWith("state:", StringComparison.Ordinal)) return a + " · 重连后控制权已失效";
            if (r.StartsWith("heartbeat:", StringComparison.Ordinal)) return a + " · 心跳被拒 · 控制权失效（" + r[10..] + "）";
            if (r.StartsWith("write:", StringComparison.Ordinal)) return a + " · 写命令被拒 · 控制权失效（" + r[6..] + "）";
            return null;
        }
        if (t.From == ClientLease.Requesting) return a + " · 申请被拒绝或超时";
        return null;
    }

    /// <summary>
    /// 本地取消的申请有了结局（R7）：触屏在取消后批准、已自动释放 → 提示一下（设备上曾短暂出现本机的控制权）；释放失败 / 拿不到租约号 → 提示到触屏收回。
    /// 样稿没有这两句（演示数据取消就是取消）；写法接着 R7 的「若触屏已批准将自动释放」。
    /// </summary>
    private void OnCancelSettled(CancelledRequestOutcome o)
    {
        MarkDirty(SourceChange.Lease);   // 拒绝 → 冷却（DenyUntil）
        if (o.Result == CancelledRequestOutcome.Denied) _denyNeutral = false;
        // 已取消的申请在 HMI 上超时了：HMI 同样冷却 30 s（从它查到的时刻起算），之后再申请才不会 429（M3 复核）
        if (o.Result == CancelledRequestOutcome.Expired) StartCooldown(o.At);
        if (o.Result == CancelledRequestOutcome.Released) Notice?.Invoke(Config.Alias + " · 触屏在取消后批准了申请 · 已自动释放", ToastKind.Info);
        else if (o.ApprovedAfterCancel) Notice?.Invoke(Config.Alias + " · 触屏在取消后批准了申请 · 自动释放失败，请在触屏收回控制权", ToastKind.Warn);
    }
}
