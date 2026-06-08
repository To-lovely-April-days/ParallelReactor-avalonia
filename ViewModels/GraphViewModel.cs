using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Models;

namespace ParallelReactor.ViewModels;

/// <summary>曲线历史采样点（设计上一条历史 = 一系列该结构）。</summary>
public readonly record struct GSample(double Ts, double T, double P, double Gas);

/// <summary>实时曲线界面（GRAPH Tab）：多通道叠加，变量可切（温度/压力/气体）。</summary>
public partial class GraphViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly Dictionary<int, List<GSample>> _hist = new();
    private double _t;                         // 运行时钟（秒，单调递增）

    // 8 通道配色（对应 HTML colors 映射）
    private static readonly Dictionary<int, string> Palette = new()
    {
        [1] = "#a3e635", [2] = "#5fa1e6", [3] = "#d4a8ff", [4] = "#f0a830",
        [5] = "#e0394c", [6] = "#62d4c2", [7] = "#f5d76e", [8] = "#9d978d",
    };

    public ObservableCollection<GraphChannel> Channels { get; } = new();
    public ObservableCollection<GraphLegendItem> Legend { get; } = new();
    public IReadOnlyDictionary<int, List<GSample>> History => _hist;

    [ObservableProperty] private string _metric = "gas";  // T | P | gas
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
            Channels.Add(new GraphChannel(this, r, Palette[r.Id], on: !r.IsIdle && r.Id <= 7));
        BuildHistory();
    }

    // —— 派生（工具栏高亮）——
    public bool MetricIsT => Metric == "T";
    public bool MetricIsP => Metric == "P";
    public bool MetricIsGas => Metric == "gas";
    public bool RangeIs5 => Range == "5";
    public bool RangeIs15 => Range == "15";
    public bool RangeIs60 => Range == "60";
    public bool RangeIsAll => Range == "all";

    public string YUnit => Metric == "T" ? "°C" : Metric == "P" ? "psi" : "mmol";
    public string YTitle => Metric == "T" ? "温度" : Metric == "P" ? "压力" : "气体消耗";
    public int Decimals => Metric == "gas" ? 2 : 0;
    public double RangeSeconds => Range == "all" ? double.MaxValue : double.Parse(Range) * 60;

    /// <summary>取某采样点当前变量的值。</summary>
    public double Val(GSample s) => Metric == "T" ? s.T : Metric == "P" ? s.P : s.Gas;

    partial void OnMetricChanged(string value)
    {
        OnPropertyChanged(nameof(MetricIsT)); OnPropertyChanged(nameof(MetricIsP)); OnPropertyChanged(nameof(MetricIsGas));
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

    [RelayCommand] private void SetMetric(string m) => Metric = m;
    [RelayCommand] private void SetRange(string r) => Range = r;
    [RelayCommand] private void AutoRange() { _main.Toast("ok", "已自适应缩放"); Refresh(); }
    [RelayCommand] private void ExportCsv() => _main.Toast("ok", "（演示）已导出 ReactorLog_2026_06_08.csv");
    [RelayCommand] private void Screenshot() => _main.Toast("ok", "（演示）已保存截图到 ./screenshots/");

    public void Refresh()
    {
        RebuildLegend();
        RecalcAnalysis();
        Changed?.Invoke();
    }

    public string ChannelValue(Reactor r) => Metric switch
    {
        "T" => $"{r.T:0.0} °C",
        "P" => $"{r.P:0.0} psi",
        _ => $"{r.Gas:0.00} mmol",
    };

    // ============ 历史数据合成（按状态生成符合实际的曲线形态）============
    private const int HistN = 170;
    private const double HistDt = 3.0;          // 历史点间隔（秒）

    private void BuildHistory()
    {
        _hist.Clear();
        _t = (HistN - 1) * HistDt;
        foreach (var c in _main.Reactors)
        {
            var list = new List<GSample>(HistN);
            if (!c.IsIdle)
            {
                double tEnd = c.T, pEnd = c.P, gEnd = c.Gas > 0 ? c.Gas : 12.5;
                double tSp = c.TSp > 0 ? c.TSp : tEnd;
                for (int i = 0; i < HistN; i++)
                {
                    double frac = i / (double)(HistN - 1);
                    double ts = i * HistDt;
                    double T, P, G;
                    switch (c.State)
                    {
                        case ReactorState.Heating:                   // 一阶逼近升温，压力略升
                            T = 25 + (tEnd - 25) * Approach(frac);
                            P = 14.7 + (pEnd - 14.7) * Approach(Math.Min(1, frac * 1.2));
                            G = 0;
                            break;
                        case ReactorState.Pressing:                  // 温度到位、压力斜升
                            T = tEnd + Wobble(frac, c.Id, 0.5);
                            P = 14.7 + (pEnd - 14.7) * Approach(frac);
                            G = 0;
                            break;
                        case ReactorState.React:                     // 温压平稳波动，气体饱和上升
                            T = tSp + Wobble(frac, c.Id, 0.7);
                            P = pEnd + Wobble(frac, c.Id * 3, 2.0);
                            G = gEnd * Saturate(frac);
                            break;
                        case ReactorState.Alarm:                     // 压力攀升并超调到当前高压
                            T = tSp + Wobble(frac, c.Id, 1.0);
                            P = pEnd * 0.62 + pEnd * 0.38 * Approach(frac) + Wobble(frac, c.Id * 2, 6) * frac;
                            G = gEnd * Saturate(frac);
                            break;
                        case ReactorState.Done:                      // 由反应工况降温降压到当前低值
                            T = tEnd + (150 - tEnd) * Math.Max(0, 1 - frac * 1.3);
                            P = pEnd + (320 - pEnd) * Math.Max(0, 1 - frac * 1.4);
                            G = gEnd;
                            break;
                        default:
                            T = tEnd; P = pEnd; G = gEnd; break;
                    }
                    list.Add(new GSample(ts, T, P, G));
                }
            }
            _hist[c.Id] = list;
        }
        Refresh();
    }

    /// <summary>实时 tick：把各通道当前读数追加为新采样点。</summary>
    public void AppendSample()
    {
        _t += 1.2;   // 与主界面 tick 周期一致
        foreach (var c in _main.Reactors)
        {
            if (!_hist.TryGetValue(c.Id, out var list) || c.IsIdle) continue;
            list.Add(new GSample(_t, c.T, c.P, c.Gas));
            if (list.Count > 2400) list.RemoveRange(0, list.Count - 2400);
        }
        foreach (var ch in Channels) ch.RaiseVal();
        Refresh();
    }

    // 一阶逼近（0→1，前快后缓，像热/压控制趋近设定值）
    private static double Approach(double x) => (1 - Math.Exp(-3.0 * x)) / (1 - Math.Exp(-3.0));
    // 气体吸收饱和曲线（0→1）
    private static double Saturate(double x) => (1 - Math.Exp(-2.6 * x)) / (1 - Math.Exp(-2.6));
    // 平滑有机抖动（模拟传感器小幅波动，比纯随机更好看）
    private static double Wobble(double frac, int seed, double amp)
        => amp * (Math.Sin(frac * 34 + seed) * 0.55 + Math.Sin(frac * 11 + seed * 1.7) * 0.45);

    private void RebuildLegend()
    {
        Legend.Clear();
        foreach (var ch in Channels)
        {
            if (!ch.IsOn || ch.Reactor.IsIdle) continue;
            var list = _hist[ch.Reactor.Id];
            if (list.Count == 0) continue;
            double v = Val(list[^1]);
            Legend.Add(new GraphLegendItem(ch.ColorHex,
                $"RV{ch.Reactor.Id} = {v.ToString("0." + new string('0', Decimals))} {YUnit}"));
        }
    }

    private void RecalcAnalysis()
    {
        var running = _main.Reactors.Where(r => r.IsRunning).ToList();
        if (running.Count == 0) { AvgRate = "—"; Rsd = "—"; MaxDev = "—"; return; }

        var gas = running.Select(r => r.Gas).Where(g => g > 0).ToList();
        if (gas.Count > 0)
        {
            double mean = gas.Average();
            double sd = Math.Sqrt(gas.Select(g => (g - mean) * (g - mean)).Average());
            AvgRate = $"{mean / 0.92:0.0} mmol/h";
            Rsd = mean > 0 ? $"{sd / mean * 100:0.0}%" : "—";
        }
        else { AvgRate = "—"; Rsd = "—"; }

        double md = running.Max(r => Math.Abs(r.T - (r.TSp > 0 ? r.TSp : r.T)));
        MaxDev = $"±{md:0.0} °C";
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
