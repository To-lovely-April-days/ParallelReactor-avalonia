using ParallelReactor.Commands;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;
using ParallelReactor.Service;

namespace ParallelReactor.ViewModels;

/// <summary>曲线历史采样点（设计上一条历史 = 一系列该结构）。</summary>
public readonly record struct GSample(double Ts, double T, double P, double Gas);

/// <summary>实时曲线界面（GRAPH Tab）：多通道叠加，变量可切（温度/压力/气体）。</summary>
public partial class GraphViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Dictionary<int, List<GSample>> _hist = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();   // 采样时间轴（秒，单调递增）
    private readonly DateTime _t0 = DateTime.Now;   // _clock 起点对应的墙钟（曲线 x 轴 / tip 显示 HH:mm:ss 用）

    // 8 通道配色（对应 HTML colors 映射）
    private static readonly Dictionary<int, string> Palette = new()
    {
        [1] = "#5fae14", [2] = "#2f7fd6", [3] = "#9b59d0", [4] = "#d68910",
        [5] = "#e0394c", [6] = "#1fa595", [7] = "#c79a1e", [8] = "#6b665e",
    };

    public ObservableCollection<GraphChannel> Channels { get; } = new();
    public ObservableCollection<GraphLegendItem> Legend { get; } = new();
    public IReadOnlyDictionary<int, List<GSample>> History => _hist;

    [ObservableProperty] private string _metric = "gas";  // T | P | gas | all（全部 = 温度/压力/气体三曲线三 Y 轴同屏）
    [ObservableProperty] private string _range = "15";    // 5 | 15 | 60 | all
    [ObservableProperty] private string _avgRate = "—";
    [ObservableProperty] private string _rsd = "—";
    [ObservableProperty] private string _maxDev = "—";

    /// <summary>通知曲线控件重绘。</summary>
    public event Action? Changed;

    public GraphViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var r in _main.Reactors)
        {
            Channels.Add(new GraphChannel(this, r, Palette[r.Id], on: true));   // 通道启用后即显示其曲线
            _hist[r.Id] = new List<GSample>();
        }
        Refresh();
        Models.Units.Changed += OnUnitChanged;   // 全局压力单位切换后刷新压力曲线的轴/读数
    }

    /// <summary>全局压力单位变化：压力视图（含「全部」）下刷新单位标签、小数位、图例与曲线。</summary>
    private void OnUnitChanged()
    {
        if (Metric != "P" && Metric != "all") return;
        OnPropertyChanged(nameof(YUnit));
        OnPropertyChanged(nameof(Decimals));
        foreach (var ch in Channels) ch.RaiseVal();
        Refresh();
    }

    // —— 派生（工具栏高亮）——
    public bool MetricIsT => Metric == "T";
    public bool MetricIsP => Metric == "P";
    public bool MetricIsGas => Metric == "gas";
    public bool MetricIsAll => Metric == "all";
    public bool RangeIs5 => Range == "5";
    public bool RangeIs15 => Range == "15";
    public bool RangeIs60 => Range == "60";
    public bool RangeIsAll => Range == "all";

    public string YUnit => MetricUnit(Metric);
    public string YTitle => Metric == "T" ? "温度" : Metric == "P" ? "压力" : "气体消耗";
    // 压力小数位随单位（MPa 3 / bar 2 / psi 1，取自 Units）；温度 0 位、气体 2 位
    public int Decimals => MetricDecimals(Metric);
    public double RangeSeconds => Range == "all" ? double.MaxValue : double.Parse(Range) * 60;

    /// <summary>取某采样点当前变量的值（压力按全局显示单位换算）。</summary>
    public double Val(GSample s) => Val(s, Metric);

    /// <summary>取某采样点指定变量（T / P / gas）的值（压力按全局显示单位换算）。</summary>
    public double Val(GSample s, string m) => m == "T" ? s.T : m == "P" ? Models.Units.P(s.P) : s.Gas;

    /// <summary>采样时间轴（秒）→ 墙钟时间（曲线轴与 tip 显示用，不含日期）。</summary>
    public DateTime WallTime(double ts) => _t0.AddSeconds(ts);

    public static string MetricUnit(string m) => m == "T" ? "°C" : m == "P" ? Models.Units.PLabel : "mmol";
    public static string MetricName(string m) => m == "T" ? "温度" : m == "P" ? "压力" : "气体";
    public static int MetricDecimals(string m) => m == "gas" ? 2 : m == "P" ? Models.Units.PDecimals : 0;

    partial void OnMetricChanged(string value)
    {
        OnPropertyChanged(nameof(MetricIsT)); OnPropertyChanged(nameof(MetricIsP));
        OnPropertyChanged(nameof(MetricIsGas)); OnPropertyChanged(nameof(MetricIsAll));
        OnPropertyChanged(nameof(YUnit)); OnPropertyChanged(nameof(YTitle)); OnPropertyChanged(nameof(Decimals));
        foreach (var ch in Channels) ch.RaiseVal();
        Refresh();
    }

    partial void OnRangeChanged(string value)
    {
        OnPropertyChanged(nameof(RangeIs5)); OnPropertyChanged(nameof(RangeIs15));
        OnPropertyChanged(nameof(RangeIs60)); OnPropertyChanged(nameof(RangeIsAll));
        Refresh();
    }

    [RelayCommand, ViewEntry("切换曲线指标")] private void SetMetric(string m) => Metric = m;
    [RelayCommand, ViewEntry("切换时间范围")] private void SetRange(string r) => Range = r;
    [RelayCommand, ViewEntry("曲线缩放")] private void AutoRange() { _main.Toast("ok", "已自适应缩放"); Refresh(); }
    [RelayCommand, ViewEntry("未实现，只提示")] private void ExportCsv() => _main.Toast("warn", "导出 CSV 尚未实现");
    [RelayCommand, ViewEntry("未实现，只提示")] private void Screenshot(Visual visual) => _main.Toast("warn", "截图保存尚未实现");

    public void Refresh()
    {
        RebuildLegend();
        RecalcAnalysis();
        Changed?.Invoke();
    }

    public string ChannelValue(Reactor r) => Metric switch
    {
        "T" => $"{r.TText} °C",
        "P" => $"{r.PText} {Models.Units.PLabel}",
        "all" => $"{r.TText} °C · {r.PText} {Models.Units.PLabel} · {r.GasText} mmol",
        _ => $"{r.GasText} mmol",
    };

    // ============ 实时采样（只记录实测值；开机为空，不合成任何历史）============
    private const int MaxSamples = 2400;   // 每通道最多保留的点数（1.2s 一点 ≈ 48 分钟）

    /// <summary>
    /// 实时 tick：把各通道当前读数追加为新采样点（停用通道不记录）。
    /// 模块离线 / 气体未实测时该量记为 NaN，曲线在此处断开，不画旧值或 0。
    /// </summary>
    public void AppendSample()
    {
        double ts = _clock.Elapsed.TotalSeconds;
        foreach (var c in _main.Reactors)
        {
            if (!_hist.TryGetValue(c.Id, out var list) || c.IsIdle) continue;
            list.Add(new GSample(ts,
                c.TOnline ? c.T : double.NaN,
                c.POnline ? c.P : double.NaN,
                c.GasValid ? c.Gas : double.NaN));
            if (list.Count > MaxSamples) list.RemoveRange(0, list.Count - MaxSamples);
        }
        foreach (var ch in Channels) ch.RaiseVal();
        Refresh();
    }

    private void RebuildLegend()
    {
        Legend.Clear();
        foreach (var ch in Channels)
        {
            if (!ch.IsOn || ch.Reactor.IsIdle) continue;
            var list = _hist[ch.Reactor.Id];
            if (list.Count == 0) continue;
            if (Metric == "all")
            {
                // 三变量同屏：图例一次性给出该通道三个最新读数
                Legend.Add(new GraphLegendItem(ch.ColorHex,
                    $"RV{ch.Reactor.Id} = {Latest(list[^1], "T")} °C · {Latest(list[^1], "P")} {Models.Units.PLabel} · {Latest(list[^1], "gas")} mmol"));
                continue;
            }
            double v = Val(list[^1]);
            string txt = double.IsNaN(v) ? "—" : v.ToString("0." + new string('0', Decimals));
            Legend.Add(new GraphLegendItem(ch.ColorHex, $"RV{ch.Reactor.Id} = {txt} {YUnit}"));
        }
    }

    /// <summary>「全部」视图图例用：按变量各自的小数位格式化最新值，无效为「—」。</summary>
    private static string Latest(GSample s, string m)
    {
        double v = m == "T" ? s.T : m == "P" ? Models.Units.P(s.P) : s.Gas;
        return double.IsNaN(v) ? "—" : v.ToString("0." + new string('0', MetricDecimals(m)));
    }

    private void RecalcAnalysis()
    {
        var running = _main.Reactors.Where(r => r.IsRunning).ToList();

        // 平均反应速率要按反应计时计算，随「反应」步骤（实测气体消耗）一起实现；此前一律显示「—」
        AvgRate = "—";

        var gas = running.Where(r => r.GasValid).Select(r => r.Gas).ToList();
        if (gas.Count >= 2 && gas.Average() > 0)
        {
            double mean = gas.Average();
            double sd = Math.Sqrt(gas.Select(g => (g - mean) * (g - mean)).Average());
            Rsd = $"{sd / mean * 100:0.0}%";
        }
        else Rsd = "—";

        // 只统计温度在线、且设了目标温度的运行中通道
        var dev = running.Where(r => r.TOnline && r.TSp > 0).Select(r => Math.Abs(r.T - r.TSp)).ToList();
        MaxDev = dev.Count > 0 ? $"±{dev.Max():0.0} °C" : "—";
    }
}

