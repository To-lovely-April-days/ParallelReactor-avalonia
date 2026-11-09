using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using AlarmDto = ParallelReactor.Services.AlarmDto;
using ClientLease = ParallelReactor.Client.LeaseState;
using CoreActions = ParallelReactor.Control.Core.Guard.DeviceActions;
using FleetActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 一台真机（PORT-PLAN §5 M2「LiveDeviceSource」；M3：每台设备一个，由 Services/DeviceRegistry 创建和持有，N 台同时连接）。包装一个 <see cref="IReactorConnection"/>：
/// <list type="bullet">
/// <item><b>重连监督</b>（M3，见 LiveDeviceSource.Connection.cs）：<see cref="StartAsync"/> 开始「要连着」：新建连接（工厂可替换，测试用假 HMI）、挂上全部事件、
///   GET /device + /control + /snapshot 再开 WS。首次连接失败 → 倒计时重试（<see cref="RetryMin"/> 2 s 起翻倍、最长 <see cref="RetryMax"/> 10 s；
///   样稿「连接失败 · 5 s 后重试」，连上过的「连接中断 · 重连中 5 s」）；连上之后 WS 断线由 Client 自己退避重连（0.5 → 10 s），这里按它的 NextRetryAt 显示倒计时；
///   「立即重连」（<see cref="ReconnectNowAsync"/>）打断任一种等待。<b>已连接 = WS 收到 hello</b>（实时数据在流）：REST 通了而 WS 连不上（例如 503 TOO_MANY_CLIENTS，R19）
///   不算已连接，失败原因可读（<see cref="LastError"/> + 提示，每种原因提示一次）。HMI 重启（bootId 变化）→ 重取 /device、/settings、当前报警与事件，清掉本机下发记录。</item>
/// <item>事件：遥测帧、连接状态、重连倒计时、控制权迁移、WS control、HMI 报告的控制权现状、报警、事件、错误、403、已取消申请的结局、HMI 重启。事件由 Client 经 UiContext
///   投递到 UI 线程；这里只记下「脏了什么」和最新一帧，<b>合并</b>成一次刷新（<see cref="Changed"/>，经构造时给的 post 排到 UI 线程，一批只排一次）——
///   多帧之间只应用最后一帧，就地更新，不重建任何集合。</item>
/// <item>释放：<see cref="DisposeAsync"/> / <see cref="DisconnectAsync"/> 停止监督、<b>摘掉挂上的每一个事件处理器</b>（旧 MainViewModel 从不摘），
///   持有控制权时先释放（≤ 3 s）、正在申请时先取消（R7：已取消的申请还在等触屏时 REST 留给 Client 的后台守卫，批准了就立即释放），再释放连接。</item>
/// <item>操作：<see cref="ExecAsync"/> 按样稿的操作调 Client（控制类走 ControlledWrites 带租约，停止类直接 REST）；「启动」「全部运行」绝不调 HMI 的 POST /run（D6）：
///   本机这次下发过程序的反应釜 → program/run，否则 → /channels/{id}/start（启用 + 加热到 SP）。</item>
/// <item>控制权：申请在后台等触屏确认（倒计时按 HMI 给的到期时刻与时钟偏差，<see cref="Lease"/>）；本地「取消申请」走 Client 的 CancelRequestAsync（R7：界面立即回到本地只读，
///   后台守卫继续查这条申请，触屏之后批准就立即释放）；丢失 → 宽限倒计时（Client 的 LostAt + 宽限）→ 恢复；被其他客户端持有（第 5 种，R8）；触屏拒绝后的冷却（守卫写明）。
///   Client 不自动重新申请（AutoReacquireLease = false，样稿：宽限期满「控制权已交回触屏」，要不要再申请由用户决定）。</item>
/// <item>令牌权限（M3）：操作被 403 FORBIDDEN_ROLE 拒绝时 <see cref="LearnForbidden"/> 记下（Core 的 TokenRole，缓存到换令牌为止），守卫随之禁用；
///   Client 内部吞掉的 403（心跳、申请轮询）经连接的 ForbiddenRole 事件同样学到。</item>
/// </list>
/// 线程：全部在 UI 线程上（Client 的事件经 UiContext；监督循环从 UI 线程开始、续体回到 UI 线程）；没有 UiContext（测试）时在调用线程 / 线程池上。
/// </summary>
public sealed partial class LiveDeviceSource : IDeviceSource
{
    private readonly Action<Action> _post;
    private readonly Func<ClientOptions, IReactorConnection> _factory;
    private readonly SynchronizationContext? _uiContext;
    private readonly ModelProfiles _models;
    private readonly Func<DateTime> _now;
    private readonly IClock _clock;
    private string _clientName;
    private readonly LiveHistoryView _history;
    private readonly Dictionary<string, AlarmItem> _alarmMap = new(StringComparer.Ordinal);
    private readonly List<EventItem> _events = new();
    private readonly Dictionary<int, ProgramDoc> _downloaded = new();

