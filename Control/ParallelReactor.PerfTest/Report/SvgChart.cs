using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ParallelReactor.PerfTest.Measure;

namespace ParallelReactor.PerfTest.Report;

/// <summary>一条曲线：左轴（温度 ℃）或右轴（压力 psi / 转速 rpm）。</summary>
public sealed record SvgSeries(string Name, string Color, IReadOnlyList<Pt> Points, bool RightAxis);
/// <summary>阴影区（WaitStable / Hold 窗口），秒为相对起点。</summary>
public sealed record SvgBand(double FromSec, double ToSec, string Label, string Fill);
/// <summary>标记竖线。</summary>
public sealed record SvgMarker(double Sec, string Label);

/// <summary>
/// 自写的小 SVG 曲线生成器：与主界面曲线同 470×190 坐标、同网格 / 轴 / 刻度字号（mock 的 .chart-box）。
/// 左轴温度（0..YLeftMax）、右轴压力 / 转速（0..YRightMax）、底轴分钟。同一函数供报告与单测。
/// </summary>
public static class SvgChart
{
    public const string Grid = "#dedfe1", Axis = "#aaaeb1", Tick = "#666a6c", Label = "#55595b";
    public const string LineTemp = "#91999a", LinePress = "#9ab4a1", LineSpeed = "#7f8d92", LineSp = "#c9b7f0";
    private const double W = 470, H = 190, L = 46, R = 424, T = 12, B = 160;

