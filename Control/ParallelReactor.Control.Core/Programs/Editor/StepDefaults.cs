using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>
/// 程序步骤 v0.8 的参数表（research/fusion-port/notes/STEPS-SPEC.md §1）：每种步骤有哪些键、默认值（压力 psi，同线上格式）、哪些键是压力 / 压降速率；
/// 以及本机步骤（<see cref="PcStep"/>，压力 MPa）与转换用的 <see cref="MigStep"/>（压力 psi）之间的换算 —— 旧格式一律经 Contracts 的
/// <see cref="StepMigration"/> 转换（程序库、草稿、导入、条件矩阵方案、记录里的「再做一次」都走这里），不另写一个转换器。
/// 不依赖设备的限值：转换出来缺的参数按 §1 的默认值补（<see cref="Fill"/>），超出设备范围的由编辑器的待改项指出。
/// </summary>
public static class StepDefaults
{
    /// <summary>每种步骤的参数与默认值（选项 = 英文码，数值 = 线上单位：压力 psi、压降 psi/h、时间 min / s）。顺序 = 参数面板的顺序。</summary>
    public static IReadOnlyList<(string Key, object Def)> Of(StepType t) => t switch
    {
        StepType.Leak => new (string, object)[] { ("pres", 435.1), ("settle", 5.0), ("hold", 30.0), ("maxDrop", 2.0), ("onFail", "stop"), ("after", "vent") },
        StepType.Exchange => new (string, object)[] { ("gas", "inert"), ("fill", 72.5), ("release", 2.9), ("cycles", 3.0), ("lineFlush", 2.0), ("maxSec", 60.0) },
        StepType.Stir => new (string, object)[] { ("rpm", 400.0) },
        StepType.Heat => new (string, object)[] { ("mode", "fixed"), ("temp", 80.0), ("rate", 0.0), ("band", 2.0), ("wait", 15.0), ("maxWait", 120.0), ("onTimeout", "stop") },
        StepType.Press => new (string, object)[] { ("gas", "A"), ("pres", 290.1), ("tol", 2.0), ("regulate", "on"), ("refill", 3.0), ("maxSec", 180.0) },
        StepType.Hold => new (string, object)[] { ("until", "time"), ("time", 10.0), ("temp", 40.0), ("pres", 14.5), ("maxWait", 60.0), ("onTimeout", "continue") },
        StepType.React => new (string, object)[] { ("time", 480.0), ("minTime", 30.0), ("mmolOn", "off"), ("mmol", 5.0), ("stallOn", "on"), ("stallRate", 0.005), ("stallMin", 30.0) },
        StepType.Vent => new (string, object)[] { ("to", 2.9), ("maxSec", 120.0), ("flush", 3.0), ("flushPres", 43.5) },
        _ => Array.Empty<(string, object)>(),
    };

