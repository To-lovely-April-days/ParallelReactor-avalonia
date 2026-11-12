using System;
using Avalonia.Threading;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 全局唯一的 1 s 定时器（PORT-PLAN §5 M2「UiTicker」、R10：动画与刷新共用一个计时器）：倒计时（控制权申请 / 宽限）、状态栏时钟、
/// 演示数据每秒推进、应用实时源攒下的新帧，都挂在 <see cref="Tick"/> 上。UI 线程上触发（DispatcherTimer，Background 优先级，不抢输入）。
/// <para>冻结（<see cref="Frozen"/>，演示截图 freeze=1）：照常触发（界面可以重绘），但参数 advance = false —— 订阅方不推进任何时间
///   （演示时钟不走、倒计时不减）。</para>
/// </summary>
public sealed class UiTicker : IDisposable
{
    private readonly DispatcherTimer _timer;

    public UiTicker(TimeSpan? interval = null)
    {
        _timer = new DispatcherTimer(interval ?? TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Fire());
    }

    /// <summary>冻结：照常触发，但 advance = false。</summary>
    public bool Frozen { get; set; }

    /// <summary>每秒一次；参数 advance = 是否推进时间（冻结时 false）。</summary>
    public event Action<bool>? Tick;

    public bool IsRunning => _timer.IsEnabled;

    public void Start() => _timer.Start();

    public void Stop() => _timer.Stop();

    /// <summary>立即触发一次（截图、测试）。</summary>
    public void Fire() => Tick?.Invoke(!Frozen);

    public void Dispose()
    {
        _timer.Stop();
        Tick = null;
    }
}