    private IReadOnlyList<AlarmItem> _alarms = Array.Empty<AlarmItem>();
    private TelemetrySnapshot? _pending;
    private SourceChange _dirty;
    private bool _flushPending, _disposed;
    /// <summary>还没开始连接（登记了、没勾自动连接 / 没有令牌）是「已断开」（样稿：用户断开的设备写「已断开」、按钮「连接」）；开始连接时变成「正在连接…」。</summary>
    private ConnStatus _conn = ConnStatus.Disconnected;
    private bool _everOnline;
    private DateTime? _since, _lostAt;
    private ModelProfile? _model;
    private TokenRole _role = TokenRole.Unknown;
    /// <summary>退出前已经试过释放控制权 / 取消申请（ReleaseLeaseQuietAsync）：释放时不再试第二次（关闭预算 5 s）。</summary>
    private bool _releaseTried;

    /// <param name="config">地址、端口、令牌、别名。</param>
    /// <param name="clientName">本机客户端名（申请控制权时报给触屏）。</param>
    /// <param name="post">把一次刷新排到 UI 线程（App：Dispatcher.UIThread.Post；测试：直接执行或测试自己的 UI 线程）。</param>
    /// <param name="factory">新建连接（默认 new ReactorConnection(options)；测试 / 截图场景 fake2 给假 HMI 上的真 ReactorConnection 或假连接）。</param>
    /// <param name="uiContext">Client 投递事件用的 UI 同步上下文（App：AvaloniaSynchronizationContext；测试 null = 调用线程，或测试自己的 UI 线程）。</param>
    /// <param name="now">本地时刻（上线 / 断线时刻、会话号）；默认按 <paramref name="clock"/>。</param>
    /// <param name="clock">倒计时与重试等待用的时钟（与 Client 的 IClock 同一个时间轴：重连 / 申请 / 宽限的到期时刻都按它算）；默认系统时钟，测试给假时钟。</param>
    public LiveDeviceSource(DeviceConfig config, string clientName, Action<Action> post, Func<ClientOptions, IReactorConnection>? factory = null,
        SynchronizationContext? uiContext = null, ModelProfiles? models = null, Func<DateTime>? now = null, IClock? clock = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _clientName = clientName;
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _factory = factory ?? (o => new ReactorConnection(o));
        _uiContext = uiContext;
        _models = models ?? ModelProfiles.Embedded;
        _clock = clock ?? SystemClock.Instance;
        _now = now ?? (() => _clock.UtcNow.ToLocalTime());
        _history = new LiveHistoryView(this);
    }

    public Guid Id => Config.Id;
    public bool IsDemo => false;
    public DeviceConfig Config { get; private set; }

    /// <summary>上次连上时的序列号（devices.json 的 lastSerial）：还没连上时设备管理表照样有 SN。</summary>
    public string? LastSerial { get; init; }

    /// <summary>上次连上时的型号键（devices.json 的 lastModel）：还没连上时按它显示型号与反应釜位置（没有时用型号表的默认 HT-PR8）。</summary>
    public string? InitialModelKey { get; init; }

    /// <summary>
    /// 最近一次连接失败的原因（可读的一句：REST 失败 = Fleet/DeviceActions.Describe 的文字；WS 握手被拒 = Client 的 WsConnectException 文字，
    /// 例如「WebSocket 连接数已达上限 · 请关闭该设备上的其他客户端（HTTP 503 TOO_MANY_CLIENTS）」）；连上（WS hello）后清空。
    /// </summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 「全部停止」连搅拌一起停（POST /stop-all 的 stopStir）。默认取 Core 的 <see cref="ControlOptions.StopAllStopsStirring"/>（全软件只有那一处设置）；
    /// FleetVm 新建数据源时把自己的值传进来（测试用它换值）。结果文字见 <see cref="StopAllText"/>。
    /// </summary>
    public bool StopAllStopsStirring { get; init; } = ControlOptions.StopAllStopsStirring;

