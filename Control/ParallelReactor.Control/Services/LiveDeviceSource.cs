using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
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
/// 一台真机（PORT-PLAN §5 M2「LiveDeviceSource」：本里程碑只管一台；重连监督、多台、只读令牌学习在 M3）。包装一个 <see cref="IReactorConnection"/>：
/// <list type="bullet">
/// <item>连接：<see cref="ConnectAsync"/> 新建连接（工厂可替换，测试用假连接）、挂上全部事件、GET /device + /control + /snapshot 再开 WS；
///   失败时摘掉事件、释放连接再抛出（同旧 ConnectionService：首次连接失败不重试，M3 加监督循环）。</item>
/// <item>事件：遥测帧、连接状态、控制权迁移、WS control、报警、事件、错误、HMI 重启。事件由 Client 经 UiContext 投递到 UI 线程；
///   这里只记下「脏了什么」和最新一帧，<b>合并</b>成一次刷新（<see cref="Changed"/>，经构造时给的 post 排到 UI 线程，一批只排一次）——
///   多帧之间只应用最后一帧，就地更新，不重建任何集合。</item>
/// <item>释放：<see cref="DisposeAsync"/> / <see cref="DisconnectAsync"/> <b>摘掉挂上的每一个事件处理器</b>（旧 MainViewModel 从不摘），持有控制权时先释放（≤ 3 s），再释放连接。</item>
/// <item>操作：<see cref="ExecAsync"/> 按样稿的操作调 Client（控制类走 ControlledWrites 带租约，停止类直接 REST）；「启动」「全部运行」绝不调 HMI 的 POST /run（D6）：
///   本机这次下发过程序的反应釜 → program/run，否则 → /channels/{id}/start（启用 + 加热到 SP）。</item>
/// <item>控制权申请在后台等触屏确认；本地「取消申请」时不断开轮询（HMI 没有取消端点，R7）：之后若触屏批准，立即释放。</item>
/// </list>
/// 线程：除 <see cref="ConnectAsync"/> 内部的网络调用外全部在 UI 线程上（事件经 UiContext）；没有 UiContext（测试）时在调用线程上。
/// </summary>
public sealed class LiveDeviceSource : IDeviceSource
{
    private readonly Action<Action> _post;
    private readonly Func<ClientOptions, IReactorConnection> _factory;
    private readonly SynchronizationContext? _uiContext;
    private readonly ModelProfiles _models;
    private readonly Func<DateTime> _now;
    private readonly string _clientName;
    private readonly LiveHistoryView _history;
    private readonly Dictionary<string, AlarmItem> _alarmMap = new(StringComparer.Ordinal);
    private readonly List<EventItem> _events = new();
    private readonly Dictionary<int, ProgramDoc> _downloaded = new();

    private IReadOnlyList<AlarmItem> _alarms = Array.Empty<AlarmItem>();
    private TelemetrySnapshot? _pending;
    private SourceChange _dirty;
    private bool _flushPending, _disposed, _attached, _cancelledRequest;
    private ConnStatus _conn = ConnStatus.Connecting;
    private bool _everOnline;
    private DateTime? _since, _lostAt, _requestAt, _graceAt;
    private ModelProfile? _model;

    /// <param name="config">地址、端口、令牌、别名。</param>
    /// <param name="clientName">本机客户端名（申请控制权时报给触屏）。</param>
    /// <param name="post">把一次刷新排到 UI 线程（App：Dispatcher.UIThread.Post；测试：直接执行）。</param>
    /// <param name="factory">新建连接（默认 new ReactorConnection(options)；测试给假连接）。</param>
    /// <param name="uiContext">Client 投递事件用的 UI 同步上下文（App：AvaloniaSynchronizationContext；测试 null = 调用线程）。</param>
    public LiveDeviceSource(DeviceConfig config, string clientName, Action<Action> post, Func<ClientOptions, IReactorConnection>? factory = null,
        SynchronizationContext? uiContext = null, ModelProfiles? models = null, Func<DateTime>? now = null)
    {
        Config = config ?? throw new ArgumentNullException(nameof(config));
        _clientName = clientName;
        _post = post ?? throw new ArgumentNullException(nameof(post));
        _factory = factory ?? (o => new ReactorConnection(o));
        _uiContext = uiContext;
        _models = models ?? ModelProfiles.Embedded;
        _now = now ?? (() => DateTime.Now);
        _history = new LiveHistoryView(this);
    }

    public Guid Id => Config.Id;
    public bool IsDemo => false;
    public DeviceConfig Config { get; private set; }

