using Avalonia.Controls;
using ParallelReactor.Control.Controls.Chart;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.Demo;

namespace ParallelReactor.Control.Views.Pages.Gallery;

/// <summary>
/// 陈列页「曲线」分区的三张曲线的输入（MOCK placeholder.js 的 syncCharts）：数据 = 样稿冻结时（freeze=1）的演示数据 —— 新建一份冻结的 Core DemoFleet（同「釜体」分区），
/// 用 Core 的 <see cref="ReactorChartOptions.FromDemo"/>（1号机 RV1、3号机 RV11，近 30 min）与 <see cref="OverviewTrendOptions.GalleryMulti"/>
/// （1号机 RV1–RV3 的温度，近 60 min）—— 与样稿的 reactorChartOpts / multi 逐项相同（Core 的 ChartTicksTests；App.Tests 的 CurvesTests 对照样稿画出来的 SVG）。
/// </summary>
public static class GalleryCharts
{
    private static (ChartOptions? A, ChartOptions? B, ChartOptions C)? _opts;

    /// <summary>三张曲线的输入（第一次用时生成，之后不变）：没有那台设备 / 那个反应釜时为 null（不画）。</summary>
    public static (ChartOptions? A, ChartOptions? B, ChartOptions C) Options
    {
        get
        {
            if (_opts is { } o) return o;
            var fleet = new DemoFleet(DemoParams.Default with { Freeze = true });
            var d1 = fleet.Device(1);
            var d3 = fleet.Device(3);
            var c1 = d1?.Channel(1);
            var c3 = d3?.Channel(11);
            var a = d1 != null && c1 != null ? ReactorChartOptions.FromDemo(d1, c1, 30) : null;
            var b = d3 != null && c3 != null ? ReactorChartOptions.FromDemo(d3, c3, 30) : null;
            var r = (a, b, OverviewTrendOptions.GalleryMulti(fleet));
            _opts = r;
            return r;
        }
    }
}

/// <summary>陈列页「曲线」分区：三张 <see cref="TimeSeriesChart"/>（输入见 <see cref="GalleryCharts"/>）。</summary>
public partial class ChartsSection : UserControl
{
    public ChartsSection()
    {
        InitializeComponent();
        var (a, b, c) = GalleryCharts.Options;
        Chart1.Options = a;
        Chart2.Options = b;
        Chart3.Options = c;
    }

    /// <summary>三张曲线（核对用）。</summary>
    public TimeSeriesChart[] Charts => new[] { Chart1, Chart2, Chart3 };
}
