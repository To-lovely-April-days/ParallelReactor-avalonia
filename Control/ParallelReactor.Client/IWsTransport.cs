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
        return _ws.ConnectAsync(uri, ct);
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