    /// <summary>当前连接（未连接 / 已断开为 null）：性能测试、程序页、导出直接用它。</summary>
    public IReactorConnection? Connection { get; private set; }

    /// <summary>要给用户的提示（控制权迁移、HMI 重启、报警、申请结果；文字同旧 MainViewModel）。在 UI 线程上触发。</summary>
    public event Action<string, ToastKind>? Notice;

    public event Action<SourceChange>? Changed;

    public ConnInfo Conn => new(_conn, _everOnline, 0, _since, _lostAt, StopReachable);

    /// <summary>
    /// 停止类命令现在还能尽力送达：有连接对象（没被用户断开、没释放）且连上过 —— 包括只是 WebSocket 断了、Client 在自己退避重连的时候
    /// （1008 慢消费者、心跳超时都会这样；REST 多半还通）。这时急停 / 全部停止 / 停止类照样发送（失败照常提示），控制类照样禁用（GuardFacts.StopReachable）。
    /// 旧界面也是只要有连接对象就能急停。
    /// </summary>
    public bool StopReachable => Connection != null && _everOnline && _conn != ConnStatus.Disconnected && !_disposed;
    public DeviceInfoDto? Info { get; private set; }
    public SettingsDto? Settings { get; private set; }
    public CommDto? Comm => null;
    public Role Role => Role.Unknown;
    public TelemetrySnapshot? Latest { get; private set; }
    public DateTime? DataAt => Latest is { } f ? f.Ts.ToLocalTime() : null;
    public IReadOnlyList<AlarmItem> Alarms => _alarms;
    public IReadOnlyList<EventItem> Events => _events;
    public IChannelHistory History => _history;

    public ModelProfile Model => _model ??= _models.Get(_models.FallbackKey) ?? _models.All[0];
    public string Sn => Info?.Serial ?? "";
    public EstopInfo Estop => Latest?.Estop is { } e ? new EstopInfo(e.Active, e.Since?.ToLocalTime()) : EstopInfo.None;
    public StirInfo? Stir => Model.StirMode == StirMode.Independent ? null : Latest?.Stir is { } s ? new StirInfo(s.On, s.RpmSp) : new StirInfo(false, 0);
    public SessionInfo? Session => Latest?.Session is { Active: true } s ? new SessionInfo(true, s.Id ?? s.TestId, null, 0) : null;

    // ———————————————————— 控制权 ————————————————————

    public LeaseView Lease
    {
        get
        {
            var c = Connection;
            var st = LeaseStatus.Local;
            string? other = null;
            int remain = 0, grace = 0;
            if (c != null && _conn != ConnStatus.Disconnected)
            {
                switch (c.Lease.State)
                {
                    case ClientLease.Holding: st = LeaseStatus.Remote; break;
                    case ClientLease.Lost:
                        st = LeaseStatus.Lost;
                        grace = Math.Max(0, c.Lease.LeaseGraceSec - Seconds(_graceAt));
                        break;
                    case ClientLease.Requesting when !_cancelledRequest:
                        st = LeaseStatus.Requested;
                        int timeout = c.ControlState?.RequestTimeoutSec > 0 ? c.ControlState.RequestTimeoutSec : 60;
                        remain = Math.Max(0, timeout - Seconds(_requestAt));
                        break;
                    default:
                        var cs = c.ControlState;
                        if (cs != null && cs.Mode is ControlModes.Remote or ControlModes.RemoteLost && !string.IsNullOrEmpty(cs.Holder)
                            && !string.Equals(cs.Holder, c.Options.ClientName, StringComparison.Ordinal))
                        {
                            st = LeaseStatus.Other;
                            other = cs.Holder;
                        }
                        break;
                }
            }
            var d = DeviceTexts.Lease(st, remain, grace, _clientName, other);
            return new LeaseView(st, remain, grace, d.Holder, d);
        }
    }

    private int Seconds(DateTime? since) => since is { } t ? (int)Math.Floor((_now() - t).TotalSeconds) : 0;

    // ———————————————————— 连接 ————————————————————

