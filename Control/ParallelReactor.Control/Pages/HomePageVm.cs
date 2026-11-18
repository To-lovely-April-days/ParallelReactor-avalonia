using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 主页（融合版 F2 的框架；样稿 renderBoard 的 page = main）：
/// <list type="bullet">
/// <item>不选设备（总览）：每台设备一张卡片（<see cref="Cards"/>，F2 的简单版；F4 做成样稿的总览卡片）。</item>
/// <item>选了设备：看板（<see cref="Board"/>）—— F2 先放三块面板的骨架（通道 / 曲线 / 共用资源，竖条与灰带同样稿），里面写 F3 要放什么。</item>
/// </list>
/// 工具条（样稿 mainTools）：数据（打标记 / 导出）· 视图（重置布局 / 全屏）。
/// </summary>
public sealed partial class HomePageVm : PageVm
{
    private readonly ShellVm _shell;
    private readonly RibbonTool _mark, _export, _reset, _full;
    private readonly IReadOnlyList<RibbonGroup> _tools;

    public HomePageVm(ShellVm shell) : base(AppPage.Home)
    {
        _shell = shell;
        _mark = new RibbonTool("f-flag", "打标记", Mark, "在当前时刻打标记（写进这台设备正在记录的会话）");
        _export = new RibbonTool("f-down", "导出", () => shell.Toast("导出：CSV 数据 / PDF 报告在「记录」页（F7）", ToastKind.Info), "导出 CSV 数据 / PDF 报告");
        _reset = new RibbonTool("f-reset", "重置布局", shell.ResetLayout, "展开所有折叠的面板，恢复默认布局");
        _full = new RibbonTool("f-max", "全屏", shell.ToggleFullScreen, "全屏（F11）");
        _tools = new[] { new RibbonGroup("数据", new[] { _mark, _export }), new RibbonGroup("视图", new[] { _reset, _full }) };
        shell.LayoutReset += (_, _) => ResetBoard();
    }

    public override IReadOnlyList<RibbonGroup> Tools => _tools;

    /// <summary>矮屏（&lt; 820）：看板上一行高一些（样稿 .gcols > .grow.r1{flex:1.3}，否则约 1.14）。</summary>
    [ObservableProperty] private bool _isShort;
    /// <summary>总览（不选设备）。</summary>
    [ObservableProperty] private bool _isOverview;
    /// <summary>看板标题行（「1号机 · 8 路 · 运行 6 · 报警 1」）。</summary>
    [ObservableProperty] private string _boardTitle = "";
    [ObservableProperty] private string _boardSub = "";
    /// <summary>总览灰带的副标题「全部设备 · 3 台 · 28 路」。</summary>
    [ObservableProperty] private string _overviewSub = "";

    /// <summary>总览卡片（每台一张，就地更新）。</summary>
    public ObservableCollection<HomeCardVm> Cards { get; } = new();

    /// <summary>看板骨架里每块面板要写的东西（F3 / F4 换成真正的内容）。</summary>
    public static readonly string MonitorPlan = "F3：运行表 —— 每路一行：状态、程序 · 当前步骤（步骤条）、本步剩余、预计结束、温度 / 压力（实测 · 设定）、吸气量、速率；选中一行打开检查器。F4：气路图 / 卡片 / 棒图三种显示方式。";
    public static readonly string CurvePlan = "F3：8 路叠加曲线（吸气量 / 温度 / 压力），选中的通道加粗，末端标签；数据来自历史缓存。";
    public static readonly string ResourcePlan = "F3：共用资源 —— 气路（惰性气 / 气体 A / 气体 B / 排空，占用与排队）、共用搅拌（转速、开关）、维护模式、会话、最近事件。";

    private DeviceVm? Device => _shell.Nav.DeviceK > 0 ? _shell.Fleet.Device(_shell.Nav.DeviceK) as DeviceVm : null;

    private void Mark()
    {
        if (Device is { } d) _ = d.Actions.RunAsync(DeviceAction.SessionMark, new ActionPayload(Text: d.Alias + " 标记"));
    }

