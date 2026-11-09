using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 页面视图模型的基类：一个页签一个实例（PageHost 给每个页签一份视图，切换页签时滚动位置和输入不丢）。
/// M2 只有骨架：总览 / 设备 / 反应釜是只带样稿面板标题的 PrPanel（内容 M4 / M5），第二阶段视图是占位页；
/// 程序编辑与性能测试是旧样式的页面（Pages/LegacyPages.cs，D5）。
/// </summary>
public abstract partial class PageVm : ObservableObject, IDisposable
{
    protected PageVm(Route route) => Route = route;

    public Route Route { get; }

    /// <summary>没有页面自己的「确认」。</summary>
    public static readonly IReadOnlyDictionary<string, string> NoOwnedAlarms = new Dictionary<string, string>();

    /// <summary>
    /// 页面上已经有「确认」按钮的报警（样稿 page.ownsAlarms()：键「k:报警 id」→ 位置「顶部报警条」/「「报警与事件」」）。同一条报警屏幕上只有一个「确认」：
    /// 检查器里的这条报警不再放第二个按钮，写「确认在 …」。M2 的页面是骨架、没有报警条：都返回空表（M4 的总览报警条 / 设备页「报警与事件」、
    /// M5 的反应釜页报警条接上）。
    /// </summary>
    public virtual IReadOnlyDictionary<string, string> OwnedAlarms => NoOwnedAlarms;

    /// <summary>页面上有这台设备的「报警与事件」（设备页，M4）：检查器的设备正文不再列报警 / 最近事件（样稿 inspDevice 的 o.page === 'device'）。M2 为 false。</summary>
    public virtual bool ShowsAlarmLog => false;

    /// <summary>
    /// 页面上有这台设备的断线说明条（带「立即重连」，样稿设备页 / 反应釜页的 .device-off / 反应釜页离线条，M4 / M5）：检查器的控制权卡不再放第二个「立即重连」
    /// （样稿 leaseBox：打开的页签就是这台设备时按钮在页面上）。M2 的页面是骨架：为 false，按钮留在检查器里。
    /// </summary>
    public virtual bool ShowsReconnect => false;

    /// <summary>设备数据变了（就地刷新标题等）。</summary>
    public virtual void Refresh(IFleetView fleet) { }

    /// <summary>页签关掉、页面被释放（退订事件等）。</summary>
    public virtual void Dispose() { }
}

/// <summary>总览里的一个设备分区（骨架：分区头 = 别名 + 型号；「打开」= 设备页，「⋯」= 这台设备的菜单）。</summary>
public sealed partial class OverviewSectionVm : ObservableObject
{
    private readonly Func<int, IReadOnlyList<MenuEntry>> _menu;

    public OverviewSectionVm(int k, Action<Route> navigate, Func<int, IReadOnlyList<MenuEntry>> menu)
    {
        K = k;
        _menu = menu;
        OpenCommand = new RelayCommand(() => navigate(Route.Device(k)));
    }

    public int K { get; }
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _model = "";

    public ICommand OpenCommand { get; }

    /// <summary>「⋯」菜单（同右键菜单，Shell/MenuModel.DeviceItems；打开时生成）。</summary>
    public IReadOnlyList<MenuEntry> MenuItems() => _menu(K);
}

/// <summary>总览（M4 做卡片网格；M2 只有每台设备一个分区面板）。</summary>
public sealed class OverviewPageVm : PageVm
{
    private readonly Action<Route> _navigate;
    private readonly Func<int, IReadOnlyList<MenuEntry>> _menu;

    public OverviewPageVm(Action<Route> navigate, Func<int, IReadOnlyList<MenuEntry>> menu) : base(Route.Overview)
    {
        _navigate = navigate;
        _menu = menu;
    }

    public ObservableCollection<OverviewSectionVm> Sections { get; } = new();

    public override void Refresh(IFleetView fleet)
    {
        // 稳定身份：按 K 增删，已有的就地更新
        foreach (var gone in Sections.Where(s => fleet.Device(s.K) == null).ToList()) Sections.Remove(gone);
        int i = 0;
        foreach (var d in fleet.Devices)
        {
            var s = Sections.FirstOrDefault(x => x.K == d.K);
            if (s == null) { s = new OverviewSectionVm(d.K, _navigate, _menu); Sections.Insert(i, s); }
            else if (Sections.IndexOf(s) != i) Sections.Move(Sections.IndexOf(s), i);
            s.Alias = d.Alias;
            s.Model = d.ModelLabel;
            i++;
        }
    }
}

/// <summary>设备页（M4：反应釜面板、共用资源、设备信息、报警与事件；M2 是这四个面板的骨架）。</summary>
public sealed partial class DevicePageVm : PageVm
{
    public DevicePageVm(Route route) : base(route) { }

    [ObservableProperty] private string _alias = "";

    public override void Refresh(IFleetView fleet) => Alias = fleet.Device(Route.K)?.Alias ?? "设备 " + Route.K;
}

/// <summary>反应釜页（M5：程序画布、釜体 / 属性查看器、曲线；M2 是这三个面板的骨架）。</summary>
public sealed partial class ReactorPageVm : PageVm
{
    public ReactorPageVm(Route route) : base(route) { }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crumb))] private string _alias = "";
    public string Rv => "RV" + Route.N;
    /// <summary>程序面板的面包屑（「1号机 › RV1」；程序名在 M5 的程序台账之后接上）。</summary>
    public string Crumb => Alias + " › " + Rv;

    public override void Refresh(IFleetView fleet) => Alias = fleet.Device(Route.K)?.Alias ?? "设备 " + Route.K;
}

/// <summary>第二阶段视图的占位页（样稿 pages/placeholder.js：标题 + 一句「第二阶段设计：……」，不做半成品）。</summary>
public sealed class PlaceholderPageVm : PageVm
{
    public PlaceholderPageVm(Route route) : base(route)
    {
        var p = Placeholders.Get(route.Key);
        Title = p?.Title ?? "未知视图";
        Icon = p?.Icon ?? "alert";
        Text = p?.Text ?? "没有名为「" + route.Key + "」的视图。";
    }

    public string Title { get; }
    public string Icon { get; }
    public string Text { get; }
    public string Hint => Placeholders.Hint;
}

/// <summary>组件陈列（#/gallery，设计 / 自检用，不在菜单里）。</summary>
public sealed class GalleryPageVm : PageVm
{
    public GalleryPageVm() : base(Route.Gallery) { }
}
