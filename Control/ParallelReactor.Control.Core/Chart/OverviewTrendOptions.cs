using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Chart;

/// <summary>温度趋势的候选设备：位置 k、别名、进入趋势的反应釜（按通道号，<see cref="OverviewTrendOptions.InTrend"/> 为真的那些）。</summary>
public sealed record TrendDevice(int K, string Alias, IReadOnlyList<int> Channels);

/// <summary>温度趋势图例的一项：反应釜号、文字（'RV1'）、颜色令牌、曲线的虚线（'7 4'；null = 实线）、图例小样的虚线（按 0.6 缩短：'4 2'）。</summary>
public sealed record TrendLegendItem(int N, string Label, string Color, string? Dash, string? SampleDash);

/// <summary>
/// 温度趋势的数据（样稿 overview.js trendData + syncChart + trendHTML）：曲线输入、图例（只有一台设备：别名 + 每条线）、
/// 进入趋势的反应釜总数（全部设备）与画出来的条数；画不全时图例末尾「显示 n/m」（<see cref="MoreText"/>，悬停 <see cref="MoreTip"/>）。
/// </summary>
public sealed record TrendData(ChartOptions Options, TrendDevice? Device, IReadOnlyList<TrendLegendItem> Legend, int Total, int Shown)
{
    public string? MoreText => Total > Shown ? "显示 " + Shown + "/" + Total : null;
    public string? MoreTip => Total > Shown ? OverviewTrendOptions.MoreTipText : null;
}

/// <summary>
/// 总览的温度趋势（样稿 js/pages/overview.js「温度趋势」：剩余高度 ≥ 200 px 时出现，见 Layout.OverviewLayout.ShowTrend）。
/// 不用设备识别色、也不把几台设备的十几条线叠在一起：只画一台 —— 选中的设备（它没有进入趋势的反应釜时取第一台有的），最多 4 条：
/// --ch-s1 / --ch-s2 两种灰轮流 × 前两条实线、后两条虚线 '7 4'；线宽 2，近 60 min，纵轴「温度 ℃」最小跨度 10；没有时「没有运行中的反应釜」。
/// 进入趋势 = 已安装，且在运行（报警也算）或程序已结束。
/// </summary>
public static class OverviewTrendOptions
{
    public const int PerDevice = 4, Minutes = 60;
    public const string Title = "温度趋势", Sub = "近 60 min · 选中设备的运行中反应釜", Empty = "没有运行中的反应釜";
    public const string MoreTipText = "选中别的设备可切换 · 全部曲线在「曲线」视图";

    /// <summary>反应釜进入趋势（样稿 trendChs）：已安装，且在运行（StateKeys.IsRunning，报警也算）或程序已结束。</summary>
    public static bool InTrend(bool installed, bool running, ProgramStatus? program) => installed && (running || program == ProgramStatus.Done);

    /// <summary>
    /// 算趋势。<paramref name="devices"/> 按设备列表顺序；<paramref name="selectedK"/> = 当前选中的设备（没有为 null）；
    /// <paramref name="series"/>(k, n) = 那个反应釜近 60 min 的温度点（最旧在前）。
    /// </summary>
    public static TrendData Build(IReadOnlyList<TrendDevice> devices, int? selectedK, DateTime now, Func<int, int, IReadOnlyList<ChartPoint>> series)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(series);
        int total = devices.Sum(d => d.Channels.Count);
        var dev = devices.FirstOrDefault(x => x.K == selectedK && x.Channels.Count > 0) ?? devices.FirstOrDefault(x => x.Channels.Count > 0);
        var list = new List<ChartSeries>();
        var legend = new List<TrendLegendItem>();
        if (dev is not null)
        {
            int i = 0;
            foreach (int n in dev.Channels.Take(PerDevice))
            {
                string color = i % 2 != 0 ? ChartTokens.S2 : ChartTokens.S1;
                string? dash = i >= 2 ? "7 4" : null;
                list.Add(new ChartSeries(dev.Alias + " · RV" + n, series(dev.K, n)) { Axis = 0, Unit = "℃", Dec = 1, Width = 2, Color = color, Dash = dash });
                legend.Add(new TrendLegendItem(n, "RV" + n, color, dash, SampleDash(dash)));
                i++;
            }
        }
        double nowMs = ChartTime.Ms(now);
        var opts = new ChartOptions
        {
            Series = list.AsReadOnly(),
            YAxes = Array.AsReadOnly(new[] { new ChartAxis { Label = ReactorChartOptions.TempAxis, Unit = "℃", MinSpan = 10 } }),
            XMin = nowMs - Minutes * 60000.0,
            XMax = nowMs,
            EmptyText = Empty,
        };
        return new TrendData(opts, dev, legend.AsReadOnly(), total, list.Count);
    }

    /// <summary>演示数据：每台设备进入趋势的反应釜（DemoChannel.IsRunning 或程序已结束）+ DemoDevice.Series 的温度。</summary>
    public static TrendData FromDemo(DemoFleet fleet, int? selectedK)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        var devs = fleet.Devices.Select(d => new TrendDevice(d.K, d.Alias,
            d.Channels.Where(c => InTrend(c.Installed, c.IsRunning, c.Program?.Status)).Select(c => c.N).ToArray())).ToArray();
        return Build(devs, selectedK, fleet.Now, (k, n) => ReactorChartOptions.Points(fleet.Device(k)!.Series(n, DemoReading.TempC, Minutes)));
    }

    /// <summary>图例小样的虚线（样稿 lgSample）：每个数字 × 0.6 取整、至少 1（'7 4' → '4 2'）。</summary>
    public static string? SampleDash(string? dash)
    {
        if (string.IsNullOrEmpty(dash)) return dash;
        return System.Text.RegularExpressions.Regex.Replace(dash, @"\d+", m =>
            Math.Max(1, Format.JsNumber.Round(double.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture) * 0.6)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// 组件陈列「曲线」分区的第三张图（样稿 placeholder.js multi）：1号机 RV1–RV3 近 60 min 的温度，线宽 1.8，虚线依次 无 / '7 4' / '2 3'，
    /// 颜色按序号自动取 --ch-s1/2/3，纵轴「温度 ℃」（最小跨度按单位 5），横轴按数据首尾。
    /// </summary>
    public static ChartOptions GalleryMulti(DemoFleet fleet)
    {
        ArgumentNullException.ThrowIfNull(fleet);
        var ss = new List<ChartSeries>();
        var d1 = fleet.Device(1);
        string?[] dashes = { null, "7 4", "2 3" };
        int i = 0;
        foreach (int n in new[] { 1, 2, 3 })
        {
            if (d1?.Channel(n) is not null)
                ss.Add(new ChartSeries(d1.Alias + " · RV" + n, ReactorChartOptions.Points(d1.Series(n, DemoReading.TempC, 60))) { Unit = "℃", Dec = 1, Width = 1.8, Dash = dashes[i] });
            i++;
        }
        return new ChartOptions
        {
            Series = ss.AsReadOnly(),
            YAxes = Array.AsReadOnly(new[] { new ChartAxis { Label = ReactorChartOptions.TempAxis, Unit = "℃" } }),
            EmptyText = ReactorChartOptions.Empty,
        };
    }
}
