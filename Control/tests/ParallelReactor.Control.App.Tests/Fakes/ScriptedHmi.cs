using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using AlarmDto = ParallelReactor.Services.AlarmDto;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>
/// 一台进程内的假 HMI（M3 重连监督的测试；不运行模拟 HMI，D9）：脚本化的 REST（<see cref="ScriptedHttpHandler"/>）+ 脚本化的 WS（<see cref="FakeWsServer"/>），
/// 经<b>真 ReactorConnection</b>连接（<see cref="Connect"/> 是给 LiveDeviceSource 的连接工厂用的）。可以模拟：
/// <list type="bullet">
/// <item>连不上（<see cref="Down"/>：每个 REST 请求都「Connection refused」，像主机没开）；</item>
/// <item>WS 握手被拒（<see cref="WsFailStatus"/>，如 503 TOO_MANY_CLIENTS）；断线（<see cref="DropWs"/>）；重启（<see cref="Restart"/>：新的 bootId / 序号纪元）；</item>
/// <item>控制权：申请 pending → 触屏批准 / 拒绝（<see cref="Approve"/> / <see cref="Deny"/>），心跳报丢失 / 恢复（<see cref="LeaseLost"/>），释放记录在 <see cref="Released"/>；</item>
/// <item>403：<see cref="Forbid"/> 里的「方法 路径」一律回 403 FORBIDDEN_ROLE。</item>
/// </list>
/// 服务端时钟 = 测试的假时钟 + <see cref="Skew"/>（检验倒计时按 hello 的时钟偏差换算）。WS 自动回 subscribed / pong。
/// </summary>
internal sealed class ScriptedHmi
{
    private readonly FakeClock _clock;
    private readonly object _lock = new();
    private long _seq = 1000;
    private int _gateAttempts;
    private DateTime _requestExpires;

    public ScriptedHmi(FakeClock clock, string host, string serial, int port = 8080)
    {
        _clock = clock;
        Host = host;
        Port = port;
        Serial = serial;
        Server.OnConnect = OnWsConnect;
        Script();
    }

    public string Host { get; }
    public int Port { get; }
    public string Serial { get; set; }
    public string BootId { get; set; } = "boot-1";
    public int SeqEpoch { get; set; } = 1;
    public TimeSpan Skew { get; set; } = TimeSpan.FromSeconds(3);
    public DateTime ServerNow => _clock.UtcNow + Skew;

    public ScriptedHttpHandler Http { get; } = new();
    public FakeWsServer Server { get; } = new();

