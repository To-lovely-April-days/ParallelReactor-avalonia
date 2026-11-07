using System;
using System.Globalization;
using System.Linq;
using System.Text;
using ParallelReactor.PerfTest.Measure;
using ParallelReactor.PerfTest.Results;

namespace ParallelReactor.PerfTest.Report;

/// <summary>一页式 CSV 汇总：每个指标值一行（含通道 / 轮次），前面是运行 meta 行。</summary>
public static class CsvSummaryWriter
{
    public static string Write(RunResult r)
    {
        var sb = new StringBuilder();
        sb.Append("runId,").Append(Q(r.RunId)).Append('\n');
        sb.Append("plan,").Append(Q(r.PlanTitle + " " + r.PlanVersion)).Append('\n');
        sb.Append("startedAt,").Append(r.StartedAt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("status,").Append(HtmlReportWriter.StatusZh(r.Status)).Append('\n');
        sb.Append("device,").Append(Q($"{r.DeviceModel} {r.DeviceSerial} HMI {r.HmiVersion}")).Append('\n');
        sb.Append("simulated,").Append(r.Simulated ? "1" : "0").Append('\n');
        sb.Append("operator,").Append(Q(r.Operator)).Append('\n');
        sb.Append('\n');
        sb.Append("itemId,title,outcome,verdict,metricKey,label,unit,channel,iteration,iterationValue,value,sampleCount,reference5\n");
        foreach (var it in r.Items)
        {
            if (it.Measures.Count == 0)
            {
                sb.Append(string.Join(",", Q(it.ItemId), Q(it.Title), HtmlReportWriter.OutcomeZh(it.Outcome), it.Verdict.ToString(), "", "", "", "", "", "", "", "", "")).Append('\n');
                continue;
            }
            foreach (var m in it.Measures)
                sb.Append(string.Join(",", Q(it.ItemId), Q(it.Title), HtmlReportWriter.OutcomeZh(it.Outcome), it.Verdict.ToString(), Q(m.Key), Q(m.Label), Q(m.Unit),
                    m.Channel?.ToString() ?? "", m.Iteration?.ToString() ?? "", m.IterationValue?.ToString("0.###", CultureInfo.InvariantCulture) ?? "",
                    double.IsNaN(m.Value) ? "" : m.Value.ToString("0.####", CultureInfo.InvariantCulture), m.SampleCount.ToString(), Q(m.Reference5))).Append('\n');
        }
        return sb.ToString();
    }

    private static string Q(string? s) => ResultStore.Q(s);
}
