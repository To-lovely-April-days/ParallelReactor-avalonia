using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using ParallelReactor.Services;

namespace ParallelReactor.Client;

/// <summary>UI / 引擎看到的连接门面（可替换为假实现）。</summary>
public interface IReactorConnection : IAsyncDisposable
{
    ClientOptions Options { get; }
    ReactorApiClient Rest { get; }
    ControlLeaseClient Lease { get; }
    TelemetryWsClient Ws { get; }
    TelemetryStore Telemetry { get; }
    ControlledWrites Control { get; }
    /// <summary>按到达顺序的全部服务端消息（telemetry / EventMsg / AlarmMsg / ControlMsg / OpEventMsg / GapMsg…），有界 <see cref="ClientOptions.MessageQueueCapacity"/>（默认 2000）、满则丢最旧，供引擎顺序消费；容量 0 时不入队。</summary>
    ChannelReader<object> Messages { get; }
    DeviceInfoDto? Device { get; }
    ControlStateDto? ControlState { get; }

    event Action<TelemetrySnapshot>? TelemetryReceived;
    event Action<EventMsg>? EventReceived;
    event Action<AlarmMsg>? AlarmChanged;
    event Action<ControlMsg>? ControlChanged;
    event Action<OpEventMsg>? OpEventReceived;
    event Action<LeaseTransition>? LeaseTransitioned;
    event Action<ConnectionState>? ConnectionStateChanged;
    event Action<Exception>? Error;
    /// <summary>bootId 变化（HMI 重启）：遥测缓冲已清空。</summary>
    event Action? ServerRestarted;

    /// <summary>GET /device + GET /control + GET /snapshot，然后启动 WS。</summary>
    Task StartAsync(CancellationToken ct);
    Task StopAsync();

    // —— M3 增补（默认实现：旧的实现者 / 测试假对象不用改；ReactorConnection 都给了真实现，事件同样经 UiContext 投递）——

    /// <summary>本地取消的控制权申请有了结局（<see cref="ControlLeaseClient.CancelledRequestSettled"/>；R7）。</summary>
    event Action<CancelledRequestOutcome>? LeaseCancelledRequestSettled { add { } remove { } }
    /// <summary>HMI 报告的控制权现状变了（持有者 / 待处理申请；<see cref="ControlLeaseClient.ServerViewChanged"/>；R8）。</summary>
    event Action? LeaseServerViewChanged { add { } remove { } }
    /// <summary>某个 REST 请求被 403 FORBIDDEN_ROLE 拒绝（<see cref="ReactorApiClient.Forbidden"/>；R9）。</summary>
    event Action<ForbiddenNotice>? ForbiddenRole { add { } remove { } }
    /// <summary>WS 退避重连的下一次尝试时刻变了（<see cref="TelemetryWsClient.NextRetryChanged"/>；null = 不在等待）。</summary>
    event Action<DateTime?>? ReconnectScheduled { add { } remove { } }
    /// <summary>立即重连：WS 正在退避等待时立刻发起下一次连接（<see cref="TelemetryWsClient.ReconnectNow"/>）。false = 没有在等待。</summary>
    bool ReconnectNow() => Ws?.ReconnectNow() ?? false;
}

/// <summary>
/// Rest + Ws + Lease + TelemetryStore 的组合：WS control 消息喂给租约状态机；租约心跳优先走 WS；
/// 事件按 <see cref="ClientOptions.UiContext"/> 投递（非空时 Post 到 UI 线程），同时入 <see cref="Messages"/> 队列。
/// </summary>
public sealed class ReactorConnection : IReactorConnection
{
    private readonly Channel<object> _messages;
    /// <summary>M3：<see cref="ClientOptions.MessageQueueCapacity"/> = 0 → 不入队（<see cref="Messages"/> 一直为空，停止时完成）。</summary>
    private readonly bool _queueMessages;

    public ReactorConnection(ClientOptions options, System.Net.Http.HttpMessageHandler? handler = null, IClock? clock = null, Func<IWsTransport>? wsFactory = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _queueMessages = options.MessageQueueCapacity > 0;
        _messages = Channel.CreateBounded<object>(new BoundedChannelOptions(Math.Max(1, options.MessageQueueCapacity)) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = false });
        var clk = clock ?? SystemClock.Instance;
        Rest = new ReactorApiClient(options, handler, clk);
        Lease = new ControlLeaseClient(Rest, options, clk);
        Ws = new TelemetryWsClient(options, Rest, clk, wsFactory);
        Telemetry = new TelemetryStore();
        Control = new ControlledWrites(this);

