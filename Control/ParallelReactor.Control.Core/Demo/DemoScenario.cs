using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>场景时间线（样稿 tl，单位：几分钟前），用于生成历史与已完成步骤的时间。字段为 null 时同 JS 的 undefined。</summary>
internal sealed class TlSpec
{
    public double? HeatAt, From, Rate, PressAt, PRate, ReactAt, PBase, StirAt, Held, PHeld, Until, CoolTo, HoldPct, RpmBefore;
    public bool Overshoot;

    public static readonly TlSpec Empty = new();
}

/// <summary>场景里的程序：库 id、当前步、状态（缺省 = 运行中）、当前步还剩几分钟。</summary>
internal sealed record ProgSpec(string Id, int? StepIndex = null, ProgramStatus? Status = null, double? RemainMin = null);

/// <summary>场景里的一个反应釜（样稿 scenario().ch[n]）。</summary>
internal sealed class ChSpec
{
    public StateKey State = StateKey.Idle;
    public double? TempC, SpC, P, PTarget, Heater, Mmol, Vol, Rpm;
    public bool Sv, StirOn, Cooling;
    public ProgSpec? Prog;
    public TlSpec? Tl;
}

/// <summary>场景里的报警。</summary>
internal sealed record AlarmSpec(AlarmLevel Level, int? Ch, string Code, string Text, DateTime At);

/// <summary>一个型号的演示场景（样稿 scenario(key)）。Stir = null 表示独立搅拌。</summary>
internal sealed class ScenarioSpec
{
    public (bool On, double Rpm)? Stir;
    public List<(string Key, bool Open)> Gas = new();
    public List<AlarmSpec> Alarms = new();
    public Dictionary<int, ChSpec> Ch = new();
}

/// <summary>
/// 演示场景与程序库（样稿 sim.js programLibrary / scenario，brief §8，数值逐个照抄）：
/// 1号机 HT-PR8 · 现场（RV1–4）、2号机 HT-PR4、3号机 HT-PR16（RV11 超压）、4号机 HT-PR8（断线）。
/// </summary>
internal static class DemoScenario
{
    /// <summary>样稿 at(h, m, s)：2026-11-08 当天的本地时刻。</summary>
    public static DateTime At(int h, int m, int s = 0) => new(2026, 11, 8, h, m, s, DateTimeKind.Local);

    private static ProgramStep S(StepKind type, double estMin, StepEnd? end, double? sp = null, double? target = null, string? gas = null,
        double? minutes = null, double? rpm = null, double? coolTo = null, bool? vent = null, int? cycles = null) =>
        new(type) { Sp = sp, Target = target, Gas = gas, Minutes = minutes, Rpm = rpm, CoolTo = coolTo, Vent = vent, Cycles = cycles, End = end, EstMin = estMin };

    private static StepEnd Temp(string op, double v) => new(EndKind.Temp, v, op);
    private static StepEnd Press(string op, double v) => new(EndKind.Press, v, op);
    private static StepEnd Time(double v) => new(EndKind.Time, v);

