using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using ParallelReactor.Commands;
using ParallelReactor.Services;

namespace ParallelReactor.Api;

/// <summary>
/// WebSocket 推送（设计稿 §8）。
/// <para>
/// 扇出架构（绝不阻塞 Tick）：UI 线程 <see cref="SnapshotStore.Publish"/> 只触发一次 <c>TryWrite</c> 到容量 1、丢旧的通道；
/// 一个后台任务把每帧序列化<b>一次</b>，再 <c>TryWrite</c> 到每个连接的遥测队列（容量 50，丢最旧，丢帧计入 <see cref="ApiStats.DroppedTelemetryFrames"/>）。
/// 事件 / 报警走每连接独立的事件队列（容量 2000，满则断开连接 1008，让客户端重同步，事件不丢）。每个连接一个写任务（串行 SendAsync）、一个读任务。
/// 中枢事件队列（容量 2000）满时同样不静默丢：计入 <see cref="ApiStats.DroppedEvents"/> 并把所有连接以 1008 断开，让客户端按 seq 重同步。
/// 连接数上限用计数器先占位再握手（并发握手不会超额）；连接 / 断开审计按「令牌@IP」每 10 s 最多一条（重连风暴不刷事件文件）。
/// </para>
/// 客户端消息：subscribe（topics / resumeAfterEventSeq）、resync（afterSeq）、ping → pong、hb（带 lease 时为租约心跳，否则只保活）。缺口太大回 <c>gap</c>。
/// P3：<see cref="Broadcast"/> 推 <c>control</c>（不含 leaseId：同一令牌的另一台 PC 也在听，leaseId 只走 HTTP）与 <c>opEvent</c>；<see cref="BroadcastTargeted"/> 保留给将来按令牌定向的消息。
/// </summary>
public sealed class WsHub : IDisposable
{
    public const int TelemetryQueue = 50;
    public const int EventQueue = 2000;
    public const int ReplayLimit = 1000;
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly IApiSources _src;
    private readonly ApiConfig _cfg;
    private readonly ApiStats _stats;
    private readonly ConcurrentDictionary<long, Conn> _conns = new();
    private readonly KeyedThrottle _auditThrottle = new(TimeSpan.FromSeconds(10));
    private int _admitted;        // 已占位的连接数（含握手中的），用于 maxClients 判定
    private long _suppressedAudits;
    private long _nextId;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<SnapshotBundle> _frames = Channel.CreateBounded<SnapshotBundle>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<object> _misc = Channel.CreateBounded<object>(new BoundedChannelOptions(EventQueue) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _fanout;

    public WsHub(IApiSources src, ApiConfig cfg, ApiStats stats)
    {
        _src = src;
        _cfg = cfg;
        _stats = stats;
        _src.Snapshots.Published += OnPublished;
        _src.Events.Appended += OnEvent;
        _src.Events.AlarmChanged += OnAlarm;
        _fanout = Task.Run(FanoutAsync);
    }

    public int ClientCount => _conns.Count;

    // —— 生产者线程（UI）上的回调：只做非阻塞入队 ——
    private void OnPublished(SnapshotBundle b) { if (!_conns.IsEmpty) _frames.Writer.TryWrite(b); }
    private void OnEvent(EventRecord r) { if (!_conns.IsEmpty && !_misc.Writer.TryWrite(r)) OnMiscOverflow(); }
    private void OnAlarm(AlarmDto a) { if (!_conns.IsEmpty && !_misc.Writer.TryWrite(a)) OnMiscOverflow(); }

    /// <summary>已序列化的控制 / opEvent 消息；Target 不为 null 时 TargetJson 只发给该令牌名的连接，其余连接收 Json（§8.2 leaseId 定向）。</summary>
    private sealed record Targeted(string Json, string? Target, string? TargetJson);

    /// <summary>向全部连接推送一条控制类消息（不受订阅开关过滤）。任意线程，非阻塞。</summary>
    public void Broadcast(string json) { if (!_conns.IsEmpty && !_misc.Writer.TryWrite(new Targeted(json, null, null))) OnMiscOverflow(); }

    /// <summary>定向推送：令牌名 = targetClient 的连接收 targetJson（带 leaseId），其他连接收 json。</summary>
    public void BroadcastTargeted(string targetJson, string targetClient, string json)
    { if (!_conns.IsEmpty && !_misc.Writer.TryWrite(new Targeted(json, targetClient, targetJson))) OnMiscOverflow(); }

    /// <summary>中枢事件队列满（扇出任务跟不上）：不是遥测丢帧，单独计数，并让全部连接走重同步（事件不静默丢）。</summary>
    private void OnMiscOverflow()
    {
        _stats.EventDropped();
        foreach (var c in _conns.Values) { c.CloseReason ??= "服务端事件队列溢出"; c.Fail(WebSocketCloseStatus.PolicyViolation); }
    }

    /// <summary>后台扇出：每条消息序列化一次，分发到各连接队列。</summary>
    private async Task FanoutAsync()
    {
        var ct = _cts.Token;
        Task<bool>? miscWait = null, frameWait = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool did = false;
                while (_misc.Reader.TryRead(out var m))
                {
                    did = true;
                    if (m is Targeted t)
                    {
                        foreach (var c in _conns.Values)
                            c.PostEvent(t.Target != null && c.Client == t.Target ? t.TargetJson! : t.Json, MsgKind.Control);
                        continue;
                    }
                    string json = m switch
                    {
                        EventRecord r => ApiJson.Serialize(EventMsg.From(r)),
                        AlarmDto a => ApiJson.Serialize(new AlarmMsg("alarm", a)),
                        _ => "",
                    };
                    if (json.Length == 0) continue;
                    var kind = m is AlarmDto ? MsgKind.Alarm : MsgKind.Event;
                    foreach (var c in _conns.Values) c.PostEvent(json, kind);
                }
                if (_frames.Reader.TryRead(out var b))
                {
                    did = true;
                    string json = ApiJson.Serialize(b.Frame);
                    foreach (var c in _conns.Values) c.PostTelemetry(json);
                }
                if (did) continue;
                miscWait ??= _misc.Reader.WaitToReadAsync(ct).AsTask();
                frameWait ??= _frames.Reader.WaitToReadAsync(ct).AsTask();
                await Task.WhenAny(miscWait, frameWait).ConfigureAwait(false);
                if (miscWait.IsCompleted) miscWait = null;
                if (frameWait.IsCompleted) frameWait = null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { try { _src.Audit("err", "接口推送任务异常退出", ex.GetBaseException().Message); } catch { } }
    }

    /// <summary>/api/v1/ws 处理器（已过白名单 + 鉴权中间件）。</summary>
    public async Task HandleAsync(HttpContext ctx)
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            await ApiErrors.WriteAsync(ctx.Response, OpCodes.BadRequest, "此路径只接受 WebSocket 升级请求", null, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }
        // 先占位再握手：并发握手下也不会超过 maxClients（判断与计数是同一个 Interlocked）
        if (Interlocked.Increment(ref _admitted) > _cfg.MaxClients)
        {
            Interlocked.Decrement(ref _admitted);
            await ApiErrors.WriteAsync(ctx.Response, OpCodes.TooManyClients, $"WebSocket 连接数已达上限 {_cfg.MaxClients}", null, ctx.RequestAborted).ConfigureAwait(false);
            return;
        }
        var auth = ApiHost.AuthOf(ctx);
        WebSocket ws;
        try { ws = await ctx.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false); }
        catch { Interlocked.Decrement(ref _admitted); return; }

