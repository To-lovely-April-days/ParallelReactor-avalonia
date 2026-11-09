using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// 重命名（样稿 dialogs.js 'rename'）：只有别名一栏（输入时就地校验，计数「5/16」）；「确定」带别名错误作原因。确定 = 样稿 act(rename)：
/// 演示改 DemoFleet，真机只改本机（devices.json），提示「新名 · 已重命名（原名 旧名）」。设备被移除时关闭。
/// </summary>
public sealed partial class RenameDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly int _k;
    private bool _closed;

    private RenameDialogVm(ShellVm shell, int k, DeviceVm d)
    {
        _shell = shell;
        _k = k;
        Name = "rename";
        Arg = DialogText.K(k);
        Icon = "edit";
        Width = 480;
        Field = new AliasFieldVm(d.Alias, a => shell.Fleet.AliasError(a, k));
        OkButton = new FootButton("确定", new AsyncRelayCommand(OkAsync), FootKind.Primary);
        Footer = new FootBar(new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this))), OkButton);
        Field.PropertyChanged += (_, _) => OkButton.Reason = FootButton.Nz(Field.Error);
        OkButton.Reason = FootButton.Nz(Field.Error);
        Body = Field;
        Refresh();
    }

    public static RenameDialogVm? Create(ShellVm shell, string? arg) =>
        DialogText.ParseK(arg) is int k && shell.Fleet.Device(k) is { } d ? new RenameDialogVm(shell, k, d) : null;

    public AliasFieldVm Field { get; }
    public FootButton OkButton { get; }

    public override void Refresh()
    {
        if (_closed) return;
        var d = _shell.Fleet.Device(_k);
        if (d == null) { _shell.Dialogs.Close(this); return; }
        Title = "重命名 " + d.Alias;
        Sub = d.ModelLabel + " · " + d.Host;
    }

    private async Task OkAsync()
    {
        var d = _shell.Fleet.Device(_k);
        if (d == null) return;
        var o = await d.Actions.RunAsync(DeviceAction.Rename, new ActionPayload(Alias: Field.Alias)).ConfigureAwait(true);
        if (o.Ok) _shell.Dialogs.Close(this);
        else Field.Validate();
    }

    public override void OnClosed() => _closed = true;
}

/// <summary>
/// 移除确认（样稿 dialogs.js 'remove'）：说明「会断开连接并从本机列表中删除…」；本机持有 / 申请着控制权时多一条 warn 说明（移除前先释放 / 取消申请）。
/// 「移除」（主要，垃圾桶）= 先释放 / 取消（quiet），关掉对话框，再 act(remove)（真机：从 devices.json 删掉、断开；提示「已移除 2号机」）。
/// </summary>
public sealed partial class RemoveDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly int _k;
    private bool _closed;

    private RemoveDialogVm(ShellVm shell, int k)
    {
        _shell = shell;
        _k = k;
        Name = "remove";
        Arg = DialogText.K(k);
        Icon = "trash";
        Width = 520;
        Footer = new FootBar(
            new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this))),
            new FootButton("移除", new AsyncRelayCommand(OkAsync), FootKind.Primary, "trash"));
        Body = this;
        Refresh();
    }

    public static RemoveDialogVm? Create(ShellVm shell, string? arg) =>
        DialogText.ParseK(arg) is int k && shell.Fleet.Device(k) != null ? new RemoveDialogVm(shell, k) : null;

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _warn = "";
    [ObservableProperty] private bool _hasWarn;

    /// <summary>样稿 live: false —— 内容只在打开时定下来（设备被移除时关闭）。</summary>
    public override void Refresh()
    {
        if (_closed) return;
        var d = _shell.Fleet.Device(_k);
        if (d == null) { _shell.Dialogs.Close(this); return; }
        if (Title.Length > 0) return;
        Title = "移除 " + d.Alias + "？";
        Sub = d.ModelLabel + " · " + d.Host;
        Text = "会断开连接并从本机列表中删除" + d.Alias + "，相关页签一起关闭。设备上的程序、数据和控制权设置不受影响，之后可重新添加。";
        Warn = d.LeaseStatus is LeaseStatus.Remote or LeaseStatus.Lost ? "本机正持有" + d.Alias + "的控制权，移除前会先释放。"
            : d.LeaseStatus == LeaseStatus.Requested ? "本机正在申请" + d.Alias + "的控制权，移除前会先取消申请。" : "";
        HasWarn = Warn.Length > 0;
    }

    private async Task OkAsync()
    {
        var d = _shell.Fleet.Device(_k);
        if (d == null) return;
        if (d.LeaseStatus is LeaseStatus.Remote or LeaseStatus.Lost) await d.Actions.RunAsync(DeviceAction.LeaseRelease, quiet: true).ConfigureAwait(true);
        else if (d.LeaseStatus == LeaseStatus.Requested) await d.Actions.RunAsync(DeviceAction.LeaseCancel, quiet: true).ConfigureAwait(true);
        _shell.Dialogs.Close(this);
        await d.Actions.RunAsync(DeviceAction.Remove).ConfigureAwait(true);
    }

    public override void OnClosed() => _closed = true;
}

