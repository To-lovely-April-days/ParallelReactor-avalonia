using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Client;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 持有当前 <see cref="ReactorConnection"/>：连接 / 断开；所有回调经 <see cref="AvaloniaSynchronizationContext"/> 投递到 UI 线程。
/// API 调用本身在线程池上跑（库内部 ConfigureAwait(false)）。
/// </summary>
public sealed class ConnectionService
{
    public IReactorConnection? Current { get; private set; }
    public bool IsConnected => Current != null;

    public event Action<IReactorConnection?>? CurrentChanged;

    public async Task<IReactorConnection> ConnectAsync(string baseUrl, string token, string clientName, CancellationToken ct)
    {
        await DisconnectAsync().ConfigureAwait(true);
        var opt = new ClientOptions(new Uri(baseUrl), token, clientName)
        {
            UiContext = new AvaloniaSynchronizationContext(DispatcherPriority.Normal),
            ClientVersion = "pc-" + (typeof(ConnectionService).Assembly.GetName().Version?.ToString(3) ?? "1.0"),
        };
        var conn = new ReactorConnection(opt);
        try
        {
            await conn.StartAsync(ct).ConfigureAwait(true);
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(true);
            throw;
        }
        Current = conn;
        CurrentChanged?.Invoke(conn);
        return conn;
    }

    /// <summary>释放租约（≤ 3 s）并 Dispose。可在任意线程调用：CurrentChanged 经 UI 线程派发，其余不回 UI 上下文（退出路径上 UI 线程可能在等我们）。</summary>
    public async Task DisconnectAsync()
    {
        var c = Current;
        if (c == null) return;
        Current = null;
        if (Dispatcher.UIThread.CheckAccess()) CurrentChanged?.Invoke(null); else Dispatcher.UIThread.Post(() => CurrentChanged?.Invoke(null));
        try
        {
            if (c.Lease.State == LeaseState.Holding)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try { await c.Lease.ReleaseAsync(cts.Token).ConfigureAwait(false); } catch { }
            }
            await c.DisposeAsync().ConfigureAwait(false);
        }
        catch { }
    }
}
