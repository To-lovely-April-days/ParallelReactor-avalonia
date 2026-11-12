#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using AlarmDto = ParallelReactor.Services.AlarmDto;
using EventRecord = ParallelReactor.Services.EventRecord;

namespace ParallelReactor.Control.Shot;

/// <summary>
/// 进程内的假 HMI（仅 Debug；截图场景 fake2 与交互走查用，PORT-PLAN §5 M3「Debug 场景 fake2」）：<b>不运行模拟 HMI</b>（D9 未获同意），
/// 也不开端口 —— App 的连接工厂给真 <see cref="ReactorConnection"/> 一个 <see cref="HttpMessageHandler"/>（REST）和一个 <see cref="IWsTransport"/>（WS），
/// 两者都直接由这里应答，形状与 HMI 相同（Contracts 的 DTO + ApiJson）。界面因此走的是真机的整条路径：ReactorConnection → LiveDeviceSource（监督）→ FleetVm。
/// 能模拟：主机不通（<see cref="Down"/>：REST「Connection refused」、WS 连不上）、断线（<see cref="Drop"/>）、控制权（autoGrant / 被其他客户端持有）、
/// 报警、事件、会话、每秒一帧遥测（读数小幅波动）。
/// </summary>
public sealed class FakeHmi : IDisposable
{
    /// <summary>一个反应釜位置的样子（HMI 的状态码：idle / ready / heating / pressing / react / cooling / done / alarm）。</summary>
    public sealed record Slot(int Id, string State, double TempC, double SpC = 0, double MPa = 0, double TargetMPa = 0, double Heater = 0,
        int Step = 0, int Steps = 0, string? ProgramState = null, string Message = "", bool Valve = false, string? AlarmCause = null);

    private readonly object _lock = new();
    private readonly List<Socket> _open = new();
    private readonly Timer _timer;
    private readonly IReadOnlyList<Slot> _slots;
    private readonly int[] _installed;
    private long _seq = 100, _op;
    private int _tick;
    private string? _lease;