    /// <summary>样稿 programLibrary(m)：按型号的程序库（气路名称、量程）。</summary>
    public static List<ProgramDef> ProgramLibrary(ModelProfile m)
    {
        string k = m.Key == "PR8-4" ? "PR8" : m.Key;
        string gasA = (m.GasInlets.FirstOrDefault(g => g.Key is "a" or "h2") ?? m.GasInlets[0]).Key;
        string inert = (m.GasInlets.FirstOrDefault(g => g.Key is "inert" or "n2") ?? m.GasInlets[0]).Key;
        var lib = new List<ProgramDef>();
        if (k == "PR4")
            lib.Add(new ProgramDef("h2-hp", "高压加氢-150", "150 ℃ · 5.2 MPa H₂ · 反应 120 min", new[]
            {
                S(StepKind.Heat, 22, Temp("≥", 149.5), sp: 150, rpm: 800),
                S(StepKind.Press, 6, Press("≥", 5.2), sp: 150, target: 5.2, gas: "h2", rpm: 800),
                S(StepKind.React, 120, Time(120), sp: 150, target: 5.2, gas: "h2", minutes: 120, rpm: 800),
                S(StepKind.Cool, 25, Temp("≤", 40), sp: 40, rpm: 300),
                S(StepKind.End, 3, Press("<", 0.2), coolTo: 40, vent: true, rpm: 0),
            }));
        if (k == "PR16")
            lib.Add(new ProgramDef("screen-80", "筛选-80 ℃", "80 ℃ · 1.2 MPa N₂ · 反应 90 min", new[]
            {
                S(StepKind.Heat, 12, Temp("≥", 79.5), sp: 80, rpm: 400),
                S(StepKind.Press, 5, Press("≥", 1.2), sp: 80, target: 1.2, gas: inert, rpm: 400),
                S(StepKind.React, 90, Time(90), sp: 80, target: 1.2, gas: inert, minutes: 90, rpm: 400),
                S(StepKind.End, 12, Temp("<", 45), coolTo: 45, vent: true, rpm: 0),
            }));
        if (k == "PR8")
            lib.Add(new ProgramDef("cat-100", "催化评价-100", "100 ℃ · 0.50 MPa · 反应 60 min", new[]
            {
                S(StepKind.Heat, 14, Temp("≥", 99.5), sp: 100, rpm: 600),
                S(StepKind.Press, 4, Press("≥", 0.5), sp: 100, target: 0.5, gas: gasA, rpm: 600),
                S(StepKind.React, 60, Time(60), sp: 100, target: 0.5, gas: gasA, minutes: 60, rpm: 600),
                S(StepKind.End, 12, Temp("<", 45), coolTo: 45, vent: true, rpm: 0),
            }));
        double lim = m.Limits.PressureMaxMPa;
        lib.Add(new ProgramDef("h2-std", "加氢-标准", "120 ℃ · 0.35 MPa · 反应 72 min", new[]
        {
            S(StepKind.Heat, 20, Temp("≥", 119.5), sp: 120, rpm: 300),
            S(StepKind.Press, 6, Press("≥", 0.35), sp: 120, target: Math.Min(0.35, lim), gas: gasA, rpm: 300),
            S(StepKind.React, 72, Time(72), sp: 120, target: Math.Min(0.35, lim), gas: gasA, minutes: 72, rpm: 300),
            S(StepKind.End, 15, Temp("<", 50), coolTo: 50, vent: true, rpm: 0),
        }));
        lib.Add(new ProgramDef("purge-3", "吹扫-置换 ×3", "惰性气体置换 3 次", new[]
        {
            S(StepKind.Purge, 6, Time(6), target: 0.3, gas: inert, cycles: 3, rpm: 0),
            S(StepKind.Wait, 5, Time(5), minutes: 5, rpm: 0),
            S(StepKind.End, 1, Time(0), vent: false, rpm: 0),
        }));
        return lib;
    }

    private static ChSpec C(StateKey state, double? tempC, double? spC = null, double? p = null, double? pTarget = null, double? heater = null,
        double? mmol = null, double? vol = null, bool sv = false, bool stirOn = false, double? rpm = null, bool cooling = false, ProgSpec? prog = null, TlSpec? tl = null) =>
        new() { State = state, TempC = tempC, SpC = spC, P = p, PTarget = pTarget, Heater = heater, Mmol = mmol, Vol = vol, Sv = sv, StirOn = stirOn, Rpm = rpm, Cooling = cooling, Prog = prog, Tl = tl };