    /// <summary>
    /// 声明范围（线上单位：压力 psi；同 HMI 的 StepSpecs：压力上限 493.1 psi（34 bar）、温度 300 ℃、转速 3000 rpm —— 设备设置再收紧的那部分由编辑器的待改项管）、
    /// 是否整数。旧格式转换出来的值超出它时收紧（<see cref="ClampLegacy"/>，同 HMI 的 ProgramJson）。不认识的键为 null。
    /// </summary>
    public static (double Min, double Max, bool Int, string Label, string Unit)? Declared(StepType t, string key) => (t, key) switch
    {
        (StepType.Leak, "pres") => (14.5, 493.1, false, "试验压力", "psi"),
        (StepType.Leak, "settle") => (0, 60, false, "稳定时间", "min"),
        (StepType.Leak, "hold") => (5, 600, false, "保压时间", "min"),
        (StepType.Leak, "maxDrop") => (0.1, 100, false, "允许压降", "psi/h"),
        (StepType.Exchange, "fill") => (7.3, 493.1, false, "充到", "psi"),
        (StepType.Exchange, "release") => (0, 493.1, false, "放到", "psi"),
        (StepType.Exchange, "cycles") => (0, 20, true, "次数", "次"),
        (StepType.Exchange, "lineFlush") => (0, 10, true, "先吹总管", "次"),
        (StepType.Exchange, "maxSec") => (10, 300, false, "单次最长", "s"),
        (StepType.Stir, "rpm") => (0, 3000, true, "转速", "rpm"),
        (StepType.Heat, "temp") => (35, 300, false, "目标温度", "°C"),
        (StepType.Heat, "rate") => (0, 20, false, "升温速率", "°C/min"),
        (StepType.Heat, "band") => (0.5, 10, false, "到位范围", "°C"),
        (StepType.Heat, "wait") => (0, 120, false, "到位后平衡", "min"),
        (StepType.Heat, "maxWait") => (10, 600, false, "最长等待", "min"),
        (StepType.Press, "pres") => (7.3, 493.1, false, "目标压力", "psi"),
        (StepType.Press, "tol") => (0.5, 10, false, "允许偏差", "%"),
        (StepType.Press, "refill") => (1, 10, false, "补气阈值", "%"),
        (StepType.Press, "maxSec") => (30, 600, false, "最长充气", "s"),
        (StepType.Hold, "time") => (0, 1440, false, "时间", "min"),
        (StepType.Hold, "temp") => (0, 300, false, "目标温度", "°C"),
        (StepType.Hold, "pres") => (0, 493.1, false, "目标压力", "psi"),
        (StepType.Hold, "maxWait") => (1, 1440, false, "最长等待", "min"),
        (StepType.React, "time") => (1, 4320, false, "最长时间", "min"),
        (StepType.React, "minTime") => (0, 4320, false, "最短时间", "min"),
        (StepType.React, "mmol") => (0.01, 100, false, "吸气量", "mmol"),
        (StepType.React, "stallRate") => (0.001, 1, false, "停止速率", "mmol/min"),
        (StepType.React, "stallMin") => (5, 240, false, "持续", "min"),
        (StepType.Vent, "to") => (0, 72.5, false, "放到", "psi"),
        (StepType.Vent, "maxSec") => (10, 600, false, "最长时间", "s"),
        (StepType.Vent, "flush") => (0, 10, true, "之后置换", "次"),
        (StepType.Vent, "flushPres") => (7.3, 493.1, false, "置换充到", "psi"),
        _ => null,
    };

    /// <summary>
    /// 旧格式转换出来的程序：超出 v0.8 声明范围的值收紧到范围内（整数字段取整），每处写一条说明（「第 01 步 置换：先吹总管 50 次超出新范围，已改为 10 次」）——
    /// 同 HMI 的 PUT（旧格式收紧 + 提示，新格式越界照样拒绝）。旧搅拌超过设备的转速上限不在这里收紧（声明上限 3000），由待改项指出（同 HMI）。
    /// </summary>
    public static List<string> ClampLegacy(IReadOnlyList<PcStep> steps)
    {
        var notes = new List<string>();
        for (int i = 0; i < steps.Count; i++)
        {
            var s = steps[i];
            foreach (var key in s.Num.Keys.ToList())
            {
                if (Declared(s.Type, key) is not { } d) continue;
                bool press = IsPressure(s.Type, key) || IsRate(s.Type, key);
                double v = s.Num[key], wire = press ? Units.MpaToPsi(v) : v;
                if (!double.IsFinite(wire)) continue;
                double c = Math.Clamp(d.Int ? Math.Round(wire) : wire, d.Min, d.Max);
                if (Math.Abs(c - wire) < 1e-6) continue;
                s.Num[key] = press ? Units.PsiToMpa(c) : c;
                // 压力按设置的压力单位写；界面单位里看不出差别的收紧（3.4 MPa = 493.128 psi → 493.1 psi）照样收紧，但不写说明（用户看不出改了什么）
                string F(double x) => press ? PressureDisplay.InputU(Units.PsiToMpa(x)) : JsNumber.ToString(Math.Round(x, 3)) + " " + d.Unit;
                if (press && F(wire) == F(c)) continue;
                notes.Add("第 " + (i + 1).ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " 步 " + s.Name + "：" + d.Label + " " + F(wire) + " 超出新范围，已改为 " + F(c));
            }
        }
        return notes;
    }

