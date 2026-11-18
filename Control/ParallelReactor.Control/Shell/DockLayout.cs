using System;
using System.Collections.Generic;
using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Control.Shell;

/// <summary>外框的停靠面板（样稿 #pDev 设备、#pInsp 检查器）。主页看板里的面板（通道 / 曲线 / 共用资源）由页面自己管（F3）。</summary>
public enum DockPaneId { Devices, Inspector }

/// <summary>面板此刻的样子：停靠（占一列）/ 收起（只剩 30 宽的竖条）/ 浮出（窄屏：竖条占位，展开的面板盖在内容上）。</summary>
public enum PaneMode { Docked, Collapsed, Float }

/// <summary>
/// 外框布局（融合版 F2，取代 pc-v3 的 ShellLayout；样稿 main-fusion.html 的 PW / PCOL / FLOATS / paneCls）。只看<b>窗口</b>尺寸（不看子元素，所以不会来回振荡）：
/// <list type="bullet">
/// <item>外框缩放：窄于 1366 或矮于 768 时按 scale = min(1, W/1366, H/768) 等比缩小，外框按 W/scale × H/scale 排版（<see cref="FrameFor"/>，FrameScaler 用它）。</item>
/// <item>宽屏（排版宽 ≥ 1600，<see cref="IsWide"/>）：设备面板默认停靠（296），检查器默认收起（30 的竖条，展开 340）。</item>
/// <item>窄屏（&lt; 1600）：两个都默认收起；展开时<b>浮出</b>（竖条留在原位占 30，面板 330 / 400 宽盖在内容上，带浮出阴影），
///   点浮出面板外面就收回去（<see cref="CloseFloats"/>，样稿捕获阶段的 click）。</item>
/// <item>窗口跨过 1600：变宽 → 设备面板展开；变窄 → 两个都收起（样稿 resize）。</item>
/// <item>「重置布局」（<see cref="Reset"/>）：回到上面的默认。</item>
/// </list>
/// 收起的面板记在 <see cref="_col"/>（同样稿 PCOL）；面板的样子由它和宽窄算出来（<see cref="Mode"/>）。
/// </summary>
public sealed partial class DockLayout : ObservableObject
{
    public const double MinW = 1366, MinH = 768;
    /// <summary>宽 / 窄屏的分界（样稿 innerWidth ≥ 1600）。</summary>
    public const double WideW = 1600;
    /// <summary>矮屏（样稿 max-height: 820px 的紧凑行）。</summary>
    public const double ShortH = 820;
    public const double DevW = 296, InspW = 340, RailColW = 30, DevFloatW = 330, InspFloatW = 400;

    /// <summary>窗口尺寸 → （外框排版尺寸, 缩放）。尺寸无效（0 / 无穷）时返回 (1366×768, 1)。</summary>
    public static (Size Frame, double Scale) FrameFor(Size window)
    {
        double w = window.Width, h = window.Height;
        if (!(w > 0) || !(h > 0) || double.IsInfinity(w) || double.IsInfinity(h)) return (new Size(MinW, MinH), 1);
        var k = Math.Min(1, Math.Min(w / MinW, h / MinH));
        return (new Size(w / k, h / k), k);
    }

    private readonly HashSet<DockPaneId> _col = new() { DockPaneId.Inspector };
    private bool _sized;

    [ObservableProperty] private double _frameWidth = 1920;
    [ObservableProperty] private double _frameHeight = 1080;
    /// <summary>排版宽 ≥ 1600（样稿 PW）。</summary>
    [ObservableProperty] private bool _isWide = true;
    /// <summary>排版高 &lt; 820（紧凑行）。</summary>
    [ObservableProperty] private bool _isShort;

    /// <summary>布局变了（宽窄、面板开关）。</summary>
    public event EventHandler? Changed;

    /// <summary>面板此刻的样子。</summary>
    public PaneMode Mode(DockPaneId p) => _col.Contains(p) ? PaneMode.Collapsed : IsWide ? PaneMode.Docked : PaneMode.Float;

