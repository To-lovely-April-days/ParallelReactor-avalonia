using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Client;

/// <summary>WebSocket 传输抽象：默认包 <see cref="ClientWebSocket"/>；单测用脚本化的假传输（macOS 上 HttpListener 的 WS 不可用）。</summary>
public interface IWsTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct);
    Task SendAsync(string text, CancellationToken ct);
    /// <summary>收一条完整文本帧；对端关闭返回 null。</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);
    bool IsOpen { get; }
    WebSocketCloseStatus? CloseStatus { get; }
    Task CloseAsync(CancellationToken ct);
    /// <summary>立刻撕掉连接（不等对端 close 帧）：pong 超时 / 对端半死时用它，<see cref="ReceiveAsync"/> 随即返回 null。</summary>
    void Abort();

    /// <summary>
    /// M3 增补：<see cref="ConnectAsync"/> 失败时服务端回的 HTTP 状态码（握手被拒，如 503 TOO_MANY_CLIENTS、401）；
    /// 没有 HTTP 响应（拒绝连接 / 超时）或没有失败为 null。默认实现返回 null（旧的传输实现不用改）。
    /// <see cref="TelemetryWsClient"/> 在释放这条传输之前读它，归类成 <see cref="WsConnectException"/>。
    /// </summary>
    int? HandshakeStatusCode => null;
}

public sealed class ClientWebSocketTransport : IWsTransport
{
    private readonly ClientWebSocket _ws = new();
    private readonly byte[] _buf = new byte[256 * 1024];
    private readonly SemaphoreSlim _send = new(1, 1);

    public bool IsOpen => _ws.State == WebSocketState.Open;
    public WebSocketCloseStatus? CloseStatus => _ws.CloseStatus;

    public Task ConnectAsync(Uri uri, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        foreach (var (k, v) in headers) _ws.Options.SetRequestHeader(k, v);
        _ws.Options.KeepAliveInterval = TimeSpan.Zero;   // 应用层 ping / pong
        _ws.Options.CollectHttpResponseDetails = true;   // M3：握手被拒时保留状态码（503 TOO_MANY_CLIENTS 等，见 HandshakeStatusCode）
        return _ws.ConnectAsync(uri, ct);
    }

    /// <summary>握手被拒时的 HTTP 状态码（<see cref="ClientWebSocket.HttpStatusCode"/>；0 / 101 → null）。</summary>
    public int? HandshakeStatusCode
    {
        get
        {
            int s;
            try { s = (int)_ws.HttpStatusCode; } catch { return null; }
            return s is 0 or 101 ? null : s;
        }
    }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        await _send.WaitAsync(ct).ConfigureAwait(false);
        try { await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false); }
        finally { _send.Release(); }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult r;
            try { r = await _ws.ReceiveAsync(_buf, ct).ConfigureAwait(false); }
            catch (WebSocketException) { return null; }
            catch (ObjectDisposedException) { return null; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }   // Abort() 打断
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(_buf, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    public async Task CloseAsync(CancellationToken ct)
    {
        try
        {
            if (_ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", ct).ConfigureAwait(false);
        }
        catch { }
    }

    public void Abort()
    {
        try { _ws.Abort(); } catch { }
    }

    public ValueTask DisposeAsync()
    {
        _ws.Dispose();
        _send.Dispose();
        return ValueTask.CompletedTask;
    }
}
