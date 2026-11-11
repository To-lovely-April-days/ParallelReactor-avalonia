using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Core.Chart;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 总览的「温度趋势」面板（样稿 js/pages/overview.js trendData / trendHTML / syncChart；下方剩余 ≥ 200 px 时出现，见 Core 的 OverviewLayout.ShowTrend）。
/// 只画一台设备 —— 选中的那台（它没有进入趋势的反应釜时取第一台有的），最多 4 条温度线：--ch-s1 / --ch-s2 两种灰轮流，前两条实线、后两条虚线「7 4」；
/// 近 60 min，纵轴「温度 ℃」最小跨度 10；没有时「没有运行中的反应釜」。进入趋势 = 已安装，且在运行（报警也算）或程序已结束。
/// 图例：设备别名（14/600）+ 每条线的小样与「RV1」；画不全时末尾「显示 n/m」（提示「选中别的设备可切换 · 全部曲线在「曲线」视图」）。
/// 数据同反应釜页的曲线：数据源的 Core 环形缓冲按窗口拷进复用的缓冲（<see cref="PointWindow"/>），输入没变时不重建（R10）。
/// </summary>
public sealed partial class OverviewTrendVm : ObservableObject
{
    private readonly PointWindow[] _wins = { new(), new(), new(), new() };
    private string _sig = "";

    public OverviewTrendVm()
    {
        Items = new[] { new OverviewTrendItemVm(), new OverviewTrendItemVm(), new OverviewTrendItemVm(), new OverviewTrendItemVm() };
    }

    public string Title => OverviewTrendOptions.Title;
    public string Sub => OverviewTrendOptions.Sub;

    /// <summary>样稿此刻画趋势面板（页面版式决定：没有报警条、下方剩余 ≥ 200 px、没有放大的分区）。</summary>
    [ObservableProperty] private bool _isShown;

    /// <summary>曲线的输入；没有设备时为 null。</summary>
    [ObservableProperty] private ChartOptions? _options;

    /// <summary>图例的设备别名（没有进入趋势的反应釜时为空，整组不显示）。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasGroup))] private string _alias = "";

    public bool HasGroup => Alias.Length > 0;

    /// <summary>「显示 n/m」（画全了为 null）与它的提示。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMore))] private string? _moreText;
    [ObservableProperty] private string? _moreTip;

    public bool HasMore => MoreText is not null;

    /// <summary>图例的四个位置（身份不变，就地更新；没用到的不显示）。</summary>
    public IReadOnlyList<OverviewTrendItemVm> Items { get; }

    /// <summary>重建次数（核对「数据没变就不重建」用）。</summary>
    public int Builds { get; private set; }

    /// <summary>
    /// 按设备列表（顺序 = 总览的分区）与当前选中的设备重算。只在显示着时调（页面版式决定）。
    /// <paramref name="fallbackNow"/>：没有任何设备进入趋势时横轴的「现在」（外框的时钟）。
    /// </summary>
    public void Refresh(IReadOnlyList<DeviceVm> devices, int? selectedK, DateTime fallbackNow)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var trend = new TrendDevice[devices.Count];
        for (int i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            var chans = new List<int>();
            for (int n = 1; n <= d.Model.ChannelCount; n++)
            {
                var rd = d.Source.Read(n);
                if (OverviewTrendOptions.InTrend(rd.Installed, rd.IsRunning, d.Source.Program(n)?.Status)) chans.Add(n);
            }
            trend[i] = new TrendDevice(d.K, d.Alias, chans.ToArray());
        }
        // 画哪一台（同 Core 的 Build）：选中的那台有进入趋势的反应釜就用它，否则第一台有的
        DeviceVm? chosen = null;
        for (int i = 0; i < devices.Count && chosen is null; i++) if (devices[i].K == selectedK && trend[i].Channels.Count > 0) chosen = devices[i];
        for (int i = 0; i < devices.Count && chosen is null; i++) if (trend[i].Channels.Count > 0) chosen = devices[i];
        var now = chosen?.Source.History.Now ?? fallbackNow;

        // 输入没变（同一台、同样的反应釜、缓冲版本、现在、别名）就不重建
        var sig = new System.Text.StringBuilder();
        sig.Append(now.Ticks).Append('|').Append(selectedK);
        for (int i = 0; i < trend.Length; i++)
        {
            sig.Append('|').Append(trend[i].K).Append(':').Append(trend[i].Alias).Append(':').Append(trend[i].Channels.Count);
            if (ReferenceEquals(devices[i], chosen))
                for (int j = 0; j < trend[i].Channels.Count && j < OverviewTrendOptions.PerDevice; j++)
                {
                    int n = trend[i].Channels[j];
                    sig.Append(',').Append(n).Append('v').Append(chosen.Source.History.Channel(n)?.Version ?? -1);
                }
        }
        var s = sig.ToString();
        if (s == _sig && Options is not null) return;
        _sig = s;
        Builds++;

        int slot = 0;
        var data = OverviewTrendOptions.Build(trend, selectedK, now, (k, n) =>
        {
            var d = chosen!;
            var w = _wins[Math.Min(slot++, _wins.Length - 1)];
            return w.Fill(d.Source.History.Channel(n), HistoryField.TempC, now, OverviewTrendOptions.Minutes);
        });
        Options = data.Options;
        Alias = data.Device?.Alias ?? "";
        MoreText = data.MoreText;
        MoreTip = data.MoreTip;
        for (int i = 0; i < Items.Count; i++)
        {
            if (i < data.Legend.Count) Items[i].Apply(data.Legend[i]);
            else Items[i].IsShown = false;
        }
    }
}

/// <summary>温度趋势图例的一项（样稿 .overview-lg：24 × 8 的线样 + 「RV1」）。</summary>
public sealed partial class OverviewTrendItemVm : ObservableObject
{
    [ObservableProperty] private bool _isShown;
    [ObservableProperty] private string _label = "";
    [ObservableProperty] private string _color = ChartTokens.S1;
    /// <summary>小样的虚线（按 0.6 缩短：「7 4」→「4 2」；实线为 null）。</summary>
    [ObservableProperty] private string? _sampleDash;

    internal void Apply(TrendLegendItem it)
    {
        Label = it.Label;
        Color = it.Color;
        SampleDash = it.SampleDash;
        IsShown = true;
    }
}
