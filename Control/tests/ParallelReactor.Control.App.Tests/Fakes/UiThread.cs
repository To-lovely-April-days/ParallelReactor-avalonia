using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelReactor.Control.Tests.Fakes;

/// <summary>
/// 测试用的「UI 线程」：一条专用线程 + 只往它投递的同步上下文（同 App 的 AvaloniaSynchronizationContext）。
/// 数据源、FleetVm 的 post 和真 ReactorConnection 的 UiContext 都给它：Client 的事件、监督循环的续体、合并后的刷新全部在这一条线程上按顺序执行，
/// 测试从自己的线程经 <see cref="Invoke{T}(Func{T})"/> 读写（不与 UI 线程竞争）。
/// </summary>
internal sealed class UiThread : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Cb, object? State)> _q = new();
    private readonly Thread _thread;
    private readonly List<Exception> _errors = new();

    public UiThread()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            SetSynchronizationContext(this);
            ready.Set();
            foreach (var (cb, state) in _q.GetConsumingEnumerable())
            {
                try { cb(state); }
                catch (Exception ex) { lock (_errors) _errors.Add(ex); }
            }
        }) { IsBackground = true, Name = "测试 UI 线程" };
        _thread.Start();
        ready.Wait();
    }

    public int ThreadId => _thread.ManagedThreadId;

    /// <summary>现在是不是在这条 UI 线程上。</summary>
    public bool IsCurrent => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

    /// <summary>UI 线程上漏出来的异常（应当为空）。</summary>
    public Exception[] Errors { get { lock (_errors) return _errors.ToArray(); } }

    public override void Post(SendOrPostCallback d, object? state)
    {
        try { _q.Add((d, state)); } catch (InvalidOperationException) { }   // 已释放
    }

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("测试 UI 线程只支持 Post");

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>数据源 / FleetVm 的 post。</summary>
    public void Post(Action a) => Post(_ => a(), null);

    public Task<T> Invoke<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => { try { tcs.SetResult(f()); } catch (Exception e) { tcs.SetException(e); } }, null);
        return tcs.Task;
    }

    public Task Invoke(Action a) => Invoke(() => { a(); return 0; });

    /// <summary>在 UI 线程上跑一个异步操作（续体回到 UI 线程），等它完成。</summary>
    public Task<T> InvokeAsync<T>(Func<Task<T>> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(async _ => { try { tcs.SetResult(await f()); } catch (Exception e) { tcs.SetException(e); } }, null);
        return tcs.Task;
    }

    public Task InvokeAsync(Func<Task> f) => InvokeAsync(async () => { await f(); return 0; });

    /// <summary>把 UI 线程堵住，直到返回的对象被释放（证明一批事件只刷新一次：先攒、后放）。</summary>
    public IDisposable Block()
    {
        var gate = new ManualResetEventSlim();
        var entered = new ManualResetEventSlim();
        Post(_ => { entered.Set(); gate.Wait(TimeSpan.FromSeconds(10)); }, null);
        entered.Wait(TimeSpan.FromSeconds(5));
        return new Release(gate);
    }

    private sealed class Release : IDisposable
    {
        private readonly ManualResetEventSlim _gate;
        public Release(ManualResetEventSlim gate) => _gate = gate;
        public void Dispose() => _gate.Set();
    }

    public void Dispose()
    {
        _q.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(3));
    }
}
