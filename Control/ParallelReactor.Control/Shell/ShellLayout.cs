using System;
using Avalonia;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ParallelReactor.Control.Shell;

/// <summary>资源管理器的位置（样稿 shell.xMode）：停靠 300 · 浮层 320 · 收起。</summary>
public enum ExplorerMode { Docked, Float, Collapsed }

/// <summary>
/// 外框布局（PORT-PLAN §2.1「外框与响应式」、§4.8 第 1 层；样稿 shell.js fit / applyLayout，css/shell.css 的 .pr-app 网格）。
/// 只看<b>窗口</b>尺寸（R4：不看任何子元素的尺寸，所以不会来回振荡）：
///  · 外框：窄于 1366 或矮于 768 时按 scale = min(1, W/1366, H/768) 等比缩小，外框按 W/scale × H/scale 排版（≥ 1366×768，正好铺满窗口；
///    网页版矮时纵向滚动，桌面程序不这样）；
///  · 宽度类 fw-s（&lt; 1600）/ fw-m / fw-l（≥ 1920），高度类 fh-s（&lt; 860）/ fh-m / fh-l（≥ 1000）；
///  · 资源管理器：≥ 1600 停靠 300，更窄时默认收起、打开时是 320 的浮层；专注模式（图标栏「收起」）收起它；
///  · 检查器：≥ 1920 为 330，更窄 300；时间轴：220，高度 &lt; 1000 时 200，收起 40；时间轴标签列 300（资源管理器停靠）/ 248 / 220（窄）。
/// 用户的开关（资源管理器、时间轴、专注）记在这里；没动过资源管理器开关时，它随窗口宽度在停靠 / 收起之间切换（同样稿 _xFromUser）。
/// </summary>
public sealed partial class ShellLayout : ObservableObject
{
    public const double MinW = 1366, MinH = 768;
    public const double NarrowW = 1600, WideW = 1920, ShortH = 860, TallH = 1000;
    public const double MenuH = 34, TabsH = 44, SideW = 64, StatusH = 30;
    public const double ExplorerW = 300, ExplorerFloatW = 320, InspW = 330, InspWNarrow = 300;
    public const double TimelineH = 220, TimelineHShort = 200, TimelineHCollapsed = 40;

    /// <summary>窗口尺寸 → （外框排版尺寸, 缩放）。尺寸无效（0 / 无穷）时返回 (1366×768, 1)。</summary>
    public static (Size Frame, double Scale) FrameFor(Size window)
    {
        double w = window.Width, h = window.Height;
        if (!(w > 0) || !(h > 0) || double.IsInfinity(w) || double.IsInfinity(h)) return (new Size(MinW, MinH), 1);
        var k = Math.Min(1, Math.Min(w / MinW, h / MinH));
        return (new Size(w / k, h / k), k);
    }

    private ExplorerMode _mode = ExplorerMode.Docked;
    private bool _modeFromUser;

    [ObservableProperty] private double _scale = 1;
    [ObservableProperty] private double _frameWidth = MinW;
    [ObservableProperty] private double _frameHeight = MinH;
    /// <summary>fw-s / fw-m / fw-l。</summary>
    [ObservableProperty] private string _widthClass = "fw-s";
    /// <summary>fh-s / fh-m / fh-l。</summary>
    [ObservableProperty] private string _heightClass = "fh-s";
    [ObservableProperty] private bool _isNarrow = true;
    [ObservableProperty] private bool _isShort = true;
    /// <summary>检查器 300（窗口 &lt; 1920，样稿 .insp-n）。</summary>
    [ObservableProperty] private bool _isInspectorNarrow = true;

    [ObservableProperty] private bool _explorerDocked;
    [ObservableProperty] private bool _explorerFloat;
    /// <summary>图标栏「设备」的开 / 关（停靠或浮层）。</summary>
    [ObservableProperty] private bool _explorerOpen;
    [ObservableProperty] private bool _focus;
    [ObservableProperty] private bool _timelineCollapsed;

    [ObservableProperty] private GridLength _explorerColumn = new(0);
    [ObservableProperty] private GridLength _inspectorColumn = new(InspWNarrow);
    [ObservableProperty] private GridLength _timelineRow = new(TimelineHShort);
    [ObservableProperty] private double _timelineLabelWidth = 220;

    /// <summary>用户选择的资源管理器模式（实际是否停靠还看宽度和专注模式）。</summary>
    public ExplorerMode Mode => _mode;

