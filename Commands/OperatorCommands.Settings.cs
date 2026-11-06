using System.Linq;
using System.Threading.Tasks;
using ParallelReactor.Models;

namespace ParallelReactor.Commands;

/// <summary>设置：数值设置项（<see cref="SettingSpecs"/>）、超压停热开关、压力单位。</summary>
public sealed partial class OperatorCommands
{
    /// <summary>
    /// 改一个数值设置项。范围取自 <see cref="SettingSpecs"/>（越界 → PARAM_OUT_OF_RANGE；本地 +/− 与键盘已限幅，不会触发）。
    /// 普通项直接写 <c>Settings</c>，没有 Toast；写设备的项（搅拌电机电流 / 待机 / 加减速 / 细分、变送器满量程）
    /// 只允许在设备触屏上进行（附录 B），立即写驱动器 / 8AI 换算，提示文字与今天一致。
    /// <para>deferLog = true：设置事件由设置页行的 1.5 s 防抖记录（本地 +/− 连点保持现状）；否则这里立即记录。</para>
    /// <para>严格守卫（§7.11）：交叉校验（温度 SP 上限 &lt; 超温报警、测试压力 &lt; 0.9 × 超压报警）→ PARAM_CROSS_CHECK；
    /// 有过程在运行（通道运行中 / 程序执行中 / 气路序列中）→ SYSTEM_BUSY。</para>
    /// </summary>
    public Task<OpResult> TrySetSetting(CommandSource src, SettingKey key, double value, bool deferLog = false)
    {
        if (Admit(src, ActionClass.Control, SettingSpecs.Of(key).Exposure) is { } deny) return deny.AsTask();
        var spec = SettingSpecs.Of(key);
        if (Invalid(SettingSpecs.Validate(key, value)) is { } bad) return bad.AsTask();
        if (CommandPolicy.Strict(src))
        {
            if (Invalid(SettingSpecs.CrossCheck(_m.Settings, key, value)) is { } cross) return cross.AsTask();
            if (_m.Program.IsRunningProgram || _m.GasBusy || _m.Reactors.Any(r => r.IsWorking))
                return OpResult.Fail(OpCodes.SystemBusy, "有通道或过程正在运行 · 请在全部停止后再修改设置").AsTask();
        }

        double old = spec.Get(_m.Settings);
        if (!deferLog) _m.LogSetting(spec.Label, Fmt(old, spec.Unit), Fmt(value, spec.Unit));
        var data = new { key = key.ToString(), value };
        switch (key)
        {
            case SettingKey.StirCurrent: return StirCurrentTailAsync(value);
            case SettingKey.StirStandbyPct: return StirStandbyTailAsync((int)value);
            case SettingKey.StirMicrostep: return StirMicrostepTailAsync(value);
            case SettingKey.StirRampMs:
                _m.Settings.StirRampMs = value;
                _m._stir.RampMs = (int)value;
                if (_m.StirOn) _ = _m._stir.SetRpmAsync(_m.StirRpm);   // 重新触发速度路径，让新加减速立即生效
                return OpResult.Done($"搅拌加减速时间已设为 {value:0} ms/1000rpm", "ok", data).AsTask();
            case SettingKey.PressFullScaleMPa:
                _m.Settings.PressFullScaleMPa = value;
                _m._press.FullScalePsi = Units.MpaToPsi(value);   // 即时生效到 8AI 换算，压力读数随之修正
                _m.RefreshSchematic();
                return OpResult.Done($"压力变送器满量程已设为 {value:0.###} MPa", "ok", data).AsTask();
            default:
                spec.Set(_m.Settings, value);
                return (OpResult.Silent with { Data = data }).AsTask();
        }
    }

    // —— 写搅拌驱动器（DM2C，存 EEPROM）：驱动器回读到生效值才提示；读不到不提示（与今天一致），远程得到 HARDWARE_WRITE_FAILED ——

    private async Task<OpResult> StirCurrentTailAsync(double amps)
    {
        _m.Settings.StirCurrent = amps;
        var a = await _m._stir.SetCurrentAsync(amps);
        if (a is not { } v) return StirUnconfirmed("峰值电流");
        _m.StirMotorCurrent = v;
        return OpResult.Done($"搅拌电机峰值电流已设为 {v:0.0} A 并保存", "ok", new { key = nameof(SettingKey.StirCurrent), value = v });
    }

    private async Task<OpResult> StirStandbyTailAsync(int pct)
    {
        _m.Settings.StirStandbyPct = pct;
        var p = await _m._stir.SetStandbyPctAsync(pct);
        return p is { } v
            ? OpResult.Done($"搅拌待机电流已设为 {v}% 并保存", "ok", new { key = nameof(SettingKey.StirStandbyPct), value = v })
            : StirUnconfirmed("待机电流");
    }

    private async Task<OpResult> StirMicrostepTailAsync(double ppr)
    {
        _m.Settings.StirMicrostep = ppr;
        var r = await _m._stir.SetMicrostepAsync((int)ppr);
        if (r is not { } n) return StirUnconfirmed("细分");
        if (_m.StirOn) _ = _m._stir.SetRpmAsync(_m.StirRpm);   // 让新细分立即生效
        return OpResult.Done($"细分已设为 {n} 脉冲/转 并保存", "ok", new { key = nameof(SettingKey.StirMicrostep), value = n });
    }

    private static OpResult StirUnconfirmed(string what)
        => OpResult.Fail(OpCodes.HardwareWriteFailed, $"搅拌驱动器{what}写入未确认（DM2C 无回读）") with { Quiet = true };

    /// <summary>「超压时停止加热」开关（只允许在设备触屏上进行）：改动立即落盘（安全相关，不等退出），记设置事件。</summary>
    public Task<OpResult> TrySetStopHeatOnOverPressure(CommandSource src, bool on)
    {
        // 打开 = 安全方向（超压时多一道停加热），按停止类仲裁：远程控制中本地操作员也能打开；关闭仍是控制类
        if (Admit(src, on ? ActionClass.Stop : ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        bool old = _m.Settings.StopHeatOnOverPressure;
        if (old == on) return OpResult.Silent.AsTask();
        _m.Settings.StopHeatOnOverPressure = on;
        _m.LogSetting("超压时停止加热", old ? "开" : "关", on ? "开" : "关");
        _m.PersistSoon();
        return (OpResult.Silent with { Data = new { on } }).AsTask();
    }

    /// <summary>压力单位（MPa / bar / psi，只改显示换算，内部仍存 psi）。记设置事件；没有 Toast。</summary>
    public Task<OpResult> TrySetPressureUnit(CommandSource src, string unit)
    {
        if (Admit(src, ActionClass.Control, Exposure.LocalOnly) is { } deny) return deny.AsTask();
        if (unit is not ("MPa" or "bar" or "psi"))
            return OpResult.Fail(OpCodes.ParamOutOfRange, "压力单位只能是 MPa / bar / psi", "err", new { value = unit }).AsTask();
        string old = Units.Pressure;
        Units.SetPressure(unit);   // MainViewModel 订阅 Units.Changed 后刷新界面
        _m.LogSetting("压力单位", old, Units.Pressure);
        return (OpResult.Silent with { Data = new { unit = Units.Pressure } }).AsTask();
    }
}
