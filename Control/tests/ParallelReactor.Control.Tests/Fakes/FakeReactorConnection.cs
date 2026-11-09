using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>
/// 假的设备连接（IReactorConnection）：给 App 的 LiveDeviceSource 用。每个事件的 add / remove 都计数（<see cref="Handlers"/>），
/// 用来证明数据源释放 / 断开时摘掉了挂上的每一个处理器；<see cref="Raise"/> 系列方法模拟 Client 投递的事件（没有 UiContext：在调用线程上）。
/// REST、控制权状态机、WS 客户端（不启动）、受控写命令（ControlledWrites，自动带租约）都取自一个<b>不启动的真 ReactorConnection</b>，
/// 它的 HTTP 走 ScriptedHttpHandler（没有脚本的路由回 404，每次请求都记在 <see cref="Http"/>.Calls 里）—— 所以测试能断言操作到底调了哪个端点
/// （D6：「启动」「全部运行」绝不调 POST /run）。控制权不发请求就一直是 None；要持有控制权，给 /control/request 脚本一个 granted 再 AcquireAsync。
/// 真连接自己的事件没人订阅；这里的事件由 Raise 系列方法触发。
/// </summary>
public sealed class FakeReactorConnection : IReactorConnection
{
    private readonly ConcurrentDictionary<string, int> _counts = new();
    private readonly Channel<object> _messages = Channel.CreateUnbounded<object>();
    private readonly ReactorConnection _real;

    public FakeReactorConnection(ClientOptions options, ScriptedHttpHandler? http = null)
    {
        Options = options;
        Http = http ?? new ScriptedHttpHandler();
        _real = new ReactorConnection(options, Http);
    }

    public ScriptedHttpHandler Http { get; }
    public ClientOptions Options { get; }
    public ReactorApiClient Rest => _real.Rest;
    public ControlLeaseClient Lease => _real.Lease;
    public TelemetryWsClient Ws => _real.Ws;
    public TelemetryStore Telemetry => _real.Telemetry;
    public ControlledWrites Control => _real.Control;
    public ChannelReader<object> Messages => _messages.Reader;
    public DeviceInfoDto? Device { get; set; }
    public ControlStateDto? ControlState { get; set; }

    /// <summary>StartAsync 时要用的设备信息与第一帧（没给就不发）。</summary>
    public DeviceInfoDto? StartDevice { get; set; }
    public TelemetrySnapshot? StartSnapshot { get; set; }
    /// <summary>StartAsync 抛这个异常（模拟连不上）。</summary>
    public Exception? StartError { get; set; }

