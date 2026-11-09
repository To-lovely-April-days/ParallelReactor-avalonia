using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>申请控制权对话框里的一步（样稿 .dlg-steps：已发送请求 ✓ / 等待现场确认（当前）/ 远程控制中（下一步））。</summary>
public sealed partial class LeaseStepVm : ObservableObject
{
    public LeaseStepVm(int no, string state) { No = no.ToString(System.Globalization.CultureInfo.InvariantCulture); State = state; }

    /// <summary>圆里的序号（已完成的画 ✓）。</summary>
    public string No { get; }
    /// <summary>done / cur / next。</summary>
    public string State { get; }
    public bool IsDone => State == "done";
    public bool IsCur => State == "cur";
    public bool IsNext => State == "next";
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _text = "";
}

/// <summary>
/// 申请控制权 · k（样稿 dialogs.js 'lease'，final-dlg-lease2）：控制权按设备独立，每台一个。按这台设备此刻的状态显示五种内容（每秒刷新，样稿 live: true）：
/// <list type="bullet">
/// <item>本地只读（以及真机的「其他客户端持有」、别人的申请待批 —— 「发送申请」带守卫的原因）：发送前的说明 + 三条要点；「取消」+「发送申请」。</item>
/// <item>申请中：倒计时环（剩余秒数）+ 三步（已发送请求 / 等待现场确认 / 远程控制中）+ 说明；底栏左「⏱ 倒计时结束自动取消」，「取消申请」+「后台等待」。</item>
/// <item>远程控制中：「1号机 远程控制中 · 本机 PC-… 持有控制权 · 心跳正常」；「释放控制权」+「完成」。</item>
/// <item>心跳中断（宽限）：红色说明（剩余宽限秒数）；「释放控制权」+「关闭」。图标块红色。</item>
/// <item>断线：红色说明（连接的原因）+「连接恢复后才能申请。」；「关闭」+「立即重连」/「连接」（用户断开的）。图标块红色。</item>
/// </list>
/// 操作都走 DeviceActions（执行前再查守卫、提示同样稿 act）。设备被移除时自动关闭（样稿 onDevices）。
/// </summary>
public sealed partial class LeaseDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly int _k;
    private bool _closed;

    private LeaseDialogVm(ShellVm shell, int k)
    {
        _shell = shell;
        _k = k;
        Name = "lease";
        Arg = DialogText.K(k);
        Icon = "key";
        Width = 600;
        Steps = new[] { new LeaseStepVm(1, "done"), new LeaseStepVm(2, "cur"), new LeaseStepVm(3, "next") };
        var close = new RelayCommand(() => shell.Dialogs.Close(this));
        CancelButton = new FootButton("取消", close);
        SendButton = new FootButton("发送申请", new AsyncRelayCommand(() => Act(DeviceAction.LeaseRequest, keepOpen: true)), FootKind.Primary, "key", large: true);
        CancelRequestButton = new FootButton("取消申请", new AsyncRelayCommand(() => Act(DeviceAction.LeaseCancel, keepOpen: false)));
        WaitButton = new FootButton("后台等待", close, FootKind.Primary);
        ReleaseButton = new FootButton("释放控制权", new AsyncRelayCommand(() => Act(DeviceAction.LeaseRelease, keepOpen: false)), icon: "unlock");
        DoneButton = new FootButton("完成", close, FootKind.Primary);
        ClosePrimaryButton = new FootButton("关闭", close, FootKind.Primary);
        CloseButton = new FootButton("关闭", close);
        ReconnectButton = new FootButton("立即重连", new AsyncRelayCommand(() => Act(DeviceAction.Reconnect, keepOpen: true)), FootKind.Primary, "refresh");
        Body = this;
        Refresh();
    }

    /// <summary>arg = 设备位置 k；设备不在时不打开。</summary>
    public static LeaseDialogVm? Create(ShellVm shell, string? arg) =>
        DialogText.ParseK(arg) is int k && shell.Fleet.Device(k) != null ? new LeaseDialogVm(shell, k) : null;

    public int K => _k;
    public IReadOnlyList<LeaseStepVm> Steps { get; }
    public DeviceBadgeVm Badge { get; } = new();

    public FootButton CancelButton { get; }
    public FootButton SendButton { get; }
    public FootButton CancelRequestButton { get; }
    public FootButton WaitButton { get; }
    public FootButton ReleaseButton { get; }
    public FootButton DoneButton { get; }
    public FootButton ClosePrimaryButton { get; }
    public FootButton CloseButton { get; }
    public FootButton ReconnectButton { get; }

    /// <summary>local / requested / remote / lost / offline（决定内容区显示哪一块）。</summary>
    [ObservableProperty] private string _mode = "local";
    [ObservableProperty] private bool _isLocal;
    [ObservableProperty] private bool _isRequested;
    [ObservableProperty] private bool _isRemote;
    [ObservableProperty] private bool _isLost;
    [ObservableProperty] private bool _isOffline;

    // 设备卡片（样稿 .dlg-devcard）
    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _ident = "";

    // 本地只读：说明（样稿 .dlg-explain）
    [ObservableProperty] private IReadOnlyList<TextSeg> _explain = Array.Empty<TextSeg>();
    [ObservableProperty] private string _point1 = "";
    public string Point2 => "停止类操作（急停、全部停止、停止加热、中止程序）不需要控制权";
    public string Point3 => "阀门 / 进气 / 排空、设备设置、急停复位只能在触屏操作";

    // 申请中：倒计时环
    [ObservableProperty] private double _remain;
    [ObservableProperty] private string _remainText = "0";
    /// <summary>
    /// 环的满格：演示 = 样稿 LEASE_WAIT_S 60；真机 = HMI 的 requestTimeoutSec（GET /control，默认也是 60 —— 设了别的值时环照样从满格走到空，M3 复核）。
    /// </summary>
    [ObservableProperty] private double _remainMax = DemoFleet.LeaseWaitS;
    public string WaitNote => "超时未确认会自动取消。等待期间：停止类操作（急停、全部停止、停止加热、中止程序）照常可用。";

    // 远程控制中（样稿 .dlg-done）
    [ObservableProperty] private string _doneTitle = "";
    [ObservableProperty] private string _doneText = "";

    // 宽限 / 断线：红色说明
    [ObservableProperty] private string _errNote = "";

    public override void Refresh()
    {
        if (_closed) return;
        var d = _shell.Fleet.Device(_k);
        if (d == null) { _shell.Dialogs.Close(this); return; }
        string client = _shell.Fleet.ClientName;
        bool online = DialogText.Online(d);
        var L = d.LeaseStatus;
        Mode = !online ? "offline" : L == LeaseStatus.Requested ? "requested" : L == LeaseStatus.Remote ? "remote" : L == LeaseStatus.Lost ? "lost" : "local";
        IsLocal = Mode == "local"; IsRequested = Mode == "requested"; IsRemote = Mode == "remote"; IsLost = Mode == "lost"; IsOffline = Mode == "offline";

        Title = "申请控制权 · " + d.Alias;
        Sub = IsRequested ? "等待" + d.Alias + "触屏确认" : "控制权按设备独立：每台一个，互不影响";
        IsDanger = IsOffline || IsLost;
        Alias = d.Alias;
        Ident = d.ModelLabel + " · " + d.Host + " · SN " + (string.IsNullOrEmpty(d.Sn) ? Core.Format.Fmt.Dash : d.Sn);   // 从未连上、没记下 SN：「—」
        Badge.Apply(d, shortLease: false);

        Explain = new[]
        {
            new TextSeg("请求会显示在"), new TextSeg(d.Alias + "触屏", Bold: true),
            new TextSeg("上，现场人员确认后，本机（" + client + "）才能对这台设备下发启用、启动、设定温度、程序等操作。"),
        };
        Point1 = "只影响" + d.Alias + "，其它设备的控制权不变";

        var lease = d.Source.Lease;
        RemainMax = Math.Max(1, lease.WaitS);
        Remain = lease.RemainS;
        RemainText = lease.RemainS.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Steps[0].Title = "已发送请求"; Steps[0].Text = "本机 " + client + " → " + d.Alias;
        Steps[1].Title = "等待现场确认"; Steps[1].Text = "请现场人员在" + d.Alias + "触屏上点「允许」";
        Steps[2].Title = "远程控制中"; Steps[2].Text = "确认后本机可下发启动、设定、程序等操作";

        DoneTitle = d.Alias + " 远程控制中";
        // 真机连续 ≥ 2 次心跳没送到时写旧界面的「心跳异常」（样稿没有这一种，见检查器的控制权卡）
        // 持有者按 HMI 认的那个名字写（真机改了本机客户端名、这条连接还在用旧名字时，Lease.Holder 是旧名字）
        DoneText = "本机 " + (L == LeaseStatus.Remote && lease.Holder.Length > 0 ? lease.Holder : client) + " 持有控制权 · " + (lease.HeartbeatDegraded ? "心跳异常" : "心跳正常");

        if (IsLost) ErrNote = "控制权心跳中断，" + d.Alias + "在宽限期（剩 " + lease.GraceS + " s）内保留本机的控制权；心跳恢复后自动继续，超时则交回触屏。";
        else if (IsOffline) ErrNote = d.Actions.GuardText(GuardKind.Conn) + "。连接恢复后才能申请。";
        else ErrNote = "";

        SendButton.Reason = FootButton.Nz(d.Actions.Why(DeviceAction.LeaseRequest));
        ReconnectButton.Label = d.ConnStatus == ConnStatus.Disconnected ? "连接" : "立即重连";
        ReconnectButton.Reason = FootButton.Nz(d.Actions.Why(DeviceAction.Reconnect));

        FooterLeft = IsRequested ? new FootNote("clock", "倒计时结束自动取消") : null;
        FootButton[] foot = Mode switch
        {
            "offline" => new[] { CloseButton, ReconnectButton },
            "requested" => new[] { CancelRequestButton, WaitButton },
            "remote" => new[] { ReleaseButton, DoneButton },
            "lost" => new[] { ReleaseButton, ClosePrimaryButton },
            _ => new[] { CancelButton, SendButton },
        };
        if (Footer is not FootBar cur || !cur.SameAs(foot)) Footer = new FootBar(foot);
    }

    /// <summary>样稿 action：申请 / 重连留在对话框里（内容跟着状态换）；取消申请、释放之后关闭。</summary>
    private async Task Act(DeviceAction a, bool keepOpen)
    {
        var d = _shell.Fleet.Device(_k);
        if (d == null) return;
        if (!keepOpen) _shell.Dialogs.Close(this);
        await d.Actions.RunAsync(a).ConfigureAwait(true);
        if (keepOpen) Refresh();
    }

    public override void OnClosed() => _closed = true;
}
