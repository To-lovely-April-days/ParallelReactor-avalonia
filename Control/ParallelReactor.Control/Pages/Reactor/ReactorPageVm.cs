using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Alarms;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using ParallelReactor.Control.Shell;

namespace ParallelReactor.Control.Pages;

/// <summary>
/// 反应釜页（样稿 js/pages/reactor.js，PORT-PLAN §5 M5）：面板上方的状态条（<see cref="Alerts"/>）+ 面板网格（视图 ReactorGridPanel，Core 的 ReactorPageLayout）：
/// 「程序」（<see cref="Program"/>）｜「釜体 / 属性查看器」（<see cref="Vessel"/>）+「曲线」（<see cref="Curves"/>）；未安装的位置只有一块说明（<see cref="Un"/>）。
/// <list type="bullet">
/// <item>状态条（样稿 alertsHTML 的顺序与文字）：连接中断（中性条 + 红色「连接中断」徽标 + 「立即重连」/「连接」；首次连接没有按钮）→ 急停 →
///   这个反应釜的报警（错误在前，最多两条；这条报警屏幕上唯一的「确认」，已确认的写「✓ 已确认」）→ 等待进气（「✋ 触屏操作」）；
///   真机的等待是排队等共用气路：「等待气路（RV3 占用中）」，没有触屏操作（§3.5 / R6）。</item>
/// <item>同一条报警屏幕上只有一个「确认」：状态条上带「确认」的报警由页面声明（<see cref="OwnedAlarms"/>，样稿 ownsAlarms），检查器不再重复这条报警。
///   断线说明条上有「立即重连」：检查器的控制权卡不再放第二个（<see cref="ShowsReconnect"/>）。</item>
/// </list>
/// </summary>
public sealed partial class ReactorPageVm : PageVm
{
    /// <summary>页面拥有的报警写在检查器里的位置（样稿 ownsAlarms 的值）。</summary>
    public const string StripOwnerName = "顶部报警条";

    public ReactorPageVm(ShellVm? shell, Route route) : base(route)
    {
        Shell = shell;
        Vessel = new ReactorVesselVm(shell, route.K, route.N);
        Curves = new ReactorCurvesVm(route.K, route.N);
        Program = new ReactorProgramVm(route.K, route.N);
        Un = new ReactorUnVm(shell, route.K, route.N);
    }

    public ShellVm? Shell { get; }

    /// <summary>「釜体 / 属性查看器」页签面板。</summary>
    public ReactorVesselVm Vessel { get; }

    /// <summary>「曲线」面板（图例 + 10 / 30 / 60 min + TimeSeriesChart）。</summary>
    public ReactorCurvesVm Curves { get; }

    /// <summary>「程序」面板（步骤卡片画布 / 程序库）。</summary>
    public ReactorProgramVm Program { get; }

    /// <summary>未安装的位置（样稿 unPanel）。</summary>
    public ReactorUnVm Un { get; }