    /// <summary>主机不通：REST 请求一律「Connection refused」（WS 同样连不上）。</summary>
    public volatile bool Down;
    /// <summary>WS 握手被拒的 HTTP 状态码（null = 正常）。</summary>
    public int? WsFailStatus { get; set; }
    /// <summary>
    /// M3 复核：WS 握手一直挂着（拔线 / 主机不回包：系统的 TCP 连接超时之前 ConnectAsync 不返回），直到 <see cref="ReleaseWs"/> 或客户端取消。
    /// 优先于 <see cref="Down"/>（REST 照 Down 拒绝）：用来检验 WS 卡住时 REST 探测（GET /health）给出的证据。
    /// </summary>
    public volatile bool WsHang;
    private TaskCompletionSource _wsGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>放开挂着的 WS 握手（之后按当时的 Down / WsFailStatus 继续）。</summary>
    public void ReleaseWs()
    {
        WsHang = false;
        var g = Interlocked.Exchange(ref _wsGate, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        g.TrySetResult();
    }
    /// <summary>经这台 HMI 发出过的 REST 请求个数（含被拒的：连接尝试的次数）。</summary>
    public int GateAttempts => Volatile.Read(ref _gateAttempts);

    // —— 控制权脚本 ——
    public volatile bool AutoGrant, Approve, Deny, LeaseLost;
    public string RequestId { get; set; } = "rq1";
    public string RequestKey { get; set; } = "K1";
    public string LeaseId { get; set; } = "L1";
    public string ControlMode { get; set; } = "local";
    public string? Holder { get; set; }
    /// <summary>这些「方法 路径」（如 "POST /stop-all"）回 403 FORBIDDEN_ROLE。</summary>
    public HashSet<string> Forbid { get; } = new(StringComparer.Ordinal);
    public List<AlarmDto> Alarms { get; } = new();
    /// <summary>POST /control/release 带的租约号（按顺序）。</summary>
    public List<string?> Released { get; } = new();

    public DeviceInfoDto Device => ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))! with
    {
        Serial = Serial, BootId = BootId, SeqEpoch = SeqEpoch, BootTime = ServerNow.AddMinutes(-5), EventSeqHead = 10,
    };

    public TelemetrySnapshot Frame(long? seq = null) => ApiJson.Deserialize<TelemetrySnapshot>(Samples.Read("live-snapshot.json"))! with
    {
        Seq = seq ?? Interlocked.Increment(ref _seq), Ts = ServerNow,
    };

    private ControlStateDto Control() => new(ControlMode, Holder, Holder == null ? null : "op", Holder == null ? "local" : LeaseLost ? "remoteLost" : "remote",
        null, null, null, AutoGrant, 10, 60, 60, "keep");

    // ———————————————————— 连接工厂 ————————————————————

    /// <summary>新建一条真 ReactorConnection，HTTP 与 WS 都接到这台假 HMI（每条连接自己的处理器：ReactorApiClient 释放时会释放它）。</summary>
    public IReactorConnection Connect(ClientOptions o) => new ReactorConnection(o, new Gate(this), _clock, Server.Next);

    /// <summary>每条连接一个：主机不通时抛「Connection refused」，否则交给共用的脚本（不释放它）。</summary>
    private sealed class Gate : HttpMessageHandler
    {
        private readonly ScriptedHmi _h;
        private readonly HttpMessageInvoker _inner;

        public Gate(ScriptedHmi h)
        {
            _h = h;
            _inner = new HttpMessageInvoker(h.Http, disposeHandler: false);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _h._gateAttempts);
            if (_h.Down)
                throw new HttpRequestException("Connection refused (" + _h.Host + ":" + _h.Port + ")", new SocketException((int)SocketError.ConnectionRefused));
            var key = request.Method.Method.ToUpperInvariant() + " " + request.RequestUri!.AbsolutePath.Replace("/api/v1", "", StringComparison.Ordinal);
            if (_h.Forbid.Contains(key))
            {
                var r = ScriptedHttpHandler.Json(403, ApiJson.Serialize(new ErrorDto(false, new ErrorBody(OpCodes.ForbiddenRole, "viewer 令牌只能读取 · 写命令需要 operator 及以上角色", null))));
                return Task.FromResult(r);
            }
            return _inner.SendAsync(request, ct);
        }
    }

    // ———————————————————— WS ————————————————————

    private void OnWsConnect(FakeWsTransport t)
    {
        if (WsHang) t.ConnectGate = _wsGate.Task;   // 握手挂着；放开之后按建这条连接时的 Down / WsFailStatus 继续（FakeWsTransport.ConnectGate）
        if (Down) { t.FailConnect = true; return; }
        if (WsFailStatus is int s) { t.FailConnect = true; t.FailStatus = s; return; }
        t.OnSent = (tr, m) =>
        {
            switch (m.GetProperty("type").GetString())
            {
                // 用 ApiJson（camelCase，同 HMI）：FakeWsTransport.Push(object) 的默认序列化是 PascalCase，客户端认不出 type
                case "subscribe": tr.Push(ApiJson.Serialize(new SubscribedMsg("subscribed", new[] { "telemetry", "events", "alarms" }, 0, 10))); break;
                case "ping": tr.Push(ApiJson.Serialize(new PongMsg("pong", ServerNow))); break;
            }
        };
        t.Push(ApiJson.Serialize(new HelloMsg("hello", "1.0", ServerNow, true, BootId, true, SeqEpoch, 10, _seq, new ModeSnap(ControlMode, Holder), 5000)));
    }

    /// <summary>当前打开的 WS 连接（最后一条）。</summary>
    public FakeWsTransport? Open => Server.Transports.LastOrDefault(t => t.IsOpen);

    /// <summary>推一帧遥测。</summary>
    public void PushFrame(long? seq = null) => Open?.Push(ApiJson.Serialize(Frame(seq)));

    /// <summary>推一条 WS control 消息。</summary>
    public void PushControl(string ev, string state, string? holder, string? requestId = null, string mode = "remote", string detail = "")
        => Open?.Push(ApiJson.Serialize(new ControlMsg("control", ev, mode, holder, state, requestId, null, null, null, 10, detail, ServerNow)));

    /// <summary>服务端断开 WS（拔线 / 重启）。</summary>
    public void DropWs()
    {
        foreach (var t in Server.Transports.ToArray()) if (t.IsOpen) t.ServerClose();
    }

    /// <summary>HMI 重启：新的 bootId 与序号纪元，控制权回到本地，WS 断开（客户端退避后重连，hello 带新的 bootId）。</summary>
    public void Restart(string bootId, string? serial = null)
    {
        BootId = bootId;
        SeqEpoch++;
        if (serial != null) Serial = serial;
        ControlMode = "local";
        Holder = null;
        LeaseLost = false;
        DropWs();
    }

    // ———————————————————— REST 脚本 ————————————————————

    private void Script()
    {
        Http.On("GET", "/health", _ => ScriptedHttpHandler.Ok(new HealthDto(true, "1.0", true, BootId, ServerNow)));
        Http.On("GET", "/device", _ => ScriptedHttpHandler.Ok(Device));
        Http.On("GET", "/control", _ => ScriptedHttpHandler.Ok(Control()));
        Http.On("GET", "/snapshot", _ => ScriptedHttpHandler.Ok(Frame()));
        Http.On("GET", "/settings", _ => ScriptedHttpHandler.Json(200, Samples.Read("live-settings.json")));
        Http.On("GET", "/alarms", _ => { lock (_lock) return ScriptedHttpHandler.Ok(new AlarmsDto(Alarms.Count, Alarms.ToArray())); });
        Http.On("GET", @"/events\?.*", _ => ScriptedHttpHandler.Ok(new EventsPageDto(SeqEpoch, 10, 0, 0, false, Array.Empty<ParallelReactor.Services.EventRecord>())));
        Http.On("POST", "/control/request", call =>
        {
            if (AutoGrant)
            {
                ControlMode = "remote";
                Holder = ClientOf(call);
                return ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto(RequestId, "granted", null, LeaseId, ServerNow.AddSeconds(10), 10, 60, RequestKey));
            }
            _requestExpires = ServerNow.AddSeconds(60);   // HMI：申请的到期时刻在申请时定下，之后每次查询都一样
            return ScriptedHttpHandler.Command(call, true, new ControlRequestResultDto(RequestId, "pending", _requestExpires, null, null, 10, 60, RequestKey));
        });
        Http.On("GET", "/control/requests/.+", call =>
        {
            if (Deny) return ScriptedHttpHandler.Ok(new ControlRequestStatusDto(RequestId, "denied", "PC", null, null, null, 10, 60));
            if (Approve)
            {
                ControlMode = "remote";
                Holder ??= "PC-测试工位1";
                var lease = call.RequestKey == RequestKey ? LeaseId : null;   // HMI：租约号只给带申请凭据的申请方
                return ScriptedHttpHandler.Ok(new ControlRequestStatusDto(RequestId, "granted", "PC", null, lease, ServerNow.AddSeconds(10), 10, 60));
            }
            return ScriptedHttpHandler.Ok(new ControlRequestStatusDto(RequestId, "pending", "PC", _requestExpires, null, null, 10, 60));
        });
        Http.On("POST", "/control/heartbeat", call => LeaseLost
            ? ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remoteLost", false, ServerNow.AddSeconds(10), ServerNow.AddSeconds(50), 10))
            : ScriptedHttpHandler.Command(call, false, new HeartbeatResultDto("remote", true, ServerNow.AddSeconds(10), ServerNow.AddSeconds(70), 10)));
        Http.On("POST", "/control/release", call =>
        {
            lock (_lock) Released.Add(call.Lease);
            ControlMode = "local";
            Holder = null;
            return ScriptedHttpHandler.Command(call, false, new { mode = "local" });
        });
        Http.On("PUT", "/channels/[0-9]+/program", call => ScriptedHttpHandler.Command(call, false, new { ok = true }));
        Http.On("POST", "/stop-all", call => ScriptedHttpHandler.Command(call, false, new { stopped = 0 }));
        Http.On("POST", "/estop", call => ScriptedHttpHandler.Command(call, false, new { ok = true }));
    }

    private static string ClientOf(ScriptedHttpHandler.Call call)
    {
        try { return call.Json.GetProperty("clientName").GetString() ?? "PC"; } catch { return "PC"; }
    }

    public int Count(string method, string path) => Http.Count(method, path);
}
