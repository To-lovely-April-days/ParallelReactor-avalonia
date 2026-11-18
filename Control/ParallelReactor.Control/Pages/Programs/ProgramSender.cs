using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Pages.Programs;

/// <summary>下发一路：通道、程序（这一路的参数）、溶液体积 mL。</summary>
public sealed record SendItem(int N, PcProgram Program, double VolumeMl);

/// <summary>一次下发的结果：成功启动的各路、没成功的（通道 + 原因）。</summary>
public sealed record SendResult(IReadOnlyList<int> Started, IReadOnlyList<(int N, string Why)> Failed);

/// <summary>
/// 下发并运行（融合版 F6，程序页两种模式共用）：每一路依次
/// <list type="number">
/// <item>溶液体积（设备上的值不同时：DeviceAction.SetVolume → 真机 PUT /channels/{n}/volume，见 Client 的 SetVolumeAsync）—— 先于程序，程序跑起来以后 HMI 不让改；</item>
/// <item>程序（真机：LiveDeviceSource.UploadProgramAsync = PUT /channels/{n}/program 并记进程序台账，libraryId = 程序库的 id；演示：DemoFleet.LoadProgram）；</item>
/// <item>运行（DeviceAction.ProgRun → POST /channels/{n}/program/run）。</item>
/// </list>
/// 每一步之前都按守卫再查一次（控制权、急停、程序没在运行）；某一路失败不影响其它路，最后汇总成一条提示。全部静默执行（quiet），提示由调用方给。
/// </summary>
public static class ProgramSender
{
    public static async Task<SendResult> SendAsync(DeviceVm d, IReadOnlyList<SendItem> items, CancellationToken ct = default)
    {
        var ok = new List<int>();
        var bad = new List<(int, string)>();
        foreach (var it in items)
        {
            string why = d.Actions.Why(DeviceAction.ProgLoad, ActionPayload.Ch(it.N));
            if (why.Length > 0) { bad.Add((it.N, why)); continue; }
            try
            {
                // 1 溶液体积（0.1 mL；设备上已经是这个值就不发）
                double ml = Math.Round(it.VolumeMl, 1, MidpointRounding.AwayFromZero);
                var cur = d.Reactor(it.N)?.SolutionMl;
                if (cur is not double c || Math.Abs(c - ml) >= 0.05)
                {
                    var v = await d.Actions.RunAsync(DeviceAction.SetVolume, new ActionPayload(N: it.N, Ml: ml), quiet: true, ct).ConfigureAwait(true);
                    if (!v.Ok) { bad.Add((it.N, v.Message)); continue; }
                }
                // 2 程序
                var load = await LoadAsync(d, it, ct).ConfigureAwait(true);
                if (!load.Ok) { bad.Add((it.N, load.Message)); continue; }
                // 3 运行
                var run = await d.Actions.RunAsync(DeviceAction.ProgRun, ActionPayload.Ch(it.N), quiet: true, ct).ConfigureAwait(true);
                if (!run.Ok) { bad.Add((it.N, run.Message)); continue; }
                ok.Add(it.N);
            }
            catch (Exception ex)
            {
                bad.Add((it.N, Fleet.DeviceActions.Describe(ex)));
            }
        }
        d.Fleet.RequestRefresh();
        return new SendResult(ok, bad);
    }

    /// <summary>放一个程序到第 n 路（已下发 · 待运行）。</summary>
    private static async Task<ActionOutcome> LoadAsync(DeviceVm d, SendItem it, CancellationToken ct)
    {
        switch (d.Source)
        {
            case DemoDeviceSource demo:
            {
                var (ok, msg) = demo.Fleet.LoadProgram(demo.Device, it.N, it.Program.ToDef(d.Model));
                return ok ? ActionOutcome.Done(msg) : ActionOutcome.Failed(msg);
            }
            case LiveDeviceSource live:
                return await live.UploadProgramAsync(it.N, it.Program.ToDoc(), ct, libraryId: it.Program.Id).ConfigureAwait(true);
            default:
                return ActionOutcome.Failed(d.Alias + " 不支持下发程序");
        }
    }

    /// <summary>汇总提示：「已下发「平行合成程序 A」到 1号机 RV5 · 开始运行」；有失败的另写「RV3 没下发：原因」。</summary>
    public static (string Text, ToastKind Kind) Summary(DeviceVm d, string name, SendResult r)
    {
        string ok = r.Started.Count > 0 ? "已下发「" + name + "」到 " + d.Alias + " " + MatrixPlan.ChList(r.Started) + " · 开始运行" : "";
        string bad = r.Failed.Count > 0 ? string.Join("；", r.Failed.GroupBy(x => x.Why).Select(g => MatrixPlan.ChList(g.Select(x => x.N)) + " 没有下发：" + g.Key)) : "";
        if (bad.Length == 0) return (ok, ToastKind.Ok);
        return ((ok.Length > 0 ? ok + " · " : "") + bad, r.Started.Count > 0 ? ToastKind.Warn : ToastKind.Err);
    }
}
