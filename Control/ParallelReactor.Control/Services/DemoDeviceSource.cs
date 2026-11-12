using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Events;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 演示数据里的一台设备（Core 的 <see cref="DemoDevice"/>，同一个 <see cref="DemoFleet"/> 里的几台共用时钟）。
/// 只读包装：状态只由 DemoFleet 改（参数、每秒推进、操作）；DemoFleet.Changed 里涉及这台（或全体）时触发 <see cref="Changed"/>（同步，
/// 在调用 Tick / Act 的线程上，即 UI 线程；FleetVm 再合并）。操作走 DemoFleet.Act（quiet：提示由 Fleet/DeviceActions 统一给），
/// 提示的写法与语气同样稿 act（「1号机 · RV1 已启用」、停止类没在运行时 info 语气）。
/// </summary>
public sealed class DemoDeviceSource : IDeviceSource
{
    private readonly DemoFleet _fleet;
    private readonly DemoHistoryView _history;
    private readonly AlarmBook _alarmBook;
    private readonly EventBook _eventBook;
    private IReadOnlyList<AlarmItem> _alarms = Array.Empty<AlarmItem>();
    private IReadOnlyList<EventItem> _events = Array.Empty<EventItem>();
    private int _alarmVersion = -1, _eventVersion = -1;
    private (int Count, string? First) _eventKey = (-1, null);
    private IReadOnlyList<ModuleComm>? _modules;
    private bool _disposed;

    public DemoDeviceSource(DemoFleet fleet, DemoDevice device)
    {
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Id = IdFor(device.K);
        _history = new DemoHistoryView(fleet, device);
        _alarmBook = new AlarmBook();
        _eventBook = new EventBook();
        _fleet.Changed += OnFleetChanged;
    }

