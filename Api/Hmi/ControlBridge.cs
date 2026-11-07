using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Api.Hmi;

/// <summary>
/// 控制权的 HMI 侧桥（设计稿 §5.4 / §5.5）：线程池 <see cref="PeriodicTimer"/> 每秒推进租约超时（不依赖 UI 线程——UI 卡死时心跳丢失仍能判定），
/// 并执行 <c>onRemoteLost</c>：keep → 事件 + 触屏提示；estop → <c>Ops.TryEstop</c>（任意线程快速路径，来源「心跳丢失」）；
/// stopHeat → 经 <see cref="UiCommandBridge"/> 对该租约启动的程序 <c>TryAbortProgram</c>、通道 <c>TryStopChannel</c>（按 StartedBy 标签，不动其他来源）。
/// 这些调用都经命令层 Ops.Try*（不在 ViewModels/ 里直接写设备，R6）。本文件与 UiCommandBridge 是 Api/Hmi 下仅有的两个允许用 Dispatcher 的文件。
/// </summary>
public sealed class ControlBridge : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly ApiHost _api;
    private readonly CancellationTokenSource _cts = new();

    public ControlBridge(MainViewModel vm, ApiHost api)
    {
        _vm = vm;
        _api = api;
        api.Control.Lost += OnLost;
        api.Control.RemoteLost += OnRemoteLost;
        _ = TickLoopAsync();
    }

    private async Task TickLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                try { _api.Control.Tick(MainViewModel.MonoNow); }
                catch (Exception ex) { try { _api.Sources.Audit("err", "租约计时异常", ex.GetBaseException().Message); } catch { } }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnLost(string client)
        => Dispatcher.UIThread.Post(() => _vm.Toast("warn", $"远程连接丢失（{client}）· 设备继续运行 · 宽限 {_api.Config.LeaseGraceSec} s"));

    /// <summary>宽限期满（租约已作废、仲裁器已回本地）：按策略处理。来源标签与该租约启动过程时写的 StartedBy 相同。</summary>
    private void OnRemoteLost(string policy, string tokenName, string clientName)
    {
        var src = CommandSource.Remote(tokenName, clientName);
        switch (policy)
        {
            case "estop":
                // 任意线程快速路径；急停本身会再次 ReturnToLocal（已是本地，无副作用）
                _ = _vm.Ops.TryEstop(src, "心跳丢失（onRemoteLost = estop）");
                Dispatcher.UIThread.Post(() => _vm.Toast("err", $"远程 {clientName} 心跳丢失 · 已按设置触发急停"));
                break;
            case "stopHeat":
                _ = StopHeatAsync(src);
                break;
            default:
                Dispatcher.UIThread.Post(() => _vm.Toast("warn", $"远程 {clientName} 宽限期满 · 已回到本地控制 · 设备继续运行（onRemoteLost = keep）"));
                break;
        }
    }

    /// <summary>stopHeat：只停由该租约启动的过程——程序（ProgramRunInfo.StartedBy）先中止，通道（Reactor.StartedBy）再停止（转 Done、停曲线 / 逼近、At=4、关 SV）。</summary>
    private async Task StopHeatAsync(CommandSource src)
    {
        string label = OperatorCommands.StartedByOf(src);
        var outcome = await UiCommandBridge.RunAsync(async () =>
        {
            var ops = _vm.Ops;
            var programs = _vm.Program.RunInfos.Where(kv => kv.Value.IsActive && kv.Value.StartedBy == label).Select(kv => kv.Key).ToList();
            foreach (int rv in programs) await ops.TryAbortProgram(src, rv);
            var channels = _vm.Reactors.Where(r => r.StartedBy == label && r.State != ReactorState.Idle).Select(r => r.Id).ToList();
            var results = new System.Collections.Generic.List<OpResult>();
            foreach (int id in channels) results.Add(await ops.TryStopChannel(src, id, disable: false));
            _vm.Alarm.Log("warn", "系统", "远程心跳丢失 · 已停止该客户端启动的过程",
                $"{src.ClientName} · onRemoteLost = stopHeat · 中止程序 {(programs.Count == 0 ? "无" : string.Join("、", programs.Select(x => "RV" + x)))} · 停止通道 {(channels.Count == 0 ? "无" : string.Join("、", channels.Select(x => "RV" + x)))}");
            _vm.Toast("warn", $"远程 {src.ClientName} 心跳丢失 · 已停止其启动的 {channels.Count} 个通道 / {programs.Count} 个程序");
            var failed = results.FirstOrDefault(r => !r.Ok);
            return failed ?? OpResult.Done("", "ok", new { programs, channels });
        }, control: false, CancellationToken.None);
        if (outcome.Kind != OutcomeKind.Completed || outcome.Result is { Ok: false })
            _api.Sources.Audit("err", "onRemoteLost = stopHeat 执行未完全成功", outcome.Result?.Message ?? outcome.Kind.ToString());
    }

    public void Dispose()
    {
        _cts.Cancel();
        _api.Control.Lost -= OnLost;
        _api.Control.RemoteLost -= OnRemoteLost;
    }
}
