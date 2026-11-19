using System;
using System.Collections.Generic;
using System.Globalization;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>
/// 推荐模板（设计稿 research/program-steps-design.html「五、推荐模板」，用户 2026-11-19 批准；没有降温步骤）：
/// <list type="bullet">
/// <item><see cref="Hydrogenation"/>：加氢筛选（默认模板，「新建程序」用它）—— 气密 3.0 MPa → 惰性置换 ×3 → 气体 A 置换 ×3 → 搅拌 400 → 升温 80 ℃ →
///   充压气体 A 2.0 MPa 稳压 → 反应 8 h 吸气停止 → 放空并惰性置换 ×3 → 结束。</item>
/// <item><see cref="LeakOnly"/>：只做气密（气密 3.0 MPa 保压 30 min 之后放空 → 结束）。</item>
/// <item><see cref="InertHeating"/>：惰性气氛加热（惰性置换 ×3 → 搅拌 400 → 升温 120 ℃ → 惰性充压 0.5 MPa 稳压 → 保持 4 h → 放空 → 结束）。</item>
/// </list>
/// 压力是本机的 MPa（下发时换算 psi）；其余参数取 STEPS-SPEC §1 的默认值（<see cref="StepDefaults.Fill"/>）。
/// </summary>
public static class ProgramTemplates
{
    /// <summary>一步：类型、名字、参数（字符串 = 选项码，数值 = 本机单位：压力 MPa）。缺的按默认值补。</summary>
    public static PcStep Step(StepType t, string? name, params (string K, object V)[] ps)
    {
        var s = new PcStep(t, name ?? t.Zh());
        foreach (var (k, v) in ps)
        {
            if (v is string str) s.Sel[k] = str;
            else s.Num[k] = Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        return StepDefaults.Fill(s);
    }

    public static PcStep Leak(double mpa = 3.0, double hold = 30, string after = "vent") =>
        Step(StepType.Leak, "气密检查", ("pres", mpa), ("hold", hold), ("after", after));

    public static PcStep Exchange(string gas, int cycles = 3, string? name = null) =>
        Step(StepType.Exchange, name ?? (gas == "inert" ? "惰性置换" : "置换"), ("gas", gas), ("fill", 0.5), ("release", 0.02), ("cycles", cycles), ("lineFlush", 2));

    public static PcStep Stir(double rpm = 400) => Step(StepType.Stir, "搅拌", ("rpm", rpm));

    public static PcStep Heat(double temp, double wait = 15, string? name = null) => Step(StepType.Heat, name ?? "升温", ("mode", "fixed"), ("temp", temp), ("wait", wait));

    public static PcStep Press(string gas, double mpa, string regulate = "on", string? name = null) =>
        Step(StepType.Press, name ?? "充压", ("gas", gas), ("pres", mpa), ("regulate", regulate));

    public static PcStep HoldTime(double min, string? name = null) => Step(StepType.Hold, name ?? "保持", ("until", "time"), ("time", min));

    /// <summary>保持到温度 ≤ x（等凉了再放空；旧程序的「先降温至」转换过来也是它）。</summary>
    public static PcStep Cool(double temp, double maxWait = 60, string? name = null) =>
        Step(StepType.Hold, name ?? "保持", ("until", "tempBelow"), ("temp", temp), ("maxWait", maxWait), ("onTimeout", "continue"));

    public static PcStep React(double min, string stall = "on", double minTime = 30, string? name = null) =>
        Step(StepType.React, name ?? "反应", ("time", min), ("minTime", Math.Min(minTime, min)), ("stallOn", stall));

    public static PcStep Vent(int flush = 3) => Step(StepType.Vent, "放空", ("to", 0.02), ("flush", flush), ("flushPres", 0.3));

    public static PcStep End() => Step(StepType.End, "结束");

    private static PcProgram P(string id, string name, string model, params PcStep[] steps)
    {
        var p = new PcProgram { Id = id, Name = name, Model = model, Draft = true };
        p.Steps.AddRange(steps);
        return p;
    }

    /// <summary>加氢筛选（默认模板）。</summary>
    public static PcProgram Hydrogenation(string id, string model, string name = "加氢筛选") => P(id, name, model,
        Leak(), Exchange("inert", name: "惰性置换"), Exchange("A", name: "气体 A 置换"), Stir(), Heat(80), Press("A", 2.0), React(480), Vent(), End());

    /// <summary>只做气密。</summary>
    public static PcProgram LeakOnly(string id, string model, string name = "只做气密") => P(id, name, model, Leak(), End());

    /// <summary>惰性气氛加热。</summary>
    public static PcProgram InertHeating(string id, string model, string name = "惰性气氛加热") => P(id, name, model,
        Exchange("inert", name: "惰性置换"), Stir(), Heat(120), Press("inert", 0.5), HoldTime(240), Vent(), End());

    /// <summary>三个推荐模板（程序库第一次打开时放进去；id 前缀 T）。</summary>
    public static IReadOnlyList<PcProgram> All(string model) => new[]
    {
        Hydrogenation("T1", model), LeakOnly("T2", model), InertHeating("T3", model),
    };
}