    /// <summary>演示设备的固定身份（按位置）。</summary>
    public static Guid IdFor(int k) => new(k, 0x5052, 0x4d32, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>包装的演示设备（演示数据重建时整个换掉，见 FleetVm）。</summary>
    public DemoDevice Device { get; }

    public DemoFleet Fleet => _fleet;

    public Guid Id { get; }
    public bool IsDemo => true;
    public DeviceConfig Config => new(Id, Device.Alias, Device.Host, Device.Port, Device.Token, Device.AutoConnect);
    public ConnInfo Conn => new(Device.Conn.State, Device.Conn.EverOnline, Device.Conn.RetryS, Device.Conn.Since, Device.Conn.LostAt, Attempts: Device.Conn.Attempts);
    public DeviceInfoDto? Info => null;
    public SettingsDto? Settings => null;
    public CommDto? Comm => null;
    public LeaseView Lease => new(Device.Lease.State, Device.Lease.RemainS, Device.Lease.GraceS, Device.Lease.Holder, Device.LeaseInfo, WaitS: DemoFleet.LeaseWaitS);
    /// <summary>演示数据没有令牌权限之分（样稿没有只读令牌）。</summary>
    public TokenRole Role => TokenRole.Unknown;

    public void LearnForbidden(DeviceAction action) { }
    public TelemetrySnapshot? Latest => null;
    public DateTime? DataAt => Device.DataAt;
    public ModelProfile Model => Device.Model;
    public string Sn => Device.Sn;
    public EstopInfo Estop => new(Device.Estop.Active, Device.Estop.At, Device.Estop.By);
    public StirInfo? Stir => Device.Stir is { } s ? new StirInfo(s.On, s.Rpm) : null;
    public IChannelHistory History => _history;

    public SessionInfo? Session => Device.Session is { } s ? new SessionInfo(s.Active, s.Id, s.StartAt, s.Markers.Count) : null;

    /// <summary>
    /// 报警簿：DemoDevice.Alarms 变了（换了条目、或某条确认了）才照它同步（Core 的 AlarmBook.Sync）；没变时直接返回，不分配（R10：
    /// 每秒刷新里分区头、报警条、页面拥有的报警、检查器都会读好几次 —— M4 复核）。比较的是条目实例与可变的确认状态，不靠 DemoFleet.Changed
    /// （FleetVm 的刷新可能比这台设备自己的 Changed 处理先跑：测试里刷新是立即执行的）。
    /// </summary>
    public AlarmBook AlarmBook
    {
        get
        {
            var src = Device.Alarms;
            if (!SameAlarms(src))
            {
                _alarmBook.Sync(src);
                Remember(src);
            }
            return _alarmBook;
        }
    }

    // 上一次同步时的条目（实例 + 确认状态）：DemoAlarm 只有 Acked / AckBy 会变，其余字段不变
    private DemoAlarm[] _seenAlarms = Array.Empty<DemoAlarm>();
    private bool[] _seenAcked = Array.Empty<bool>();
    private string?[] _seenAckBy = Array.Empty<string?>();
    private bool _alarmsSynced;

    private bool SameAlarms(IReadOnlyList<DemoAlarm> src)
    {
        if (!_alarmsSynced || src.Count != _seenAlarms.Length) return false;
        for (int i = 0; i < src.Count; i++)
        {
            var a = src[i];
            if (!ReferenceEquals(a, _seenAlarms[i]) || a.Acked != _seenAcked[i] || !string.Equals(a.AckBy, _seenAckBy[i], StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private void Remember(IReadOnlyList<DemoAlarm> src)
    {
        if (_seenAlarms.Length != src.Count)
        {
            _seenAlarms = new DemoAlarm[src.Count];
            _seenAcked = new bool[src.Count];
            _seenAckBy = new string?[src.Count];
        }
        for (int i = 0; i < src.Count; i++)
        {
            _seenAlarms[i] = src[i];
            _seenAcked[i] = src[i].Acked;
            _seenAckBy[i] = src[i].AckBy;
        }
        _alarmsSynced = true;
    }

    /// <summary>事件簿：DemoDevice.Events 只在前面追加、从后面淘汰 —— 条数或最新一条变了才重新同步。</summary>
    public EventBook EventBook
    {
        get
        {
            var src = Device.Events;
            var key = (src.Count, src.Count > 0 ? src[0].Id : null);
            if (key != _eventKey)
            {
                _eventKey = key;
                _eventBook.Sync(src);
            }
            return _eventBook;
        }
    }

    public IReadOnlyList<AlarmItem> Alarms
    {
        get
        {
            var book = AlarmBook;
            if (book.Version != _alarmVersion)
            {
                _alarmVersion = book.Version;
                _alarms = book.Current.Select(a => new AlarmItem(a.Id, a.Level, a.Ch, a.Code, a.Text, a.At, a.Acked, a.AckBy)).ToArray();
            }
            return _alarms;
        }
    }

    public IReadOnlyList<EventItem> Events
    {
        get
        {
            var book = EventBook;
            if (book.Version != _eventVersion)
            {
                _eventVersion = book.Version;
                _events = book.Items.Select(e => new EventItem(e.Key, e.At, e.Level, e.N, e.Text)).ToArray();
            }
            return _events;
        }
    }

    /// <summary>模块通讯（DemoDevice.Comm：在线 / 最近成功由 DemoFleet 每秒推进；断线时停在断线前）。</summary>
    public IReadOnlyList<ModuleComm> Modules
    {
        get
        {
            var src = Device.Comm;
            var cur = _modules;
            bool same = cur != null && cur.Count == src.Count;
            for (int i = 0; same && i < src.Count; i++)
                same = cur![i].Key == src[i].Key && cur[i].Online == src[i].Online && cur[i].LastOk == src[i].LastOk && cur[i].Count == src[i].Count;
            if (!same) _modules = src.Select(c => new ModuleComm(c.Key, c.Name, c.Count, c.Online, c.LastOk)).ToArray();
            return _modules!;
        }
    }

    public string HmiVersion => string.IsNullOrEmpty(Device.HmiVersion) ? Core.Format.Fmt.Dash : Device.HmiVersion;
    public string ApiVersion => string.IsNullOrEmpty(Device.ApiVersion) ? Core.Format.Fmt.Dash : Device.ApiVersion;

    public ChannelExtras? Extras(int n)
    {
        var ch = Device.Channel(n);
        if (ch is null || !ch.Installed) return null;
        // 设定转速同样稿 numInput 的 ch.rpm：不看连接、不看搅拌开没开（有效转速 Rpm 关着是 0）
        return new ChannelExtras(Device.Reading(ch, DemoReading.VolumeMl), Device.Reading(ch, DemoReading.Rpm), ch.StirOn, ch.Cooling, Device.Reading(ch, DemoReading.Mmol),
            RpmSp: ch.Rpm);
    }

    public ProgramRecord? Program(int n) => Device.Channel(n)?.Program;

    private IReadOnlyList<ProgramChoice>? _choices;

    /// <summary>演示程序库（同一型号的全部程序，样稿 d.programs）：每个反应釜都一样。</summary>
    public IReadOnlyList<ProgramChoice> ProgramChoices(int n) => _choices ??= Device.Programs.Select(p => new ProgramChoice(p.Id, p.Name)).ToArray();

    /// <summary>演示程序库（样稿 PR.sim.programs(d)）：每个反应釜都一样。</summary>
    public IReadOnlyList<ProgramDef> Library(int n) => Device.Programs;

    public ChannelReading Read(int n) => ChannelProjection.FromDemo(Device, n);

    public GuardFacts Facts() => Device.Facts();

    public bool GasOpen(string key) => Device.GasOpen(key);

    public event Action<SourceChange>? Changed;

    private void OnFleetChanged(object? sender, DemoChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Device is null || ReferenceEquals(e.Device, Device))
        {
            _history.Invalidate();
            Changed?.Invoke(SourceChange.All);
        }
    }

    // ———————————————————— 操作 ————————————————————

    public Task<ActionOutcome> ExecAsync(DeviceAction action, ActionPayload args, CancellationToken ct) => Task.FromResult(Exec(action, args));

    /// <summary>
    /// 同步执行（样稿 act）：DemoFleet 自己会再查一次守卫（被拦下时 Ok = false、Message 是原因）。提示文字与语气照抄 DemoFleet.Act 的非 quiet 分支。
    /// </summary>
    public ActionOutcome Exec(DeviceAction action, ActionPayload? args = null)
    {
        var r = _fleet.Act(Device, action, args, quiet: true);
        if (!r.Ok) return ActionOutcome.Failed(r.Message);
        // 别名取执行之后的（样稿 act 的 toast 用 D.alias：改名之后是新名字「反应台B · 已重命名（原名 2号机）」）
        var alias = Device.Alias;
        bool warnLevel = !r.Info && action is DeviceAction.Estop or DeviceAction.StopAll or DeviceAction.Stop or DeviceAction.ProgAbort or DeviceAction.Disable;
        var tone = r.Info ? ToastKind.Info : action == DeviceAction.Estop ? ToastKind.Err : warnLevel ? ToastKind.Warn : ToastKind.Ok;
        var text = (action == DeviceAction.Remove || r.Message.StartsWith(alias, StringComparison.Ordinal) ? "" : alias + " · ") + r.Message;
        return ActionOutcome.Done(text, tone, info: r.Info);
    }

    public Task RequestLeaseAsync() { Exec(DeviceAction.LeaseRequest); return Task.CompletedTask; }
    public Task CancelLeaseRequestAsync() { Exec(DeviceAction.LeaseCancel); return Task.CompletedTask; }
    public Task ReleaseLeaseAsync() { Exec(DeviceAction.LeaseRelease); return Task.CompletedTask; }
    public Task ReconnectNowAsync() { Exec(DeviceAction.Reconnect); return Task.CompletedTask; }
    public Task DisconnectAsync() { Exec(DeviceAction.Disconnect); return Task.CompletedTask; }

    public ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            _fleet.Changed -= OnFleetChanged;
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// 曲线：DemoDevice 的历史（60 min 预生成 + 每 10 s 追加，样稿 series）同步进 Core 的 <see cref="DeviceHistory"/>（10 s 一格的环形缓冲，逐点相同）——
    /// 演示数据变了（每秒的推进、操作）之后第一次取的时候才同步一次（只追加新的采样；演示数据重建过就整个重来）。
    /// 会话标记照样稿 reactorChartOpts：这台设备当前会话（结束了的也算）的标记。
    /// </summary>
    private sealed class DemoHistoryView : IChannelHistory
    {
        private readonly DemoFleet _fleet;
        private readonly DemoDevice _d;
        private readonly DeviceHistory _h = new();
        private bool _dirty = true;
        private DateTime _syncedAt;
        private DemoSession? _marksOf;
        private int _marksCount = -1;
        private SessionMark[] _marks = Array.Empty<SessionMark>();

        public DemoHistoryView(DemoFleet fleet, DemoDevice d)
        {
            _fleet = fleet;
            _d = d;
        }

        /// <summary>演示数据变了（DemoFleet.Changed 涉及这台）：下一次取曲线时再同步。</summary>
        public void Invalidate() => _dirty = true;

        public IReadOnlyList<(DateTime T, double? V)> Series(int n, HistoryKey key, double minutes = 60) => _d.Series(n, key switch
        {
            HistoryKey.TempC => DemoReading.TempC,
            HistoryKey.PressureMPa => DemoReading.PressureMPa,
            HistoryKey.HeaterPct => DemoReading.HeaterPct,
            HistoryKey.Rpm => DemoReading.Rpm,
            HistoryKey.Mmol => DemoReading.Mmol,
            HistoryKey.SpC => DemoReading.SpC,
            _ => DemoReading.PTargetMPa,
        }, minutes);

        public ChannelHistory? Channel(int n)
        {
            if (_dirty || _syncedAt != _fleet.Now)
            {
                _h.SyncDemo(_d);
                _dirty = false;
                _syncedAt = _fleet.Now;
            }
            return _h.Has(n) ? _h.Channel(n) : null;
        }

        public DateTime Now => _fleet.Now;

        public IReadOnlyList<SessionMark> Marks
        {
            get
            {
                var s = _d.Session;
                int count = s?.Markers.Count ?? 0;
                if (!ReferenceEquals(s, _marksOf) || count != _marksCount)
                {
                    _marksOf = s;
                    _marksCount = count;
                    _marks = new SessionMark[count];
                    for (int i = 0; i < count; i++) _marks[i] = new SessionMark(s!.Markers[i].At, s.Markers[i].Text, s.Markers[i].N);
                }
                return _marks;
            }
        }
    }
}
