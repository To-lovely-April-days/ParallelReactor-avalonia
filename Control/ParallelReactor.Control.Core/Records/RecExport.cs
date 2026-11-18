using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ParallelReactor.Control.Core.Records;

/// <summary>
/// 记录页的导出（融合版 F7）：「生成报告」= 8 路对比的结果表 + 统计（CSV，Excel 直接打开；PDF 报告在后续阶段）；
/// 「下载数据」在没有设备上的原始 CSV 时（演示数据、进行中的会话）按各路数据写一份简表（时间、每路 温度 / 压力 / 吸气量）。
/// 数字按 InvariantCulture；文字字段按 RFC 4180 加引号。
/// </summary>
public static class RecExport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string Q(string s) => s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    private static string N(double v, string f) => double.IsFinite(v) ? v.ToString(f, Inv) : "";

    /// <summary>结果表（每路一行 + 统计行）。</summary>
    public static string ResultsCsv(RecSession s, string deviceAlias, RecCompare c)
    {
        var sb = new StringBuilder();
        sb.Append("会话,").Append(Q(s.Id)).Append('\n');
        sb.Append("名称,").Append(Q(s.Name)).Append('\n');
        sb.Append("设备,").Append(Q(deviceAlias)).Append('\n');
        sb.Append("程序,").Append(Q(RecTexts.ProgramText(s))).Append('\n');
        sb.Append("开始,").Append(s.Start.ToString("yyyy-MM-dd HH:mm", Inv)).Append('\n');
        sb.Append("时长 min,").Append(N(c.Span, "0")).Append('\n');
        sb.Append('\n');
        if (c.IsHold)
        {
            sb.Append("通道,压降 psi/h,压降 kPa/h,判定\n");
            foreach (var r in c.Rows)
                sb.Append("RV").Append(r.N.ToString(Inv)).Append(',').Append(r.Used ? N(r.Drop, "0.00") : "").Append(',')
                  .Append(r.Used ? N(r.Drop * RecCompute.KPaPerPsi, "0.0") : "").Append(',').Append(Q(r.Result)).Append('\n');
            sb.Append("判据,≤ ").Append(c.HoldLimit.ToString("0.0##", Inv)).Append(" psi/h（第 10–40 min）\n");
            return sb.ToString();
        }
        sb.Append("通道,吸气 mmol,与平均相比 %,最大速率 mmol/min,到 90 % 用时 min,状态,计入统计\n");
        foreach (var r in c.Rows)
        {
            sb.Append("RV").Append(r.N.ToString(Inv)).Append(',');
            if (r.Used)
                sb.Append(N(r.G, "0.0")).Append(',').Append(N(RecCompute.Rel(r.G, c.Stats.Mean), "0.0")).Append(',').Append(N(r.Rate, "0.00")).Append(',')
                  .Append(r.T90 is double t ? N(t, "0") : r.Live ? "未到" : "").Append(',');
            else sb.Append(",,,,");
            sb.Append(Q(r.Result)).Append(',').Append(r.Used ? "是" : "否").Append('\n');
        }
        sb.Append("平均,").Append(N(c.Stats.Mean, "0.00")).Append('\n');
        sb.Append("SD,").Append(N(c.Stats.Sd, "0.00")).Append('\n');
        sb.Append("RSD %,").Append(N(c.Stats.Rsd, "0.0")).Append('\n');
        sb.Append("路数,").Append(c.Stats.N.ToString(Inv)).Append('\n');
        return sb.ToString();
    }

    /// <summary>各路数据的简表：时间、RVn 温度 ℃ / 压力 MPa / 吸气量 mmol（按第一路的采样时刻对齐，各路线性插值）。</summary>
    public static string SeriesCsv(RecSession s, RecSeriesSet set)
    {
        var sb = new StringBuilder("时间");
        foreach (var c in set.Channels)
        {
            string p = "RV" + c.N.ToString(Inv) + "_";
            sb.Append(',').Append(p).Append("PV(℃)").Append(',').Append(p).Append("压力(MPa)").Append(',').Append(p).Append("吸气量(mmol)");
        }
        sb.Append('\n');
        var grid = set.Channels.OrderByDescending(c => c.Points.Count).FirstOrDefault()?.Points;
        if (grid == null) return sb.ToString();
        var cur = new int[set.Channels.Count * 3];
        foreach (var g in grid)
        {
            sb.Append(s.Start.AddMinutes(g.M).ToString("yyyy-MM-ddTHH:mm:ss", Inv));
            for (int i = 0; i < set.Channels.Count; i++)
            {
                var pts = set.Channels[i].Points;
                sb.Append(',').Append(N(RecCompute.Interp(pts, g.M, ref cur[i * 3], p => p.T), "0.0##"));
                sb.Append(',').Append(N(RecCompute.Interp(pts, g.M, ref cur[i * 3 + 1], p => p.P), "0.00000"));
                sb.Append(',').Append(N(RecCompute.Interp(pts, g.M, ref cur[i * 3 + 2], p => p.G), "0.000"));
            }
            sb.Append('\n');
        }
        return sb.ToString();
    }
}
