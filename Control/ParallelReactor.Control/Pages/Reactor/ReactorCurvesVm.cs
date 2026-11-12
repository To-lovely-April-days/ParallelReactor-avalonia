using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 反应釜页的「曲线」面板（样稿 js/pages/reactor.js curvesPanel / chartOpts / syncChart；PORT-PLAN §5 M5「曲线」）。一个反应釜页一个实例。
/// <list type="bullet">
/// <item>标题栏：图例（温度 / 设定 / 压力 / 目标 / 标记，窄面板依次让出去，见视图）+ 分段 10 / 30 / 60 min（<see cref="Range"/>，默认 30；每段提示「近 30 min」）。</item>
/// <item>曲线：Core 的 <see cref="ReactorChartOptions.Build"/>（样稿 reactorChartOpts：温度 --ch-t 实线 2.5、温度设定 --ch-t-sp 虚线、压力 --ch-p 右轴、压力设定 --ch-p-sp 虚线、
///   这个反应釜的和整机的会话标记 --ch-mark；反应釜自己是报警状态或压力 ≥ 量程 × 90 % 时加 --ch-limit「上限 3.000 MPa」；分带：温度在上、压力在下），
///   设备断线时空数据写「离线 · 暂无数据」。</item>
/// <item>数据：数据源的 Core 环形缓冲（IChannelHistory.Channel，10 s 一格、增量追加），每次把窗口里的点拷进这里复用的四个缓冲（<see cref="PointWindow"/>，不分配）；
///   输入（缓冲的版本、横轴的「现在」、窗口、报警、当前压力、在线、标记、型号）都没变时不重建（冻结的截图里一次都不重建）。</item>
/// </list>
/// 纯视图模型（App.Tests 直接测：与样稿同一个演示数据下的 FromDemo 逐点相同）。
/// </summary>
public sealed partial class ReactorCurvesVm : ObservableObject
{
    public const string DefaultRange = "30";

    private readonly PointWindow _t = new(), _tsp = new(), _p = new(), _psp = new();
    private DeviceVm? _d;
    private (ChannelHistory? H, int Version, DateTime Now, int Min, bool Alarm, double? PNow, bool Online, IReadOnlyList<SessionMark>? Marks, ModelProfile? Model, bool Installed) _key;
    private bool _built;

    public ReactorCurvesVm(int k, int n)
    {
        K = k;
        N = n;
        var opts = new List<SegOption>();
        foreach (int m in ReactorChartOptions.Ranges)
            opts.Add(new SegOption { Value = m.ToString(System.Globalization.CultureInfo.InvariantCulture), Label = m.ToString(System.Globalization.CultureInfo.InvariantCulture), Tip = ReactorChartOptions.RangeTip(m) });
        RangeOptions = opts.AsReadOnly();
    }

    public int K { get; }
    public int N { get; }

    /// <summary>分段 10 / 30 / 60（SegControl 的选项；值是分钟数的文字）。</summary>
    public IReadOnlyList<SegOption> RangeOptions { get; }

    /// <summary>窗口（分钟数的文字，SegControl 的 Value，双向）：「10」/「30」/「60」，别的值回到「30」。</summary>
    [ObservableProperty] private string _range = DefaultRange;

    /// <summary>窗口（分钟）。</summary>
    public int Minutes => int.TryParse(Range, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var m) && IsRange(m) ? m : ReactorChartOptions.DefaultMinutes;

    /// <summary>曲线的输入（数据变了才换一个新实例）；没有设备 / 反应釜未安装为 null。</summary>
    [ObservableProperty] private ChartOptions? _options;

    /// <summary>重建次数（核对「数据没变就不重建」用）。</summary>
    public int Builds { get; private set; }

    private static bool IsRange(int m)
    {
        foreach (int r in ReactorChartOptions.Ranges) if (r == m) return true;
        return false;
    }

    partial void OnRangeChanged(string value)
    {
        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var m) || !IsRange(m))
        {
            Range = DefaultRange;
            return;
        }
        Rebuild();
    }

    /// <summary>像新打开的页面（截图、外框的重置视图）：窗口回到 30 min。</summary>
    public void ResetView() => Range = DefaultRange;

    /// <summary>外框每次数据变化后（页面显示着时）就地刷新。</summary>
    public void Refresh(DeviceVm? d)
    {
        _d = d;
        Rebuild();
    }

    private void Rebuild()
    {
        var d = _d;
        if (d is null)
        {
            _built = false;
            Options = null;
            return;
        }
        var src = d.Source;
        var rd = src.Read(N);
        var hist = src.History;
        var h = rd.Installed ? hist.Channel(N) : null;
        var now = hist.Now;
        int min = Minutes;
        bool online = src.Conn.Online;
        // 样稿 reactorChartOpts：nearLimit = ch.state === 'alarm'（反应釜自己的状态，不按断线改写）|| 当前压力 ≥ 量程 × 0.9
        bool alarm = rd.Own == StateKey.Alarm;
        var marks = hist.Marks;
        var key = (h, h?.Version ?? -1, now, min, alarm, rd.PressureMPa, online, (IReadOnlyList<SessionMark>?)marks, (ModelProfile?)src.Model, rd.Installed);
        if (_built && key == _key) return;
        _key = key;
        _built = true;
        if (!rd.Installed)
        {
            Options = null;
            return;
        }
        Builds++;
        Options = ReactorChartOptions.Build(src.Model, N, now, min,
            _t.Fill(h, HistoryField.TempC, now, min), _tsp.Fill(h, HistoryField.SpC, now, min),
            _p.Fill(h, HistoryField.PressureMPa, now, min), _psp.Fill(h, HistoryField.PTargetMPa, now, min),
            marks, rd.PressureMPa, alarm, bands: true, offline: !online);
    }
}