/// <summary>
/// 断开确认（样稿 dialogs.js 'disconnect'，只在本机持有或正在申请控制权时打开 —— ShellVm.RequestDisconnect）：说明断开后不能再发任何命令（包括急停）；
/// warn 说明先释放控制权 / 先取消申请。「释放并断开」/「取消申请并断开」（主要，unplug）= 关掉对话框，act(disconnect)（释放 / 取消在断开里做）。每秒刷新。
/// </summary>
public sealed partial class DisconnectDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly int _k;
    private bool _closed;

    private DisconnectDialogVm(ShellVm shell, int k)
    {
        _shell = shell;
        _k = k;
        Name = "disconnect";
        Arg = DialogText.K(k);
        Icon = "unplug";
        Width = 540;
        OkButton = new FootButton("释放并断开", new AsyncRelayCommand(OkAsync), FootKind.Primary, "unplug");
        Footer = new FootBar(new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this))), OkButton);
        Body = this;
        Refresh();
    }

    public static DisconnectDialogVm? Create(ShellVm shell, string? arg) =>
        DialogText.ParseK(arg) is int k && shell.Fleet.Device(k) != null ? new DisconnectDialogVm(shell, k) : null;

    public FootButton OkButton { get; }
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _warn = "";

    public override void Refresh()
    {
        if (_closed) return;
        var d = _shell.Fleet.Device(_k);
        if (d == null) { _shell.Dialogs.Close(this); return; }
        bool req = d.LeaseStatus == LeaseStatus.Requested;
        Title = "断开 " + d.Alias + "？";
        Sub = d.ModelLabel + " · " + d.Host + ":" + d.Port;
        Text = "断开后本机不再接收" + d.Alias + "的数据，也不能向它发送任何命令（包括急停）；设备本身继续运行。";
        Warn = req ? "本机正在申请" + d.Alias + "的控制权，断开前会先取消申请。" : "本机正持有" + d.Alias + "的控制权，断开前会先释放，设备回到触屏本地控制。";
        OkButton.Label = req ? "取消申请并断开" : "释放并断开";
    }

    private async Task OkAsync()
    {
        var d = _shell.Fleet.Device(_k);
        if (d == null) return;
        _shell.Dialogs.Close(this);
        await d.Actions.RunAsync(DeviceAction.Disconnect).ConfigureAwait(true);
    }

    public override void OnClosed() => _closed = true;
}

/// <summary>「全部运行」确认清单的一行（反应釜 · 程序名 · 程序状态）。</summary>
public sealed record RunAllRowVm(string Name, string Sub, string Status);

/// <summary>
/// 「全部运行」的确认（真机，D6「先列清单确认」）：清单 = 这台设备上「全部运行」会运行的反应釜（本机这次下发过程序、已启用、没在运行 ——
/// 与守卫同一个判断 ActionGuard.IsRunAllTarget），每行写程序名和程序状态。确认 = 对清单里的每个反应釜运行程序（program/run，逐个过守卫），
/// 运行的正是确认时看到的这些（打开之后才下发的不算）。绝不调 HMI 的 POST /run（触屏的「开始运行」，含义不同）。
/// 打开时守卫不让运行（没有可运行的反应釜、没有控制权…）：不打开，提示原因。演示数据照样稿直接运行（不注册这个对话框的确认）。
/// 样稿没有这个对话框：外观照样稿其它对话框（行 = 急停 / 全部停止的勾选行，不能勾），文字新加 —— 行「1号机 · RV1」/「程序「…」」/ 程序状态、
/// 底栏左「已选 N 个反应釜」、按钮「全部运行」（样稿的按钮名）。
/// </summary>
public sealed partial class RunAllDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly DeviceVm _d;
    private readonly IReadOnlyList<int> _targets;

    private RunAllDialogVm(ShellVm shell, DeviceVm d, IReadOnlyList<int> targets, LiveDeviceSource live)
    {
        _shell = shell;
        _d = d;
        _targets = targets;
        Name = "runall";
        Arg = DialogText.K(d.K);
        Title = "全部运行";
        Sub = "运行" + d.Alias + "上已下发程序的反应釜";
        Icon = "play";
        Width = 560;
        var facts = d.Facts;
        Rows = targets.Select(n =>
        {
            var name = live.Downloaded(n)?.Name ?? "";
            var st = facts.Channel(n)?.Program;
            return new RunAllRowVm(d.Alias + " · RV" + n, "程序「" + name + "」", st is { } ps ? Core.Programs.Progress.StatusText(ps) : "");
        }).ToList();
        FooterLeft = "已选 " + targets.Count + " 个反应釜";
        Footer = new FootBar(
            new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this))),
            new FootButton("全部运行", new AsyncRelayCommand(GoAsync), FootKind.Primary, "play", large: true) { Reason = targets.Count == 0 ? "没有可运行的反应釜" : null });
        Body = this;
    }

    /// <summary>arg = 设备位置 k。设备不在、不是真机、或守卫不让「全部运行」时不打开（守卫的原因 toast 出来）。</summary>
    public static RunAllDialogVm? Create(ShellVm shell, string? arg)
    {
        if (DialogText.ParseK(arg) is not int k || shell.Fleet.Device(k) is not { } d) return null;
        if (d.Source is not LiveDeviceSource live) return null;
        var why = d.Actions.Why(DeviceAction.RunAll);
        if (why.Length > 0) { shell.Toast(why, ToastKind.Warn); return null; }
        return new RunAllDialogVm(shell, d, live.RunAllTargets(), live);
    }

    public IReadOnlyList<RunAllRowVm> Rows { get; }

    private async Task GoAsync()
    {
        if (_targets.Count == 0) return;
        _shell.Dialogs.Close(this);
        int k = 0;
        var failed = new List<ActionOutcome>();
        foreach (var n in _targets)
        {
            var o = await _d.Actions.RunAsync(DeviceAction.ProgRun, ActionPayload.Ch(n), quiet: true).ConfigureAwait(true);
            if (o.Ok) k++; else failed.Add(o);
        }
        if (k > 0) _shell.Toast(_d.Alias + " · 已启动 " + k + " 个反应釜", ToastKind.Ok);
        foreach (var f in failed) _shell.Toast(f.Message, f.Tone == ToastKind.Err ? ToastKind.Err : ToastKind.Warn);
    }
}
