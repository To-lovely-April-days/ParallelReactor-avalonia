using System;
using System.Collections.Generic;
using System.Text.Json;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// HMI 程序（§6.9 的 ProgramDoc：步骤类型 + params，压力 psi）→ 样稿的步骤（<see cref="ProgramStep"/>，压力 MPa），
/// 供 <see cref="StepText"/> / <see cref="Progress"/> 显示真机上本机下发的程序（M5 的程序台账）。
/// 参数名、默认值与「保持上一步温度 / 压力」的判断沿用旧 PlanEditorVm.BuildSections / DurationOf（只读复用，PORT-PLAN §1.3）：
/// <list type="bullet">
/// <item>heat：temp（150 ℃）、wait（等待平衡 15 min → 条件「步骤时间 &gt; 15 min」）、mode = curve → 曲线升温。</item>
/// <item>press：gas（A）、pres（50 psi → MPa）、regulate（on = 稳压保持，off = 目标后保持）；条件「压力 ≥ 目标」；温度保持上一步。</item>
/// <item>react：time（120 min）；压力 = 上一步充压的目标、气路 = 上一步的气路。wait：time（30 min）。stir：rpm（1000）、dur（5 min）。</item>
/// <item>purge：gas（inert）、cycles（10）；时长 = cycles × (fill 3 s + vent 5 s + 1 s) / 60，保留 1 位小数。</item>
/// <item>end：cool（40 ℃ → 条件「温度 &lt; 40 ℃」）、mode = vent → 泄压（seal / purge / quench 按不泄压显示）。</item>
/// </list>
/// 真机程序只有搅拌步骤带转速（PORT-PLAN §3.5），其它步骤的转速为 null（卡片显示「—」）；没有 cool 步骤。
/// 不认识的步骤类型跳过。预估分钟数：按时间结束的步骤 = 该时长，按温度 / 压力结束的为 0（未知，M5 的 EtaEstimator 再估）。
/// </summary>
public static class ProgramDocs
{
    /// <summary>转换全部步骤。</summary>
    public static IReadOnlyList<ProgramStep> ToSteps(ProgramDoc doc, ModelProfile? model)
    {
        if (doc is null) throw new ArgumentNullException(nameof(doc));
        var list = new List<ProgramStep>(doc.Steps.Count);
        double? lastTemp = null, lastPres = null;
        string? lastGas = null;
        foreach (var s in doc.Steps)
        {
            switch (s.Type)
            {
                case "heat":
                {
                    double temp = Num(s, "temp", 150), wait = Num(s, "wait", 15);
                    lastTemp = temp;
                    list.Add(new ProgramStep(StepKind.Heat) { Sp = temp, End = new StepEnd(EndKind.Time, wait), EstMin = wait, HeatCurve = Sel(s, "mode", "fixed") == "curve" });
                    break;
                }
                case "press":
                {
                    double mpa = Units.PsiToMpa(Num(s, "pres", 50));
                    string gas = GasKey(model, Sel(s, "gas", "A"));
                    lastPres = mpa;
                    lastGas = gas;
                    list.Add(new ProgramStep(StepKind.Press) { Sp = lastTemp, Target = mpa, Gas = gas, End = new StepEnd(EndKind.Press, mpa, "≥"), Regulate = Sel(s, "regulate", "on") == "on" });
                    break;
                }
                case "react":
                {
                    double time = Num(s, "time", 120);
                    list.Add(new ProgramStep(StepKind.React) { Sp = lastTemp, Target = lastPres, Gas = lastGas, Minutes = time, End = new StepEnd(EndKind.Time, time), EstMin = time });
                    break;
                }
                case "wait":
                {
                    double time = Num(s, "time", 30);
                    list.Add(new ProgramStep(StepKind.Wait) { Sp = lastTemp, Minutes = time, End = new StepEnd(EndKind.Time, time), EstMin = time });
                    break;
                }
                case "stir":
                {
                    double dur = Num(s, "dur", 5);
                    list.Add(new ProgramStep(StepKind.Stir) { Sp = lastTemp, Rpm = Num(s, "rpm", 1000), Minutes = dur, End = new StepEnd(EndKind.Time, dur), EstMin = dur });
                    break;
                }
                case "purge":
                {
                    double cycles = Num(s, "cycles", 10);
                    double min = Math.Round(cycles * (Num(s, "fill", 3) + Num(s, "vent", 5) + 1) / 60.0 * 10, MidpointRounding.AwayFromZero) / 10;
                    list.Add(new ProgramStep(StepKind.Purge) { Sp = lastTemp, Gas = GasKey(model, Sel(s, "gas", "inert")), Cycles = (int)Math.Round(cycles), End = new StepEnd(EndKind.Time, min), EstMin = min });
                    break;
                }
                case "end":
                {
                    double cool = Num(s, "cool", 40);
                    list.Add(new ProgramStep(StepKind.End) { CoolTo = cool, Vent = Sel(s, "mode", "seal") == "vent", End = new StepEnd(EndKind.Temp, cool, "<") });
                    break;
                }
            }
        }
        return list;
    }

    /// <summary>转换成程序库条目（名称取 doc.Name）。</summary>
    public static ProgramDef ToDef(ProgramDoc doc, ModelProfile? model, string id = "") =>
        new(id, doc?.Name ?? "", "", ToSteps(doc!, model));

    /// <summary>HMI 的气路代码（'A' / 'inert'）→ 型号表的键（'a' / 'inert'）；型号表没有对应代码时原样。</summary>
    public static string GasKey(ModelProfile? model, string code) => model?.GasByHmi(code)?.Key ?? code;

    private static double Num(ProgramStepDoc s, string key, double fallback) =>
        s.Params.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v) && double.IsFinite(v) ? v : fallback;

    private static string Sel(ProgramStepDoc s, string key, string fallback) =>
        s.Params.TryGetValue(key, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() ?? fallback : fallback;
}