    public bool IsOpen(DockPaneId p) => !_col.Contains(p);

    public PaneMode DevicesMode => Mode(DockPaneId.Devices);
    public PaneMode InspectorMode => Mode(DockPaneId.Inspector);

    /// <summary>有浮出的面板（点外面收回、Esc 收回）。</summary>
    public bool AnyFloat => !IsWide && (IsOpen(DockPaneId.Devices) || IsOpen(DockPaneId.Inspector));

    /// <summary>面板这一列的宽：停靠 = 面板宽，收起 / 浮出 = 竖条 30。</summary>
    public double ColumnWidth(DockPaneId p) => Mode(p) == PaneMode.Docked ? (p == DockPaneId.Devices ? DevW : InspW) : RailColW;

    /// <summary>浮出时面板的宽（设备 330、检查器 400）。</summary>
    public static double FloatWidth(DockPaneId p) => p == DockPaneId.Devices ? DevFloatW : InspFloatW;

    /// <summary>窗口尺寸变了（MainWindow 在 ClientSize 变化时调用）。第一次按宽窄给默认；之后跨过 1600 时照样稿改开关。</summary>
    public void Update(Size window)
    {
        var (frame, _) = FrameFor(window);
        FrameWidth = frame.Width;
        FrameHeight = frame.Height;
        bool wide = frame.Width >= WideW;
        IsShort = frame.Height < ShortH;
        if (!_sized)
        {
            _sized = true;
            IsWide = wide;
            Defaults();
        }
        else if (wide != IsWide)
        {
            IsWide = wide;
            if (wide) _col.Remove(DockPaneId.Devices);
            else { _col.Add(DockPaneId.Devices); _col.Add(DockPaneId.Inspector); }
        }
        Raise();
    }

    /// <summary>竖条上的按钮：收起 ↔ 展开（窄屏展开 = 浮出）。</summary>
    public void Toggle(DockPaneId p)
    {
        if (!_col.Remove(p)) _col.Add(p);
        Raise();
    }

    /// <summary>展开（选中通道时打开检查器，样稿 PCOL.delete('pInsp')）。</summary>
    public void Open(DockPaneId p)
    {
        if (_col.Remove(p)) Raise();
    }

    public void Collapse(DockPaneId p)
    {
        if (_col.Add(p)) Raise();
    }

    /// <summary>收回浮出的面板（点外面 / Esc）。返回是否收了。</summary>
    public bool CloseFloats()
    {
        if (!AnyFloat) return false;
        _col.Add(DockPaneId.Devices);
        _col.Add(DockPaneId.Inspector);
        Raise();
        return true;
    }

    /// <summary>重置布局（样稿 reset）：检查器收起，窄屏时设备面板也收起。</summary>
    public void Reset()
    {
        Defaults();
        Raise();
    }

    /// <summary>截图 / 演示参数（样稿 po / pc：展开 / 收起的面板，逗号分隔：dev、insp）。</summary>
    public void ApplyParams(string? open, string? collapse)
    {
        foreach (var p in Ids(open)) _col.Remove(p);
        foreach (var p in Ids(collapse)) _col.Add(p);
        Raise();
    }

    private static IEnumerable<DockPaneId> Ids(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) yield break;
        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part is "dev" or "pDev" or "devices") yield return DockPaneId.Devices;
            else if (part is "insp" or "pInsp" or "inspector") yield return DockPaneId.Inspector;
        }
    }

    private void Defaults()
    {
        _col.Clear();
        _col.Add(DockPaneId.Inspector);
        if (!IsWide) _col.Add(DockPaneId.Devices);
    }

    private void Raise()
    {
        OnPropertyChanged(nameof(DevicesMode));
        OnPropertyChanged(nameof(InspectorMode));
        OnPropertyChanged(nameof(AnyFloat));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
