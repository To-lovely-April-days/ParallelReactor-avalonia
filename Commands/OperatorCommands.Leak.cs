using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>泄漏测试：开始 / 中止。测试流程本身在 LeakViewModel.RunAsync（进度与结果是事件，不是命令的返回）。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 开始泄漏测试（惰性气体低压保压）。守卫与今天一致：测试进行中不重复启动；至少选一个通道；
    /// 开始前按当前状态重查所选通道（期间可能有通道开始运行 / 开始执行程序）；气路公共检查（阀门 / 压力采集须在线，
    /// 充压按实测压力闭环）；测试压力须低于超压报警值的 90%。testPsi 覆盖本次测试压力（远程用），同样须满足范围与 90% 限制。
    /// 通过后在后台执行流程，返回 Accepted、不提示。
    /// </summary>
    public Task<OpResult> TryStartLeakTest(CommandSource src, IReadOnlyList<int> channels, double? testPsi = null)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (_m.Leak.Running)
            return (OpResult.Fail(OpCodes.LeakTestRunning, "泄漏测试正在进行 · 请等待完成或先中止", "warn") with { Quiet = true }).AsTask();
        var ids = channels.Distinct().ToList();
        if (ids.Count == 0) return OpResult.Fail(OpCodes.BadRequest, "请至少选择一个测试通道", "warn").AsTask();
        foreach (int id in ids)
            if (ChannelOr(id, out _) is { } nf) return nf.AsTask();
        var nowBusy = ids.Where(id => _m.FindReactor(id) is not { } r || r.IsRunning || _m.Program.IsProgramOwned(id))
                         .Select(id => "RV" + id).ToList();
        if (nowBusy.Count > 0)
            return OpResult.Fail(OpCodes.ChannelBusy,
                $"{string.Join("、", nowBusy)} 已在运行或正在执行程序 · 请重新打开泄漏测试选择通道").AsTask();
        if (GasBlocked(needPressure: true) is { } gb) return gb.AsTask();
        double target = testPsi ?? _m.Settings.LeakTestPsi;
        if (testPsi is { } t && Invalid(SettingSpecs.Validate(SettingKey.LeakTestPsi, t)) is { } bad) return bad.AsTask();
        if (target >= _m.Settings.OverPressure * SettingSpecs.LeakTestMaxRatio)
            return OpResult.Fail(OpCodes.ParamCrossCheck, "测试压力须低于超压报警值的 90% · 请在「设置 → 参数与报警」调整",
                "err", new { testPsi = target, max = _m.Settings.OverPressure * SettingSpecs.LeakTestMaxRatio }).AsTask();

        _ = _m.Leak.RunAsync(ids, target);   // 同步部分已占用气路并锁定面板；之后的进度 / 结果由流程自己提示
        return OpResult.Accepted("", "ok", new { runId = _m.Leak.CurrentRunId, channels = ids, testPsi = target }).AsTask();
    }

    /// <summary>
    /// 中止泄漏测试（序列收到取消后全关阀）。只中止泄漏测试自己（C8）：气路此刻若由程序占用，不受影响。
    /// 没有泄漏测试在进行时 Silent（幂等，今天什么也不做）。严格守卫：气路由其他占用者持有时返回 GAS_OWNED_BY_OTHER。
    /// </summary>
    public Task<OpResult> TryAbortLeakTest(CommandSource src)
    {
        if (Admit(src, ActionClass.Stop) is { } deny) return deny.AsTask();
        if (!_m.GasOwnedBy(GasOwner.Leak))
        {
            if (CommandPolicy.Strict(src) && _m.GasBusy)
                return OpResult.Fail(OpCodes.GasOwnedByOther, $"气路由「{_m.GasTask}」占用 · 当前没有泄漏测试").AsTask();
            return OpResult.Silent.AsTask();
        }
        _m.AbortGas(GasOwner.Leak);
        return (OpResult.Done("泄漏测试已中止 · 正在全关阀", "warn") with { Quiet = true }).AsTask();
    }
}