    public override void Refresh(IFleetView fleet)
    {
        var d = Device;
        IsOverview = d == null;
        IsShort = _shell.Layout.IsShort;
        _mark.Reason = d == null ? "先选一台设备" : Nz(d.Actions.Why(DeviceAction.SessionMark));
        if (d != null)
        {
            BoardTitle = d.Alias;
            int err = d.ErrAlarms;
            BoardSub = " · " + d.ChannelCount.ToString(CultureInfo.InvariantCulture) + " 路 · 运行 " + d.Running.ToString(CultureInfo.InvariantCulture)
                + (err > 0 ? " · 报警 " + err.ToString(CultureInfo.InvariantCulture) : "");
            RefreshBoard(d);
        }
        var ds = fleet.Devices.OfType<DeviceVm>().ToList();
        OverviewSub = "全部设备 · " + ds.Count.ToString(CultureInfo.InvariantCulture) + " 台 · " + ds.Sum(x => x.ChannelCount).ToString(CultureInfo.InvariantCulture) + " 路";
        SyncCards(fleet);
    }

    private void SyncCards(IFleetView fleet)
    {
        var ds = fleet.Devices.OfType<DeviceVm>().ToList();
        for (int i = Cards.Count - 1; i >= 0; i--) if (!ds.Contains(Cards[i].D)) Cards.RemoveAt(i);
        for (int i = 0; i < ds.Count; i++)
        {
            var at = -1;
            for (int j = 0; j < Cards.Count; j++) if (ReferenceEquals(Cards[j].D, ds[i])) { at = j; break; }
            if (at < 0) Cards.Insert(i, new HomeCardVm(_shell, ds[i]));
            else if (at != i) Cards.Move(at, i);
        }
        foreach (var c in Cards) c.Refresh();
    }

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>总览里的一张设备卡片（F2 的简单版）：别名、型号、连接 / 控制权、运行 k/n、报警、记录中；点击 = 选中这台（看板）。</summary>
public sealed partial class HomeCardVm : ObservableObject
{
    private readonly ShellVm _shell;

    public HomeCardVm(ShellVm shell, DeviceVm d)
    {
        _shell = shell;
        D = d;
        OpenCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => shell.SelectDevice(D.K));
    }

    public DeviceVm D { get; }
    public System.Windows.Input.ICommand OpenCommand { get; }

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _line1 = "";
    [ObservableProperty] private string _line2 = "";
    /// <summary>第一行里红色的一段（「 · 报警 1」/「 · 急停」）与之后的中性部分（「 · 提醒 1」）。</summary>
    [ObservableProperty] private string _alarmRed = "";
    [ObservableProperty] private string _alarmTail = "";
    [ObservableProperty] private DeviceHealth _health;
    [ObservableProperty] private bool _remote;
    /// <summary>不是远程控制时的控制权小块文字（「本地只读」「申请中 45 s」…）。</summary>
    [ObservableProperty] private string _leaseShort = "";

    internal void Refresh()
    {
        var d = D;
        Alias = d.Alias;
        Model = d.ModelLabel;
        Health = d.Health;
        Remote = d.Lease == LeaseState.Remote && !d.IsOffline;
        LeaseShort = d.IsOffline ? d.ConnShort : d.Lease == LeaseState.Local ? "本地只读" : d.LeaseShort;
        Line1 = d.IsOffline ? d.ConnText : (d.RunText ?? "") + (d.SessionId != null ? " · 记录中" : "");
        Line2 = d.EtaText.Length > 0 ? d.EtaText : d.LeaseText;
        // 红色只给事实（急停、错误报警）；提醒中性
        AlarmRed = d.EstopActive ? " · " + d.EstopText : d.ErrAlarms > 0 ? " · 报警 " + d.ErrAlarms.ToString(CultureInfo.InvariantCulture) : "";
        AlarmTail = d.WarnAlarms > 0 && !d.EstopActive ? " · 提醒 " + d.WarnAlarms.ToString(CultureInfo.InvariantCulture) : "";
    }
}