        Lease.WsHeartbeat = Ws.SendHeartbeatAsync;
        Lease.Transitioned += t => Post(() => LeaseTransitioned?.Invoke(t));
        Lease.Error += e => Post(() => Error?.Invoke(e));
        Ws.TelemetryReceived += f => { Telemetry.Add(f); Enqueue(f); Post(() => TelemetryReceived?.Invoke(f)); };
        Ws.EventReceived += e => { Enqueue(e); Post(() => EventReceived?.Invoke(e)); };
        Ws.AlarmReceived += a => { Enqueue(a); Post(() => AlarmChanged?.Invoke(a)); };
        Ws.ControlReceived += c => { Lease.OnControlMessage(c); ControlState = ControlState == null ? null : ControlState with { Mode = c.Mode, Holder = c.Holder, State = c.State, LeaseExpiresAt = c.LeaseExpiresAt }; Enqueue(c); Post(() => ControlChanged?.Invoke(c)); };
        Ws.OpEventReceived += o => { Enqueue(o); Post(() => OpEventReceived?.Invoke(o)); };
        Ws.GapDetected += g => Enqueue(g);
        Ws.StateChanged += s => Post(() => ConnectionStateChanged?.Invoke(s));
        Ws.Error += e => Post(() => Error?.Invoke(e));
        // M3：租约的宽限 / 申请倒计时按服务端时钟修正（先于下面的 GET /control 对齐挂上）
        Ws.HelloReceived += _ => Lease.ServerClockOffset = Ws.ServerClockOffset;
        Ws.HelloReceived += h => { var __t = RefreshControlAsync(); };
        // HMI 重启：遥测 seq 从 1 重计，环形缓冲必须清空，否则新帧全被当重复帧丢弃（直到超过旧 seq）
        Ws.ServerRestarted += (_, _) => { Telemetry.Clear(); Post(() => ServerRestarted?.Invoke()); };
        // WS hb 的服务端应答 {"type":"error",code}：租约类 code 等同于写命令被 409 拒绝
        Ws.ErrorReceived += e =>
        {
            if (e.Code is OpCodes.ModeLocal or OpCodes.NotControlHolder or OpCodes.ControlLeaseExpired)
                Lease.OnWriteFailed(new ApiException(e.Code, 409, e.Message ?? e.Code));
        };
        // M3 增补：新事件同样经 UiContext 投递
        Lease.CancelledRequestSettled += o => Post(() => LeaseCancelledRequestSettled?.Invoke(o));
        Lease.ServerViewChanged += () => Post(() => LeaseServerViewChanged?.Invoke());
        Rest.Forbidden += n => Post(() => ForbiddenRole?.Invoke(n));
        Ws.NextRetryChanged += at => Post(() => ReconnectScheduled?.Invoke(at));
    }

    public ClientOptions Options { get; }
    public ReactorApiClient Rest { get; }
    public ControlLeaseClient Lease { get; }
    public TelemetryWsClient Ws { get; }
    public TelemetryStore Telemetry { get; }
    public ControlledWrites Control { get; }
    public ChannelReader<object> Messages => _messages.Reader;
    public DeviceInfoDto? Device { get; private set; }
    public ControlStateDto? ControlState { get; private set; }

    public event Action<TelemetrySnapshot>? TelemetryReceived;
    public event Action<EventMsg>? EventReceived;
    public event Action<AlarmMsg>? AlarmChanged;
    public event Action<ControlMsg>? ControlChanged;
    public event Action<OpEventMsg>? OpEventReceived;
    public event Action<LeaseTransition>? LeaseTransitioned;
    public event Action<ConnectionState>? ConnectionStateChanged;
    public event Action<Exception>? Error;
    public event Action? ServerRestarted;
    // M3 增补
    public event Action<CancelledRequestOutcome>? LeaseCancelledRequestSettled;
    public event Action? LeaseServerViewChanged;
    public event Action<ForbiddenNotice>? ForbiddenRole;
    public event Action<DateTime?>? ReconnectScheduled;

    /// <summary>M3：立即重连（<see cref="TelemetryWsClient.ReconnectNow"/>）。false = WS 没有在退避等待。</summary>
    public bool ReconnectNow() => Ws.ReconnectNow();

    public async Task StartAsync(CancellationToken ct)
    {
        Device = await Rest.DeviceAsync(ct).ConfigureAwait(false);
        ControlState = await Rest.ControlAsync(ct).ConfigureAwait(false);
        Lease.ObserveControlState(ControlState);   // M3：持有者 / 待处理申请（R8），不动租约状态
        var snap = await Rest.SnapshotAsync(ct).ConfigureAwait(false);
        Telemetry.Add(snap);
        Post(() => TelemetryReceived?.Invoke(snap));
        Ws.Start();
    }

    public async Task StopAsync()
    {
        await Ws.StopAsync().ConfigureAwait(false);
        await Lease.DisposeAsync().ConfigureAwait(false);
        _messages.Writer.TryComplete();
    }

    /// <summary>重连后对齐 GET /control（WS control 消息不带 leaseId，本地租约只能靠自己的心跳结果判断）。</summary>
    private async Task RefreshControlAsync()
    {
        try
        {
            var s = await Rest.ControlAsync(CancellationToken.None).ConfigureAwait(false);
            ControlState = s;
            Lease.OnControlState(s);
        }
        catch (Exception ex) { Post(() => Error?.Invoke(ex)); }
    }

    private void Enqueue(object m)
    {
        if (_queueMessages) _messages.Writer.TryWrite(m);
    }

    private void Post(Action a)
    {
        var ctx = Options.UiContext;
        if (ctx == null) { try { a(); } catch { } }
        else ctx.Post(_ => { try { a(); } catch { } }, null);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Rest.Dispose();
    }
}