    /// <summary>布局变了（列宽、类名、开关）。</summary>
    public event EventHandler? Changed;

    /// <summary>窗口尺寸变了（MainWindow 在 ClientSize 变化时调用）。</summary>
    public void Update(Size window)
    {
        var (frame, k) = FrameFor(window);
        Scale = k;
        FrameWidth = frame.Width;
        FrameHeight = frame.Height;
        bool narrow = frame.Width < NarrowW;
        if (!_modeFromUser) _mode = narrow ? ExplorerMode.Collapsed : ExplorerMode.Docked;
        Apply();
    }

    /// <summary>
    /// 地址参数（截图路由、样稿 README「常用查询参数」）：explorer=0 收起、explorer=1 打开（宽 ≥ 1600 停靠，否则浮层）；tl=0 时间轴收起、tl=1 展开。
    /// null = 不改。
    /// </summary>
    public void ApplyParams(string? explorer, string? tl)
    {
        if (explorer == "0") { _mode = ExplorerMode.Collapsed; _modeFromUser = true; }
        else if (explorer == "1") { _mode = IsNarrow ? ExplorerMode.Float : ExplorerMode.Docked; _modeFromUser = true; }
        if (tl != null) TimelineCollapsed = tl == "0";
        Apply();
    }

    /// <summary>图标栏「设备」/ 视图菜单「设备资源管理器」/ 资源管理器标题栏的收起（样稿 toggleExplorer）。</summary>
    public void ToggleExplorer()
    {
        _modeFromUser = true;
        if (Focus) { Focus = false; _mode = IsNarrow ? ExplorerMode.Float : ExplorerMode.Docked; }
        else if (IsNarrow) _mode = _mode == ExplorerMode.Float ? ExplorerMode.Collapsed : ExplorerMode.Float;
        else _mode = _mode == ExplorerMode.Docked ? ExplorerMode.Collapsed : ExplorerMode.Docked;
        Apply();
    }

    /// <summary>浮层收起（点遮罩、Esc、在浮层里打开了页签）。</summary>
    public void CloseFloat()
    {
        if (_mode != ExplorerMode.Float) return;
        _mode = ExplorerMode.Collapsed;
        Apply();
    }

    /// <summary>视图菜单「时间轴」/ 时间轴标题栏的收起（样稿 toggleTimeline）。</summary>
    public void ToggleTimeline()
    {
        TimelineCollapsed = !TimelineCollapsed;
        if (Focus) Focus = false;
        Apply();
    }

    /// <summary>图标栏底部「收起 / 展开」：专注模式收起资源管理器与时间轴。返回新的状态。</summary>
    public bool ToggleFocus()
    {
        Focus = !Focus;
        if (!Focus && _mode == ExplorerMode.Float) _mode = ExplorerMode.Collapsed;
        Apply();
        return Focus;
    }

    /// <summary>视图菜单「重置布局」（样稿 resetLayout 的外框部分）。</summary>
    public void Reset()
    {
        Focus = false;
        TimelineCollapsed = false;
        _modeFromUser = false;
        _mode = IsNarrow ? ExplorerMode.Collapsed : ExplorerMode.Docked;
        Apply();
    }

    private void Apply()
    {
        double w = FrameWidth, h = FrameHeight;
        IsNarrow = w < NarrowW;
        IsShort = h < ShortH;
        WidthClass = w < NarrowW ? "fw-s" : w < WideW ? "fw-m" : "fw-l";
        HeightClass = h < ShortH ? "fh-s" : h < TallH ? "fh-m" : "fh-l";
        IsInspectorNarrow = w < WideW;

        bool docked = _mode == ExplorerMode.Docked && !IsNarrow && !Focus;
        ExplorerDocked = docked;
        ExplorerFloat = _mode == ExplorerMode.Float;
        ExplorerOpen = ExplorerFloat || docked;
        bool tlCol = TimelineCollapsed || Focus;

        ExplorerColumn = new GridLength(docked ? ExplorerW : 0);
        InspectorColumn = new GridLength(IsInspectorNarrow ? InspWNarrow : InspW);
        TimelineRow = new GridLength(tlCol ? TimelineHCollapsed : h >= TallH ? TimelineH : TimelineHShort);
        TimelineLabelWidth = docked ? ExplorerW : IsNarrow ? 220 : 248;
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(TimelineFolded));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>时间轴是否只剩标题栏（用户收起或专注模式）。</summary>
    public bool TimelineFolded => TimelineCollapsed || Focus;
}
