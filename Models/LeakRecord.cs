using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ParallelReactor.Services;

namespace ParallelReactor.Models;

/// <summary>泄漏测试的一个采样点（某通道某次读压）。</summary>
public sealed record LeakSample(int Rv, string Phase, double TSec, DateTimeOffset Time, int? Raw, double Psig, double TempC);

/// <summary>泄漏测试单通道汇总（写 summary CSV / JSON）。无法计算的量为 null。</summary>
public sealed class LeakChannelSummary
{
    public int Rv { get; init; }
    public string Status { get; init; } = "";        // ok / bad / skip / ""（未完成）
    public string Detail { get; init; } = "";        // 界面上的结论文字
    public double? JudgedDropPsiPerH { get; init; }  // 现有判读用的压降速率（正=降），与界面一致
    public int N { get; init; }                      // 测量窗口内点数
    public double? WindowStartSec { get; init; }     // 测量窗口（相对测试开始，秒）
    public double? WindowEndSec { get; init; }
    public double? TRefC { get; init; }              // 补偿参考温度（测量窗口起点釜温）
    public double? StartPsig { get; init; }
    public double? EndPsig { get; init; }
    public double? SlopeRawPsiPerH { get; init; }    // 未补偿表压斜率 dP/dt（负=降）
    public double? SlopeRawKpaPerH { get; init; }
    public double? R2Raw { get; init; }
    public int NComp { get; init; }                  // 有有效温度、参与补偿拟合的点数
    public double? SlopeCompPsiPerH { get; init; }   // 补偿后绝压斜率 dP/dt（负=降）
    public double? SlopeCompKpaPerH { get; init; }
    public double? R2Comp { get; init; }
}

/// <summary>
/// 一次泄漏测试的逐点记录与汇总（纯逻辑）。阶段：prep（初始读压）/ fill / equilibrate / measure / vent。
/// 补偿参考温度 T_ref 取测量窗口起点釜温（与判读用的 <see cref="LeakMath.Compensate"/> 同一参考），
/// P_corr = P_abs × T_ref / T（K），大气压按 101.325 kPa（见 <see cref="LeakFit"/>）。
/// </summary>
public sealed class LeakRecord
{
    public const string Prep = "prep", Fill = "fill", Equilibrate = "equilibrate", Measure = "measure", Vent = "vent";

    public string RunId { get; }
    public DateTimeOffset Start { get; }
    public List<LeakSample> Samples { get; } = new();
    public Dictionary<int, double> TRef { get; } = new();

    public LeakRecord(DateTimeOffset start, string? runId = null)
    {
        Start = start;
        RunId = runId ?? "leak-" + start.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
    }

    public void Add(int rv, string phase, DateTimeOffset t, int? raw, double psig, double tempC)
        => Samples.Add(new LeakSample(rv, phase, (t - Start).TotalSeconds, t, raw, psig, tempC));

    /// <summary>补偿后绝压（kPa），温度或参考温度无效时 NaN。</summary>
    public double CorrKpa(LeakSample s)
        => TRef.TryGetValue(s.Rv, out var r) ? LeakFit.CorrectedAbsKpa(s.Psig, s.TempC, r) : double.NaN;

    public static readonly string SamplesHeader = Csv.Line(new[]
    {
        "测试编号", "RV", "阶段", "t(s)", "时间", "压力原码", "压力(psig)", "压力(kPa表压)", "温度(℃)",
        "P_abs(kPa)", "T_ref(℃)", "P_corr(kPa绝压)", "P_corr(psia)", "已温度补偿",
    });

