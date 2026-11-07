using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;

namespace ParallelReactor.PerfTest.Engine;

/// <summary>把 <see cref="IReactorConnection"/> 适配成 <see cref="IReactorApi"/>：控制类走 Control（自动带租约），停止 / 记录类走 Rest；异常 → ApiOutcome。</summary>
public sealed class ReactorApiAdapter : IReactorApi
{
    private readonly IReactorConnection _c;
    public ReactorApiAdapter(IReactorConnection c) => _c = c;

    public bool CanControl => _c.Lease.CanControl;

    private static async Task<ApiOutcome> Wrap<T>(Func<Task<CommandResult<T>>> f)
    {
        try
        {
            var r = await f().ConfigureAwait(false);
            return new ApiOutcome(r.Ok, r.OpId, r.Status, r.HttpStatus, null, r.Replayed ? "replayed" : null, r.OpEventSeq);
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiException ex) { return new ApiOutcome(false, ex.OpId, "failed", ex.Status, ex.Code, ex.Message, ex.OpEventSeq); }
        catch (Exception ex) { return ApiOutcome.Fail("NETWORK", ex.GetType().Name + ": " + ex.Message); }
    }

    private static async Task<T?> Read<T>(Func<Task<T>> f) where T : class
    {
        try { return await f().ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public Task<DeviceInfoDto?> GetDeviceAsync(CancellationToken ct) => Read(() => _c.Rest.DeviceAsync(ct));
    public Task<SettingsDto?> GetSettingsAsync(CancellationToken ct) => Read(() => _c.Rest.SettingsAsync(ct));
    public Task<TelemetrySnapshot?> GetSnapshotAsync(CancellationToken ct) => Read(() => _c.Rest.SnapshotAsync(ct));
    public Task<CommDto?> GetCommAsync(CancellationToken ct) => Read(() => _c.Rest.CommAsync(ct));

    public Task<ApiOutcome> EnableChannelAsync(int rv, CancellationToken ct) => Wrap(() => _c.Control.EnableChannelAsync(rv, ct));
    public Task<ApiOutcome> DisableChannelAsync(int rv, CancellationToken ct) => Wrap(() => _c.Rest.DisableChannelAsync(rv, ct));
    public Task<ApiOutcome> StopChannelAsync(int rv, CancellationToken ct) => Wrap(() => _c.Rest.StopChannelAsync(rv, ct));
    public Task<ApiOutcome> StartHeatAsync(int rv, double? tempC, CancellationToken ct) => Wrap(() => _c.Control.StartHeatAsync(rv, tempC, ct));
    public Task<ApiOutcome> SetSetpointAsync(int rv, double tempC, CancellationToken ct) => Wrap(() => _c.Control.SetSetpointAsync(rv, tempC, ct));
    public Task<ApiOutcome> StopHeatAsync(int rv, CancellationToken ct) => Wrap(() => _c.Rest.StopHeatAsync(rv, ct));
    public Task<ApiOutcome> SetStirAsync(bool? on, int? rpm, CancellationToken ct) => Wrap(() => _c.Control.SetStirAsync(on, rpm, ct));
    public Task<ApiOutcome> StopStirAsync(CancellationToken ct) => Wrap(() => _c.Rest.StopStirAsync(ct));
    public Task<ApiOutcome> ReplaceProgramAsync(int rv, string programJson, CancellationToken ct) => Wrap(() => _c.Control.ReplaceProgramAsync(rv, ProgramDoc.FromJson(programJson, "perf"), ct));
    public Task<ApiOutcome> RunProgramAsync(int rv, string? sessionId, string idem, CancellationToken ct) => Wrap(() => _c.Control.RunProgramAsync(rv, sessionId, idem, ct));
    public Task<ApiOutcome> AbortProgramAsync(int rv, CancellationToken ct) => Wrap(() => _c.Rest.AbortProgramAsync(rv, ct));
    public Task<ApiOutcome> StartAutotuneAsync(int rv, double tempC, CancellationToken ct) => Wrap(() => _c.Control.StartAutotuneAsync(rv, tempC, ct));
    public Task<ApiOutcome> CancelAutotuneAsync(int rv, CancellationToken ct) => Wrap(() => _c.Rest.CancelAutotuneAsync(rv, ct));
    public Task<ApiOutcome> StartLeakTestAsync(int[] channels, double? testPsi, string idem, CancellationToken ct) => Wrap(() => _c.Control.StartLeakTestAsync(channels, testPsi, idem, ct));
    public Task<ApiOutcome> AbortLeakTestAsync(CancellationToken ct) => Wrap(() => _c.Rest.AbortLeakTestAsync(ct));
    public Task<ApiOutcome> EstopAsync(string? note, CancellationToken ct) => Wrap(() => _c.Rest.EstopAsync(note, ct));
    public Task<ApiOutcome> StopAllAsync(bool stopStir, CancellationToken ct) => Wrap(() => _c.Rest.StopAllAsync(stopStir, ct));
    public async Task<ApiOutcome> RequestControlAsync(CancellationToken ct)
    {
        try
        {
            bool ok = await _c.Lease.AcquireAsync(ct).ConfigureAwait(false);
            return ok ? new ApiOutcome(true, _c.Lease.LeaseId, "confirmed", 200, null, "granted") : ApiOutcome.Fail("CONTROL_DENIED", "申请被拒 / 超时 / 过期");
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiException ex) { return new ApiOutcome(false, null, "failed", ex.Status, ex.Code, ex.Message); }
        catch (Exception ex) { return ApiOutcome.Fail("NETWORK", ex.GetType().Name + ": " + ex.Message); }
    }

    public async Task<ApiOutcome> BeginSessionAsync(string testId, string? note, string idem, CancellationToken ct)
    {
        var o = await Wrap(() => _c.Rest.BeginSessionAsync(testId, note, idem, ct)).ConfigureAwait(false);
        if (!o.Ok) return o;
        // 会话 id 放在 Message（接口约定）
        try
        {
            var r = await _c.Rest.SessionCurrentAsync(ct).ConfigureAwait(false);
            return o with { Message = r?.Id ?? testId };
        }
        catch (OperationCanceledException) { throw; }
        catch { return o with { Message = testId }; }
    }
    public Task<ApiOutcome> EndSessionAsync(CancellationToken ct) => Wrap(() => _c.Rest.EndSessionAsync(ct));
    public Task<ApiOutcome> AddMarkerAsync(string text, string idem, CancellationToken ct) => Wrap(() => _c.Rest.AddMarkerAsync(text, idem, ct));

    public async Task<bool> DownloadSessionCsvAsync(string sessionId, Stream dest, CancellationToken ct)
    {
        try { await _c.Rest.DownloadFileAsync("sessions", sessionId + ".csv", dest, ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }
}

/// <summary>
/// 把连接的遥测 / 报警 / 控制权事件适配成 <see cref="ITelemetryFeed"/>。安全事件：level=err 报警或 state=active 的 alarm 消息、
/// 急停上升沿、控制权 lost / expired / takenBack / invalidated、压力模块离线、PV &gt; SP+15 ℃。
/// </summary>
public sealed class TelemetryFeedAdapter : ITelemetryFeed, IDisposable
{
    private readonly IReactorConnection _c;
    private bool _estop, _commPressureLost;
    private readonly bool[] _overTemp = new bool[9];
    private readonly DateTime?[] _offlineSince = new DateTime?[3];
    private readonly bool[] _offlineRaised = new bool[3];
    /// <summary>温度 / 搅拌 / IO 模块离线超过此时长 → 安全事件（方案「其他模块离线 &gt; 30 s」）。</summary>
    public static readonly TimeSpan OtherModuleOfflineGrace = TimeSpan.FromSeconds(30);

    public TelemetryFeedAdapter(IReactorConnection c)
    {
        _c = c;
        _c.TelemetryReceived += OnFrame;
        _c.AlarmChanged += OnAlarm;
        _c.ControlChanged += OnControl;
        _c.LeaseTransitioned += OnLease;
    }

    public event Action<TelemetrySnapshot>? Frame;
    public event Action<SafetyEvent>? Safety;
    public TelemetrySnapshot? Latest => _c.Telemetry.Latest;

    private void OnFrame(TelemetrySnapshot f)
    {
        Frame?.Invoke(f);
        if (f.Estop.Active && !_estop) Safety?.Invoke(new SafetyEvent(SafetyKind.Estop, "急停", $"急停激活 · 加热 {f.Estop.Heat} / 搅拌 {f.Estop.Stir} / 阀门 {f.Estop.Valves}", f.Ts));
        _estop = f.Estop.Active;
        if (!f.Comm.Pressure && !_commPressureLost) Safety?.Invoke(new SafetyEvent(SafetyKind.CommLost, "压力模块离线", "方案 §0.1-11：压力模块离线立即中止", f.Ts));
        _commPressureLost = !f.Comm.Pressure;
        CheckModule(0, f.Comm.Temp, "温度模块（AI-8）", f.Ts);
        CheckModule(1, f.Comm.Stir, "搅拌驱动（DM2C）", f.Ts);
        CheckModule(2, f.Comm.Io, "IO 模块（IO16R）", f.Ts);
        foreach (var c in f.Channels)
        {
            if (c.Id < 1 || c.Id > 8) continue;
            bool over = c.TempC.HasValue && c.TempSpC >= 35 && c.TempC.Value > c.TempSpC + 15;
            if (over && !_overTemp[c.Id]) Safety?.Invoke(new SafetyEvent(SafetyKind.OverTempAlert, $"RV{c.Id} 温度超过 SP+15 ℃", $"PV {c.TempC:0.0} ℃ / SP {c.TempSpC:0.0} ℃（§0.1-11 中止条件）", f.Ts, c.Id));
            _overTemp[c.Id] = over;
        }
    }

    private void CheckModule(int i, bool online, string name, DateTime ts)
    {
        if (online) { _offlineSince[i] = null; _offlineRaised[i] = false; return; }
        _offlineSince[i] ??= ts;
        if (!_offlineRaised[i] && ts - _offlineSince[i]!.Value >= OtherModuleOfflineGrace)
        {
            _offlineRaised[i] = true;
            Safety?.Invoke(new SafetyEvent(SafetyKind.CommLost, name + " 离线 > 30 s", "方案 §0.1-11：其他模块离线 > 30 s 中止", ts));
        }
    }

    private void OnAlarm(AlarmMsg m)
    {
        var a = m.Alarm;
        if (a.Level == "err" && a.State == "active")
            Safety?.Invoke(new SafetyEvent(SafetyKind.Alarm, a.Title, a.Message, a.RaisedAt, a.Channel));
    }

    private void OnControl(ControlMsg m)
    {
        if (m.Event is "lost" or "expired" or "takenBack" or "invalidated")
            Safety?.Invoke(new SafetyEvent(SafetyKind.ControlLost, "控制权变化：" + m.Event, m.Detail, m.Ts));
    }

    private void OnLease(LeaseTransition t)
    {
        if (t.From == LeaseState.Holding && t.To != LeaseState.Holding)
            Safety?.Invoke(new SafetyEvent(SafetyKind.ControlLost, "本机租约失效", $"{t.From}→{t.To}：{t.Reason}", t.At));
    }

    public void Dispose()
    {
        _c.TelemetryReceived -= OnFrame;
        _c.AlarmChanged -= OnAlarm;
        _c.ControlChanged -= OnControl;
        _c.LeaseTransitioned -= OnLease;
    }
}