    public bool Started { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>当前挂着的处理器个数（按事件名）。</summary>
    public IReadOnlyDictionary<string, int> Handlers => _counts;

    /// <summary>全部事件上挂着的处理器总数。</summary>
    public int TotalHandlers
    {
        get
        {
            int n = 0;
            foreach (var kv in _counts) n += kv.Value;
            return n;
        }
    }

    private void Add(string name) => _counts.AddOrUpdate(name, 1, (_, v) => v + 1);
    private void Remove(string name) => _counts.AddOrUpdate(name, 0, (_, v) => Math.Max(0, v - 1));

    private Action<TelemetrySnapshot>? _telemetry;
    private Action<EventMsg>? _event;
    private Action<AlarmMsg>? _alarm;
    private Action<ControlMsg>? _control;
    private Action<OpEventMsg>? _opEvent;
    private Action<LeaseTransition>? _lease;
    private Action<ConnectionState>? _conn;
    private Action<Exception>? _error;
    private Action? _restarted;

    public event Action<TelemetrySnapshot>? TelemetryReceived { add { _telemetry += value; Add(nameof(TelemetryReceived)); } remove { _telemetry -= value; Remove(nameof(TelemetryReceived)); } }
    public event Action<EventMsg>? EventReceived { add { _event += value; Add(nameof(EventReceived)); } remove { _event -= value; Remove(nameof(EventReceived)); } }
    public event Action<AlarmMsg>? AlarmChanged { add { _alarm += value; Add(nameof(AlarmChanged)); } remove { _alarm -= value; Remove(nameof(AlarmChanged)); } }
    public event Action<ControlMsg>? ControlChanged { add { _control += value; Add(nameof(ControlChanged)); } remove { _control -= value; Remove(nameof(ControlChanged)); } }
    public event Action<OpEventMsg>? OpEventReceived { add { _opEvent += value; Add(nameof(OpEventReceived)); } remove { _opEvent -= value; Remove(nameof(OpEventReceived)); } }
    public event Action<LeaseTransition>? LeaseTransitioned { add { _lease += value; Add(nameof(LeaseTransitioned)); } remove { _lease -= value; Remove(nameof(LeaseTransitioned)); } }
    public event Action<ConnectionState>? ConnectionStateChanged { add { _conn += value; Add(nameof(ConnectionStateChanged)); } remove { _conn -= value; Remove(nameof(ConnectionStateChanged)); } }
    public event Action<Exception>? Error { add { _error += value; Add(nameof(Error)); } remove { _error -= value; Remove(nameof(Error)); } }
    public event Action? ServerRestarted { add { _restarted += value; Add(nameof(ServerRestarted)); } remove { _restarted -= value; Remove(nameof(ServerRestarted)); } }

    public Task StartAsync(CancellationToken ct)
    {
        if (StartError != null) return Task.FromException(StartError);
        Started = true;
        Device = StartDevice;
        if (StartSnapshot != null) RaiseTelemetry(StartSnapshot);
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        await Lease.DisposeAsync();   // 停掉心跳循环（持有过控制权时）
        Rest.Dispose();
    }

    /// <summary>
    /// 让 /control/request 直接批准（autoGrant）并申请：之后 <see cref="Lease"/> 是 Holding、ControlledWrites 带着租约头。
    /// 心跳 / 释放也给了脚本（测试里不会因为 404 掉控制权）。
    /// </summary>
    public static void ScriptGrantedLease(ScriptedHttpHandler h, string leaseId = "L-test")
    {
        h.On("POST", "/control/request", c => ScriptedHttpHandler.Command(c, true,
            new ControlRequestResultDto("rq-test", "granted", null, leaseId, DateTime.UtcNow.AddMinutes(5), 10, 60, "key-test")));
        h.On("POST", "/control/heartbeat", c => ScriptedHttpHandler.Command(c, false,
            new HeartbeatResultDto("remote", false, DateTime.UtcNow.AddMinutes(5), DateTime.UtcNow.AddMinutes(6), 10)));
        h.On("POST", "/control/release", c => ScriptedHttpHandler.Command(c, false, new { mode = "local" }));
    }

    public void RaiseTelemetry(TelemetrySnapshot f) { Telemetry.Add(f); _telemetry?.Invoke(f); }
    public void RaiseAlarm(AlarmMsg a) => _alarm?.Invoke(a);
    public void RaiseEvent(EventMsg e) => _event?.Invoke(e);
    public void RaiseConnection(ConnectionState s) => _conn?.Invoke(s);
    public void RaiseLease(LeaseTransition t) => _lease?.Invoke(t);
    public void RaiseControl(ControlMsg m) => _control?.Invoke(m);
    public void RaiseError(Exception ex) => _error?.Invoke(ex);
    public void RaiseRestarted() => _restarted?.Invoke();
}

/// <summary>手动的「排到 UI 线程」：数据源把刷新排进来，测试决定什么时候执行（证明多次事件只刷新一次）。</summary>
public sealed class ManualPost
{
    private readonly ConcurrentQueue<Action> _q = new();

    public int Pending => _q.Count;

    public void Post(Action a) => _q.Enqueue(a);

    /// <summary>执行排着的全部（执行中又排进来的也执行），返回执行了几个。</summary>
    public int Run()
    {
        int n = 0;
        while (_q.TryDequeue(out var a)) { a(); n++; }
        return n;
    }
}
