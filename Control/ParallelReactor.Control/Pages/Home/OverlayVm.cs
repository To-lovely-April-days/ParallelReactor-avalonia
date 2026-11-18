using ParallelReactor.Control.Core.Format;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages.Home;

/// <summary>叠加曲线显示哪个量（样稿 OVM：G 吸气量 / T 温度 / P 压力）。</summary>
public enum OverlayMode { G, T, P }

/// <summary>叠加曲线的一条线：RV 号、颜色、虚线（16 路的 RV9–16）、点（时刻 ms + 值，NaN = 断开）。</summary>
public sealed record OverlayLine(int N, Color Color, bool Dashed, ChartPoint[] Points, int Count);

/// <summary>
/// 叠加曲线一帧的输入（每次刷新一个新实例，控件拿到就重画）：时间窗（起点 / 现在，ms）、量（标题、单位、小数位）、各条线、选中的一路、压力上限（P 模式画红虚线）、刻度间隔、
/// 纵轴至少画到的值（0 = 控件的默认：吸气量 5、其余 1；压力 = 1 MPa 换算成设置的单位）。压力的点和上限已经是设置的压力单位。
/// </summary>
public sealed record OverlayData(double T0, double T1, OverlayMode Mode, string Title, string Unit, int Decimals,
    IReadOnlyList<OverlayLine> Lines, int Selected, double? Limit, DateTime Start, double MinTop = 0);

/// <summary>图例芯片（样稿 #pCurve .lgc）：点一下选中这一路。</summary>
public sealed partial class OverlayChipVm : ObservableObject
{
    public OverlayChipVm(int n, Action<int> pick)
    {
        N = n;
        Label = "RV" + n.ToString(CultureInfo.InvariantCulture);
        Brush = RunRowText.ChBrush(n);
        PickCommand = new RelayCommand(() => pick(N));
    }

    public int N { get; }
    public string Label { get; }
    public IBrush Brush { get; }
    public ICommand PickCommand { get; }
    [ObservableProperty] private bool _isOn;
}

/// <summary>
/// 8 路叠加曲线（融合版 F3，样稿 drawOv + #pCurve 的 ptool）：分段「吸气量 / 温度 / 压力」、说明「8 路叠加 · 13:00 – 现在 · ⓘ设备按压降计算」、
/// 每路一个芯片（点 = 选中这一路）；16 路的设备不放芯片，写「RV1–8 实线 · RV9–16 同色虚线」。数据 = 设备的历史缓存（Core 的 ChannelHistory，10 s 一格）。
/// 时间窗从这批程序最早的开始时刻起（整 15 min 往前取整，至少 30 min、至多 240 min）到「现在」。
/// </summary>
public sealed partial class OverlayVm : ObservableObject
{
    private readonly ShellVm _shell;
    private readonly Dictionary<int, ChartPoint[]> _bufs = new();
    private int _chipsFor;

    public OverlayVm(ShellVm shell)
    {
        _shell = shell;
    }

    public ObservableCollection<OverlayChipVm> Chips { get; } = new();

    [ObservableProperty] private string _modeKey = "G";
    [ObservableProperty] private OverlayData? _data;
    [ObservableProperty] private string _optText = "";
    [ObservableProperty] private bool _isUptake = true;
    [ObservableProperty] private bool _isC16;

    public OverlayMode Mode => ModeKey switch { "T" => OverlayMode.T, "P" => OverlayMode.P, _ => OverlayMode.G };

    partial void OnModeKeyChanged(string value)
    {
        IsUptake = Mode == OverlayMode.G;
        if (_shell.Nav.DeviceK > 0 && _shell.Fleet.Device(_shell.Nav.DeviceK) is { } d) Refresh(d);
    }

    private void Pick(int n)
    {
        if (_shell.Nav.DeviceK > 0) _shell.SelectChannel(_shell.Nav.DeviceK, n);
    }

    /// <summary>窗口起点：这台设备历史缓存里最早的采样（真机 = 连上之后，演示 = 本次运行 13:00），往前取整到 15 min；至少 30 min、至多 240 min；没有数据时现在 − 60 min。</summary>
    public static DateTime WindowStart(DateTime now, IEnumerable<DateTime?> starts)
    {
        var first = starts.Where(s => s.HasValue).Select(s => s!.Value).DefaultIfEmpty(now.AddMinutes(-60)).Min();
        if (now - first > TimeSpan.FromMinutes(240)) first = now.AddMinutes(-240);
        if (now - first < TimeSpan.FromMinutes(30)) first = now.AddMinutes(-30);
        var q = new DateTime(first.Year, first.Month, first.Day, first.Hour, first.Minute / 15 * 15, 0, first.Kind);
        return q;
    }

    internal void Refresh(DeviceVm d)
    {
        var src = d.Source;
        var now = src.History.Now;
        var mode = Mode;
        var field = mode switch { OverlayMode.T => HistoryField.TempC, OverlayMode.P => HistoryField.PressureMPa, _ => HistoryField.Mmol };
        var live = d.Reactors.Where(r => r.Installed && !r.TempNa).ToList();
        IsC16 = d.ChannelCount > 8;

        if (_chipsFor != d.K || Chips.Count != live.Count || !Chips.Select(c => c.N).SequenceEqual(live.Select(r => r.N)))
        {
            Chips.Clear();
            if (!IsC16) foreach (var r in live) Chips.Add(new OverlayChipVm(r.N, Pick));
            _chipsFor = d.K;
        }
        int sel = _shell.Nav.ChannelOf(d.K);
        foreach (var c in Chips) c.IsOn = c.N == sel;

        var start = WindowStart(now, live.Select(r => src.History.Channel(r.N) is { Count: > 0 } ch ? (DateTime?)ch[0].T.ToLocalTime() : null));
        double minutes = (now - start).TotalMinutes;
        // 压力按设置的单位画（MPa 时倍数 1，与改单位之前相同）
        double k = PressureUnits.PerMpa(PressureDisplay.Primary);
        var lines = new List<OverlayLine>(live.Count);
        foreach (var r in live)
        {
            var h = src.History.Channel(r.N);
            if (h == null) continue;
            _bufs.TryGetValue(r.N, out var buf);
            buf ??= Array.Empty<ChartPoint>();
            int n = h.CopyPoints(field, now, minutes, ref buf);
            _bufs[r.N] = buf;
            // 每次一份新数组（控件拿着上一帧画到下一帧来之前）
            var pts = new ChartPoint[n];
            Array.Copy(buf, pts, n);
            if (mode == OverlayMode.P && k != 1) for (int i = 0; i < n; i++) pts[i] = new ChartPoint(pts[i].T, pts[i].V * k);
            lines.Add(new OverlayLine(r.N, RunRowText.ChColor(r.N), r.N > 8, pts, n));
        }
        var (title, unit, dec) = mode switch
        {
            OverlayMode.T => ("温度", "°C", 1),
            OverlayMode.P => ("压力", PressureDisplay.Unit, PressureDisplay.Decimals(2)),
            _ => ("吸气量", "mmol", 1),
        };
        Data = new OverlayData(ChartTime.Ms(start), ChartTime.Ms(now), mode, title, unit, dec, lines, sel,
            mode == OverlayMode.P ? src.Plant.OverPressureMPa * k : null, start, mode == OverlayMode.P ? k : 0);
        OptText = lines.Count.ToString(CultureInfo.InvariantCulture) + " 路叠加 · " + start.ToString("HH:mm", CultureInfo.InvariantCulture) + " – 现在";
    }
}