/// <summary>
/// 受控写命令的包装：自动带当前租约；没有租约 → 本地快速失败 <see cref="ApiException"/>(NOT_CONTROL_HOLDER, 409)；
/// 服务端 409 租约类拒绝 → 通知状态机（自动重申请按配置）。停止类 / 记录类直接用 <see cref="ReactorApiClient"/>。
/// </summary>
public sealed class ControlledWrites
{
    private readonly ReactorConnection _c;
    internal ControlledWrites(ReactorConnection c) => _c = c;

    private string Lease() => _c.Lease.LeaseId ?? throw new ApiException(OpCodes.NotControlHolder, 409, "尚未持有控制权 · 请先申请远程控制");

    private async Task<T> Run<T>(Func<string, Task<T>> f)
    {
        var lease = Lease();
        try { return await f(lease).ConfigureAwait(false); }
        catch (ApiException ex) when (ex.IsLeaseError) { _c.Lease.OnWriteFailed(ex); throw; }
    }

    public Task<CommandResult<JsonElement>> RunAsync(string? idem, CancellationToken ct) => Run(l => _c.Rest.RunAsync(l, idem, ct));
    public Task<CommandResult<JsonElement>> EnableChannelAsync(int id, CancellationToken ct) => Run(l => _c.Rest.EnableChannelAsync(l, id, ct));
    public Task<CommandResult<JsonElement>> StartChannelAsync(int id, double? tempC, CancellationToken ct) => Run(l => _c.Rest.StartChannelAsync(l, id, tempC, ct));
    public Task<CommandResult<JsonElement>> CopyChannelConfigAsync(int from, int[]? to, string? idem, CancellationToken ct) => Run(l => _c.Rest.CopyChannelConfigAsync(l, from, to, idem, ct));
    public Task<CommandResult<JsonElement>> ResetGasCounterAsync(int id, CancellationToken ct) => Run(l => _c.Rest.ResetGasCounterAsync(l, id, ct));
    public Task<CommandResult<JsonElement>> StartHeatAsync(int id, double? tempC, CancellationToken ct) => Run(l => _c.Rest.StartHeatAsync(l, id, tempC, ct));
    public Task<CommandResult<SetpointResult>> SetSetpointAsync(int id, double tempC, CancellationToken ct) => Run(l => _c.Rest.SetSetpointAsync(l, id, tempC, ct));
    public Task<CommandResult<JsonElement>> SetSpModeAsync(int id, string spMode, CancellationToken ct) => Run(l => _c.Rest.SetSpModeAsync(l, id, spMode, ct));
    public Task<CommandResult<JsonElement>> ReplaceProfileAsync(int id, IEnumerable<(double TempC, double Minutes)> segs, CancellationToken ct) => Run(l => _c.Rest.ReplaceProfileAsync(l, id, segs, ct));
    public Task<CommandResult<JsonElement>> StartProfileAsync(int id, CancellationToken ct) => Run(l => _c.Rest.StartProfileAsync(l, id, ct));
    public Task<CommandResult<JsonElement>> SetPidAsync(int id, int band, double p, double i, double d, CancellationToken ct) => Run(l => _c.Rest.SetPidAsync(l, id, band, p, i, d, ct));
    public Task<CommandResult<JsonElement>> StartAutotuneAsync(int id, double tempC, CancellationToken ct) => Run(l => _c.Rest.StartAutotuneAsync(l, id, tempC, ct));
    public Task<CommandResult<JsonElement>> SetStirAsync(bool? on, int? rpm, CancellationToken ct) => Run(l => _c.Rest.SetStirAsync(l, on, rpm, ct));
    public Task<CommandResult<LeakTestStartResult>> StartLeakTestAsync(int[] channels, double? testPsi, string? idem, CancellationToken ct) => Run(l => _c.Rest.StartLeakTestAsync(l, channels, testPsi, idem, ct));
    public Task<CommandResult<JsonElement>> ReplaceProgramAsync(int id, ProgramDoc program, CancellationToken ct) => Run(l => _c.Rest.ReplaceProgramAsync(l, id, program, ct));
    public Task<CommandResult<ProgramRunResult>> RunProgramAsync(int id, string? sessionId, string? idem, CancellationToken ct) => Run(l => _c.Rest.RunProgramAsync(l, id, sessionId, idem, ct));
    public Task<CommandResult<JsonElement>> CopyProgramAsync(int id, int[] to, string? idem, CancellationToken ct) => Run(l => _c.Rest.CopyProgramAsync(l, id, to, idem, ct));
}
