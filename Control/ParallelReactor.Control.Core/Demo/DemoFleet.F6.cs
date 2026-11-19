using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Events;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.Programs.Editor;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 融合版 F6：程序页往演示设备下发「本机编辑的程序」（真机走 PUT /channels/{n}/program，演示数据没有接口，直接放进反应釜）。
/// 守卫同下发（<see cref="DeviceAction.ProgLoad"/>：控制权、程序没在运行）；之后用普通的「运行程序」（ProgRun）跑它 —— RunProgramNow 认的是记录里的步骤。
/// </summary>
public sealed partial class DemoFleet
{
    /// <summary>下发一个程序到 d 的第 n 路（已下发 · 待运行）。返回 (ok, 文字)；守卫不过时不改、文字 = 原因。</summary>
    public (bool Ok, string Msg) LoadProgram(DemoDevice d, int n, ProgramDef def)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        if (def is null) throw new ArgumentNullException(nameof(def));
        var p = ActionPayload.Ch(n);
        string r = d.Why(DeviceAction.ProgLoad, p);
        if (r.Length > 0) return (false, r);
        var ch = d.Channel(n)!;
        ch.Program = MakeProgram(def, new ProgSpec(def.Id, Status: ProgramStatus.Loaded), null, Now);
        if (ch.State == StateKey.Idle) ch.State = StateKey.Ready;
        string msg = "程序「" + def.Name + "」已下发到 RV" + n;
        Log(d, EventLevel.Op, n, msg);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d, DeviceAction.ProgLoad));
        return (true, msg);
    }
}

/// <summary>
/// 融合版 F6 的演示程序库（样稿 LIB：名字、适用型号、修改时刻与人、草稿；步骤按程序步骤 v0.8 重写，STEPS-SPEC.md，压力 MPa）。
/// id 与 DemoFusion.Library 相同（A / H1 / LK / OX / B / C），所以运行表上在跑的程序能在程序库里对上「运行 N 路」；另有设计稿的推荐模板「惰性气氛加热」（IH）。
/// <list type="bullet">
/// <item>A / B / C：升温 → 充压 → 反应 → 降温（保持 · 温度 ≤）→ 放空 → 结束（运行表上在跑的就是它们，步骤序号同 DemoFusion）。</item>
/// <item>H1：设计稿的加氢筛选模板（120 ℃、3.0 MPa、90 min）；LK：只做气密；IH：惰性气氛加热。</item>
/// <item>OX（草稿）：按旧格式写的程序，读进来时经 StepMigration 转换（编辑器顶上提示「已从旧格式转换，请检查」）；充压 3.4 MPa 超出上限、没有放空，演示待改项与提示。</item>
/// </list>
/// 另有样稿 mxBuild 的演示条件矩阵（1号机 · H1：Pd/C、Pt/C 各一组，100–130 ℃ 四档，RV4 压力故意填错）。
/// </summary>
public static class DemoPrograms
{
    private static DateTime T(int mo, int d, int h, int mi) => new(2026, mo, d, h, mi, 0, DateTimeKind.Local);

    private static PcProgram P(string id, string name, string model, DateTime mod, string by, bool draft, params PcStep[] steps)
    {
        var p = new PcProgram { Id = id, Name = name, Model = model, Modified = mod, By = by, Draft = draft };
        p.Steps.AddRange(steps);
        return p;
    }

    /// <summary>升温 → 充压 → 反应 → 降温 → 放空 → 结束（A / B / C）。</summary>
    private static PcStep[] Basic(double temp, double wait, double mpa, double react, double cool, string gas = "A", string reactName = "反应") => new[]
    {
        ProgramTemplates.Heat(temp, wait), ProgramTemplates.Press(gas, mpa, name: "升压"), ProgramTemplates.React(react, "off", minTime: 0, name: reactName),
        ProgramTemplates.Cool(cool), ProgramTemplates.Vent(), ProgramTemplates.End(),
    };

