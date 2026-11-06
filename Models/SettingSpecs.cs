using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Commands;

namespace ParallelReactor.Models;

/// <summary>设置页「参数与报警」的数值项（与 <see cref="AppSettings"/> 同名属性一一对应）。</summary>
public enum SettingKey
{
    // 可设置参数范围
    TMaxSp, PMaxSp, RpmMax, VolMax,
    // 报警阈值
    OverPressure, OverTemp, HeatTimeout, PressDeviation, LeakRate, LeakReminderDays,
    // 泄漏测试
    LeakTestPsi, LeakSettleMin, LeakMeasureMin,
    // 定值加热防超调
    ApproachDelta, ApproachFastRate, ApproachFinalZone, ApproachRate, ApproachSettleBand, ApproachSettleSec,
    // 搅拌电机（写驱动器 / EEPROM）
    StirCurrent, StirStandbyPct, StirRampMs, StirMicrostep,
    // 压力变送器（改 8AI 换算）
    PressFullScaleMPa,
}

/// <summary>
/// 一个设置项：标签、单位、范围、步进、说明，是否对外（<see cref="Exposure"/>），是否写设备，以及在 AppSettings 上的读写。
/// 界面行（SettingViewModel.Seed）和命令层（TrySetSetting）的范围都取自这里，只有一个来源。
/// </summary>
public sealed record SettingSpec(
    SettingKey Key, string Label, string Unit, double Min, double Max, double Step, string? Sub,
    Exposure Exposure, bool WritesDevice,
    Func<AppSettings, double> Get, Action<AppSettings, double> Set);

/// <summary>设置项表 + 校验（批 C · C5）。纯逻辑：测试工程直接链接本文件。</summary>
public static class SettingSpecs
{
    private static SettingSpec S(SettingKey k, string label, string unit, double min, double max, double step,
        Func<AppSettings, double> get, Action<AppSettings, double> set, string? sub = null, bool device = false)
        => new(k, label, unit, min, max, step, sub, device ? Exposure.LocalOnly : Exposure.Remote, device, get, set);