    /// <summary>这个键是压力（线上 psi，本机 MPa）。</summary>
    public static bool IsPressure(StepType t, string key) => (t, key) switch
    {
        (StepType.Leak, "pres") or (StepType.Exchange, "fill" or "release") or (StepType.Press, "pres") or (StepType.Hold, "pres") or (StepType.Vent, "to" or "flushPres") => true,
        _ => false,
    };

    /// <summary>这个键是压降速率（线上 psi/h，本机 MPa/h）：气密的允许压降。</summary>
    public static bool IsRate(StepType t, string key) => t == StepType.Leak && key == "maxDrop";

    /// <summary>v0.8 的键（不在表里的键下发时不带：旧程序里留下的 dur / endBy / mode … 不会发出去）。</summary>
    public static bool Known(StepType t, string key) => Of(t).Any(x => x.Key == key);

    /// <summary>
    /// HMI 的 psi → 本机存的 MPa：在设置的压力单位里按输入小数位取整再换回 MPa（MPa 时 = 取到 0.01 MPa；psi 时 50 psi 仍是 50 psi）。
    /// 线上来的程序（草稿、导入）与按 psi 写的默认值都这样换，界面上不会出现 2.0002 MPa。
    /// </summary>
    public static double PsiToStored(double psi)
    {
        var u = PressureDisplay.Primary;
        return PressureUnits.ToMpa(Math.Round(PressureUnits.FromPsi(psi, u), PressureUnits.InputDecimals(u), MidpointRounding.AwayFromZero), u);
    }

    /// <summary>psi/h → 本机存的 MPa/h（按设置的单位取到压降的输入小数位）。</summary>
    public static double RateToStored(double psiH)
    {
        var u = PressureDisplay.Primary;
        return PressureUnits.ToMpa(Math.Round(PressureUnits.FromPsi(psiH, u), FieldSpec.RateDecimals(u), MidpointRounding.AwayFromZero), u);
    }

    /// <summary>本机值（MPa、MPa/h）→ 线上值（psi 1 位小数、psi/h 2 位）。其余原样。</summary>
    public static double ToWire(StepType t, string key, double v) =>
        IsPressure(t, key) ? Math.Round(Units.MpaToPsi(v), 1, MidpointRounding.AwayFromZero)
        : IsRate(t, key) ? Math.Round(Units.MpaToPsi(v), 2, MidpointRounding.AwayFromZero) : v;

    /// <summary>补上缺的参数（§1 的默认值；压力按设置的单位取整）。曲线升温没有段时补一段（同编辑器新建）。</summary>
    public static PcStep Fill(PcStep s)
    {
        foreach (var (k, def) in Of(s.Type))
        {
            if (def is string code) { if (!s.Sel.ContainsKey(k)) s.Sel[k] = code; continue; }
            if (s.Num.ContainsKey(k)) continue;
            double v = (double)def;
            s.Num[k] = IsPressure(s.Type, k) ? PsiToStored(v) : IsRate(s.Type, k) ? RateToStored(v) : v;
        }
        if (s.Type == StepType.Heat && s.Segments.Count == 0 && s.S("mode") == "curve") s.Segments.Add(new CurveSeg(s.N("temp", 80), 3, 20));
        return s;
    }

    // ———————————————————— 旧格式转换（STEPS-SPEC §3，经 Contracts 的 StepMigration） ————————————————————

    /// <summary>步骤名经转换带过去用的键（只在转换的内存里，不进线上格式）。</summary>
    private const string NameKey = "\u0001name";