        long id = Interlocked.Increment(ref _nextId);
        string ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "-";
        var conn = new Conn(id, ws, auth.Name, _stats, _cts.Token);
        string auditKey = auth.Name + "@" + ip;
        try
        {
            // hello 必须是第一条：先入本连接队列，再登记到 _conns（登记后扇出任务才会往它投事件），最后记连接审计（那条事件自己也会被推过来）
            var cur = _src.Snapshots.Current;
            var ev = _src.Events;
            conn.PostEvent(ApiJson.Serialize(new HelloMsg("hello", ApiVersionInfo.ApiVersion, DateTime.UtcNow, _src.ClockSynced, _src.BootId, _src.Simulated,
                ev.SeqEpoch, ev.Head, cur?.Frame.Seq ?? 0, _src.Control, EventStore.RingSize)), MsgKind.Control);
            if (cur != null) conn.PostTelemetry(ApiJson.Serialize(cur.Frame));   // 不必等下一个 Tick
            _conns[id] = conn;
            _stats.ClientConnected();
            Audit("接口客户端已连接", "c:" + auditKey, $"{auth.Name} @ {ip} · WS #{id} · 当前 {_conns.Count} 个");
            var writer = conn.WriteLoopAsync();
            var reader = ReadLoopAsync(conn);
            await Task.WhenAny(writer, reader).ConfigureAwait(false);
            conn.Cancel();
            await Task.WhenAll(Quiet(writer), Quiet(reader)).ConfigureAwait(false);
        }
        finally
        {
            if (_conns.TryRemove(id, out _)) _stats.ClientDisconnected();
            Interlocked.Decrement(ref _admitted);
            await conn.CloseAsync().ConfigureAwait(false);
            Audit("接口客户端已断开", "d:" + auditKey, $"{auth.Name} @ {ip} · WS #{id} · {conn.CloseReason ?? "客户端关闭"} · 发送帧 {conn.FramesSent} · 丢帧 {conn.Dropped}");
        }
    }

    /// <summary>连接 / 断开审计：同一「令牌@IP」每 10 s 最多一条，被压掉的条数在下一条里带出（重连循环不刷事件文件、不把真实事件挤出重同步环）。</summary>
    private void Audit(string title, string key, string detail)
    {
        if (!_auditThrottle.Allow(key, DateTime.UtcNow)) { Interlocked.Increment(ref _suppressedAudits); return; }
        long sup = Interlocked.Exchange(ref _suppressedAudits, 0);
        try { _src.Audit("info", title, detail + (sup > 0 ? $" · 期间省略 {sup} 条连接 / 断开记录（同一客户端 10 秒内只记一条）" : "")); } catch { }
    }

    private static async Task Quiet(Task t) { try { await t.ConfigureAwait(false); } catch { } }

    private async Task ReadLoopAsync(Conn c)
    {
        var buf = new byte[16 * 1024];
        var ct = c.Token;
        try
        {
            while (!ct.IsCancellationRequested && c.Ws.State == WebSocketState.Open)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await c.Ws.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (r.MessageType == WebSocketMessageType.Close) { c.CloseReason = "客户端关闭"; return; }
                    ms.Write(buf, 0, r.Count);
                    if (ms.Length > 64 * 1024) { c.CloseReason = "消息过大"; c.Fail(WebSocketCloseStatus.MessageTooBig); return; }
                } while (!r.EndOfMessage);
                if (r.MessageType != WebSocketMessageType.Text) continue;
                Handle(c, Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { c.CloseReason ??= "连接中断"; }
        catch (Exception ex) { c.CloseReason ??= "读取异常：" + ex.Message; }
    }

    private void Handle(Conn c, string text)
    {
        string type;
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
            type = doc.RootElement.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
        }
        catch
        {
            c.PostEvent(ApiJson.Serialize(new WsErrorMsg("error", OpCodes.BadRequest, "消息不是合法 JSON")), MsgKind.Control);
            return;
        }
        using (doc)
        {
            var root = doc.RootElement;
            switch (type)
            {
                case "ping":
                    c.PostEvent(ApiJson.Serialize(new PongMsg("pong", DateTime.UtcNow)), MsgKind.Control);
                    break;
                case "hb":
                    {
                        // §5.5：只有带当前有效租约的 hb 才计入远程心跳；不带租约的只用于保活。无效租约回 error（客户端据此重新申请）
                        if (root.TryGetProperty("lease", out var lz) && lz.ValueKind == JsonValueKind.String && lz.GetString() is { Length: > 0 } lease)
                        {
                            var hb = _src.Lease.Heartbeat(lease, c.Client, _src.MonoNow);
                            if (!hb.Ok) c.PostEvent(ApiJson.Serialize(new WsErrorMsg("error", hb.Code!, hb.Message!)), MsgKind.Control);
                        }
                        break;
                    }
                case "subscribe":
                    {
                        var topics = new List<string>();
                        if (root.TryGetProperty("topics", out var ts) && ts.ValueKind == JsonValueKind.Array)
                            foreach (var x in ts.EnumerateArray()) if (x.ValueKind == JsonValueKind.String) topics.Add(x.GetString()!);
                        if (topics.Count > 0)
                        {
                            c.SubTelemetry = topics.Contains("telemetry");
                            c.SubEvents = topics.Contains("events");
                            c.SubAlarms = topics.Contains("alarms");
                        }
                        int replayed = 0;
                        if (root.TryGetProperty("resumeAfterEventSeq", out var rs) && rs.ValueKind == JsonValueKind.Number && rs.TryGetInt64(out var after))
                        {
                            if (root.TryGetProperty("seqEpoch", out var ep) && ep.ValueKind == JsonValueKind.Number && ep.TryGetInt32(out var e) && e != _src.Events.SeqEpoch)
                            {
                                c.PostEvent(ApiJson.Serialize(new GapMsg("gap", "epoch_changed", _src.Events.Head, _src.Events.OldestBuffered)), MsgKind.Control);
                            }
                            else replayed = Replay(c, after);
                        }
                        c.PostEvent(ApiJson.Serialize(new SubscribedMsg("subscribed",
                            new[] { c.SubTelemetry ? "telemetry" : null, c.SubEvents ? "events" : null, c.SubAlarms ? "alarms" : null }.Where(s => s != null).Select(s => s!).ToArray(),
                            replayed, _src.Events.Head)), MsgKind.Control);
                        break;
                    }
                case "resync":
                    {
                        long after = root.TryGetProperty("afterSeq", out var a) && a.ValueKind == JsonValueKind.Number && a.TryGetInt64(out var v) ? v : 0;
                        int n = Replay(c, after);
                        c.PostEvent(ApiJson.Serialize(new SubscribedMsg("resynced", Array.Empty<string>(), n, _src.Events.Head)), MsgKind.Control);
                        break;
                    }
                case "unsubscribe":
                    c.SubTelemetry = c.SubEvents = c.SubAlarms = false;
                    break;
                default:
                    c.PostEvent(ApiJson.Serialize(new WsErrorMsg("error", OpCodes.BadRequest, $"未知消息类型「{type}」（支持 subscribe / unsubscribe / resync / ping / hb）")), MsgKind.Control);
                    break;
            }
        }
    }

    /// <summary>从环形缓冲补发 afterSeq 之后的事件；缺口太大发 gap。返回补发条数。</summary>
    private int Replay(Conn c, long afterSeq)
    {
        var r = _src.Events.Resync(afterSeq, ReplayLimit);
        if (r.Gap)
        {
            c.PostEvent(ApiJson.Serialize(new GapMsg("gap", "gap_too_large", r.Head, r.OldestBuffered)), MsgKind.Control);
            return 0;
        }
        foreach (var e in r.Items) c.PostEvent(ApiJson.Serialize(EventMsg.From(e)), MsgKind.Control);   // 补发不看订阅开关
        return r.Items.Count;
    }

    /// <summary>关闭全部连接并停止扇出。</summary>
    public void Dispose()
    {
        _src.Snapshots.Published -= OnPublished;
        _src.Events.Appended -= OnEvent;
        _src.Events.AlarmChanged -= OnAlarm;
        _cts.Cancel();
        foreach (var c in _conns.Values) { c.CloseReason ??= "服务端停止"; c.Cancel(); }
    }

    /// <summary>事件队列里消息的类别（订阅过滤用）。</summary>
    public enum MsgKind { Control, Event, Alarm }

    /// <summary>一个 WebSocket 连接：两个有界队列 + 单写任务。</summary>
    private sealed class Conn
    {
        public readonly long Id;
        public readonly WebSocket Ws;
        public readonly string Client;
        private readonly ApiStats _stats;
        private readonly CancellationTokenSource _cts;
        private readonly Channel<string> _tele = Channel.CreateBounded<string>(new BoundedChannelOptions(TelemetryQueue) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
        private readonly Channel<string> _evt = Channel.CreateBounded<string>(new BoundedChannelOptions(EventQueue) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        private long _dropped, _frames;
        private WebSocketCloseStatus? _failStatus;

        public volatile bool SubTelemetry = true, SubEvents = true, SubAlarms = true;
        public string? CloseReason;
        public long Dropped => Interlocked.Read(ref _dropped);
        public long FramesSent => Interlocked.Read(ref _frames);
        public CancellationToken Token => _cts.Token;

        public Conn(long id, WebSocket ws, string client, ApiStats stats, CancellationToken hubToken)
        {
            Id = id; Ws = ws; Client = client; _stats = stats;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(hubToken);
        }

        /// <summary>遥测帧入队（满则丢最旧并计数）。</summary>
        public void PostTelemetry(string json)
        {
            if (!SubTelemetry || _cts.IsCancellationRequested) return;
            if (_tele.Reader.Count >= TelemetryQueue) { Interlocked.Increment(ref _dropped); _stats.Dropped(); }
            _tele.Writer.TryWrite(json);
        }

        /// <summary>事件 / 报警 / 控制消息入队（按订阅开关过滤；控制消息总是发）；满则断开（1008，让客户端重同步）。</summary>
        public void PostEvent(string json, MsgKind kind)
        {
            if (_cts.IsCancellationRequested) return;
            if (kind == MsgKind.Event && !SubEvents) return;
            if (kind == MsgKind.Alarm && !SubAlarms) return;
            if (!_evt.Writer.TryWrite(json))
            {
                CloseReason ??= "事件队列溢出（慢客户端）";
                Fail(WebSocketCloseStatus.PolicyViolation);
            }
        }

        public void Fail(WebSocketCloseStatus status) { _failStatus ??= status; Cancel(); }
        public void Cancel() { try { _cts.Cancel(); } catch { } }

        public async Task WriteLoopAsync()
        {
            var ct = _cts.Token;
            Task<bool>? ew = null, tw = null;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (_evt.Reader.TryRead(out var m)) { await SendAsync(m, ct).ConfigureAwait(false); _stats.EventSent(); continue; }
                    if (_tele.Reader.TryRead(out m)) { await SendAsync(m, ct).ConfigureAwait(false); _stats.FrameSent(); Interlocked.Increment(ref _frames); continue; }
                    ew ??= _evt.Reader.WaitToReadAsync(ct).AsTask();
                    tw ??= _tele.Reader.WaitToReadAsync(ct).AsTask();
                    await Task.WhenAny(ew, tw).ConfigureAwait(false);
                    if (ew.IsCompleted) ew = null;
                    if (tw.IsCompleted) tw = null;
                }
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { CloseReason ??= "发送失败（连接中断）"; }
            catch (Exception ex) { CloseReason ??= "发送异常：" + ex.Message; }
        }

        private async Task SendAsync(string json, CancellationToken ct)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(SendTimeout);
            try { await Ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                CloseReason ??= $"发送超时 {SendTimeout.TotalSeconds:0} s（慢客户端）";
                Fail(WebSocketCloseStatus.PolicyViolation);
                throw;
            }
        }

        public async Task CloseAsync()
        {
            try
            {
                if (Ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await Ws.CloseOutputAsync(_failStatus ?? WebSocketCloseStatus.NormalClosure, _failStatus != null ? "resync required" : "bye", cts.Token).ConfigureAwait(false);
                }
            }
            catch { }
            try { Ws.Dispose(); } catch { }
            _cts.Dispose();
        }
    }
}