/// <summary>曲线页的一个通道开关。</summary>
public partial class GraphChannel : ObservableObject
{
    private readonly GraphViewModel _vm;
    public Reactor Reactor { get; }
    public string ColorHex { get; }

    [ObservableProperty] private bool _isOn;

    public GraphChannel(GraphViewModel vm, Reactor r, string color, bool on)
    {
        _vm = vm; Reactor = r; ColorHex = color; _isOn = on;
    }

    public string Title => $"RV{Reactor.Id}";
    public string ValText => Reactor.IsIdle ? "停用" : _vm.ChannelValue(Reactor);
    public bool IsDim => !IsOn || Reactor.IsIdle;
    public IBrush Dot => (IsOn && !Reactor.IsIdle)
        ? new SolidColorBrush(Color.Parse(ColorHex)) : Brushes.Transparent;
    public IBrush DotBorder => new SolidColorBrush(Color.Parse(ColorHex));

    [RelayCommand]
    [ViewEntry("显示 / 隐藏该通道曲线")]
    private void Toggle()
    {
        if (Reactor.IsIdle) return;
        IsOn = !IsOn;
        _vm.Refresh();
    }

    partial void OnIsOnChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDim));
        OnPropertyChanged(nameof(Dot));
    }

    public void RaiseVal()
    {
        OnPropertyChanged(nameof(ValText));
        OnPropertyChanged(nameof(Dot));
        OnPropertyChanged(nameof(IsDim));
    }
}

/// <summary>曲线下方图例（graph-info）一项。</summary>
public class GraphLegendItem
{
    public IBrush Color { get; }
    public string Text { get; }
    public GraphLegendItem(string colorHex, string text)
    {
        Color = new SolidColorBrush(Avalonia.Media.Color.Parse(colorHex));
        Text = text;
    }
}