    /// <summary>
    /// 本机步骤（压力 MPa）→ 转换用的步骤（压力 psi，不取整）。旧格式只有 press.pres 是压力；新格式的压力键按 <see cref="IsPressure"/>。
    /// </summary>
    public static MigStep ToMig(PcStep s)
    {
        var p = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var (k, v) in s.Sel) p[k] = v;
        foreach (var (k, v) in s.Num) p[k] = IsPressure(s.Type, k) || IsRate(s.Type, k) ? Units.MpaToPsi(v) : v;
        p[NameKey] = s.Name;
        return new MigStep(s.Type.ToCode(), p, s.Segments.Select(g => (g.Temp, g.Rate, g.Hold)));
    }

    /// <summary>
    /// 转换后的一步 → 本机步骤：只留 v0.8 的键，压力 psi → MPa（<paramref name="exact"/> = 不取整，程序库里本来就是 MPa 的值原样回来；
    /// 否则按设置的单位取整，见 <see cref="PsiToStored"/>），缺的按默认值补。不认识的类型为 null。
    /// 步骤名：没转换的步骤保留原名；转换出来的步骤按类型（「保持 · 温度 ≤」叫「降温」，同旧的「先降温至」）。
    /// </summary>
    public static PcStep? FromMig(MigStep m, bool exact)
    {
        if (!StepTypeCodes.TryParse(m.Type, out var t) || t.IsLegacy()) return null;
        string name = m.P.TryGetValue(NameKey, out var nm) && nm is string ns && ns.Length > 0 ? ns
            : t.Zh();   // 旧的「先降温至」转成「保持 · 温度 ≤ x」，名字也叫「保持」（用户不要降温步骤）
        var s = new PcStep(t, name);
        foreach (var (k, v) in m.P)
        {
            if (k == NameKey || !Known(t, k)) continue;
            if (v is string str) { s.Sel[k] = str; continue; }
            if (m.Num(k) is not double x) continue;
            s.Num[k] = IsPressure(t, k) ? (exact ? Units.PsiToMpa(x) : PsiToStored(x))
                : IsRate(t, k) ? (exact ? Units.PsiToMpa(x) : RateToStored(x)) : x;
        }
        if (t == StepType.Heat) s.Segments.AddRange(m.Segments.Select(g => new CurveSeg(g.Temp, g.Rate, g.Hold)));
        return Fill(s);
    }

    /// <summary>
    /// 一串本机步骤（可能是旧格式）→ v0.8 步骤 + 有没有转换过。程序库 / 条件矩阵方案（library.json / experiments.json，压力已经是 MPa）用：
    /// 换成 psi 交给 <see cref="StepMigration.Migrate"/>，再按原值换回（<c>exact</c>），新格式的程序不变（只补末尾的结束、缺的参数）。
    /// </summary>
    public static List<PcStep> Migrate(IReadOnlyList<PcStep> steps, out bool migrated) => Migrate(steps, out migrated, out _);

    /// <summary>同上，另给旧值收紧的说明（转换过的程序才收紧，见 <see cref="ClampLegacy"/>）。</summary>
    public static List<PcStep> Migrate(IReadOnlyList<PcStep> steps, out bool migrated, out List<string> notes)
    {
        var r = StepMigration.Migrate(steps.Select(ToMig).ToList());
        migrated = r.Migrated;
        var list = r.Steps.Select(x => FromMig(x, exact: true)).Where(x => x != null).Select(x => x!).ToList();
        notes = migrated ? ClampLegacy(list) : new List<string>();
        return list;
    }

    /// <summary>线上格式的步骤（压力 psi：草稿、导入的 HMI 程序 JSON）→ v0.8 本机步骤 + 有没有转换过。</summary>
    public static List<PcStep> FromWire(IReadOnlyList<MigStep> steps, out bool migrated) => FromWire(steps, out migrated, out _);

    /// <summary>同上，另给旧值收紧的说明。</summary>
    public static List<PcStep> FromWire(IReadOnlyList<MigStep> steps, out bool migrated, out List<string> notes)
    {
        var r = StepMigration.Migrate(steps);
        migrated = r.Migrated;
        var list = r.Steps.Select(x => FromMig(x, exact: false)).Where(x => x != null).Select(x => x!).ToList();
        notes = migrated ? ClampLegacy(list) : new List<string>();
        return list;
    }
}
