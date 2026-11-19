using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>容积标定（吸气量用，气体膨胀法）：开始 / 中止。流程在 CalibViewModel.RunAsync，结果是事件与设置，不是命令的返回。只允许本地：要在设备旁开釜、加水。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 开始一遍容积标定。withWater = false：空釜那一遍；true：加水后的第二遍（须先做完同一通道的空釜一遍），waterMl = 加入的水 mL（1..VolMax）。
    /// 守卫：标定 / 泄漏测试进行中不重复启动；通道存在且没在运行、没有程序；气路公共检查（阀门 / 压力采集在线、气路空闲、无急停……）。
    /// 通过后在后台执行，返回 Accepted、不提示。
    /// </summary>
    public Task<OpResult> TryStartVolumeCalib(CommandSource src, int id, bool withWater, double waterMl)
    {
        if (Admit(src, ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (_m.Calib.Running)
            return (OpResult.Fail(OpCodes.SystemBusy, "容积标定正在进行 · 请等待完成或先中止", "warn") with { Quiet = true }).AsTask();
        if (_m.Leak.Running)
            return OpResult.Fail(OpCodes.LeakTestRunning, "泄漏测试正在进行 · 请等待完成后再标定", "warn").AsTask();
        if (ChannelOr(id, out var c) is { } nf) return nf.AsTask();
        if (c.IsRunning || _m.Program.IsProgramOwned(id))
            return OpResult.Fail(OpCodes.ChannelBusy, $"RV{id} 正在运行或正在执行程序 · 请选一个空闲通道").AsTask();
        if (withWater)
        {
            if (_m.Calib.FirstPassChannel != id)
                return OpResult.Fail(OpCodes.BadRequest, $"RV{id} 还没有做空釜一遍 · 请先开始标定", "warn").AsTask();
            if (Invalid(ValueRules.Range("加水体积", waterMl, 0.5, _m.Settings.VolMax, "mL")) is { } bad) return bad.AsTask();
        }
        if (GasBlocked(needPressure: true) is { } gb) return gb.AsTask();

        _ = _m.Calib.RunAsync(id, withWater, waterMl, src.Label);   // 同步部分已占用气路；之后的进度 / 结果由流程自己提示（发起人给快照 calib.startedBy）
        return OpResult.Accepted("", "ok", new { channel = id, withWater, waterMl }).AsTask();
    }

    /// <summary>中止容积标定（序列收到取消后全关阀）。只中止标定自己；没有标定在进行时 Silent。</summary>
    public Task<OpResult> TryAbortVolumeCalib(CommandSource src)
    {
        if (Admit(src, ActionClass.Stop, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (!_m.GasOwnedBy(GasOwner.Calib)) return OpResult.Silent.AsTask();
        _m.AbortGas(GasOwner.Calib);
        return (OpResult.Done("容积标定已中止 · 正在全关阀", "warn") with { Quiet = true }).AsTask();
    }
}