    /// <summary>样稿 scenario(key)：PR4 / PR16 / PR8；其它键（含 PR8-4）都是 1号机 · 现场的场景。</summary>
    public static ScenarioSpec Scenario(string key)
    {
        var sc = new ScenarioSpec();
        if (key == "PR4")
        {
            sc.Stir = null;
            sc.Gas = new() { ("h2", true), ("n2", false), ("vent", false) };
            sc.Ch[1] = C(StateKey.React, 150.0, 150, 5.2, 5.2, 45, 12.6, 50, sv: true, stirOn: true, rpm: 800,
                prog: new ProgSpec("h2-hp", 2), tl: new TlSpec { HeatAt = 58, From = 24.8, Rate = 7, PressAt = 36, PRate = 0.9, ReactAt = 30 });
            sc.Ch[2] = C(StateKey.Heat, 112.6, 150, 0.12, null, 100, null, 60, sv: false, stirOn: true, rpm: 500,
                prog: new ProgSpec("h2-hp", 0, RemainMin: 8), tl: new TlSpec { HeatAt = 22, From = 25.1, PBase = 0.12, StirAt = 23 });
            sc.Ch[3] = C(StateKey.Cool, 68.3, 40, 0.35, null, 0, 9.8, 45, sv: false, stirOn: true, rpm: 300, cooling: true,
                prog: new ProgSpec("h2-hp", 3, RemainMin: 12), tl: new TlSpec { Held = 150, PHeld = 5.0, Until = 20, CoolTo = 25, PBase = 0.35, HoldPct = 44, RpmBefore = 800 });
            sc.Ch[4] = C(StateKey.Done, 31.6, null, 0.10, null, 0, 15.4, 50, sv: false, stirOn: false, rpm: 0,
                prog: new ProgSpec("h2-hp", Status: ProgramStatus.Done), tl: new TlSpec { Held = 150, PHeld = 5.2, Until = 40, CoolTo = 24, PBase = 0.10, HoldPct = 46, RpmBefore = 800 });
            return sc;
        }
        if (key == "PR16")
        {
            double[] rT = { 80.1, 79.8, 80.3, 80.0, 79.9, 80.2 }, rP = { 1.20, 1.19, 1.21, 1.20, 1.18, 1.20 }, rM = { 0.62, 0.58, 0.66, 0.60, 0.55, 0.63 };
            for (int n = 1; n <= 6; n++)
                sc.Ch[n] = C(StateKey.React, rT[n - 1], 80, rP[n - 1], 1.2, 30 + n, rM[n - 1], 5, sv: true,
                    prog: new ProgSpec("screen-80", 2), tl: new TlSpec { HeatAt = 50 - n, From = 24, Rate = 5, PressAt = 38 - n, PRate = 0.25, ReactAt = 33 - n });
            sc.Ch[7] = C(StateKey.Heat, 58.2, 80, 0.005, null, 100, null, 5, prog: new ProgSpec("screen-80", 0, RemainMin: 6), tl: new TlSpec { HeatAt = 9, From = 24 });
            sc.Ch[8] = C(StateKey.Heat, 63.5, 80, 0.005, null, 100, null, 5, prog: new ProgSpec("screen-80", 0, RemainMin: 4), tl: new TlSpec { HeatAt = 10, From = 24.2 });
            sc.Ch[9] = C(StateKey.Press, 80.1, 80, 0.64, 1.2, 33, null, 5, sv: true, prog: new ProgSpec("screen-80", 1, RemainMin: 3), tl: new TlSpec { HeatAt = 30, From = 24.2, Rate = 5, PressAt = 3 });
            sc.Ch[10] = C(StateKey.Press, 79.9, 80, 0.88, 1.2, 35, null, 5, sv: true, prog: new ProgSpec("screen-80", 1, RemainMin: 2), tl: new TlSpec { HeatAt = 31, From = 24.1, Rate = 5, PressAt = 4 });
            sc.Ch[11] = C(StateKey.Alarm, 80.4, 80, 3.12, 1.2, 34, null, 5, sv: false, prog: new ProgSpec("screen-80", 1, ProgramStatus.Held),
                tl: new TlSpec { HeatAt = 33, From = 24, Rate = 5, PressAt = 6, Overshoot = true });
            sc.Ch[12] = C(StateKey.Done, 35.1, null, 0.006, null, 0, 0.71, 5, sv: false, prog: new ProgSpec("screen-80", Status: ProgramStatus.Done),
                tl: new TlSpec { Held = 80, PHeld = 1.2, Until = 22, CoolTo = 24, HoldPct = 33 });
            sc.Ch[13] = C(StateKey.Ready, 23.9, p: 0.005, vol: 5);
            sc.Ch[14] = C(StateKey.Ready, 24.1, p: 0.004, vol: 5);
            sc.Ch[15] = C(StateKey.Idle, 24.0, p: 0.005, vol: 5);
            sc.Ch[16] = C(StateKey.Idle, 23.8, p: 0.005, vol: 5);
            sc.Stir = (true, 400);
            sc.Gas = new() { ("n2", true), ("vent", false) };
            sc.Alarms.Add(new AlarmSpec(AlarmLevel.Err, 11, "P-HIGH", "RV11 超压 3.12 MPa ≥ 3.00 MPa · 已关闭 SV11", At(10, 9, 42)));
            return sc;
        }
        if (key == "PR8")
        {
            // 4号机（4 台组合时断线重连中）：全 8 路，与 1号机做不同的事，避免混淆
            foreach (var r in new[] { (1, 100.2, 0.498, 0.96, 24.0), (2, 99.8, 0.502, 0.88, 22.0), (3, 100.1, 0.497, 0.79, 20.0) })
                sc.Ch[r.Item1] = C(StateKey.React, r.Item2, 100, r.Item3, 0.5, 33, r.Item4, 15, sv: true,
                    prog: new ProgSpec("cat-100", 2), tl: new TlSpec { HeatAt = r.Item5 + 18, From = 24, Rate = 6, PressAt = r.Item5 + 4, PRate = 0.12, ReactAt = r.Item5 });
            sc.Ch[4] = C(StateKey.Heat, 78.4, 100, 0.005, null, 100, null, 15, prog: new ProgSpec("cat-100", 0, RemainMin: 4), tl: new TlSpec { HeatAt = 10, From = 24 });
            sc.Ch[5] = C(StateKey.Heat, 71.0, 100, 0.005, null, 100, null, 15, prog: new ProgSpec("cat-100", 0, RemainMin: 5), tl: new TlSpec { HeatAt = 9, From = 24.3 });
            sc.Ch[6] = C(StateKey.Done, 38.2, null, 0.006, null, 0, 1.12, 15, prog: new ProgSpec("cat-100", Status: ProgramStatus.Done),
                tl: new TlSpec { Held = 100, PHeld = 0.5, Until = 16, CoolTo = 24, HoldPct = 31 });
            sc.Ch[7] = C(StateKey.Ready, 24.1, p: 0.004, vol: 10);
            sc.Ch[8] = C(StateKey.Idle, 23.9, p: 0.005, vol: 10);
            sc.Stir = (true, 600);
            sc.Gas = new() { ("inert", false), ("a", true), ("b", false), ("vent", false) };
            return sc;
        }
        // PR8-4（1号机 · 现场：RV1–RV4 装）
        sc.Stir = (true, 300);
        sc.Gas = new() { ("inert", false), ("a", true), ("b", false), ("vent", false) };
        sc.Alarms.Add(new AlarmSpec(AlarmLevel.Warn, 3, "HEAT-SLOW", "RV3 升温较慢 · 已用 18 min", At(10, 11, 30)));
        sc.Ch[1] = C(StateKey.React, 119.8, 120, 0.349, 0.35, 38, 1.82, 15, sv: true,
            prog: new ProgSpec("h2-std", 2), tl: new TlSpec { HeatAt = 55, From = 24.5, Rate = 6.5, PressAt = 36, PRate = 0.06, ReactAt = 30 });
        sc.Ch[2] = C(StateKey.React, 120.3, 120, 0.346, 0.35, 36, 1.74, 15, sv: true,
            prog: new ProgSpec("h2-std", 2), tl: new TlSpec { HeatAt = 53, From = 24.3, Rate = 6.5, PressAt = 34, PRate = 0.06, ReactAt = 28 });
        sc.Ch[3] = C(StateKey.Heat, 86.4, 120, 0.006, null, 100, null, 12, sv: false,
            prog: new ProgSpec("h2-std", 0, RemainMin: 9), tl: new TlSpec { HeatAt = 18, From = 24.1 });
        sc.Ch[4] = C(StateKey.Press, 120.1, 120, 0.217, 0.35, 41, null, 18, sv: true,
            prog: new ProgSpec("h2-std", 1, RemainMin: 3), tl: new TlSpec { HeatAt = 48, From = 24.6, Rate = 6.5, PressAt = 4, PRate = 0.053 });
        return sc;
    }

    /// <summary>「添加设备」的空白场景（样稿 opts.blank）：没有程序 / 报警 / 历史事件，共用搅拌关、气路全关。</summary>
    public static ScenarioSpec Blank(ModelProfile m)
    {
        var sc = new ScenarioSpec { Stir = m.StirMode == StirMode.Shared ? (false, 300) : null };
        foreach (var g in m.GasKeys) sc.Gas.Add((g, false));
        return sc;
    }
}