    public static string Render(IReadOnlyList<SvgSeries> series, double? yLeftMax = null, double? yRightMax = null, string xLabel = "时间 (mins)",
        string yLeftLabel = "温度 (℃)", string yRightLabel = "压力 (psi) / 转速 (rpm)", IReadOnlyList<SvgBand>? bands = null, IReadOnlyList<SvgMarker>? markers = null)
    {
        var ci = CultureInfo.InvariantCulture;
        double xMaxSec = Math.Max(60, series.SelectMany(s => s.Points).Select(p => p.Sec).DefaultIfEmpty(60).Max());
        double xMaxMin = Math.Ceiling(xMaxSec / 60.0);
        double yl = yLeftMax ?? NiceMax(series.Where(s => !s.RightAxis).SelectMany(s => s.Points).Select(p => p.Y), 100);
        double yr = yRightMax ?? NiceMax(series.Where(s => s.RightAxis).SelectMany(s => s.Points).Select(p => p.Y), 500);
        double X(double sec) => L + (R - L) * (sec / 60.0) / xMaxMin;
        double YL(double v) => B - (B - T) * Math.Clamp(v / yl, -0.05, 1.05);
        double YR(double v) => B - (B - T) * Math.Clamp(v / yr, -0.05, 1.05);

        var sb = new StringBuilder();
        sb.Append("<svg class=\"chart\" viewBox=\"0 0 470 190\" preserveAspectRatio=\"none\" xmlns=\"http://www.w3.org/2000/svg\" font-family=\"Arial\" font-size=\"7\">");
        sb.Append("<rect x=\"0\" y=\"0\" width=\"470\" height=\"190\" fill=\"#fafafa\"/>");
        // 阴影窗口
        if (bands != null)
            foreach (var b in bands)
            {
                double x0 = X(Math.Max(0, b.FromSec)), x1 = X(Math.Min(xMaxMin * 60, b.ToSec));
                if (x1 <= x0) continue;
                sb.Append($"<rect x=\"{F(x0)}\" y=\"{F(T)}\" width=\"{F(x1 - x0)}\" height=\"{F(B - T)}\" fill=\"{b.Fill}\" opacity=\"0.35\"/>");
                sb.Append($"<text x=\"{F(x0 + 2)}\" y=\"{F(T + 7)}\" fill=\"{Label}\" font-size=\"6\">{Html.Esc(b.Label)}</text>");
            }
        // 网格 + 刻度
        for (int i = 0; i <= 10; i++)
        {
            double y = B - (B - T) * i / 10.0;
            sb.Append($"<line x1=\"{F(L)}\" y1=\"{F(y)}\" x2=\"{F(R)}\" y2=\"{F(y)}\" stroke=\"{Grid}\" stroke-width=\"0.8\"/>");
            sb.Append($"<text x=\"{F(L - 4)}\" y=\"{F(y + 2.5)}\" text-anchor=\"end\" fill=\"{Tick}\">{Metrics.Fmt(yl * i / 10, 1)}</text>");
            sb.Append($"<text x=\"{F(R + 4)}\" y=\"{F(y + 2.5)}\" fill=\"{Tick}\">{Metrics.Fmt(yr * i / 10, 1)}</text>");
        }
        int xTicks = xMaxMin <= 10 ? (int)xMaxMin : 8;
        for (int i = 0; i <= xTicks; i++)
        {
            double sec = xMaxMin * 60 * i / xTicks;
            double x = X(sec);
            sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(T)}\" x2=\"{F(x)}\" y2=\"{F(B)}\" stroke=\"{Grid}\" stroke-width=\"0.8\"/>");
            sb.Append($"<text x=\"{F(x)}\" y=\"{F(B + 9)}\" text-anchor=\"middle\" fill=\"{Tick}\">{Metrics.Fmt(sec / 60, 1)}</text>");
        }
        sb.Append($"<line x1=\"{F(L)}\" y1=\"{F(T)}\" x2=\"{F(L)}\" y2=\"{F(B)}\" stroke=\"{Axis}\" stroke-width=\"0.9\"/>");
        sb.Append($"<line x1=\"{F(R)}\" y1=\"{F(T)}\" x2=\"{F(R)}\" y2=\"{F(B)}\" stroke=\"{Axis}\" stroke-width=\"0.9\"/>");
        sb.Append($"<line x1=\"{F(L)}\" y1=\"{F(B)}\" x2=\"{F(R)}\" y2=\"{F(B)}\" stroke=\"{Axis}\" stroke-width=\"0.9\"/>");
        // 曲线
        foreach (var s in series)
        {
            if (s.Points.Count == 0) continue;
            var pts = new StringBuilder();
            foreach (var p in s.Points)
            {
                if (double.IsNaN(p.Y)) continue;
                pts.Append(F(X(p.Sec))).Append(',').Append(F(s.RightAxis ? YR(p.Y) : YL(p.Y))).Append(' ');
            }
            sb.Append($"<polyline points=\"{pts.ToString().TrimEnd()}\" fill=\"none\" stroke=\"{s.Color}\" stroke-width=\"1.15\" stroke-linejoin=\"round\" stroke-linecap=\"round\"/>");
        }
        // 标记线
        if (markers != null)
            foreach (var m in markers)
            {
                double x = X(m.Sec);
                sb.Append($"<line x1=\"{F(x)}\" y1=\"{F(T)}\" x2=\"{F(x)}\" y2=\"{F(B)}\" stroke=\"#765cc5\" stroke-width=\"0.8\" stroke-dasharray=\"2 2\"/>");
                sb.Append($"<text x=\"{F(x + 2)}\" y=\"{F(B - 3)}\" fill=\"#765cc5\" font-size=\"6\">{Html.Esc(m.Label)}</text>");
            }
        // 轴标签 + 图例
        sb.Append($"<text x=\"{F((L + R) / 2)}\" y=\"184\" text-anchor=\"middle\" fill=\"{Label}\">{Html.Esc(xLabel)}</text>");
        sb.Append($"<text transform=\"translate(9,{F((T + B) / 2)}) rotate(-90)\" text-anchor=\"middle\" fill=\"{Label}\">{Html.Esc(yLeftLabel)}</text>");
        sb.Append($"<text transform=\"translate(463,{F((T + B) / 2)}) rotate(90)\" text-anchor=\"middle\" fill=\"{Label}\">{Html.Esc(yRightLabel)}</text>");
        double lx = L + 4;
        foreach (var s in series)
        {
            sb.Append($"<line x1=\"{F(lx)}\" y1=\"176\" x2=\"{F(lx + 10)}\" y2=\"176\" stroke=\"{s.Color}\" stroke-width=\"1.5\"/>");
            sb.Append($"<text x=\"{F(lx + 13)}\" y=\"178.5\" fill=\"{Label}\" font-size=\"6.5\">{Html.Esc(s.Name)}</text>");
            lx += 16 + s.Name.Length * 6.5;
        }
        sb.Append("</svg>");
        return sb.ToString();
    }

    private static double NiceMax(IEnumerable<double> values, double fallback)
    {
        var v = values.Where(x => !double.IsNaN(x)).DefaultIfEmpty(double.NaN).Max();
        if (double.IsNaN(v) || v <= 0) return fallback;
        return Metrics.RoundUpToStep(v * 1.1);
    }

    private static string F(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
}
