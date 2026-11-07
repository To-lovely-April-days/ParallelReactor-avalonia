using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Services;

namespace ParallelReactor.Client;

/// <summary>WS 连接状态。</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    /// <summary>已收到 hello 并订阅。</summary>
    Connected,
    /// <summary>收到 gap，正在用 REST 补事件 / 快照。</summary>
    Resyncing,
    /// <summary>断开后等待退避重连。</summary>
    Reconnecting,
    /// <summary>调用方停止。</summary>
    Stopped,
}

/// <summary>
/// WebSocket 客户端（设计稿 §8；使用说明 §3）：hello → subscribe（resumeAfterEventSeq / seqEpoch）→ 分发 telemetry / event / alarm / control / opEvent；
/// 事件 seq 守卫（跳号发 resync；gap → REST 分页 + GET /snapshot + 重订阅）；5 s ping / 10 s 无 pong 判死；指数退避重连（0.5 → 10 s，±20%）；
/// 持租约时由 <see cref="ControlLeaseClient"/> 经 <see cref="SendHeartbeatAsync"/> 发 <c>{"type":"hb","lease":…}</c>。
/// 接收循环只解析 + 触发事件；慢消费者由上层（<see cref="ReactorConnection"/> 的有界队列）自保。事件在线程池线程触发。
/// </summary>
public sealed class TelemetryWsClient : IAsyncDisposable
{
    private readonly ClientOptions _opt;
    private readonly ReactorApiClient _rest;
    private readonly IClock _clock;
    private readonly Func<IWsTransport> _factory;
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private IWsTransport? _ws;
    private ConnectionState _state = ConnectionState.Disconnected;
    private long _lastSeq;
    private int _seqEpoch;
    private bool _everConnected, _resyncPending;
    private DateTime _lastPongAt;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private int _attempt;

    public TelemetryWsClient(ClientOptions options, ReactorApiClient rest, IClock? clock = null, Func<IWsTransport>? transportFactory = null)
    {
        _opt = options ?? throw new ArgumentNullException(nameof(options));
        _rest = rest ?? throw new ArgumentNullException(nameof(rest));
        _clock = clock ?? SystemClock.Instance;
        _factory = transportFactory ?? (() => new ClientWebSocketTransport());
    }

    public ConnectionState State { get { lock (_lock) return _state; } }
    public long LastEventSeq => Interlocked.Read(ref _lastSeq);
    public int SeqEpoch => Volatile.Read(ref _seqEpoch);
    public HelloMsg? Hello { get; private set; }
    public string? BootId => Hello?.BootId;
    /// <summary>按消息 type 的计数（telemetry / event / alarm / control / opEvent / pong / gap / subscribed / resynced / error / eventDup / eventGap）。</summary>
    public IReadOnlyDictionary<string, int> MessageCounts => _counts;
    public int ReconnectCount { get; private set; }

    public event Action<ConnectionState>? StateChanged;
    public event Action<HelloMsg>? HelloReceived;
    /// <summary>bootId 变了（HMI 重启）：幂等表失效，未完成命令按 §13.3 查 op.* 事件。</summary>
    public event Action<string, string>? ServerRestarted;
    public event Action<TelemetrySnapshot>? TelemetryReceived;
    public event Action<EventMsg>? EventReceived;
    public event Action<AlarmMsg>? AlarmReceived;
    public event Action<ControlMsg>? ControlReceived;
    public event Action<OpEventMsg>? OpEventReceived;
    public event Action<GapMsg>? GapDetected;
    public event Action<SubscribedMsg>? Subscribed;
    public event Action<SubscribedMsg>? Resynced;
    public event Action<WsErrorMsg>? ErrorReceived;
    /// <summary>每条消息的 (type, json) 原文（录制 / 调试）。</summary>
    public event Action<string, string>? RawReceived;
    public event Action<Exception>? Error;

    // ======================================================================
    // 生命周期
    // ======================================================================

