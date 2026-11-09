using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Controls.Shell;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 一个文档页签（PORT-PLAN §2.1「文档页签栏」；样稿 shell.js tabInfo / renderTabs）。身份 = <see cref="Id"/>（路由路径），集合里就地更新，不重建。
/// 设备 / 反应釜页签只写名字：只有两种标记 —— 断线（wifi-off 图标）和错误报警 / 急停（红点）；没有设备色条、没有状态点。
/// 反应釜页签的别名太长时只省略别名，「 · RV16」永远完整（<see cref="Alias"/> + <see cref="RvPart"/>）。
/// </summary>
public sealed partial class DocTabVm : ObservableObject, IOverflowItem
{
    private readonly Action<DocTabVm> _activate;
    private readonly Action<DocTabVm> _close;

    public DocTabVm(Route route, bool pinned, Action<DocTabVm> activate, Action<DocTabVm> close)
    {
        Route = route;
        Pinned = pinned;
        _activate = activate;
        _close = close;
        ActivateCommand = new RelayCommand(() => _activate(this));
        CloseCommand = pinned ? null : new RelayCommand(() => _close(this));
    }

    public Route Route { get; }
    public string Id => Route.Id;
    public PageKind Kind => Route.Page;
    /// <summary>设备（设备 / 反应釜页签）：设备的位置号（样稿 d.k，1号机 = 1，在设备列表里不变），否则 0。</summary>
    public int DeviceId => Route.K;
    /// <summary>RV 号（反应釜页签），否则 0。</summary>
    public int Rv => Route.N;
    /// <summary>固定页签（总览）：没有 ×，不能关。</summary>
    public bool Pinned { get; }

    public ICommand ActivateCommand { get; }
    /// <summary>关闭命令；固定页签为 null（PrDocTab 的 × 不显示）。</summary>
    public ICommand? CloseCommand { get; }

    /// <summary>完整名称（「1号机 · RV1」「总览」…，窗口菜单、溢出菜单用）。</summary>
    [ObservableProperty] private string _label = "";
    /// <summary>反应釜页签：别名部分（可省略）。其它页签：同 <see cref="Label"/>。</summary>
    [ObservableProperty] private string _alias = "";
    /// <summary>反应釜页签：「 · RV1」（不省略）；其它页签空串。</summary>
    [ObservableProperty] private string _rvPart = "";
    /// <summary>页签图标（总览、占位页、组件陈列）；设备 / 反应釜页签没有图标（只有断线时的 wifi-off）。</summary>
    [ObservableProperty] private string? _icon;
    /// <summary>菜单里用的图标（设备 / 反应釜页签：machine / devices）。</summary>
    [ObservableProperty] private string _menuIcon = "layout";
    [ObservableProperty] private bool _offline;
    [ObservableProperty] private bool _errAlarm;
    [ObservableProperty] private string _tip = "";
    [ObservableProperty] private bool _isActive;
    /// <summary>放不下、收进了「›」菜单。</summary>
    [ObservableProperty] private bool _isHidden;

    public bool HasRvPart => RvPart.Length > 0;
    partial void OnRvPartChanged(string value) => OnPropertyChanged(nameof(HasRvPart));

    /// <summary>按当前数据刷新名称和标记（只在变化时触发通知）。</summary>
    internal void Refresh(IFleetView fleet)
    {
        switch (Kind)
        {
            case PageKind.Overview:
                Set("总览", "总览", "", "overview", "overview", false, false, "总览");
                break;
            case PageKind.Gallery:
                Set("组件", "组件", "", "layout", "layout", false, false, "组件");
                break;
            case PageKind.Placeholder:
                var ph = Placeholders.Get(Route.Key);
                var t = ph?.Title ?? Route.Key ?? "";
                Set(t, t, "", ph?.Icon ?? "layout", ph?.Icon ?? "layout", false, false, t);
                break;
            case PageKind.Device:
            {
                var d = fleet.Device(DeviceId);
                if (d == null) { var l = "设备 " + DeviceId; Set(l, l, "", null, "machine", false, false, l); break; }
                bool off = d.Conn != ConnState.Online;
                int alarms = d.ErrAlarms + d.WarnAlarms;
                var tip = d.Alias + " · " + d.ModelLabel + " · " + d.ConnText + (alarms > 0 ? " · " + ShellText.AlarmText(d) : "") + (d.EstopActive ? " · " + d.EstopText : "");
                Set(d.Alias, d.Alias, "", null, "machine", off, d.ErrAlarms > 0 || d.EstopActive, tip);
                break;
            }
            case PageKind.Reactor:
            {
                var d = fleet.Device(DeviceId);
                var ch = d?.Channels.FirstOrDefault(c => c.N == Rv);
                var rv = "RV" + Rv;
                if (d == null || ch == null) { var l = (d?.Alias ?? "设备 " + DeviceId) + " · " + rv; Set(l, l, "", null, "devices", false, false, l); break; }
                bool off = d.Conn != ConnState.Online;
                var word = off ? StateKeys.OfflineText(d.EverOnline) : ch.Word;
                var tip = d.Alias + " · " + rv + " · " + word + (off && d.DataText.Length > 0 ? " · " + d.DataText : "");
                Set(d.Alias + " · " + rv, d.Alias, "\u00a0·\u00a0" + rv, null, "devices", off, ch.Key == StateKey.Alarm, tip);
                break;
            }
        }
    }

