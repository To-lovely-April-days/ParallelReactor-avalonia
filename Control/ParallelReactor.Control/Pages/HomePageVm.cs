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
using ParallelReactor.Control.Pages.Home;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 主页（融合版 F2 的框架；样稿 renderBoard 的 page = main）：
/// <list type="bullet">
/// <item>不选设备（总览）：每台设备一张卡片（<see cref="Cards"/>，融合版 F4 照样稿 overviewPanel）。</item>
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
        _mark = new RibbonTool("flag", "打标记", Mark, "在当前时刻打标记（写进这台设备正在记录的会话）");
        _export = new RibbonTool("download", "导出", () => shell.Toast("导出：CSV 数据 / PDF 报告在「记录」页（F7）", ToastKind.Info), "导出 CSV 数据 / PDF 报告");
        _reset = new RibbonTool("reset", "重置布局", shell.ResetLayout, "展开所有折叠的面板，恢复默认布局");
        _full = new RibbonTool("max", "全屏", shell.ToggleFullScreen, "全屏（F11）");
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

/// <summary>总览卡片里的一个通道小格（样稿 .mats i）：编号；运行 = 底部 3 px 运行绿，报警 = 白底红字 + 1.5 px 红框，其余灰底。</summary>
public sealed record OvMatVm(string Num, RunDot Dot, string Tip)
{
    public bool IsRun => Dot == RunDot.Run;
    public bool IsErr => Dot == RunDot.Err;
}

/// <summary>
/// 总览里的一张设备卡片（融合版 F4，样稿 overviewPanel 的 .ovdev）：顶上 3 px 状态条（报警 / 急停红、在跑绿、其余灰）·
/// 卡头「● 1号机 HT-PR8 … 远程控制 / 本地只读」· 指标「运行 6/8 · 报警 1 · 气体消耗 86.6 mmol」· 通道小格（每行 8 个）。点击 = 选中这台（看板）。
/// </summary>
public sealed partial class HomeCardVm : ObservableObject
{
    public HomeCardVm(ShellVm shell, DeviceVm d)
    {
        D = d;
        OpenCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() => shell.SelectDevice(D.K));
    }

    public DeviceVm D { get; }
    public System.Windows.Input.ICommand OpenCommand { get; }

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _model = "";
    /// <summary>状态条 / 卡头圆点：Err（报警 / 急停）、Run（有通道在跑）、Idle。</summary>
    [ObservableProperty] private RunDot _state;
    [ObservableProperty] private bool _remote;
    /// <summary>不是远程控制时的控制权小块文字（「本地只读」「申请中 45 s」「离线」…）。</summary>
    [ObservableProperty] private string _leaseShort = "";
    [ObservableProperty] private string _runText = "";
    [ObservableProperty] private string _errText = "0";
    [ObservableProperty] private bool _hasErr;
    [ObservableProperty] private string _gasText = "";
    [ObservableProperty] private string _tip = "";
    [ObservableProperty] private IReadOnlyList<OvMatVm> _mats = Array.Empty<OvMatVm>();

    internal void Refresh()
    {
        var d = D;
        Alias = d.Alias;
        Model = d.ModelLabel;
        Remote = d.Lease == LeaseState.Remote && !d.IsOffline;
        LeaseShort = d.IsOffline ? d.ConnShort : d.Lease == LeaseState.Local ? "本地只读" : d.LeaseShort;
        int err = d.Reactors.Count(r => r.Installed && r.Own == StateKey.Alarm);
        State = err > 0 || d.EstopActive ? RunDot.Err : d.Running > 0 ? RunDot.Run : RunDot.Idle;
        RunText = d.Running.ToString(CultureInfo.InvariantCulture) + "/" + d.Installed.ToString(CultureInfo.InvariantCulture);
        ErrText = err.ToString(CultureInfo.InvariantCulture);
        HasErr = err > 0;
        var gas = d.Reactors.Where(r => r.Installed).Select(r => r.GasMmol).ToList();
        GasText = gas.Any(g => g != null) ? gas.Sum(g => g ?? 0).ToString("0.0", CultureInfo.InvariantCulture) : Core.Format.Fmt.Dash;
        Tip = "进入" + d.Alias + "的看板（通道 · 曲线 · 共用资源）" + (d.IsOffline ? " · " + d.ConnText : "");
        var mats = d.Reactors.Where(r => r.Installed).Select(r =>
        {
            var dot = r.Own == StateKey.Alarm ? RunDot.Err : StateKeys.IsRunning(r.Own) ? RunDot.Run : RunDot.Idle;
            var word = Home.RunRowText.Word(r.Own, r.Cause, r.OwnWord);
            return new OvMatVm(r.N.ToString(CultureInfo.InvariantCulture), dot, r.Rv + " · " + word + " · " + r.TempText + " °C");
        }).ToList();
        if (Mats.Count != mats.Count || !Mats.SequenceEqual(mats)) Mats = mats;
    }
}
