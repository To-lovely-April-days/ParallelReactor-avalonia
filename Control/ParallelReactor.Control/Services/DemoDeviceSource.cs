using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
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
    private IReadOnlyList<AlarmItem> _alarms = Array.Empty<AlarmItem>();
    private IReadOnlyList<EventItem> _events = Array.Empty<EventItem>();
    private (int Count, string? First, int Acked) _alarmKey = (-1, null, 0);
    private (int Count, string? First) _eventKey = (-1, null);
    private bool _disposed;

    public DemoDeviceSource(DemoFleet fleet, DemoDevice device)
    {
        _fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
        Device = device ?? throw new ArgumentNullException(nameof(device));
        Id = IdFor(device.K);
        _history = new DemoHistoryView(device);
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

    public IReadOnlyList<AlarmItem> Alarms
    {
        get
        {
            var src = Device.Alarms;
            var key = (src.Count, src.Count > 0 ? src[0].Id : null, src.Count(a => a.Acked));
            if (key != _alarmKey)
            {
                _alarmKey = key;
                _alarms = src.Select(a => new AlarmItem(a.Id, a.Level, a.Ch, a.Code, a.Text, a.At, a.Acked, a.AckBy)).ToArray();
            }
            return _alarms;
        }
    }

    public IReadOnlyList<EventItem> Events
    {
        get
        {
            var src = Device.Events;
            var key = (src.Count, src.Count > 0 ? src[0].Id : null);
            if (key != _eventKey)
            {
                _eventKey = key;
                _events = src.Select(e => new EventItem(e.Id, e.At, e.Level, e.N, e.Text)).ToArray();
            }
            return _events;
        }
    }

    public ChannelExtras? Extras(int n)
    {
        var ch = Device.Channel(n);
        if (ch is null || !ch.Installed) return null;
        return new ChannelExtras(Device.Reading(ch, DemoReading.VolumeMl), Device.Reading(ch, DemoReading.Rpm), ch.StirOn, ch.Cooling, Device.Reading(ch, DemoReading.Mmol));
    }

    public ProgramRecord? Program(int n) => Device.Channel(n)?.Program;

    private IReadOnlyList<ProgramChoice>? _choices;

    /// <summary>演示程序库（同一型号的全部程序，样稿 d.programs）：每个反应釜都一样。</summary>
    public IReadOnlyList<ProgramChoice> ProgramChoices(int n) => _choices ??= Device.Programs.Select(p => new ProgramChoice(p.Id, p.Name)).ToArray();

    public ChannelReading Read(int n) => ChannelProjection.FromDemo(Device, n);

    public GuardFacts Facts() => Device.Facts();

    public bool GasOpen(string key) => Device.GasOpen(key);

    public event Action<SourceChange>? Changed;

    private void OnFleetChanged(object? sender, DemoChangedEventArgs e)
    {
        if (_disposed) return;
        if (e.Device is null || ReferenceEquals(e.Device, Device)) Changed?.Invoke(SourceChange.All);
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

    /// <summary>曲线：DemoDevice.Series（60 min 预生成 + 每 10 s 追加）。</summary>
    private sealed class DemoHistoryView : IChannelHistory
    {
        private readonly DemoDevice _d;
        public DemoHistoryView(DemoDevice d) => _d = d;

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
    }
}
