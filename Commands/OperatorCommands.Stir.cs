using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>全局搅拌：开停、转速、清驱动器报警。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 开 / 停全局搅拌。开为 Control、停为 Stop。触屏工具栏传入目标方向（!StirOn）。
    /// 现有守卫：开启时急停锁定拒绝。严格守卫：搅拌未配置串口、程序搅拌步骤进行中拒绝开启。
    /// </summary>
    public Task<OpResult> TrySetStir(CommandSource src, bool on)
    {
        if (Admit(src, on ? ActionClass.Control : ActionClass.Stop) is { } deny) return deny.AsTask();
        if (on && EstopOr("急停状态下不可启动搅拌 · 请先复位") is { } es) return es.AsTask();
        if (CommandPolicy.Strict(src) && on)
        {
            if (!Services.HardwareOptions.UseRealStir)
                return OpResult.Fail(OpCodes.HwNotConfigured, "搅拌未配置串口（PR_STIR_PORT），无法启动搅拌").AsTask();
            if (_m.Program.StirStepRv is { } rv)
                return OpResult.Fail(OpCodes.StirProgramOwned, $"RV{rv} 程序搅拌步骤进行中 · 搅拌由程序控制").AsTask();
        }

        string msg = on ? $"搅拌已开启 · 全部反应釜 {_m.StirRpm} rpm" : "搅拌已停止 · 全部反应釜";
        string kind = on ? "ok" : "warn";
        if (_m.StirOn == on) return OpResult.Done(msg, kind).AsTask();   // 幂等：已是目标状态（触屏 Toggle 不会走到这里）

        _m.StirOn = on;   // setter：同步桨叶动画并下发启动 / 停止帧
        _m.LogFileEvent("info", "搅拌", on ? "搅拌开启" : "搅拌停止",
            $"{_m.StirRpm} rpm · {(src.IsRemote ? src.Label : "工具栏")}");
        return OpResult.Done(msg, kind, new { on, rpm = _m.StirRpm }).AsTask();
    }

    /// <summary>
    /// 设全局搅拌转速（工具栏键盘；程序页「全局搅拌」批 C 改走这里）。范围 0..RpmMax（本地键盘已限幅，不会触发）。
    /// 记「设置 · 搅拌转速」事件；搅拌运行中提示「已设为…」，未运行时不提示（与今天一致）。
    /// 严格守卫：程序执行中拒绝（STIR_PROGRAM_OWNED）。
    /// </summary>
    public Task<OpResult> TrySetStirRpm(CommandSource src, int rpm)
    {
        if (Admit(src, ActionClass.Control) is { } deny) return deny.AsTask();
        if (Invalid(ValueRules.Rpm(rpm, _m.Settings.RpmMax)) is { } bad) return bad.AsTask();
        if (CommandPolicy.Strict(src) && _m.Program.AnyProgramRunning)
            return OpResult.Fail(OpCodes.StirProgramOwned, "程序执行中 · 搅拌转速由程序控制").AsTask();

        _m.LogSetting("搅拌转速", $"{_m.StirRpm} rpm", $"{rpm} rpm");
        _m.StirRpm = rpm;   // setter：同步桨叶动画并下发转速
        return (_m.StirOn
            ? OpResult.Done($"搅拌转速已设为 {_m.StirRpm} rpm · 全部反应釜", "ok", new { rpm })
            : OpResult.Silent with { Data = new { rpm } }).AsTask();
    }

    /// <summary>清除搅拌驱动器报警（只允许在设备触屏上进行，附录 B）。清完回读报警码，仍在则 warn 提示。</summary>
    public Task<OpResult> TryClearStirFault(CommandSource src)
    {
        if (Admit(src, ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        return ClearStirFaultTailAsync();
    }

    private async Task<OpResult> ClearStirFaultTailAsync()
    {
        await _m._stir.ClearAlarmAsync();
        await _m.RefreshStirFaultAsync();
        return _m.StirHasFault
            ? OpResult.Done($"报警仍在：{_m.StirFaultText}（过流需排查后才能清）", "warn", new { fault = _m.StirFaultText })
            : OpResult.Done("已清除搅拌电机报警");
    }
}