    /// <summary>逐点 CSV（含表头与换行）。</summary>
    public string SamplesCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine(SamplesHeader);
        foreach (var s in Samples)
        {
            double corr = CorrKpa(s);
            double? tref = TRef.TryGetValue(s.Rv, out var r) ? r : null;
            sb.AppendLine(Csv.Line(new[]
            {
                RunId, $"RV{s.Rv}", s.Phase, Csv.Num(s.TSec, "0.0##"), Csv.Time(s.Time), Csv.Int(s.Raw),
                Csv.Num(s.Psig, "0.0###"), Csv.Num(s.Psig * LeakFit.KpaPerPsi, "0.0##"), Csv.Num(s.TempC, "0.0##"),
                Csv.Num(LeakFit.PsigToAbsKpa(s.Psig), "0.0##"), Csv.Num(tref, "0.0##"),
                Csv.Num(corr, "0.0##"), Csv.Num(corr / LeakFit.KpaPerPsi, "0.0###"), Csv.Bool(!double.IsNaN(corr)),
            }));
        }
        return sb.ToString();
    }

    /// <summary>按通道汇总测量窗口（phase=measure）的拟合结果。</summary>
    public List<LeakChannelSummary> Summarize(IEnumerable<(int Rv, string Status, string Detail)> cells,
        IReadOnlyDictionary<int, double>? judged)
    {
        var list = new List<LeakChannelSummary>();
        foreach (var (rv, status, detail) in cells)
        {
            var m = Samples.Where(s => s.Rv == rv && s.Phase == Measure).OrderBy(s => s.TSec).ToList();
            var raw = LeakFit.FitPerHour(m.Select(s => (s.TSec, s.Psig)).ToList());
            var compPts = m.Select(s => (s.TSec, Kpa: CorrKpa(s))).Where(p => !double.IsNaN(p.Kpa)).ToList();
            var comp = LeakFit.FitPerHour(compPts.Select(p => (p.TSec, p.Kpa / LeakFit.KpaPerPsi)).ToList());
            list.Add(new LeakChannelSummary
            {
                Rv = rv,
                Status = status,
                Detail = detail,
                JudgedDropPsiPerH = judged != null && judged.TryGetValue(rv, out var j) ? j : null,
                N = m.Count,
                WindowStartSec = m.Count > 0 ? m[0].TSec : null,
                WindowEndSec = m.Count > 0 ? m[^1].TSec : null,
                TRefC = TRef.TryGetValue(rv, out var tr) && !double.IsNaN(tr) ? tr : null,
                StartPsig = m.Count > 0 ? m[0].Psig : null,
                EndPsig = m.Count > 0 ? m[^1].Psig : null,
                SlopeRawPsiPerH = V(raw.Slope),
                SlopeRawKpaPerH = V(raw.Slope * LeakFit.KpaPerPsi),
                R2Raw = V(raw.R2),
                NComp = compPts.Count,
                SlopeCompPsiPerH = V(comp.Slope),
                SlopeCompKpaPerH = V(comp.Slope * LeakFit.KpaPerPsi),
                R2Comp = V(comp.R2),
            });
        }
        return list;
    }

    private static double? V(double d) => double.IsNaN(d) || double.IsInfinity(d) ? null : d;

    public static readonly string SummaryHeader = Csv.Line(new[]
    {
        "测试编号", "RV", "结论", "详情", "判读压降速率(psi/h,正=降)", "测量点数", "窗口起(s)", "窗口止(s)", "T_ref(℃)",
        "起始压力(psig)", "结束压力(psig)", "未补偿斜率(psi/h)", "未补偿斜率(kPa/h)", "未补偿R²",
        "补偿点数", "补偿斜率(psi/h)", "补偿斜率(kPa/h)", "补偿R²",
    });

    public string SummaryCsv(IEnumerable<LeakChannelSummary> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryHeader);
        foreach (var r in rows)
            sb.AppendLine(Csv.Line(new[]
            {
                RunId, $"RV{r.Rv}", r.Status, r.Detail, Csv.Num(r.JudgedDropPsiPerH, "0.0###"), Csv.Int(r.N),
                Csv.Num(r.WindowStartSec, "0.0#"), Csv.Num(r.WindowEndSec, "0.0#"), Csv.Num(r.TRefC, "0.0##"),
                Csv.Num(r.StartPsig, "0.0###"), Csv.Num(r.EndPsig, "0.0###"),
                Csv.Num(r.SlopeRawPsiPerH, "0.0####"), Csv.Num(r.SlopeRawKpaPerH, "0.0####"), Csv.Num(r.R2Raw, "0.0####"),
                Csv.Int(r.NComp), Csv.Num(r.SlopeCompPsiPerH, "0.0####"), Csv.Num(r.SlopeCompKpaPerH, "0.0####"),
                Csv.Num(r.R2Comp, "0.0####"),
            }));
        return sb.ToString();
    }

    /// <summary>汇总 JSON（含测试参数与假设说明）。</summary>
    public string SummaryJson(IEnumerable<LeakChannelSummary> rows, object parameters)
        => JsonSerializer.Serialize(new
        {
            runId = RunId,
            start = Csv.Time(Start),
            assumptions = "P_corr = P_abs × T_ref / T（K）；T_ref = 测量窗口起点釜温；P_abs = 表压 + 101.325 kPa（标准大气压，系统无气压计）；斜率为最小二乘 dP/dt（负=压力下降）",
            parameters,
            channels = rows,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
}
