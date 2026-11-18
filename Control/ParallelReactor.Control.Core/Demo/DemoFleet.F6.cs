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
/// 融合版 F6 的演示程序库（样稿 LIB：名字、适用型号、修改时刻与人、草稿、步骤 —— 参数换成 HMI 的键和协议码，压力 MPa）。
/// id 与 DemoFusion.Library 相同（A / H1 / LK / OX / B / C），所以运行表上在跑的程序能在程序库里对上「运行 N 路」。
/// 另有样稿 mxBuild 的演示条件矩阵（1号机 · H1：Pd/C、Pt/C 各一组，100–130 ℃ 四档，RV4 压力故意填错）。
/// </summary>
public static class DemoPrograms
{
    private static DateTime T(int mo, int d, int h, int mi) => new(2026, mo, d, h, mi, 0, DateTimeKind.Local);

    private static PcStep S(StepType t, string name, params (string K, object V)[] ps)
    {
        var s = new PcStep(t, name);
        foreach (var (k, v) in ps)
        {
            if (v is string str) s.Sel[k] = str;
            else s.Num[k] = Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture);
        }
        return s;
    }

    private static PcStep Purge(string name, int cyc) => S(StepType.Purge, name, ("gas", "inert"), ("cycles", cyc), ("fill", 3), ("vent", 5));
    private static PcStep Heat(string name, double temp, double wait) => S(StepType.Heat, name, ("mode", "fixed"), ("temp", temp), ("wait", wait));
    private static PcStep Press(string name, double p, string gas = "A", string reg = "on") => S(StepType.Press, name, ("gas", gas), ("pres", p), ("regulate", reg));
    private static PcStep Stir(string name, double rpm, double dur) => S(StepType.Stir, name, ("rpm", rpm), ("dur", dur));
    private static PcStep Wait(string name, double max) => S(StepType.Wait, name, ("mode", "fixed"), ("time", max));
    private static PcStep React(string name, double max, string end = "time", double mmol = 5) => S(StepType.React, name, ("endBy", end), ("time", max), ("mmol", mmol));
    private static PcStep End(string name, double cool, string how = "seal") => S(StepType.End, name, ("mode", how), ("cool", cool));

    private static PcProgram P(string id, string name, string model, DateTime mod, string by, bool draft, params PcStep[] steps)
    {
        var p = new PcProgram { Id = id, Name = name, Model = model, Modified = mod, By = by, Draft = draft };
        p.Steps.AddRange(steps);
        return p;
    }

    /// <summary>样稿 LIB（顺序同样稿）。</summary>
    public static List<PcProgram> Library() => new()
    {
        P("A", "平行合成程序 A", "PR8", T(11, 14, 16, 20), "张工", false,
            Heat("升温", 150, 15), Press("升压", 2.5), React("恒温反应", 45), End("降温", 40)),
        P("H1", "催化加氢筛选 · 120 ℃", "PR8", T(11, 12, 9, 41), "李工", false,
            Purge("吹扫置换", 8), Heat("升温", 120, 10), Stir("搅拌", 400, 10), Press("升压", 3.0), React("反应", 90), End("降温", 35)),
        P("LK", "气密预检", "PR8", T(11, 8, 14, 5), "张工", false,
            Purge("吹扫", 5), Press("充压", 1.0, reg: "off"), Wait("保压观察", 30), End("排空", 40, "vent")),
        P("OX", "低温氧化 · 60 ℃", "PR8", T(11, 15, 10, 12), "张工", true,
            Heat("升温", 60, 10), Press("升压", 3.4, "B"), React("反应", 240, "either", 12), End("降温", 30)),
        P("B", "高压加氢-150", "PR4", T(11, 10, 11, 30), "王工", false,
            Heat("升温", 150, 10), Press("升压", 5.2), React("反应", 120), End("降温", 40)),
        P("C", "筛选-80 ℃", "PR16", T(11, 15, 8, 50), "李工", false,
            Heat("升温", 80, 5), Press("升压", 1.2), React("反应", 60), End("降温", 25)),
    };

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
