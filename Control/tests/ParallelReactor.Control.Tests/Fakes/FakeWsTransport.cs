using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ParallelReactor.Client;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>脚本化的 WS 传输：测试向客户端推帧（<see cref="Push"/>），客户端发出的帧记录在 <see cref="Sent"/>，可挂回调自动应答（subscribe → subscribed 等）。</summary>
public sealed class FakeWsTransport : IWsTransport
{
    private readonly Channel<string> _toClient = Channel.CreateUnbounded<string>();
    private readonly object _lock = new();
    public List<string> Sent { get; } = new();
    public bool FailConnect { get; set; }
    public bool IsOpen { get; private set; }
    public bool Connected { get; private set; }
    public bool Closed { get; private set; }
    public WebSocketCloseStatus? CloseStatus { get; private set; }
    public Uri? Uri { get; private set; }
    public IReadOnlyDictionary<string, string>? Headers { get; private set; }
    /// <summary>客户端每发一帧的回调（可用 Push 应答）。</summary>
    public Action<FakeWsTransport, JsonElement>? OnSent { get; set; }

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (ConnectGate is { } gate) return ConnectAfterAsync(gate, uri, headers, ct);
        if (FailConnect)
            throw new WebSocketException(FailStatus is int s ? $"The server returned status code '{s}' when status code '101' was expected." : "连接被拒（脚本）");
        Uri = uri; Headers = headers; IsOpen = true; Connected = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// M3 复核（只加）：非空时握手先等这个任务（可被取消）—— 模拟拔线 / 主机不回包时 ConnectAsync 一直挂着（系统的 TCP 连接超时）；为 null 时同旧行为。
    /// </summary>
    public Task? ConnectGate { get; set; }

    private async Task ConnectAfterAsync(Task gate, Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        ConnectGate = null;
        await ConnectAsync(uri, headers, ct).ConfigureAwait(false);
    }

    /// <summary>M3：<see cref="FailConnect"/> 时模拟握手被拒的 HTTP 状态码（如 503 TOO_MANY_CLIENTS）：异常文字与真 ClientWebSocket 相同，<see cref="HandshakeStatusCode"/> 返回它。为 null 时同旧行为（网络层失败）。</summary>
    public int? FailStatus { get; set; }
    public int? HandshakeStatusCode => FailConnect ? FailStatus : null;

    public Task SendAsync(string text, CancellationToken ct)
    {
        if (!IsOpen) throw new WebSocketException("未连接");
        lock (_lock) Sent.Add(text);
        OnSent?.Invoke(this, JsonDocument.Parse(text).RootElement.Clone());
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            if (await _toClient.Reader.WaitToReadAsync(ct)) return await _toClient.Reader.ReadAsync(ct);
            return null;
        }
        catch (ChannelClosedException) { return null; }
    }

    /// <summary>true：CloseAsync 像真 ClientWebSocket.CloseOutputAsync 一样只发 close 帧，不结束接收（对端半死）；只有 Abort 能结束。</summary>
    public bool CloseWaitsForPeer { get; set; }
    public bool Aborted { get; private set; }

    public Task CloseAsync(CancellationToken ct)
    {
        IsOpen = false; Closed = true;
        if (!CloseWaitsForPeer) _toClient.Writer.TryComplete();
        return Task.CompletedTask;
    }

    public void Abort()
    {
        IsOpen = false; Aborted = true;
        _toClient.Writer.TryComplete();
    }

    public ValueTask DisposeAsync() { IsOpen = false; _toClient.Writer.TryComplete(); return ValueTask.CompletedTask; }

    // —— 测试侧 ——

    public void Push(string json) => _toClient.Writer.TryWrite(json);
    public void Push(object o) => Push(JsonSerializer.Serialize(o));

    /// <summary>服务端关闭（如 1008）。</summary>
    public void ServerClose(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        CloseStatus = status; IsOpen = false;
        _toClient.Writer.TryComplete();
    }

    public int CountSent(string type)
    {
        lock (_lock)
        {
            int n = 0;
            foreach (var s in Sent) { using var d = JsonDocument.Parse(s); if (d.RootElement.GetProperty("type").GetString() == type) n++; }
            return n;
        }
    }

    public JsonElement? LastSent(string type)
    {
        lock (_lock)
        {
            for (int i = Sent.Count - 1; i >= 0; i--)
            {
                var d = JsonDocument.Parse(Sent[i]).RootElement.Clone();
                if (d.GetProperty("type").GetString() == type) return d;
            }
            return null;
        }
    }
}

/// <summary>每次连接发下一条传输（脚本化重连）。</summary>
public sealed class FakeWsServer
{
    public List<FakeWsTransport> Transports { get; } = new();
    public Action<FakeWsTransport>? OnConnect { get; set; }
    private readonly object _lock = new();

    public IWsTransport Next()
    {
        var t = new FakeWsTransport();
        lock (_lock) Transports.Add(t);
        OnConnect?.Invoke(t);
        return t;
    }

    public FakeWsTransport this[int i] { get { lock (_lock) return Transports[i]; } }
    public int Count { get { lock (_lock) return Transports.Count; } }
}