    /// <summary>后台启动（重连循环直到 <see cref="StopAsync"/>）。</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_runTask != null) return;
            _runCts = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_runCts.Token));
        }
    }

    public async Task StopAsync()
    {
        Task? t; CancellationTokenSource? cts; IWsTransport? ws;
        lock (_lock) { t = _runTask; cts = _runCts; ws = _ws; _runTask = null; _runCts = null; }
        if (cts == null) return;
        try { cts.Cancel(); } catch { }
        if (ws != null)
        {
            // 礼貌关闭最多等 1 s，然后 Abort（对端半死时不能卡住退出）
            try { using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1)); await ws.CloseAsync(closeCts.Token).ConfigureAwait(false); } catch { }
            ws.Abort();
        }
        if (t != null) { try { await t.ConfigureAwait(false); } catch { } }
        cts.Dispose();
        SetState(ConnectionState.Stopped);
    }

    /// <summary>连接 / 重连循环（可直接 await，用于单测）。</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = _opt.ReconnectMin;
        while (!ct.IsCancellationRequested)
        {
            SetState(_attempt == 0 ? ConnectionState.Connecting : ConnectionState.Reconnecting);
            var ws = _factory();
            try
            {
                await ws.ConnectAsync(_opt.WsUri, new Dictionary<string, string>
                {
                    ["Authorization"] = "Bearer " + _opt.Token,
                    [ApiHeaders.ClientVersion] = _opt.ClientVersion,
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { await ws.DisposeAsync().ConfigureAwait(false); break; }
            catch (Exception ex)
            {
                Error?.Invoke(ex);
                await ws.DisposeAsync().ConfigureAwait(false);
                _attempt++;
                if (!await BackoffAsync(backoff, ct).ConfigureAwait(false)) break;
                backoff = Next(backoff);
                continue;
            }
            lock (_lock) _ws = ws;
            if (_attempt > 0) ReconnectCount++;
            _attempt++;
            backoff = _opt.ReconnectMin;
            _lastPongAt = _clock.UtcNow;
            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pinger = PingLoopAsync(ws, connCts.Token);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var msg = await ws.ReceiveAsync(ct).ConfigureAwait(false);
                    if (msg == null) break;
                    await DispatchAsync(ws, msg, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { Error?.Invoke(ex); }
            finally
            {
                connCts.Cancel();
                try { await pinger.ConfigureAwait(false); } catch { }
                lock (_lock) { if (_ws == ws) _ws = null; }
                try { using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(1)); await ws.CloseAsync(closeCts.Token).ConfigureAwait(false); } catch { }
                ws.Abort();
                await ws.DisposeAsync().ConfigureAwait(false);
            }
            if (ct.IsCancellationRequested) break;
            // 1008（慢消费者 / 队列溢出）或任何断开：退避后重连，subscribe 带 resumeAfterEventSeq 续传
            Count(ws.CloseStatus == WebSocketCloseStatus.PolicyViolation ? "close1008" : "closed");
            SetState(ConnectionState.Reconnecting);
            if (!await BackoffAsync(backoff, ct).ConfigureAwait(false)) break;
            backoff = Next(backoff);
        }
        SetState(ct.IsCancellationRequested ? ConnectionState.Stopped : ConnectionState.Disconnected);
    }

    private async Task<bool> BackoffAsync(TimeSpan backoff, CancellationToken ct)
    {
        var jitter = 1 + (Random.Shared.NextDouble() * 0.4 - 0.2);
        try { await _clock.Delay(TimeSpan.FromTicks((long)(backoff.Ticks * jitter)), ct).ConfigureAwait(false); return true; }
        catch (OperationCanceledException) { return false; }
    }

    private TimeSpan Next(TimeSpan b) => b * 2 > _opt.ReconnectMax ? _opt.ReconnectMax : b * 2;

    private async Task PingLoopAsync(IWsTransport ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _clock.Delay(_opt.PingInterval, ct).ConfigureAwait(false);
                if (!ws.IsOpen) return;
                if (_clock.UtcNow - _lastPongAt > _opt.PongTimeout)
                {
                    // 对端半死（拔线 / 进程冻结）时 CloseOutputAsync 发出 close 帧后会一直等对端回 close，ReceiveAsync 永不返回；
                    // 必须 Abort 撕掉连接，接收循环才会退出并进入退避重连
                    Count("pongTimeout");
                    ws.Abort();
                    return;
                }
                await ws.SendAsync("""{"type":"ping"}""", ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    // ======================================================================
    // 发送
    // ======================================================================

    /// <summary>发送任意 JSON 文本；未连接返回 false。</summary>
    public async Task<bool> SendAsync(string json, CancellationToken ct)
    {
        IWsTransport? ws;
        lock (_lock) ws = _ws;
        if (ws == null || !ws.IsOpen) return false;
        try { await ws.SendAsync(json, ct).ConfigureAwait(false); return true; }
        catch (Exception ex) { Error?.Invoke(ex); return false; }
    }

    /// <summary>租约心跳 <c>{"type":"hb","lease":id}</c>；WS 不在线返回 false（调用方走 REST）。</summary>
    public Task<bool> SendHeartbeatAsync(string leaseId, CancellationToken ct)
        => State == ConnectionState.Connected ? SendAsync(JsonSerializer.Serialize(new { type = "hb", lease = leaseId }), ct) : Task.FromResult(false);

    private Task SendSubscribeAsync(IWsTransport ws, bool resume, CancellationToken ct)
    {
        var o = resume
            ? JsonSerializer.Serialize(new { type = "subscribe", topics = new[] { "telemetry", "events", "alarms" }, resumeAfterEventSeq = LastEventSeq, seqEpoch = SeqEpoch })
            : JsonSerializer.Serialize(new { type = "subscribe", topics = new[] { "telemetry", "events", "alarms" } });
        return ws.SendAsync(o, ct);
    }

    // ======================================================================
    // 分发
    // ======================================================================

    private async Task DispatchAsync(IWsTransport ws, string json, CancellationToken ct)
    {
        string type;
        try
        {
            using var doc = JsonDocument.Parse(json);
            type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        }
        catch (JsonException ex) { Count("badJson"); Error?.Invoke(ex); return; }
        Count(type);
        try { RawReceived?.Invoke(type, json); } catch { }
        try
        {
            switch (type)
            {
                case "hello":
                    {
                        var h = ApiJson.Deserialize<HelloMsg>(json)!;
                        var prev = Hello;
                        Hello = h;
                        if (prev != null && prev.BootId != h.BootId) { try { ServerRestarted?.Invoke(prev.BootId, h.BootId); } catch { } }
                        bool resume;
                        lock (_lock)
                        {
                            if (!_everConnected)
                            {
                                _everConnected = true;
                                _seqEpoch = h.SeqEpoch;
                                if (!_opt.ReplayEventsFromStart) Interlocked.Exchange(ref _lastSeq, h.EventSeqHead);
                                resume = _opt.ReplayEventsFromStart;
                            }
                            else
                            {
                                if (h.SeqEpoch != _seqEpoch) { _seqEpoch = h.SeqEpoch; Interlocked.Exchange(ref _lastSeq, 0); }
                                resume = true;
                            }
                            _resyncPending = false;
                        }
                        try { HelloReceived?.Invoke(h); } catch { }
                        await SendSubscribeAsync(ws, resume, ct).ConfigureAwait(false);
                        SetState(ConnectionState.Connected);
                        break;
                    }
                case "telemetry":
                    {
                        var f = ApiJson.Deserialize<TelemetrySnapshot>(json);
                        if (f != null) TelemetryReceived?.Invoke(f);
                        break;
                    }
                case "event":
                    {
                        var e = ApiJson.Deserialize<EventMsg>(json);
                        if (e == null) break;
                        bool emit = false, resync = false;
                        lock (_lock)
                        {
                            if (e.Seq <= _lastSeq) { Count("eventDup"); }
                            else if (e.Seq > _lastSeq + 1 && _lastSeq > 0)
                            {
                                Count("eventGap");
                                if (!_resyncPending) { _resyncPending = true; resync = true; }
                            }
                            else { _lastSeq = e.Seq; emit = true; }
                        }
                        if (resync) await ws.SendAsync(JsonSerializer.Serialize(new { type = "resync", afterSeq = LastEventSeq }), ct).ConfigureAwait(false);
                        if (emit) EventReceived?.Invoke(e);
                        break;
                    }
                case "alarm":
                    {
                        var a = ApiJson.Deserialize<AlarmMsg>(json);
                        if (a != null) AlarmReceived?.Invoke(a);
                        break;
                    }
                case "control":
                    {
                        var c = ApiJson.Deserialize<ControlMsg>(json);
                        if (c != null) ControlReceived?.Invoke(c);
                        break;
                    }
                case "opEvent":
                    {
                        var o = ApiJson.Deserialize<OpEventMsg>(json);
                        if (o != null) OpEventReceived?.Invoke(o);
                        break;
                    }
                case "subscribed":
                    {
                        var s = ApiJson.Deserialize<SubscribedMsg>(json);
                        if (s != null) Subscribed?.Invoke(s);
                        break;
                    }
                case "resynced":
                    {
                        lock (_lock) _resyncPending = false;
                        var s = ApiJson.Deserialize<SubscribedMsg>(json);
                        if (s != null) Resynced?.Invoke(s);
                        break;
                    }
                case "gap":
                    {
                        var g = ApiJson.Deserialize<GapMsg>(json);
                        if (g != null) { GapDetected?.Invoke(g); await RestResyncAsync(ws, g, ct).ConfigureAwait(false); }
                        break;
                    }
                case "pong":
                    _lastPongAt = _clock.UtcNow;
                    break;
                case "error":
                    {
                        var e = ApiJson.Deserialize<WsErrorMsg>(json);
                        if (e != null) ErrorReceived?.Invoke(e);
                        break;
                    }
                default:
                    Count("unknown");
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Error?.Invoke(ex); }
    }

    /// <summary>gap：REST 分页补事件（GET /events?afterSeq，≤ 1000/页，直到 more=false）→ GET /snapshot → 重订阅。epoch_changed 先清本地 seq。</summary>
    private async Task RestResyncAsync(IWsTransport ws, GapMsg g, CancellationToken ct)
    {
        SetState(ConnectionState.Resyncing);
        try
        {
            if (g.Reason == "epoch_changed") Interlocked.Exchange(ref _lastSeq, 0);
            if (Hello != null && g.Reason == "epoch_changed") Volatile.Write(ref _seqEpoch, Hello.SeqEpoch);
            while (true)
            {
                var page = await _rest.EventsAsync(LastEventSeq, 1000, ct).ConfigureAwait(false);
                Volatile.Write(ref _seqEpoch, page.SeqEpoch);
                foreach (var r in page.Items)
                {
                    if (r.Seq <= LastEventSeq) continue;
                    Interlocked.Exchange(ref _lastSeq, r.Seq);
                    Count("eventRest");
                    EventReceived?.Invoke(EventMsg.From(r));
                }
                if (!page.More) { if (page.EventSeqHead > LastEventSeq && page.Count == 0) Interlocked.Exchange(ref _lastSeq, page.EventSeqHead); break; }
            }
            var snap = await _rest.SnapshotAsync(ct).ConfigureAwait(false);
            TelemetryReceived?.Invoke(snap);
            lock (_lock) _resyncPending = false;
            await SendSubscribeAsync(ws, resume: true, ct).ConfigureAwait(false);
            Count("restResync");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Error?.Invoke(ex); }
        finally { if (State == ConnectionState.Resyncing) SetState(ConnectionState.Connected); }
    }

    // ======================================================================
    // 内部
    // ======================================================================

    private void Count(string key) => _counts.AddOrUpdate(key, 1, (_, n) => n + 1);

    private void SetState(ConnectionState s)
    {
        lock (_lock) { if (_state == s) return; _state = s; }
        try { StateChanged?.Invoke(s); } catch { }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