    /// <summary>这一页的设备（被移除后为 null）。</summary>
    public DeviceVm? D { get; private set; }

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Crumb))] private string _alias = "";
    public string Rv => "RV" + Route.N.ToString(CultureInfo.InvariantCulture);
    /// <summary>「1号机 › RV1」（未安装的位置的面板标题）。</summary>
    public string Crumb => Alias + " › " + Rv;

    /// <summary>这个位置装了反应釜（否则整页只有一块「未安装」说明）。</summary>
    [ObservableProperty] private bool _installed = true;

    /// <summary>面板上方的状态条。按键就地增删。</summary>
    public ObservableCollection<ReactorAlertVm> Alerts { get; } = new();

    /// <summary>藏在别的页签后面时不刷新，切回来时先刷新再显示（R10，同总览 / 设备页；默认页签里有三个反应釜页）。</summary>
    public override bool RefreshWhenHidden => false;

    /// <summary>
    /// 状态条上带「确认」的报警（样稿 ownsAlarms：这个反应釜的报警条最多两条，没确认的）：键「k:报警 id」→「顶部报警条」。
    /// 检查器里这条报警不再列出（同一条报警屏幕上只有一个「确认」）。未安装的位置没有报警条。
    /// </summary>
    public override IReadOnlyDictionary<string, string> OwnedAlarms
    {
        get
        {
            if (D is not { } d || !Installed) return NoOwnedAlarms;
            Dictionary<string, string>? map = null;
            foreach (var a in StripAlarms(d, Route.N))
                if (!a.Acked) (map ??= new Dictionary<string, string>(StringComparer.Ordinal))[d.K.ToString(CultureInfo.InvariantCulture) + ":" + a.Id] = StripOwnerName;
            return map ?? NoOwnedAlarms;
        }
    }

    /// <summary>断线时页面顶部有说明条和「立即重连」（样稿 alertsHTML 的离线条）：检查器的控制权卡不再放第二个。</summary>
    public override bool ShowsReconnect => true;

    private string _ownSig = "";

    public override void Refresh(IFleetView fleet)
    {
        var d = (Shell?.Fleet.Device(Route.K)) ?? fleet.Device(Route.K) as DeviceVm;
        D = d;
        Alias = d?.Alias ?? fleet.Device(Route.K)?.Alias ?? "设备 " + Route.K;
        var r = d?.Reactor(Route.N);
        Installed = r is { Installed: true };
        Vessel.Refresh(d);
        Curves.Refresh(d);
        Program.Refresh(d);
        Un.Refresh(d);
        SyncAlerts(d == null ? Array.Empty<ReactorAlertVm>() : AlertsOf(d, r));
        SyncOwnership();
    }

    /// <summary>页面拥有的报警变了：检查器重新问一次（样稿 paint 末尾的 syncInspector）。</summary>
    private void SyncOwnership()
    {
        var sig = string.Join(",", OwnedAlarms.Keys.OrderBy(x => x, StringComparer.Ordinal));
        if (sig == _ownSig) return;
        _ownSig = sig;
        if (Shell != null && ReferenceEquals(Shell.Page, this)) Shell.Inspector.Refresh();
    }

    /// <summary>像新打开的页面（截图、外框的重置视图）：程序面板的放大回到 100 %、程序库选第一项。</summary>
    public void ResetView() => Program.ResetView();

    private void SyncAlerts(IReadOnlyList<ReactorAlertVm> want)
    {
        for (int i = Alerts.Count - 1; i >= 0; i--)
            if (!want.Any(w => w.Key == Alerts[i].Key)) Alerts.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            int at = -1;
            for (int j = 0; j < Alerts.Count; j++) if (Alerts[j].Key == want[i].Key) { at = j; break; }
            if (at < 0) Alerts.Insert(Math.Min(i, Alerts.Count), want[i]);
            else
            {
                Alerts[at].Apply(want[i]);
                if (at != i) Alerts.Move(at, i);
            }
        }
    }

    /// <summary>这个反应釜的报警条（样稿 stripAlarms）：报警在前、提醒在后（同级按时间新的在前），最多两条。</summary>
    public static IReadOnlyList<AlarmEntry> StripAlarms(DeviceVm d, int n) =>
        d.AlarmList.Where(a => a.Ch == n).OrderBy(a => a.IsErr ? 0 : 1).ThenByDescending(a => a.At).Take(2).ToList();

    /// <summary>
    /// 状态条（样稿 alertsHTML 的顺序与文字）：连接中断 / 首次连接 → 急停 → 这个反应釜的报警（错误在前，最多两条）→ 等待进气。
    /// </summary>
    public static IReadOnlyList<ReactorAlertVm> AlertsOf(DeviceVm d, ReactorVm? r)
    {
        var o = new List<ReactorAlertVm>();
        var src = d.Source;
        bool on = !d.IsOffline;
        if (!on)
        {
            // 一个红色信号：「连接中断」徽标；说明文字中性。首次连接没有按钮（还没连上过，正在自动连）
            bool first = d.IsFirst;
            var text = first ? "首次连接 · 尚未收到数据"
                : "读数停在最后时刻 · " + DeviceTexts.DataText(src.DataAt, d.EverOnline) + (src.Lease.State == LeaseStatus.Lost ? " · 本机控制权保留 " + src.Lease.GraceS + " s" : "");
            var a = new ReactorAlertVm("ra-off", ReactorAlertTone.Off, "", "", text, "") { Conn = d.Conn, ConnText = d.ConnText };
            if (!first)
            {
                a.ButtonText = d.ConnStatus == ConnStatus.Disconnected ? "连接" : "立即重连";
                a.ButtonIcon = "refresh";
                a.ButtonReason = Nz(d.Actions.Why(DeviceAction.Reconnect));
                a.Run = () => _ = d.Actions.RunAsync(DeviceAction.Reconnect);
            }
            o.Add(a);
        }
        if (src.Estop.Active)
            o.Add(new ReactorAlertVm("ra-estop", ReactorAlertTone.Err, "estop", d.EstopText, "加热 / 进气 / 搅拌已切断 · 复位只能在" + d.Alias + "触屏上操作",
                src.Estop.At is { } at ? Fmt.Time(at) : ""));
        if (r is not { Installed: true }) return o;
        foreach (var a in StripAlarms(d, r.N))
        {
            var p = r.Program?.Record;
            string held = a.IsErr && p is { Status: Core.Programs.ProgramStatus.Held }
                ? " · 程序在第 " + (p.StepIndex + 1) + "/" + p.Steps.Count + " 步暂停" : "";
            string title = a.Code == AlarmCodes.PressureHigh ? "超压报警" : a.IsErr ? "报警" : "提醒";
            var v = new ReactorAlertVm("ra-" + a.Key, a.IsErr ? ReactorAlertTone.Err : ReactorAlertTone.Warn, a.IsErr ? "alert" : "warn", title, a.Text + held, a.AtText)
            {
                Acked = a.Acked,
            };
            if (!a.Acked)
            {
                var id = a.Id;
                v.ButtonText = "确认";
                v.ButtonIcon = "check";
                v.ButtonReason = Nz(d.Actions.Why(DeviceAction.AlarmAck, new ActionPayload(Id: id)));
                v.Run = () => _ = d.Actions.RunAsync(DeviceAction.AlarmAck, new ActionPayload(Id: id));
            }
            o.Add(v);
        }
        if (on && r.Program?.Progress is { WaitGas: true } pr)
        {
            if (src.IsDemo)
                o.Add(new ReactorAlertVm("ra-wait", ReactorAlertTone.Wait, "hand", pr.WaitWord,
                    pr.GasName + " 进气口关闭 · 需在" + d.Alias + "触屏打开 · 第 " + pr.K + "/" + pr.N + " 步「" + pr.StepName + "」暂停计时", "") { Touch = true });
            else
                o.Add(LiveWaitAlert(d, "第 " + pr.K + "/" + pr.N + " 步「" + pr.StepName + "」暂停计时"));
        }
        else if (on && !src.IsDemo && r.Program is null && r.Hmi is { Running: true, WaitingGas: true, StepCount: > 0 } h)
            o.Add(LiveWaitAlert(d, "第 " + Math.Clamp(h.StepIndex + 1, 1, h.StepCount) + "/" + h.StepCount + " 步暂停计时"));
        return o;
    }

    /// <summary>
    /// 真机的等待条：waitingGas 是程序排队等共用气路（别的反应釜正占用），不是样稿的「进气口关闭、去触屏打开」（PORT-PLAN §3.5 / R6）——
    /// 标题「等待气路（RV3 占用中）」（StateMap.WaitGasText，占用者取帧的 programs.gasHolder），说明只写哪一步在等，没有「✋ 触屏操作」。
    /// </summary>
    private static ReactorAlertVm LiveWaitAlert(DeviceVm d, string text) =>
        new("ra-wait", ReactorAlertTone.Wait, "clock", StateMap.WaitGasText(d.GasHolder), text, "");

    private static string? Nz(string s) => s.Length == 0 ? null : s;
}