    private void Set(string label, string alias, string rvPart, string? icon, string menuIcon, bool off, bool err, string tip)
    {
        Label = label; Alias = alias; RvPart = rvPart; Icon = icon; MenuIcon = menuIcon; Offline = off; ErrAlarm = err; Tip = tip;
    }
}

/// <summary>
/// 文档页签栏（样稿 shell.js 页签部分）：默认页签同 defaultTabs（总览、1号机、1号机 · RV1、1号机 · RV3、2号机 · RV1、3号机、全部曲线；
/// 不存在的设备 / 反应釜跳过）；路由打开的页签不在栏里时追加到末尾；关闭当前页签时切到左边一个；设备没了的页签自动去掉。
/// 放不下时的隐藏由 Controls/Shell/TabOverflowPanel 量出来、写回 <see cref="SetHidden"/>（「›」上的未显示个数）。
/// </summary>
public sealed partial class TabsVm : ObservableObject
{
    private readonly Func<IFleetView> _fleet;
    private readonly Action<Route> _navigate;

    public TabsVm(Func<IFleetView> fleet, Action<Route> navigate)
    {
        _fleet = fleet;
        _navigate = navigate;
    }

    public ObservableCollection<DocTabVm> Tabs { get; } = new();

    [ObservableProperty] private int _hiddenCount;
    [ObservableProperty] private string _moreTip = "全部页签";

    public bool HasHidden => HiddenCount > 0;
    /// <summary>「›」上的未显示个数（0 时 null，不画）。</summary>
    public string? HiddenCountText => HiddenCount > 0 ? HiddenCount.ToString() : null;
    partial void OnHiddenCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasHidden));
        OnPropertyChanged(nameof(HiddenCountText));
        MoreTip = "全部页签" + (value > 0 ? " · " + value + " 个未显示" : "");
    }

    public DocTabVm? Active => Tabs.FirstOrDefault(t => t.IsActive);

    /// <summary>样稿 defaultTabs（设备 / 反应釜不存在或未安装时跳过）。</summary>
    public static IReadOnlyList<Route> DefaultRoutes(IFleetView fleet)
    {
        var list = new List<Route> { Route.Overview };
        void Add(Route r) { if (Exists(fleet, r)) list.Add(r); }
        Add(Route.Device(1)); Add(Route.Reactor(1, 1)); Add(Route.Reactor(1, 3)); Add(Route.Reactor(2, 1)); Add(Route.Device(3));
        Add(Route.Placeholder("curves"));
        return list;
    }

    /// <summary>这个路由的对象还在（设备在、反应釜在且已安装）。</summary>
    public static bool Exists(IFleetView fleet, Route r) => r.Page switch
    {
        PageKind.Device => fleet.Device(r.K) != null,
        PageKind.Reactor => fleet.Device(r.K)?.Channels.Any(c => c.N == r.N && c.Installed) == true,
        _ => true,
    };

    /// <summary>换成默认页签（设备组合变了时，同样稿 pruneTabs 的 _tabsFor）。</summary>
    public void ResetToDefaults()
    {
        Tabs.Clear();
        foreach (var r in DefaultRoutes(_fleet())) Tabs.Add(Make(r));
        Refresh();
    }

    private DocTabVm Make(Route r) => new(r, r.Page == PageKind.Overview, t => _navigate(t.Route), Close);

    /// <summary>当前路由的页签（没有就追加）设为当前。</summary>
    public void Activate(Route route)
    {
        var tab = Tabs.FirstOrDefault(t => t.Id == route.Id);
        if (tab == null) { tab = Make(route); Tabs.Add(tab); tab.Refresh(_fleet()); }
        foreach (var t in Tabs) t.IsActive = ReferenceEquals(t, tab);
        OnPropertyChanged(nameof(Active));
    }

    public void Close(DocTabVm tab)
    {
        int i = Tabs.IndexOf(tab);
        if (i < 0 || tab.Pinned) return;
        bool wasActive = tab.IsActive;
        Tabs.RemoveAt(i);
        if (wasActive && Tabs.Count > 0) _navigate(Tabs[Math.Max(0, i - 1)].Route);
    }

    /// <summary>窗口菜单「关闭其他页签」：只留固定页签和当前页签。</summary>
    public void CloseOthers()
    {
        foreach (var t in Tabs.Where(t => !t.Pinned && !t.IsActive).ToList()) Tabs.Remove(t);
    }

    /// <summary>去掉对象已经不在的页签；刷新名称与标记。</summary>
    public void Refresh()
    {
        var fleet = _fleet();
        foreach (var t in Tabs.Where(t => !Exists(fleet, t.Route)).ToList()) Tabs.Remove(t);
        foreach (var t in Tabs) t.Refresh(fleet);
    }

    /// <summary>TabOverflowPanel 量出的隐藏集合（页签 Id）。</summary>
    public void SetHidden(IReadOnlyCollection<string> ids)
    {
        foreach (var t in Tabs) t.IsHidden = ids.Contains(t.Id);
        HiddenCount = ids.Count;
    }
}
