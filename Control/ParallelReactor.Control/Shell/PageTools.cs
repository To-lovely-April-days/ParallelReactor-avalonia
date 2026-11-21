using System;
using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 工具条右侧的一个按钮（样稿 renderRibbon 的 rb(ic, lab, at, dis, tip)：图标 + 文字，高 30 的 GBG 直角按钮）。
/// 页面给出（<see cref="Pages.PageVm.Tools"/>），工具条（Views/Shell/RibbonView）只负责画：<see cref="Reason"/> 非空 = 带原因的禁用（点了说明原因，b:Pr.Reason），
/// <see cref="IsPrimary"/> = 品牌蓝实心（样稿 PRI：下发并运行、开始测试…，每条工具条最多一个）。按钮对象由页面持有、就地改状态（不要每秒新建）。
/// </summary>
public sealed partial class RibbonTool : ObservableObject
{
    public RibbonTool(string icon, string label, Action run, string? tip = null, bool primary = false)
    {
        _icon = icon;
        _label = label;
        _tip = tip ?? label;
        IsPrimary = primary;
        Command = new RelayCommand(run);
    }

    public ICommand Command { get; }
    public bool IsPrimary { get; }

    /// <summary>统一图标名（Phosphor Icons，见 Controls/IconSet.g.cs）。</summary>
    [ObservableProperty] private string _icon;
    [ObservableProperty] private string _label;
    /// <summary>可用时的悬停提示（默认 = 文字）。</summary>
    [ObservableProperty] private string _tip;
    /// <summary>不能用的原因（null = 能用）。</summary>
    [ObservableProperty] private string? _reason;
}

/// <summary>
/// 一组工具条按钮（样稿 .rg，组与组之间一条 1 × 22 的竖线 .rsep）。<see cref="Title"/> 同样稿的 title（「数据」「视图」「文件」…）：
/// 窄屏（&lt; 1600）时「文件 / 导出 / 报告 / 配置 / 视图」这几组只剩图标（30 × 30，文字进悬停提示，样稿 @media max-width 1599px）。
/// </summary>
public sealed record RibbonGroup(string Title, IReadOnlyList<RibbonTool> Tools)
{
    private static readonly HashSet<string> CompactTitles = new(StringComparer.Ordinal) { "文件", "导出", "报告", "配置", "视图" };

    /// <summary>窄屏时只剩图标。</summary>
    public bool CompactWhenNarrow => CompactTitles.Contains(Title);
}