    /// <summary>全部设置项（顺序即设置页显示顺序）。</summary>
    public static readonly IReadOnlyList<SettingSpec> All = new[]
    {
        S(SettingKey.TMaxSp, "温度 SP 上限", "°C", 50, 300, 5, s => s.TMaxSp, (s, v) => s.TMaxSp = v),
        S(SettingKey.PMaxSp, "压力 SP 上限", "psi", 100, 1000, 10, s => s.PMaxSp, (s, v) => s.PMaxSp = v),
        S(SettingKey.RpmMax, "搅拌转速上限", "rpm", 500, 3000, 100, s => s.RpmMax, (s, v) => s.RpmMax = v),
        S(SettingKey.VolMax, "溶液体积上限", "mL", 5, 200, 5, s => s.VolMax, (s, v) => s.VolMax = v),

        S(SettingKey.OverPressure, "超压报警", "psi", 100, 1000, 5, s => s.OverPressure, (s, v) => s.OverPressure = v,
            "压力 ≥ 此值触发硬报警"),
        S(SettingKey.OverTemp, "超温报警", "°C", 50, 400, 5, s => s.OverTemp, (s, v) => s.OverTemp = v,
            "温度 ≥ 此值触发报警"),
        S(SettingKey.HeatTimeout, "升温超时报警", "分钟", 5, 60, 1, s => s.HeatTimeout, (s, v) => s.HeatTimeout = v,
            "升温超过此时长未到 SP 时提示"),
        S(SettingKey.PressDeviation, "压力偏离 SP 报警", "psi", 1, 50, 1, s => s.PressDeviation, (s, v) => s.PressDeviation = v,
            "稳压后偏离超过此值时提示"),
        S(SettingKey.LeakRate, "泄漏率阈值", "psi/hr", 0.5, 10, 0.5, s => s.LeakRate, (s, v) => s.LeakRate = v,
            "泄漏测试判定不通过的阈值"),
        S(SettingKey.LeakReminderDays, "泄漏测试提醒周期", "天", 1, 30, 1, s => s.LeakReminderDays, (s, v) => s.LeakReminderDays = v,
            "超过此天数未测试则提醒"),

        S(SettingKey.LeakTestPsi, "测试压力", "psi", 10, 150, 5, s => s.LeakTestPsi, (s, v) => s.LeakTestPsi = v,
            "低压即可检出密封问题；须低于超压报警值的 90%，惰性气体减压阀调到略高于此值"),
        S(SettingKey.LeakSettleMin, "平衡时间", "分钟", 1, 30, 1, s => s.LeakSettleMin, (s, v) => s.LeakSettleMin = v,
            "充压后静置，等气体温度回稳再开始测量"),
        S(SettingKey.LeakMeasureMin, "测量时间", "分钟", 5, 120, 5, s => s.LeakMeasureMin, (s, v) => s.LeakMeasureMin = v,
            "越长越能分辨小泄漏；压降速率按温度补偿后的最小二乘拟合计算"),

        S(SettingKey.ApproachDelta, "预留温差 Δ", "°C", 0, 30, 0.5, s => s.ApproachDelta, (s, v) => s.ApproachDelta = v,
            "须大于实测最大冲温（建议取 1.5 倍）；0 = 关闭逼近、直接写目标"),
        S(SettingKey.ApproachFastRate, "快段速率", "°C/min", 0.1, 10, 0.1, s => s.ApproachFastRate, (s, v) => s.ApproachFastRate = v,
            "离目标大于末段范围时使用；PV 领先时 SP 直接跟随，不暂停"),
        S(SettingKey.ApproachFinalZone, "末段范围", "°C", 0.3, 5, 0.1, s => s.ApproachFinalZone, (s, v) => s.ApproachFinalZone = v,
            "只有最后这段慢推，PV 偏高才暂停；建议略大于实测冲温"),
        S(SettingKey.ApproachRate, "末段速率", "°C/min", 0.02, 2, 0.02, s => s.ApproachRate, (s, v) => s.ApproachRate = v,
            "防超调的关键：越慢越稳、越省不了时间"),
        S(SettingKey.ApproachSettleBand, "判稳范围", "±°C", 0.1, 2, 0.1, s => s.ApproachSettleBand, (s, v) => s.ApproachSettleBand = v,
            "PV 到达 中间SP−此值 后开始判峰；持续落在 ±此值 内也可开始逼近"),
        S(SettingKey.ApproachSettleSec, "判稳时长", "s", 10, 900, 10, s => s.ApproachSettleSec, (s, v) => s.ApproachSettleSec = v,
            "兜底条件：PV 过峰（不再上升）会提前开始逼近，不必等满此时长"),

        S(SettingKey.StirCurrent, "峰值电流", "A", 0.3, 3.2, 0.1, s => s.StirCurrent, (s, v) => s.StirCurrent = v,
            "按电机铭牌额定电流设：带负载卡顿调大、发烫调小（驱动器硬限 0.3–3.2A）", device: true),
        S(SettingKey.StirStandbyPct, "待机电流", "%", 0, 100, 5, s => s.StirStandbyPct, (s, v) => s.StirStandbyPct = v,
            "停转后自动降到此百分比，减少电机发热", device: true),
        S(SettingKey.StirRampMs, "加减速时间", "ms/1000rpm", 50, 10000, 50, s => s.StirRampMs, (s, v) => s.StirRampMs = v,
            "起停平缓度：越大越缓。值小起停顿挫/像“起飞”，值大更柔和（1000≈300rpm 用 0.3s 升起）", device: true),
        S(SettingKey.StirMicrostep, "细分", "脉冲/转", 200, 51200, 200, s => s.StirMicrostep, (s, v) => s.StirMicrostep = v,
            "指令脉冲数/转：越高低速越平顺、越能压共振。常用 10000 / 20000 / 25600", device: true),

        S(SettingKey.PressFullScaleMPa, "变送器满量程", "MPa", 0.1, 60, 0.1, s => s.PressFullScaleMPa, (s, v) => s.PressFullScaleMPa = v,
            "必须与变送器铭牌量程一致（如 0–10MPa 填 10），否则压力读数按比例偏", device: true),
    };

    private static readonly Dictionary<SettingKey, SettingSpec> ByKey = All.ToDictionary(s => s.Key);

    public static SettingSpec Of(SettingKey key) => ByKey[key];

    /// <summary>范围检查（越界 / NaN / ∞ → PARAM_OUT_OF_RANGE，消息带范围）。</summary>
    public static RuleViolation? Validate(SettingKey key, double value)
    {
        var s = Of(key);
        return ValueRules.Range(s.Label, value, s.Min, s.Max, s.Unit);
    }

    /// <summary>泄漏测试压力须低于超压报警值的这个比例（与 LeakViewModel / 泄漏测试启动守卫一致）。</summary>
    public const double LeakTestMaxRatio = 0.9;

    /// <summary>
    /// 交叉校验（严格守卫，§7.11）：把 key 改成 value 之后，温度 SP 上限须低于超温报警值，
    /// 泄漏测试压力须低于超压报警值的 90%。违反 → PARAM_CROSS_CHECK。
    /// </summary>
    public static RuleViolation? CrossCheck(AppSettings current, SettingKey key, double value)
    {
        double V(SettingKey k, Func<AppSettings, double> get) => k == key ? value : get(current);
        double tMax = V(SettingKey.TMaxSp, s => s.TMaxSp), overT = V(SettingKey.OverTemp, s => s.OverTemp);
        if (tMax >= overT)
            return new RuleViolation(OpCodes.ParamCrossCheck, "温度 SP 上限须低于超温报警值", double.NegativeInfinity, overT, tMax);
        double leak = V(SettingKey.LeakTestPsi, s => s.LeakTestPsi), overP = V(SettingKey.OverPressure, s => s.OverPressure);
        if (leak >= overP * LeakTestMaxRatio)
            return new RuleViolation(OpCodes.ParamCrossCheck, "测试压力须低于超压报警值的 90%",
                double.NegativeInfinity, overP * LeakTestMaxRatio, leak);
        return null;
    }
}