/// <summary>状态条的语气：报警（浅红底、红图标）/ 提醒 / 等待进气 / 连接中断（中性）。</summary>
public enum ReactorAlertTone { Err, Warn, Wait, Off }

/// <summary>
/// 反应釜页的一条状态条（样稿 .reactor-alert）：图标 + 粗体标题 + 说明（省略 + 悬停全文）+ 时间（13 px，靠右）+ 右侧（按钮「确认」/「立即重连」、「✓ 已确认」、「✋ 触屏操作」）；
/// 连接中断条是「连接中断 · 重连中 12 s」徽标 + 说明 + 按钮。身份 = <see cref="Key"/>，就地更新。
/// </summary>
public sealed partial class ReactorAlertVm : ObservableObject
{
    public ReactorAlertVm(string key, ReactorAlertTone tone, string icon, string title, string text, string time)
    {
        Key = key;
        _tone = tone;
        _icon = icon;
        _title = title;
        _text = text;
        _time = time;
        ButtonCommand = new RelayCommand(() => Run?.Invoke());
    }

    public string Key { get; }
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsErr), nameof(IsOff), nameof(IsWait), nameof(HasIcon))] private ReactorAlertTone _tone;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIcon))] private string _icon;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTitle))] private string _title;
    [ObservableProperty] private string _text;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasTime))] private string _time;
    /// <summary>连接中断条的徽标（状态 + 文字「连接中断 · 重连中 12 s」）。</summary>
    [ObservableProperty] private ConnState _conn;
    [ObservableProperty] private string _connText = "";
    /// <summary>右侧按钮（「确认」/「立即重连」/「连接」）；没有为 null。</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasButton))] private string? _buttonText;
    [ObservableProperty] private string _buttonIcon = "check";
    /// <summary>按钮不能点的原因（断线时的「确认」…）；能点为 null。</summary>
    [ObservableProperty] private string? _buttonReason;
    /// <summary>报警已确认：右侧写「✓ 已确认」。</summary>
    [ObservableProperty] private bool _acked;
    /// <summary>等待进气：右侧「✋ 触屏操作」。</summary>
    [ObservableProperty] private bool _touch;

    /// <summary>按钮要做的事（执行时守卫再查一次，见 Fleet/DeviceActions）。</summary>
    public Action? Run { get; set; }
    public ICommand ButtonCommand { get; }

    public bool IsErr => Tone == ReactorAlertTone.Err;
    public bool IsOff => Tone == ReactorAlertTone.Off;
    public bool IsWait => Tone == ReactorAlertTone.Wait;
    public bool HasIcon => !IsOff && Icon.Length > 0;
    public bool HasTitle => Title.Length > 0;
    public bool HasTime => Time.Length > 0;
    public bool HasButton => ButtonText is not null;

    internal void Apply(ReactorAlertVm o)
    {
        Tone = o.Tone;
        Icon = o.Icon;
        Title = o.Title;
        Text = o.Text;
        Time = o.Time;
        Conn = o.Conn;
        ConnText = o.ConnText;
        ButtonText = o.ButtonText;
        ButtonIcon = o.ButtonIcon;
        ButtonReason = o.ButtonReason;
        Acked = o.Acked;
        Touch = o.Touch;
        Run = o.Run;
    }
}
