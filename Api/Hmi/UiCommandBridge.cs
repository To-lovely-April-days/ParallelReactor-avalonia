using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Commands;

namespace ParallelReactor.Api.Hmi;

/// <summary>
/// 接口层进 UI 线程的<b>唯一</b>入口（设计稿 §13.1；架构测试 ApiArchitectureTests 只允许本文件与 ControlBridge 使用 Dispatcher）。
/// <list type="number">
/// <item><see cref="InvokeAsync{T}"/>：/preflight 之类的只读计算——5 s 内未开始执行则 <c>Abort()</c> 并抛 <see cref="UiThreadTimeoutException"/>；已开始再等 5 s 仍无结果同样抛（不无限等）。</item>
/// <item><see cref="RunAsync"/>：写命令——三种结局（<see cref="OutcomeKind"/>）：UI 线程 5 s 未响应且撤销成功 → UiThreadTimeout（确定未执行）；
/// 已开始但再等 5 s 未返回 → OutcomeUnknown（内层继续跑，<see cref="CommandOutcome.Pending"/> 回填）；同步头已返回而硬件尾部 2 s 未完成 → HwUnconfirmed。</item>
/// </list>
/// 控制类命令经全局 <see cref="SemaphoreSlim"/>(1) 串行进入 UI 线程（排队顺序不依赖 Dispatcher 调度）；停止类 / 记录类绕过信号量并以 Normal 优先级投递，
/// 控制类用 Background 优先级（不抢 Tick）。信号量在同步头返回后就释放，不等硬件尾部。
/// <para>取消：<c>ct</c>（客户端断线）只在<b>投递前</b>（排队进信号量）生效；一旦操作已投递到 UI 线程就不再观察它——命令照常执行到底，
/// 调用方（Endpoints.CommandAsync）照常审计、推 opEvent、回填幂等表（§5.4「断线时命令执行完并记入 EventStore」、§13.3）。</para>
/// </summary>
public static class UiCommandBridge
{
    public static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan HwWait = TimeSpan.FromSeconds(2);
    private static readonly SemaphoreSlim ControlGate = new(1, 1);

    /// <summary>只读计算（/preflight）。</summary>
    public static async Task<T> InvokeAsync<T>(Func<T> f, CancellationToken ct, DispatcherPriority? priority = null, TimeSpan? timeout = null)
    {
        var op = Dispatcher.UIThread.InvokeAsync(f, priority ?? DispatcherPriority.Background);
        var (status, _) = await WaitAsync(op.GetTask(), op, timeout ?? UiTimeout, ct).ConfigureAwait(false);
        if (status != Ui.Done) throw new UiThreadTimeoutException();
        return await op.GetTask().ConfigureAwait(false);
    }

    /// <summary>
    /// 写命令：onUi 在 UI 线程上调用一个 Try*（经 <see cref="OpResult.Guard"/>，同步头异常转 INTERNAL）。
    /// control = true 时经全局信号量并以 Background 优先级投递；否则（停止 / 记录类）直接以 Normal 优先级投递。
    /// </summary>
    public static async Task<CommandOutcome> RunAsync(Func<Task<OpResult>> onUi, bool control, CancellationToken ct, TimeSpan? uiTimeout = null, TimeSpan? hwWait = null)
    {
        bool gated = false;
        if (control)
        {
            // 排队进入 UI 线程最多也等一个超时：排不进去按「未执行」处理，客户端可重试
            if (!await ControlGate.WaitAsync(uiTimeout ?? UiTimeout, ct).ConfigureAwait(false)) return CommandOutcome.UiTimeout;
            gated = true;
        }
        try
        {
            // 用返回 object 的委托拿到 DispatcherOperation（Func<Task<T>> 的重载会自动解包成 Task<T>，拿不到可 Abort 的操作句柄）
            // 投递之后不再观察 ct（见类说明）：断线不能让已投递的命令「执行了却没人记账」
            var op = Dispatcher.UIThread.InvokeAsync<object>(() => OpResult.Guard(onUi), control ? DispatcherPriority.Background : DispatcherPriority.Normal);
            var (status, _) = await WaitAsync(op.GetTask(), op, uiTimeout ?? UiTimeout, CancellationToken.None).ConfigureAwait(false);
            if (status == Ui.Aborted) return CommandOutcome.UiTimeout;   // 已撤销：op.GetTask() 以取消结束，这里不再碰它
            var outer = Outer(op);   // Task<Task<OpResult>>：外层 = 同步头是否已跑完；内层 = 尾部
            if (status == Ui.Stuck) return CommandOutcome.Unknown(Unwrap(outer));
            if (gated) { ControlGate.Release(); gated = false; }   // 同步头已返回：下一条控制命令可以进 UI 线程了

            var tail = await Unwrap2(outer).ConfigureAwait(false);
            if (tail.IsCompleted) return CommandOutcome.Done(await Settle(tail).ConfigureAwait(false));
            var done = await Task.WhenAny(tail, Task.Delay(hwWait ?? HwWait)).ConfigureAwait(false);
            if (done == tail) return CommandOutcome.Done(await Settle(tail).ConfigureAwait(false));
            return CommandOutcome.Unconfirmed(Settle(tail));
        }
        finally { if (gated) ControlGate.Release(); }
    }

    private enum Ui { Done, Aborted, Stuck }

    private static async Task<Task<OpResult>> Outer(DispatcherOperation<object> op) => (Task<OpResult>)await op.GetTask().ConfigureAwait(false);

    /// <summary>等 UI 线程：超时且仍 Pending → Abort；已开始执行则再等一个超时。</summary>
    private static async Task<(Ui, bool)> WaitAsync(Task task, DispatcherOperation op, TimeSpan timeout, CancellationToken ct)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (done == task) return (Ui.Done, true);
        ct.ThrowIfCancellationRequested();
        // 还没开始执行：撤销，确定未执行（§13.1）；已开始执行则再等一个超时，仍没回来按 UI 线程卡住处理（不无限等）
        if (op.Status == DispatcherOperationStatus.Pending && op.Abort()) return (Ui.Aborted, false);
        done = await Task.WhenAny(task, Task.Delay(timeout, ct)).ConfigureAwait(false);
        if (done == task) return (Ui.Done, true);
        ct.ThrowIfCancellationRequested();
        return (Ui.Stuck, false);
    }

    /// <summary>外层（同步头）异常 → 已完成的 INTERNAL 内层任务（Guard 已包了一层，这里兜框架异常）。</summary>
    private static async Task<Task<OpResult>> Unwrap2(Task<Task<OpResult>> outer)
    {
        try { return await outer.ConfigureAwait(false); }
        catch (Exception ex) { return Task.FromResult(OpResult.InternalOf(ex)); }
    }

    private static async Task<OpResult> Unwrap(Task<Task<OpResult>> outer)
    {
        try { return await Settle(await outer.ConfigureAwait(false)).ConfigureAwait(false); }
        catch (Exception ex) { return OpResult.InternalOf(ex); }
    }

    /// <summary>尾部异常 → INTERNAL（Guard 已包了一层，这里兜 Abort 之类的框架异常）。</summary>
    private static async Task<OpResult> Settle(Task<OpResult> t)
    {
        try { return await t.ConfigureAwait(false); }
        catch (Exception ex) { return OpResult.InternalOf(ex); }
    }
}