    /// <summary>
    /// 本机客户端名（申请控制权时报给触屏）。改了之后<b>下一次新建连接</b>才用新名字（Client 的 ClientOptions 每条连接一份、不能改）：
    /// 已经建好的连接照旧用它自己的名字（HMI 认的持有者也是那个名字，R8 的比较按连接走），断开再连 / 改地址后换成新的。
    /// </summary>
    public string ClientName
    {
        get => _clientName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _clientName) return;
            _clientName = value;
            MarkDirty(SourceChange.Lease);
        }
    }

    /// <summary>当前连接用的客户端名（HMI 认的那个；没有连接时 = <see cref="ClientName"/>）：控制权文字「本机 PC-… 持有控制权」按它写。</summary>
    private string LeaseClientName => Connection?.Options.ClientName is { Length: > 0 } n ? n : _clientName;

    /// <summary>当前连接（未连接 / 已断开 / 首次连接的重试等待中为 null）：性能测试、程序页、导出直接用它。</summary>
    public IReactorConnection? Connection { get; private set; }

    /// <summary>要给用户的提示（控制权迁移、HMI 重启、报警、申请结果、连接失败的原因）。在 UI 线程上触发。</summary>
    public event Action<string, ToastKind>? Notice;

    public event Action<SourceChange>? Changed;

    /// <summary>
    /// 连接（样稿 conn）：状态、连上过、倒计时（断线时：首次连接的重试 / WS 的退避，按 <see cref="IClock"/>）、上线 / 断线时刻、停止类能不能尽力送达、
    /// 样稿的 attempts（见 <see cref="ConnInfo.Attempts"/>）。
    /// </summary>
    public ConnInfo Conn => new(_conn, _everOnline, _conn == ConnStatus.Offline ? RetrySeconds() : 0, _since, _lostAt, StopReachable, Attempts());

    /// <summary>
    /// 停止类命令现在能送达（GuardFacts.StopReachable）：有连接对象（没被用户断开、没释放）<b>且有正面的证据表明 REST 通</b>（<see cref="_restOk"/>，
    /// M3 复核）—— REST 起步完成、WS 收到 hello、WS 握手被 HTTP 拒绝（503 TOO_MANY_CLIENTS / 401…：主机答话了）、WS 断线后的探测 GET /health 有响应、命令送达。
    /// 最近一次证据是网络层失败（WS 连不上：拒绝连接 / 找不到主机 / 超时；探测失败；命令网络错误 / 超时）时为 false：主机真的不通，只剩 none 类（样稿）。
    /// 所以只是 WebSocket 断了（1008 慢消费者、心跳超时）而 REST 还通时，急停 / 全部停止 / 停止类照样发送（失败照常提示），控制类照样禁用；
    /// 从未连上、但 REST 起步成功而 WS 被拒（例如 503）时同样能发停止类。
    /// </summary>
    public bool StopReachable => Connection != null && _restOk && _conn != ConnStatus.Disconnected && !_disposed;
    public DeviceInfoDto? Info { get; private set; }
    public SettingsDto? Settings { get; private set; }
    public CommDto? Comm => null;
    /// <summary>本机令牌的权限（第一个 403 之后学到，缓存到换令牌为止，见 <see cref="UpdateConfigAsync"/>）。</summary>
    public TokenRole Role => _role;

    /// <summary>操作被 403 FORBIDDEN_ROLE 拒绝：记下令牌权限（只会收紧），守卫立即按它禁用（刷新一次）。</summary>
    public void LearnForbidden(DeviceAction action)
    {
        var next = _role.Learn(action);
        if (next == _role) return;
        _role = next;
        MarkDirty(SourceChange.Lease);
    }
    public TelemetrySnapshot? Latest { get; private set; }
    public DateTime? DataAt => Latest is { } f ? f.Ts.ToLocalTime() : null;
    public IReadOnlyList<AlarmItem> Alarms => _alarms;
    public IReadOnlyList<EventItem> Events => _events;
    public IChannelHistory History => _history;

    public ModelProfile Model => _model ??= _models.Get(InitialModelKey) ?? _models.Get(_models.FallbackKey) ?? _models.All[0];
    public string Sn => Info?.Serial ?? LastSerial ?? "";
    public EstopInfo Estop => Latest?.Estop is { } e ? new EstopInfo(e.Active, e.Since?.ToLocalTime()) : EstopInfo.None;
    public StirInfo? Stir => Model.StirMode == StirMode.Independent ? null : Latest?.Stir is { } s ? new StirInfo(s.On, s.RpmSp) : new StirInfo(false, 0);
    public SessionInfo? Session => Latest?.Session is { Active: true } s ? new SessionInfo(true, s.Id ?? s.TestId, null, 0) : null;

    // ———————————————————— 合并刷新 ————————————————————

    private void MarkDirty(SourceChange c)
    {
        if (_disposed) return;
        _dirty |= c;
        if (_flushPending) return;
        _flushPending = true;
        _post(Flush);
    }

    /// <summary>应用攒下的变化：最新一帧变成 <see cref="Latest"/>，然后触发一次 <see cref="Changed"/>（UI 线程）。</summary>
    private void Flush()
    {
        _flushPending = false;
        if (_disposed) return;
        var d = _dirty;
        _dirty = SourceChange.None;
        if (_pending != null) { Latest = _pending; _pending = null; }
        if (d != SourceChange.None) Changed?.Invoke(d);
    }

    // ———————————————————— 报警 / 事件归一化（M4 换成 Core 的 AlarmBook / EventBook） ————————————————————

    private static readonly Regex RvRe = new(@"^RV(\d+)", RegexOptions.CultureInvariant);

    private void UpsertAlarm(AlarmDto a)
    {
        if (a.Level == "info" || a.ClearedAt != null) { _alarmMap.Remove(a.Id); }
        else
        {
            int? ch = a.Channel ?? (RvRe.Match(a.Title ?? "") is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
            var text = a.Title + (string.IsNullOrWhiteSpace(a.Message) ? "" : " · " + a.Message);
            _alarmMap[a.Id] = new AlarmItem(a.Id, a.Level is "err" or "error" ? AlarmLevel.Err : AlarmLevel.Warn, ch, AlarmCode(a.Title), text,
                a.RaisedAt.ToLocalTime(), a.State is "ack" or "done");
        }
        _alarms = _alarmMap.Values.OrderByDescending(x => x.At).ToArray();
    }

    /// <summary>当前报警整个换成 GET /alarms 的结果（每次（重新）连上、HMI 重启后：断线期间的变化与旧启动的报警都不留下）。</summary>
    private void ReplaceAlarms(IEnumerable<AlarmDto> items)
    {
        _alarmMap.Clear();
        _alarms = Array.Empty<AlarmItem>();
        foreach (var dto in items) UpsertAlarm(dto);
    }

    /// <summary>报警代码按关键字推断（HMI 的 AlarmDto 没有 code，A6）。</summary>
    public static string AlarmCode(string? title)
    {
        var t = title ?? "";
        if (t.Contains("急停")) return "ESTOP";
        if (t.Contains("超压") || t.Contains("压力")) return "P-HIGH";
        if (t.Contains("超温")) return "T-HIGH";
        if (t.Contains("升温")) return "HEAT-SLOW";
        return "ALARM";
    }

    /// <summary>事件的身份 = 「序号纪元:序号」（HMI 重启后序号从头计，纪元随之变：新旧事件不会撞号、也不会被当成重复丢掉）。</summary>
    private static string EventId(int epoch, long seq) =>
        epoch.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + seq.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private void AddEvent(string id, DateTime ts, string level, string kind, int? channel, string title, string message)
    {
        if (_events.Any(e => e.Id == id)) return;
        var lv = kind.StartsWith("op.", StringComparison.Ordinal) || kind.StartsWith("control.", StringComparison.Ordinal) || kind.StartsWith("session.", StringComparison.Ordinal)
            ? EventLevel.Op
            : level switch { "warn" => EventLevel.Warn, "err" or "error" => EventLevel.Err, _ => EventLevel.Info };
        int? n = channel ?? (RvRe.Match(title ?? "") is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : null);
        var item = new EventItem(id, ts.ToLocalTime(), lv, n, title + (string.IsNullOrWhiteSpace(message) ? "" : " · " + message));
        int at = _events.FindIndex(e => e.At < item.At);
        if (at < 0) _events.Add(item); else _events.Insert(at, item);
        while (_events.Count > 200) _events.RemoveAt(_events.Count - 1);
    }

    // ———————————————————— 读 ————————————————————

    private bool InstalledHint(int n) => Info?.InstalledChannels is { Length: > 0 } inst ? inst.Contains(n) : Model.IsInstalled(n);

    public ChannelReading Read(int n) => Fleet.ChannelProjection.FromFrame(Latest, n, _conn == ConnStatus.Online, _everOnline, InstalledHint(n));

    public ChannelExtras? Extras(int n) => null;

    /// <summary>程序台账在 M5；M2 只记本机这次下发过的程序（给「启动」用），不当成正在运行的程序显示。</summary>
    public ProgramRecord? Program(int n) => null;

    /// <summary>
    /// 检查器的程序下拉：本机给第 n 路的程序草稿（drafts.json，同旧界面的「下发程序」；按型号的程序库在 M7）。
    /// 只在检查器换对象时读一次（不是每帧）；草稿没有时是模板「加氢测试-0n」（不写文件）。
    /// </summary>
    public IReadOnlyList<ProgramChoice> ProgramChoices(int n) => new[] { new ProgramChoice("draft-" + n, new ProgramDraftStore().Get(n).Name) };

    /// <summary>本机这次下发到第 n 路的程序（下发成功后记下；HMI 重启 / 断开后清空）。</summary>
    public ProgramDoc? Downloaded(int n) => _downloaded.TryGetValue(n, out var d) ? d : null;

    /// <summary>「全部运行」要运行的反应釜（D6：本机下发过、已启用、没在运行；守卫、确认清单、执行用同一个判断 ActionGuard.IsRunAllTarget）。</summary>
    public IReadOnlyList<int> RunAllTargets() => ActionGuard.RunAllTargets(Facts());

    public bool GasOpen(string key)
    {
        var g = Latest?.Gas;
        if (g is null) return false;
        if (Model.Vent?.Key == key) return g.VentOpen;
        var line = Model.GasInlets.FirstOrDefault(x => x.Key == key);
        var code = line?.HmiCode ?? key;
        return g.Inlets.Any(i => i.Id == code && i.Open);
    }

    public GuardFacts Facts()
    {
        var lease = Lease;
        var frame = Latest;
        var chans = new List<ChannelFacts>();
        for (int n = 1; n <= Model.ChannelCount; n++)
        {
            var r = Read(n);
            ChannelSnap? snap = frame?.Channels.FirstOrDefault(c => c.Id == n);
            bool mine = _downloaded.ContainsKey(n);
            ProgramStatus? prog = snap?.Program is { } p ? HmiProgramStatus(p) : mine ? ProgramStatus.Loaded : null;
            // Downloaded：「全部运行」只运行本机这次下发的程序（D6）；触屏放上去的程序（HMI 快照里有、本机没下发）不算
            chans.Add(new ChannelFacts(n, r.Installed, r.Own, r.Heating, false, prog, snap?.TempSpC, Downloaded: mine));
        }
        var conn = Conn;
        return new GuardFacts
        {
            Exists = !_disposed,
            Alias = Config.Alias,
            Conn = conn.State,
            EverOnline = conn.EverOnline,
            RetryS = conn.RetryS,
            StopReachable = conn.StopReachable,
            Lease = lease.State,
            LeaseRemainS = lease.RemainS,
            LeaseGraceS = lease.GraceS,
            LeaseHolder = lease.State == LeaseStatus.Other ? lease.Holder : null,
            OtherRequest = OtherRequest(),
            LeaseDenyS = Connection is { } c && _conn == ConnStatus.Online ? DenySeconds(c) : 0,
            LeaseDenyNeutral = Connection is { } c2 && DenyIsNeutral(c2),
            Orphan = Connection is { } c3 && _conn == ConnStatus.Online ? OrphanOf(c3) : OrphanLease.None,
            Role = _role,
            EstopActive = Estop.Active,
            Model = Model,
            StirOn = frame?.Stir.On ?? false,
            Channels = chans,
            ProgramIds = null,
            SessionActive = Session is { Active: true },
            SessionId = Session?.Id,
            Alarms = _alarms.Select(a => new AlarmFacts(a.Id, a.Acked)).ToArray(),
        };
    }

    /// <summary>HMI 的程序状态 → 样稿的程序状态（报警中止 failed 单列，§3.5）。</summary>
    public static ProgramStatus HmiProgramStatus(ChannelProgramSnap p) => p.State switch
    {
        ChannelProgramStates.Completed => ProgramStatus.Done,
        ChannelProgramStates.Failed => ProgramStatus.Failed,
        ChannelProgramStates.Aborted => ProgramStatus.Aborted,
        _ => p.Running ? ProgramStatus.Running : ProgramStatus.Done,
    };

    // ———————————————————— 操作 ————————————————————

    private IReactorConnection Conn0 => Connection ?? throw new InvalidOperationException("未连接设备");

    /// <summary>
    /// 执行一个操作（见 <see cref="IDeviceSource.ExecAsync"/>）。发给 HMI 的命令的结局也是「REST 通不通」的证据（<see cref="StopReachable"/>）：
    /// HMI 回了 HTTP（成功或 4xx / 5xx）→ 通；网络层失败 / 超时 → 不通（例如断线时尽力发出的急停没送到：这台立即只剩 none 类）。
    /// </summary>
    public async Task<ActionOutcome> ExecAsync(DeviceAction action, ActionPayload a, CancellationToken ct)
    {
        var c = Connection;
        bool rest = c != null && action is not (DeviceAction.Reconnect or DeviceAction.LeaseCancel or DeviceAction.LeaseRequest or DeviceAction.Disconnect
            or DeviceAction.Rename or DeviceAction.Remove);
        try
        {
            var o = await ExecCoreAsync(action, a, ct).ConfigureAwait(true);
            if (rest) NoteRestOutcome(c!, null, ct);
            return o;
        }
        catch (Exception ex) when (rest)
        {
            NoteRestOutcome(c!, ex, ct);
            throw;
        }
    }

    private async Task<ActionOutcome> ExecCoreAsync(DeviceAction action, ActionPayload a, CancellationToken ct)
    {
        string rv = "RV" + a.N;
        int n = a.N ?? 0;
        string alias = Config.Alias;
        ActionOutcome Ok(string msg, IReadOnlyList<string>? warnings = null, ToastKind tone = ToastKind.Ok, bool info = false) =>
            ActionOutcome.Done((msg.StartsWith(alias, StringComparison.Ordinal) ? "" : alias + " · ") + msg, tone, warnings, info);
        // 不需要连接的操作（重连、取消申请）：已断开时也能做
        if (action == DeviceAction.Reconnect)
        {
            // 没有令牌（devices.json 里没记住、本次也还没输入）：连不了 —— 先「编辑设置」填令牌（措辞同添加设备表单的校验「请输入访问令牌」）
            if (string.IsNullOrWhiteSpace(Config.Token)) return ActionOutcome.Failed(alias + " · 请输入访问令牌");
            _ = ReconnectNowAsync();
            return Ok("正在重连…");
        }
        if (action == DeviceAction.LeaseCancel)
        {
            await CancelLeaseRequestAsync().ConfigureAwait(true);
            // R7（PORT-PLAN §6）：HMI 没有取消端点，这条申请还在触屏上 —— 界面写明「若触屏已批准将自动释放」（样稿只有「已取消控制权申请」，后半句取自 R7）
            bool guarding = Connection?.Lease.CancelledRequestPending == true;
            return Ok("已取消控制权申请" + (guarding ? " · 若触屏已批准将自动释放" : ""));
        }
        var c = Conn0;
        bool running(int k) => Read(k).IsRunning;
        switch (action)
        {
            case DeviceAction.Enable:
                return Ok(rv + " 已启用", (await c.Control.EnableChannelAsync(n, ct).ConfigureAwait(true)).Warnings);
            case DeviceAction.Disable:
            {
                bool idle = Read(n).Own == StateKey.Idle;
                var r = await c.Rest.DisableChannelAsync(n, ct).ConfigureAwait(true);
                return idle ? Ok(rv + " 本来就是停用 · 已发送停用", r.Warnings, ToastKind.Info, true) : Ok(rv + " 已停用", r.Warnings, ToastKind.Warn);
            }
            case DeviceAction.Start:
            {
                // D6：本机下发过程序 → 运行它；否则启用 + 加热到 SP（POST /channels/{id}/start），不调 POST /run
                if (_downloaded.TryGetValue(n, out var doc))
                {
                    var r = await c.Control.RunProgramAsync(n, null, IdempotencyKeys.New("pc-start-rv" + n), ct).ConfigureAwait(true);
                    return Ok(rv + " 已启动 · 程序「" + doc.Name + "」", r.Warnings);
                }
                double? sp = a.Sp ?? Latest?.Channels.FirstOrDefault(x => x.Id == n)?.TempSpC;
                var s = await c.Control.StartChannelAsync(n, sp, ct).ConfigureAwait(true);
                return Ok(rv + " 已启动 · 加热至 " + (sp is double v ? JsNumber.ToString(v) : "—") + " ℃", s.Warnings);
            }
            case DeviceAction.Stop:
            {
                bool was = running(n);
                var r = await c.Rest.StopChannelAsync(n, ct).ConfigureAwait(true);
                return was ? Ok(rv + " 已停止", r.Warnings, ToastKind.Warn) : Ok(rv + " 未在运行 · 已发送停止", r.Warnings, ToastKind.Info, true);
            }
            case DeviceAction.HeatStart:
            {
                double? sp = a.Sp ?? Latest?.Channels.FirstOrDefault(x => x.Id == n)?.TempSpC;
                var r = await c.Control.StartHeatAsync(n, sp, ct).ConfigureAwait(true);
                return Ok(rv + " 开始加热 · 设定 " + (sp is double v ? JsNumber.ToString(v) : "—") + " ℃", r.Warnings);
            }
            case DeviceAction.HeatStop:
            {
                bool heating = Read(n).Heating;
                var r = await c.Rest.StopHeatAsync(n, ct).ConfigureAwait(true);
                return heating ? Ok(rv + " 已停止加热", r.Warnings) : Ok(rv + " 未在加热 · 已发送停止加热", r.Warnings, ToastKind.Info, true);
            }
            case DeviceAction.SetSp:
            {
                double sp = a.Sp ?? throw new ArgumentException("请输入设定温度");
                var r = await c.Control.SetSetpointAsync(n, sp, ct).ConfigureAwait(true);
                return Ok(rv + " 设定温度 " + JsNumber.ToString(sp) + " ℃", r.Warnings);
            }
            case DeviceAction.ProgLoad:
            {
                // 程序库 / 台账在 M5 / M7：M2 下发本机这个反应釜的草稿（同旧界面的「下发程序」）
                var doc = new ProgramDraftStore().Get(n);
                return await UploadProgramAsync(n, doc, ct).ConfigureAwait(true);
            }
            case DeviceAction.ProgRun:
            {
                var r = await c.Control.RunProgramAsync(n, null, IdempotencyKeys.New("pc-run-rv" + n), ct).ConfigureAwait(true);
                var name = _downloaded.TryGetValue(n, out var d) ? d.Name : "";
                return Ok(rv + " 程序「" + name + "」开始运行", r.Warnings);
            }
            case DeviceAction.ProgAbort:
            {
                bool was = Latest?.Channels.FirstOrDefault(x => x.Id == n)?.Program?.Running ?? false;
                var r = await c.Rest.AbortProgramAsync(n, ct).ConfigureAwait(true);
                return was ? Ok(rv + " 程序已中止", r.Warnings, ToastKind.Warn) : Ok(rv + " 没有运行中的程序 · 已发送中止", r.Warnings, ToastKind.Info, true);
            }
            case DeviceAction.StirOn:
            {
                int rpm = (int)Math.Round(a.Rpm ?? Latest?.Stir.RpmSp ?? 300);
                var r = await c.Control.SetStirAsync(true, rpm, ct).ConfigureAwait(true);
                return Ok("共用搅拌已启动 · " + rpm + " rpm", r.Warnings);
            }
            case DeviceAction.StirOff:
            {
                bool on = Latest?.Stir.On ?? false;
                var r = await c.Rest.StopStirAsync(ct).ConfigureAwait(true);
                return on ? Ok("共用搅拌已停止", r.Warnings) : Ok("共用搅拌未运行 · 已发送停止", r.Warnings, ToastKind.Info, true);
            }
            case DeviceAction.StirSet:
            {
                int rpm = (int)Math.Round(a.Rpm ?? throw new ArgumentException("请输入转速"));
                var r = await c.Control.SetStirAsync(null, rpm, ct).ConfigureAwait(true);
                return Ok("共用搅拌转速 " + rpm + " rpm", r.Warnings);
            }
            case DeviceAction.RunAll:
            {
                // D6：只运行本机下发过、没在运行的反应釜（program/run 逐个），绝不调 POST /run。
                // 对象 = 守卫用的同一个判断（ActionGuard.IsRunAllTarget），所以「全部运行」能点时就一定有要运行的反应釜
                int k = 0;
                var warnings = new List<string>();
                foreach (var rvN in RunAllTargets())
                {
                    var r = await c.Control.RunProgramAsync(rvN, null, IdempotencyKeys.New("pc-runall-rv" + rvN), ct).ConfigureAwait(true);
                    warnings.AddRange(r.Warnings);
                    k++;
                }
                return Ok("已启动 " + k + " 个反应釜", warnings);
            }
            case DeviceAction.StopAll:
            {
                int k = Enumerable.Range(1, Model.ChannelCount).Count(running);
                // 样稿「全部停止」：中止程序、停止加热、关闭进气；搅拌保持当前状态（要彻底切断用急停）。
                // 停不停搅拌只看 StopAllStopsStirring（默认 = ControlOptions.StopAllStopsStirring）→ POST /stop-all {stopStir}
                bool stir = StopAllStopsStirring;
                var r = await c.Rest.StopAllAsync(stir, ct).ConfigureAwait(true);
                return k > 0 ? Ok(StopAllText.Stopped(k, stir), r.Warnings, ToastKind.Warn) : Ok(StopAllText.NothingRunning(stir), r.Warnings, ToastKind.Info, true);
            }
            case DeviceAction.Estop:
            {
                var r = await c.Rest.EstopAsync("PC 急停", ct).ConfigureAwait(true);
                return Ok("急停已发送 · 复位需在触屏操作", r.Warnings, ToastKind.Err);
            }
            case DeviceAction.SessionStart:
            {
                var now = _now();
                string id = "PT-" + now.ToString("yyyyMMdd-HHmm", System.Globalization.CultureInfo.InvariantCulture) + (Sn.Length > 0 ? "-" + Sn : "");
                var r = await c.Rest.BeginSessionAsync(id, a.Text, IdempotencyKeys.New("pc-session"), ct).ConfigureAwait(true);
                return Ok("会话 " + (r.Result?.Id ?? id) + " 开始记录", r.Warnings);
            }
            case DeviceAction.SessionMark:
            {
                string text = !string.IsNullOrEmpty(a.Text) ? a.Text : a.N is int mn && mn != 0 ? "RV" + mn + " 标记" : "标记";
                var r = await c.Rest.AddMarkerAsync(text, IdempotencyKeys.New("pc-mark"), ct).ConfigureAwait(true);
                return Ok("已打标记「" + text + "」", r.Warnings);
            }
            case DeviceAction.SessionEnd:
            {
                var id = Session?.Id;
                var r = await c.Rest.EndSessionAsync(ct).ConfigureAwait(true);
                return Ok("会话 " + id + " 已结束", r.Warnings);
            }
            case DeviceAction.AlarmAck:
            {
                var al = _alarmMap.TryGetValue(a.Id ?? "", out var x) ? x : null;
                var r = await c.Rest.AckAlarmAsync(a.Id ?? "", ct).ConfigureAwait(true);
                string what = al is null || al.Level != AlarmLevel.Err ? "提醒" : al.Code == "P-HIGH" ? "超压报警" : al.Code == "ESTOP" ? "急停报警" : "报警";
                return Ok((al?.Ch is int ac ? "RV" + ac + " " : "") + what + "已确认", r.Warnings);
            }
            case DeviceAction.LeaseRequest:
                await RequestLeaseAsync().ConfigureAwait(true);
                return Ok("已申请控制权 · 等待触屏确认");
            case DeviceAction.LeaseRelease:
                await ReleaseLeaseAsync().ConfigureAwait(true);
                return Ok("控制权已释放 · 回到本地只读");
            case DeviceAction.Disconnect:
            {
                var held = Lease.State;
                await DisconnectAsync().ConfigureAwait(true);
                return Ok("已断开连接" + (held is LeaseStatus.Remote or LeaseStatus.Lost ? " · 控制权已释放" : held == LeaseStatus.Requested ? " · 控制权申请已取消" : ""));
            }
            default:
                throw new NotSupportedException(CoreActions.Label(action) + "：这台设备不支持");
        }
    }

    /// <summary>下发程序到第 n 路（PUT /channels/{n}/program，需要控制权）并记下（「启动」「全部运行」用，D6）。</summary>
    public async Task<ActionOutcome> UploadProgramAsync(int n, ProgramDoc doc, CancellationToken ct)
    {
        var r = await Conn0.Control.ReplaceProgramAsync(n, doc, ct).ConfigureAwait(true);
        _downloaded[n] = doc;
        MarkDirty(SourceChange.Programs);
        return ActionOutcome.Done(Config.Alias + " · 程序「" + doc.Name + "」已下发到 RV" + n, ToastKind.Ok, r.Warnings);
    }

    /// <summary>改别名（本机的事，不发给 HMI）。</summary>
    public void Rename(string alias)
    {
        Config = Config with { Alias = alias };
        MarkDirty(SourceChange.Info);
    }

    /// <summary>曲线（实时）：TelemetryStore 里的帧（最多 7200 帧），压力 psi → MPa。M5 换成 10 s 一格的 ChannelHistory，增量追加。</summary>
    private sealed class LiveHistoryView : IChannelHistory
    {
        private readonly LiveDeviceSource _s;
        public LiveHistoryView(LiveDeviceSource s) => _s = s;

        public IReadOnlyList<(DateTime T, double? V)> Series(int n, HistoryKey key, double minutes = 60)
        {
            var c = _s.Connection;
            if (c == null) return Array.Empty<(DateTime, double?)>();
            var frames = c.Telemetry.Since(DateTime.UtcNow.AddMinutes(-Math.Max(1, minutes)));
            var list = new List<(DateTime, double?)>(frames.Length);
            foreach (var f in frames)
            {
                var ch = f.Channels.FirstOrDefault(x => x.Id == n);
                double? v = ch is null ? null : key switch
                {
                    HistoryKey.TempC => ch.TempC,
                    HistoryKey.PressureMPa => Units.PsiToMpa(ch.PressurePsi),
                    HistoryKey.HeaterPct => ch.HeatOutputPct,
                    HistoryKey.Rpm => f.Stir.On ? f.Stir.RpmSp : 0,
                    HistoryKey.SpC => ch.TempSpC,
                    HistoryKey.PTargetMPa => ch.PressureSpPsi > 0 ? Units.PsiToMpa(ch.PressureSpPsi) : null,
                    _ => null,
                };
                list.Add((f.Ts.ToLocalTime(), v));
            }
            return list;
        }
    }
}