    /// <summary>
    /// 连接（新建连接、挂事件、StartAsync）。失败时摘掉事件、释放连接、回到「已断开」再抛出。已有连接时先断开。
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(LiveDeviceSource));
        if (Connection != null) await CloseAsync(releaseLease: true).ConfigureAwait(true);
        var opt = new ClientOptions(Config.BaseUrl, Config.Token ?? "", _clientName)
        {
            UiContext = _uiContext,
            ClientVersion = "pc-" + (typeof(LiveDeviceSource).Assembly.GetName().Version?.ToString(3) ?? "1.0"),
        };
        var c = _factory(opt);
        Connection = c;
        Attach(c);
        SetConn(ConnStatus.Connecting);
        try
        {
            await c.StartAsync(ct).ConfigureAwait(true);
        }
        catch
        {
            Detach(c);
            Connection = null;
            try { await c.DisposeAsync().ConfigureAwait(true); } catch { }
            SetConn(ConnStatus.Disconnected);
            throw;
        }
        Info = c.Device;
        _model = Info != null ? _models.Resolve(Info, Settings) : null;
        _everOnline = true;
        SetConn(ConnStatus.Online);
        MarkDirty(SourceChange.All);
        _ = LoadExtrasAsync(c);
    }

    /// <summary>连上之后的一次性读取（尽力而为）：/settings（型号的量程）、/alarms（当前报警）、最近的事件。</summary>
    private async Task LoadExtrasAsync(IReactorConnection c)
    {
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
            foreach (var dto in a.Items) UpsertAlarm(dto);
            MarkDirty(SourceChange.Alarms);
        }
        catch { }
        try
        {
            long head = Info?.EventSeqHead ?? 0;
            var page = await c.Rest.EventsAsync(Math.Max(0, head - 200), 200, CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(c, Connection)) return;
            foreach (var r in page.Items) AddEvent(r.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture), r.Ts, r.Level, r.Kind, r.Channel, r.Title, r.Message);
            MarkDirty(SourceChange.Events);
        }
        catch { }
    }

    private void Attach(IReactorConnection c)
    {
        if (_attached) return;
        c.TelemetryReceived += OnTelemetry;
        c.ConnectionStateChanged += OnConnState;
        c.LeaseTransitioned += OnLease;
        c.ControlChanged += OnControl;
        c.AlarmChanged += OnAlarm;
        c.EventReceived += OnEvent;
        c.Error += OnError;
        c.ServerRestarted += OnRestarted;
        _attached = true;
    }

    /// <summary>摘掉 <see cref="Attach"/> 挂上的每一个处理器（一一对应）。</summary>
    private void Detach(IReactorConnection c)
    {
        if (!_attached) return;
        c.TelemetryReceived -= OnTelemetry;
        c.ConnectionStateChanged -= OnConnState;
        c.LeaseTransitioned -= OnLease;
        c.ControlChanged -= OnControl;
        c.AlarmChanged -= OnAlarm;
        c.EventReceived -= OnEvent;
        c.Error -= OnError;
        c.ServerRestarted -= OnRestarted;
        _attached = false;
    }

    private void OnTelemetry(TelemetrySnapshot f)
    {
        _pending = f;
        MarkDirty(SourceChange.Frame);
    }

    private void OnConnState(ConnectionState s)
    {
        // WS 断开后 Client 自己退避重连（0.5 → 10 s）；重连倒计时在 M3（TelemetryWsClient.NextRetryAt）
        var st = s switch
        {
            ConnectionState.Connected or ConnectionState.Resyncing => ConnStatus.Online,
            ConnectionState.Connecting or ConnectionState.Reconnecting => ConnStatus.Connecting,
            _ => ConnStatus.Disconnected,
        };
        if (Connection != null && s is ConnectionState.Connecting && _conn == ConnStatus.Online) return;   // 首次 Start 之后 WS 才在连：REST 已经连上
        SetConn(st);
    }

    private void SetConn(ConnStatus st)
    {
        if (st == _conn) return;
        if (st == ConnStatus.Online) { _since = _now(); _lostAt = null; }
        else if (_conn == ConnStatus.Online) _lostAt = _now();
        _conn = st;
        MarkDirty(SourceChange.Conn);
    }

    private void OnLease(LeaseTransition t)
    {
        if (t.To == ClientLease.Requesting) _requestAt ??= _now();
        else _requestAt = null;
        if (t.To == ClientLease.Lost) _graceAt = _now(); else if (t.To != ClientLease.Lost) _graceAt = null;
        MarkDirty(SourceChange.Lease);
        // 本地已取消的申请被触屏批准：立即释放（R7：不留没人心跳的孤儿租约）
        if (_cancelledRequest && t.To == ClientLease.Holding)
        {
            _cancelledRequest = false;
            _ = ReleaseQuietAsync();
            return;
        }
        if (t.To != ClientLease.Requesting) _cancelledRequest = false;
        // 提示（同旧 MainViewModel.OnLease）
        string? msg = null;
        if (t.To == ClientLease.Lost) msg = "控制权连接丢失 · 宽限期内自动恢复";
        else if (t.From == ClientLease.Lost && t.To == ClientLease.Holding) msg = "控制权已恢复";
        else if (t.To == ClientLease.None && t.Reason.Contains("takenBack")) msg = "触屏已收回控制权";
        else if (t.To == ClientLease.None && t.Reason.Contains("invalidated")) msg = "急停：控制权已失效";
        else if (t.To == ClientLease.None && t.From is ClientLease.Holding or ClientLease.Lost && t.Reason.StartsWith("state:", StringComparison.Ordinal))
            msg = "重连后设备已回到本地模式 · 控制权失效" + (t.Reason == "state:local" ? "，正在重新申请" : "");
        else if (t.To == ClientLease.None && t.From is ClientLease.Holding or ClientLease.Lost && t.Reason.StartsWith("heartbeat:", StringComparison.Ordinal))
            msg = "心跳被拒 · 控制权失效（" + t.Reason[10..] + "）";
        else if (t.To == ClientLease.None && t.From is ClientLease.Holding or ClientLease.Lost && t.Reason.StartsWith("write:", StringComparison.Ordinal))
            msg = "写命令被拒 · 控制权失效（" + t.Reason[6..] + "）";
        else if (t.To == ClientLease.None && t.Reason == "expired") msg = "控制权宽限期满 · 正在重新申请";
        if (msg != null) Notice?.Invoke(Config.Alias + " · " + msg, t.To is ClientLease.Lost or ClientLease.None ? ToastKind.Warn : ToastKind.Info);
    }

    private void OnControl(ControlMsg m) => MarkDirty(SourceChange.Lease);

    private void OnAlarm(AlarmMsg a)
    {
        UpsertAlarm(a.Alarm);
        MarkDirty(SourceChange.Alarms);
        if (a.Alarm.Level is "err" or "error" || a.Alarm.State == "active" && a.Alarm.Level == "warn")
            Notice?.Invoke("报警 " + a.Alarm.Rv + "：" + a.Alarm.Title, a.Alarm.Level is "err" or "error" ? ToastKind.Err : ToastKind.Warn);
    }

    private void OnEvent(EventMsg e)
    {
        AddEvent(e.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture), e.Ts, e.Level, e.Kind, e.Channel, e.Title, e.Message);
        MarkDirty(SourceChange.Events);
    }

    private void OnError(Exception ex) => MarkDirty(SourceChange.Lease);   // 心跳异常时控制权徽标要跟着变

    private void OnRestarted()
    {
        _alarmMap.Clear();
        _alarms = Array.Empty<AlarmItem>();
        // HMI 重启后它手里的程序不一定还是本机下发的那份：「启动」回到启用 + 加热（D6），「全部运行」没有对象，要先重新下发
        _downloaded.Clear();
        Notice?.Invoke(Config.Alias + " · 设备 HMI 已重启 · 曲线缓冲已清空", ToastKind.Warn);
        MarkDirty(SourceChange.All);
    }

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

    /// <summary>每秒（UiTicker）：申请 / 宽限倒计时的文字要变。</summary>
    public void Tick()
    {
        var c = Connection;
        if (c != null && c.Lease.State is ClientLease.Requesting or ClientLease.Lost) MarkDirty(SourceChange.Lease);
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
        return new GuardFacts
        {
            Exists = !_disposed,
            Alias = Config.Alias,
            Conn = _conn,
            EverOnline = _everOnline,
            RetryS = 0,
            StopReachable = StopReachable,
            Lease = lease.State,
            LeaseRemainS = lease.RemainS,
            LeaseGraceS = lease.GraceS,
            LeaseHolder = lease.State == LeaseStatus.Other ? lease.Holder : null,
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

    public async Task<ActionOutcome> ExecAsync(DeviceAction action, ActionPayload a, CancellationToken ct)
    {
        string rv = "RV" + a.N;
        int n = a.N ?? 0;
        string alias = Config.Alias;
        ActionOutcome Ok(string msg, IReadOnlyList<string>? warnings = null, ToastKind tone = ToastKind.Ok, bool info = false) =>
            ActionOutcome.Done((msg.StartsWith(alias, StringComparison.Ordinal) ? "" : alias + " · ") + msg, tone, warnings, info);
        // 不需要连接的操作（重连、取消申请）：已断开时也能做
        if (action == DeviceAction.Reconnect)
        {
            _ = ReconnectNowAsync();
            return Ok("正在重连…");
        }
        if (action == DeviceAction.LeaseCancel)
        {
            await CancelLeaseRequestAsync().ConfigureAwait(true);
            return Ok("已取消控制权申请");
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
                // 样稿「全部停止」：中止程序、停止加热、关闭进气；搅拌保持当前状态（要彻底切断用急停）
                var r = await c.Rest.StopAllAsync(false, ct).ConfigureAwait(true);
                return k > 0 ? Ok("已停止 " + k + " 个反应釜", r.Warnings, ToastKind.Warn) : Ok("没有运行中的反应釜 · 已发送全部停止", r.Warnings, ToastKind.Info, true);
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

    /// <summary>申请控制权：后台等触屏确认（倒计时见 <see cref="Lease"/>），结果用 <see cref="Notice"/> 提示（文字同旧界面）。</summary>
    public Task RequestLeaseAsync()
    {
        var c = Conn0;
        _cancelledRequest = false;
        _requestAt = _now();
        MarkDirty(SourceChange.Lease);
        _ = AcquireAsync(c);
        return Task.CompletedTask;
    }

    private async Task AcquireAsync(IReactorConnection c)
    {
        try
        {
            bool ok = await c.Lease.AcquireAsync(CancellationToken.None).ConfigureAwait(true);
            if (!ReferenceEquals(c, Connection)) return;
            if (_cancelledRequest)
            {
                _cancelledRequest = false;
                if (ok) await ReleaseQuietAsync().ConfigureAwait(true);
                return;
            }
            Notice?.Invoke(Config.Alias + " · " + (ok ? "已取得远程控制权" : "申请被拒绝或超时"), ok ? ToastKind.Ok : ToastKind.Warn);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(c, Connection)) Notice?.Invoke(Config.Alias + " · " + FleetActions.Describe(ex), ToastKind.Err);
        }
        finally { MarkDirty(SourceChange.Lease); }
    }

    /// <summary>
    /// 取消申请（HMI 没有取消端点，A5）：界面立即回到「本地只读」，后台照样等结果；若触屏之后批准，立即释放（R7）。
    /// </summary>
    public Task CancelLeaseRequestAsync()
    {
        if (Connection?.Lease.State == ClientLease.Requesting) _cancelledRequest = true;
        MarkDirty(SourceChange.Lease);
        return Task.CompletedTask;
    }

    public async Task ReleaseLeaseAsync()
    {
        var c = Conn0;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await c.Lease.ReleaseAsync(cts.Token).ConfigureAwait(true);
        MarkDirty(SourceChange.Lease);
    }

    private async Task ReleaseQuietAsync()
    {
        try { if (Connection is { } c) { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3)); await c.Lease.ReleaseAsync(cts.Token).ConfigureAwait(true); } }
        catch { }
        MarkDirty(SourceChange.Lease);
    }

    /// <summary>立即重连：已断开时按原配置重新连接（WS 断线后的自动重连由 Client 自己做；倒计时与「立即重连」在 M3）。</summary>
    public async Task ReconnectNowAsync()
    {
        if (Connection != null || _disposed) return;
        try { await ConnectAsync(CancellationToken.None).ConfigureAwait(true); }
        catch (Exception ex) { Notice?.Invoke(Config.Alias + " · 连接失败：" + FleetActions.Describe(ex), ToastKind.Err); }
    }

    /// <summary>用户断开：持有控制权时先释放（≤ 3 s），摘掉事件，释放连接；设备留在列表里（「已断开」，可以再连）。</summary>
    public async Task DisconnectAsync()
    {
        await CloseAsync(releaseLease: true).ConfigureAwait(true);
        SetConn(ConnStatus.Disconnected);
        MarkDirty(SourceChange.All);
    }

    /// <summary>改别名（M2 只有这一项能在本机改）。</summary>
    public void Rename(string alias)
    {
        Config = Config with { Alias = alias };
        MarkDirty(SourceChange.Info);
    }

    private async Task CloseAsync(bool releaseLease)
    {
        var c = Connection;
        if (c == null) return;
        Detach(c);
        Connection = null;
        _cancelledRequest = false;
        _downloaded.Clear();
        try
        {
            if (releaseLease && c.Lease.State is ClientLease.Holding or ClientLease.Lost)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await c.Lease.ReleaseAsync(cts.Token).ConfigureAwait(false); } catch { }
            }
            await c.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }

    /// <summary>释放：摘掉全部事件、释放控制权（≤ 3 s）、释放连接。可在任意线程调用。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await CloseAsync(releaseLease: true).ConfigureAwait(false);
        Changed = null;
        Notice = null;
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