    public FakeHmi(string host, int port, string serial, string bootId, IReadOnlyList<Slot> slots, int[] installed)
    {
        Host = host;
        Port = port;
        Serial = serial;
        BootId = bootId;
        _slots = slots;
        _installed = installed;
        _timer = new Timer(_ => PushFrame(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public string Host { get; }
    public int Port { get; }
    public string Serial { get; }
    public string BootId { get; }

    /// <summary>主机不通：REST 一律「Connection refused」，WS 连不上。</summary>
    public volatile bool Down;
    /// <summary>申请控制权直接批准（HMI 的 autoGrantRemote）。</summary>
    public bool AutoGrant { get; set; } = true;
    /// <summary>控制权被另一个客户端持有（R8：PC 上显示「其他客户端 X」）。</summary>
    public string? OtherHolder { get; set; }
    /// <summary>本机持有控制权时的客户端名（申请时记下）。</summary>
    public string? Holder { get; private set; }
    public bool StirOn { get; set; }
    public int Rpm { get; set; } = 300;
    public string? SessionId { get; set; }
    public List<AlarmDto> Alarms { get; } = new();
    public List<EventRecord> Events { get; } = new();

    /// <summary>每条连接一个处理器（ReactorApiClient 释放时会释放它）。</summary>
    public HttpMessageHandler NewHandler() => new Handler(this);

    /// <summary>每次 WS 连接尝试一条传输。</summary>
    public IWsTransport NewTransport() => new Socket(this);

    /// <summary>断线：主机从此不通，已经打开的 WS 由服务端关闭（客户端随即退避重连，连不上）。</summary>
    public void Drop()
    {
        Down = true;
        Socket[] open;
        lock (_lock) open = _open.ToArray();
        foreach (var s in open) s.ServerClose();
    }

    public void Dispose()
    {
        _timer.Dispose();
        Drop();
    }

    // ———————————————————— 数据 ————————————————————

    private static DateTime Now => DateTime.UtcNow;

    private string Mode => OtherHolder != null || Holder != null ? "remote" : "local";
    private string? CurrentHolder => OtherHolder ?? Holder;

    public DeviceInfoDto Device() => new("ParallelReactor-8", Serial, "1.0.3+fake2", "1.0", 8,
        Enumerable.Range(1, 8).Select(n => new ChannelInstalledDto(n, _installed.Contains(n))).ToArray(), _installed,
        new HardwareDto(new HardwareModuleDto(true, "sim", 19200, null), new HardwareModuleDto(true, "sim", 9600, 4.137),
            new HardwareModuleDto(true, "sim", 38400, null), new HardwareModuleDto(true, "sim", 9600, null)),
        new UnitsDto("C", "psi", "rpm"), true, false, Now.AddHours(-3), BootId, true, 40 + Events.Count, 1);

    private ControlStateDto Control() => new(Mode, CurrentHolder, CurrentHolder == null ? null : "op", CurrentHolder == null ? "local" : "remote",
        CurrentHolder == null ? null : Now.AddSeconds(10), CurrentHolder == null ? null : Now.AddSeconds(70), null, AutoGrant, 10, 60, 60, "keep");

    /// <summary>HMI 的 /settings（同实测样本 live-settings.json 的量程：200 ℃、500 psi、1000 rpm、满量程 4.137 MPa）。</summary>
    private const string SettingsJson = """
        {"limits":{"tempSpMaxC":200,"pressureSpMaxPsi":500,"rpmMax":1000,"volumeMaxMl":50},"alarms":{"overPressurePsi":510,"overTempC":250,"stopHeatOnOverPressure":true,"heatTimeoutMin":20,"pressDeviationPsi":8},"leakTest":{"testPsi":50,"settleMin":10,"measureMin":30,"rateLimitPsiPerHr":2,"reminderDays":7,"lastTestUtc":null},"approach":{"deltaC":5,"rateCPerMin":0.2,"fastRateCPerMin":1,"finalZoneC":1,"settleBandC":0.2,"settleSec":90},"stir":{"peakCurrentA":1,"standbyPct":50,"rampMsPer1000Rpm":1000,"microstep":10000},"pressure":{"fullScaleMPa":4.137},"tempBands":[{"index":0,"loC":0,"hiC":100},{"index":1,"loC":100,"hiC":200}],"notes":{"unused":["heatTimeoutMin","pressDeviationPsi","reminderDays","pressureSpMaxPsi"]},"revision":7}
        """;

    /// <summary>一帧遥测：各位置的读数加一点抖动（每帧不同，看得出数据在流）。</summary>
    public TelemetrySnapshot Frame()
    {
        long seq = Interlocked.Increment(ref _seq);
        int k = Interlocked.Increment(ref _tick);
        double wobble = (k % 4 - 1.5) * 0.05;
        var chans = Enumerable.Range(1, 8).Select(n =>
        {
            bool inst = _installed.Contains(n);
            var s = _slots.FirstOrDefault(x => x.Id == n) ?? new Slot(n, "idle", 24.6 + n * 0.1);
            bool heating = s.State is "heating" or "react" or "pressing" || s.Heater > 0;
            ChannelProgramSnap? prog = s.ProgramState == null ? null
                : new ChannelProgramSnap(s.ProgramState is "running" or "waitingGas", s.Step, s.Steps, s.ProgramState, s.Message, "远程（PC-测试工位1）", Now.AddMinutes(-42), s.ProgramState == "waitingGas");
            double t = Math.Round(s.TempC + (heating ? wobble : wobble / 5), 1);
            return new ChannelSnap(n, inst, inst ? s.State : "idle", s.AlarmCause, false, s.AlarmCause == "overPressure", s.State == "idle" ? null : "远程（PC-测试工位1）",
                inst ? t : null, s.SpC, heating ? s.SpC : null, s.Heater, inst, false, heating ? 0 : 4, "fixed",
                new ApproachSnap(s.State == "heating" ? "creep" : "off", s.State == "heating" ? s.SpC : null, null, ""), null,
                inst ? Math.Round(s.MPa * 145.0377377 + wobble, 2) : null, inst ? 7700 : null, s.TargetMPa * 145.0377377, null, s.Valve, prog != null, prog);
        }).ToArray();
        var running = chans.Where(c => c.Program is { Running: true }).Select(c => c.Id).ToArray();
        return new TelemetrySnapshot("telemetry", seq, Now, true, 40 + Events.Count, new ModeSnap(Mode, CurrentHolder),
            new EstopSnap(false, "ok", "ok", "ok", null, false, false), new CommSnap(true, true, true, true), chans,
            new StirSnap(StirOn, Rpm, true, false, null, null, null, null, 1000),
            new GasSnap(new[] { new GasInletSnap("h2", false), new GasInletSnap("n2", false) }, false, false, null, null),
            new ProgramSnap(running.Length > 0, running.FirstOrDefault(), running.Length > 0 ? "running" : "idle", 0, 0, "", ""),
            new ProgramsSnap(running.Length, running, null, Array.Empty<int>(), ""),
            new LeakSnap(false, 0, null, null, false, false),
            new SessionSnap(SessionId != null, SessionId, SessionId, SessionId == null ? null : SessionId + ".csv"));
    }

    private void PushFrame()
    {
        if (Down) return;
        Socket[] open;
        lock (_lock) open = _open.ToArray();
        if (open.Length == 0) return;
        var json = ApiJson.Serialize(Frame());
        foreach (var s in open) s.Push(json);
    }

    private HelloMsg Hello() => new("hello", "1.0", Now, true, BootId, true, 1, 40 + Events.Count, _seq, new ModeSnap(Mode, CurrentHolder), 5000);

    // ———————————————————— REST ————————————————————

    private sealed class Handler : HttpMessageHandler
    {
        private readonly FakeHmi _h;
        public Handler(FakeHmi h) => _h = h;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (_h.Down)
                throw new HttpRequestException("Connection refused (" + _h.Host + ":" + _h.Port + ")", new SocketException((int)SocketError.ConnectionRefused));
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith(ApiHeaders.Prefix, StringComparison.Ordinal)) path = path[ApiHeaders.Prefix.Length..];
            path = path.Trim('/');
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? idem = request.Headers.TryGetValues(ApiHeaders.Idempotency, out var v) ? v.FirstOrDefault() : null;
            return _h.Respond(request.Method.Method.ToUpperInvariant(), path, body, idem);
        }
    }

    private static HttpResponseMessage Json(int status, string json) =>
        new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private HttpResponseMessage Ok<T>(T dto) => Json(200, ApiJson.Serialize(dto));

    private HttpResponseMessage Command(string? idem, object? result) =>
        Json(200, ApiJson.Serialize(new CommandResponseDto(true, idem ?? Guid.NewGuid().ToString("N"), "confirmed", ApiJson.ToElement(result), Array.Empty<string>(), Interlocked.Increment(ref _op))));

    private HttpResponseMessage Respond(string method, string path, string body, string? idem)
    {
        lock (_lock)
        {
            switch (method, path)
            {
                case ("GET", "health"): return Ok(new HealthDto(true, "1.0", true, BootId, Now));
                case ("GET", "device"): return Ok(Device());
                case ("GET", "control"): return Ok(Control());
                case ("GET", "snapshot"): return Ok(Frame());
                case ("GET", "settings"): return Json(200, SettingsJson);
                case ("GET", "alarms"): return Ok(new AlarmsDto(Alarms.Count, Alarms.ToArray()));
                case ("GET", "events"): return Ok(new EventsPageDto(1, 40 + Events.Count, 0, Events.Count, false, Events.ToList()));
                case ("POST", "control/request"):
                {
                    string client = "PC";
                    try { client = JsonDocument.Parse(body).RootElement.GetProperty("clientName").GetString() ?? "PC"; } catch { }
                    if (OtherHolder != null)
                        return Json(409, ApiJson.Serialize(new CommandErrorDto(false, idem ?? "x", new CommandErrorBody(OpCodes.NotControlHolder, "当前控制权属于「" + OtherHolder + "」", null), 0)));
                    if (!AutoGrant) return Command(idem, new ControlRequestResultDto("rq-" + Host, "pending", Now.AddSeconds(60), null, null, 10, 60, "key-" + Host));
                    Holder = client;
                    _lease = "L-" + Host;
                    return Command(idem, new ControlRequestResultDto("rq-" + Host, "granted", null, _lease, Now.AddSeconds(10), 10, 60, "key-" + Host));
                }
                case ("POST", "control/heartbeat"): return Command(idem, new HeartbeatResultDto("remote", false, Now.AddSeconds(10), Now.AddSeconds(70), 10));
                case ("POST", "control/release"):
                    Holder = null;
                    _lease = null;
                    return Command(idem, new { mode = "local" });
            }
            if (method == "GET" && path.StartsWith("control/requests/", StringComparison.Ordinal))
                return Ok(new ControlRequestStatusDto("rq-" + Host, Holder != null ? "granted" : "pending", Holder ?? "PC", Now.AddSeconds(60), _lease, Now.AddSeconds(10), 10, 60));
            if (method is "POST" or "PUT" or "DELETE") return Command(idem, null);
            return Json(404, ApiJson.Serialize(new ErrorDto(false, new ErrorBody(OpCodes.NotFound, "假 HMI 没有这个端点：" + method + " " + path, null))));
        }
    }

    // ———————————————————— WS ————————————————————

    private void Register(Socket s) { lock (_lock) _open.Add(s); }
    private void Unregister(Socket s) { lock (_lock) _open.Remove(s); }

    private sealed class Socket : IWsTransport
    {
        private readonly FakeHmi _h;
        private readonly Channel<string> _q = Channel.CreateUnbounded<string>();

        public Socket(FakeHmi h) => _h = h;

        public bool IsOpen { get; private set; }
        public WebSocketCloseStatus? CloseStatus { get; private set; }

        public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
        {
            if (_h.Down)
                throw new WebSocketException("Unable to connect to the remote server",
                    new HttpRequestException("Connection refused (" + _h.Host + ":" + _h.Port + ")", new SocketException((int)SocketError.ConnectionRefused)));
            IsOpen = true;
            _h.Register(this);
            _q.Writer.TryWrite(ApiJson.Serialize(_h.Hello()));
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken ct)
        {
            if (!IsOpen) throw new WebSocketException("未连接");
            string type = "";
            try { type = JsonDocument.Parse(text).RootElement.GetProperty("type").GetString() ?? ""; } catch { }
            if (type == "subscribe") _q.Writer.TryWrite(ApiJson.Serialize(new SubscribedMsg("subscribed", new[] { "telemetry", "events", "alarms" }, 0, 40)));
            else if (type == "ping") _q.Writer.TryWrite(ApiJson.Serialize(new PongMsg("pong", Now)));
            return Task.CompletedTask;   // hb（租约心跳）HMI 不回
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            try
            {
                if (await _q.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return await _q.Reader.ReadAsync(ct).ConfigureAwait(false);
                return null;
            }
            catch (ChannelClosedException) { return null; }
        }

        public void Push(string json) { if (IsOpen) _q.Writer.TryWrite(json); }

        public void ServerClose()
        {
            CloseStatus = WebSocketCloseStatus.EndpointUnavailable;
            Close();
        }

        private void Close()
        {
            IsOpen = false;
            _q.Writer.TryComplete();
            _h.Unregister(this);
        }

        public Task CloseAsync(CancellationToken ct) { Close(); return Task.CompletedTask; }
        public void Abort() => Close();
        public ValueTask DisposeAsync() { Close(); return ValueTask.CompletedTask; }
    }

    // ———————————————————— 场景 fake2 的四台设备 ————————————————————

    /// <summary>
    /// 场景 fake2（样稿 overview4dev 的样子，用真机能报出的数据）：1号机 HT-PR8 · 现场（RV1–RV4 运行中，本机申请后 autoGrant → 远程控制中，一条提醒，会话记录中）、
    /// 2号机 HT-PR8（被其他客户端 PC-工位2 持有，RV7 超压报警）、3号机（主机不通：首次连接失败、倒计时重试）、4号机（连上后断线：连接中断、倒计时重连）。
    /// </summary>
    public static FakeHmi[] Fleet2()
    {
        var a = new FakeHmi("192.168.1.20", 8080, "PR8-2026-0001", "fake-a", new[]
        {
            new Slot(1, "react", 119.8, 120, 0.349, 0.35, 45, 3, 4, "running", "反应 · 保温 90 min"),
            new Slot(2, "react", 120.3, 120, 0.346, 0.35, 44, 3, 4, "running", "反应 · 保温 90 min"),
            new Slot(3, "heating", 86.4, 120, 0.004, 0, 72, 1, 4, "running", "升温至 120 ℃"),
            new Slot(4, "pressing", 98.7, 120, 0.217, 0.35, 30, 2, 4, "running", "充压至 0.35 MPa", Valve: true),
        }, new[] { 1, 2, 3, 4 }) { StirOn = true, Rpm = 300, SessionId = "PT-20261109-0930" };
        a.Alarms.Add(new AlarmDto("3", "warn", "RV3", 3, "RV3 升温较慢", "已用 18 min", DateTime.UtcNow.AddMinutes(-1), "active", null));
        a.Events.Add(new EventRecord(38, 1, DateTime.UtcNow.AddMinutes(-42), "", "info", "op.program.run", 1, "remote", "RV1", Me, null, "RV1 程序开始", "加氢-标准 · 4 步"));
        a.Events.Add(new EventRecord(39, 1, DateTime.UtcNow.AddMinutes(-1), "", "warn", "alarm.raise", 3, "system", "RV3", null, null, "RV3 升温较慢", "已用 18 min"));

        var b = new FakeHmi("192.168.1.24", 8080, "PR8-2026-0003", "fake-b", new[]
        {
            new Slot(1, "react", 150.0, 150, 2.10, 2.10, 52, 3, 5, "running", "反应 · 保温 60 min"),
            new Slot(2, "heating", 112.6, 150, 0.006, 0, 80, 1, 5, "running", "升温至 150 ℃"),
            new Slot(3, "cooling", 68.3, 40, 0.110),
            new Slot(4, "done", 31.6, 0, 0.005, 0, 0, 5, 5, "completed", "程序完成"),
            new Slot(5, "ready", 25.1),
            new Slot(7, "alarm", 80.4, 80, 3.12, 2.00, 0, 2, 4, "failed", "超压 · 已中止", AlarmCause: "overPressure"),
        }, Enumerable.Range(1, 8).ToArray()) { OtherHolder = "PC-工位2", AutoGrant = false };
        b.Alarms.Add(new AlarmDto("7", "err", "RV7", 7, "RV7 超压", "3.12 MPa ≥ 3.00 MPa · 已关闭 SV7", DateTime.UtcNow.AddMinutes(-2), "active", null));

        var c = new FakeHmi("192.168.1.36", 8080, "PR8-2026-0002", "fake-c", Array.Empty<Slot>(), Enumerable.Range(1, 8).ToArray()) { Down = true };

        var d = new FakeHmi("192.168.1.41", 8080, "PR8-2026-0004", "fake-d", new[]
        {
            new Slot(1, "react", 100.2, 100, 0.498, 0.50, 40, 3, 4, "running", "反应 · 保温 30 min"),
            new Slot(2, "react", 99.8, 100, 0.502, 0.50, 41, 3, 4, "running", "反应 · 保温 30 min"),
            new Slot(3, "heating", 78.4, 100, 0.005, 0, 66, 1, 4, "running", "升温至 100 ℃"),
            new Slot(4, "ready", 24.1),
        }, Enumerable.Range(1, 8).ToArray());
        return new[] { a, b, c, d };
    }

    private const string Me = "PC-测试工位1";
}
#endif