    /// <summary>
    /// OX：旧格式（吹扫 12 次 → 升温 → 充压 B 3.4 MPa → 反应「两者先到」→ 结束 · 密封 · 先降温至 30 ℃），读进来时转换（同程序库 library.json 的旧程序）：
    /// 吹扫 → 置换（只吹总管，12 次超出新范围收紧到 10，写一条说明）、结束 → 保持 · 温度 ≤ 30 → 结束。
    /// </summary>
    private static PcProgram LegacyOx()
    {
        var legacy = new List<PcStep>
        {
            Raw(StepType.Purge, "吹扫", ("gas", "inert"), ("cycles", 12), ("fill", 3), ("vent", 5)),
            ProgramTemplates.Step(StepType.Heat, "升温", ("mode", "fixed"), ("temp", 60), ("wait", 10)),
            ProgramTemplates.Step(StepType.Press, "升压", ("gas", "B"), ("pres", 3.4), ("regulate", "on")),
            Raw(StepType.React, "反应", ("endBy", "either"), ("time", 240), ("mmol", 12)),
            Raw(StepType.End, "降温", ("mode", "seal"), ("cool", 30)),
        };
        var p = P("OX", "低温氧化 · 60 ℃", "PR8", T(11, 15, 10, 12), "张工", true);
        p.Steps.AddRange(StepDefaults.Migrate(legacy, out bool migrated, out var notes));
        p.Migrated = migrated;
        p.MigrationNotes.AddRange(notes);
        return p;
    }

    /// <summary>不补默认值的一步（旧格式的参数原样）。</summary>
    private static PcStep Raw(StepType t, string name, params (string K, object V)[] ps)
    {
        var s = new PcStep(t, name);
        foreach (var (k, v) in ps)
        {
            if (v is string str) s.Sel[k] = str;
            else s.Num[k] = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        return s;
    }

    /// <summary>样稿 LIB（顺序同样稿）+ 推荐模板「惰性气氛加热」。</summary>
    public static List<PcProgram> Library()
    {
        var h1 = ProgramTemplates.Hydrogenation("H1", "PR8", "催化加氢筛选 · 120 ℃");
        h1.Modified = T(11, 12, 9, 41); h1.By = "李工"; h1.Draft = false;
        h1.Steps[4] = ProgramTemplates.Heat(120, 10);
        h1.Steps[5] = ProgramTemplates.Press("A", 3.0, name: "升压");
        h1.Steps[6] = ProgramTemplates.React(90);
        var lk = ProgramTemplates.LeakOnly("LK", "PR8", "气密预检");
        lk.Modified = T(11, 8, 14, 5); lk.By = "张工"; lk.Draft = false;
        var ih = ProgramTemplates.InertHeating("IH", "PR8");
        ih.Modified = T(11, 13, 15, 30); ih.By = "张工"; ih.Draft = false;
        return new()
        {
            P("A", "平行合成程序 A", "PR8", T(11, 14, 16, 20), "张工", false, Basic(150, 15, 2.5, 45, 40, reactName: "恒温反应")),
            h1,
            lk,
            LegacyOx(),
            ih,
            P("B", "高压加氢-150", "PR4", T(11, 10, 11, 30), "王工", false, Basic(150, 10, 5.2, 120, 40)),
            P("C", "筛选-80 ℃", "PR16", T(11, 15, 8, 50), "李工", false, Basic(80, 5, 1.2, 60, 25)),
        };
    }

    /// <summary>样稿 MX.name。</summary>
    public const string MatrixName = "催化剂 × 温度筛选 · 批次 8";

    /// <summary>
    /// 样稿 mxBuild 的演示部分（只在 1号机 · H1 第一次打开时）：第 j 路（含停用的）升温 100 + (j % 4) × 10 ℃；前 4 路 Pd/C 5%、其余 Pt/C 5%；RV4 压力 4.0 MPa（超范围，演示校验）。
    /// </summary>
    public static void SeedMatrix(MatrixPlan m, IReadOnlyList<int> channels)
    {
        int hi = m.Steps.FindIndex(s => s.Type == StepType.Heat), pi = m.Steps.FindIndex(s => s.Type == StepType.Press);
        for (int j = 0; j < channels.Count; j++)
        {
            int n = channels[j];
            if (hi >= 0) m.SetPer(hi, "temp", n, 100 + (j % 4) * 10);
            m.Samples[n] = j < 4 ? "Pd/C 5%" : "Pt/C 5%";
        }
        if (pi >= 0 && channels.Contains(4)) m.SetPer(pi, "pres", 4, 4.0);
    }
}
