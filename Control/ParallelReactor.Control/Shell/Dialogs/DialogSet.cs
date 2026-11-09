using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;
using DeviceActions = ParallelReactor.Control.Fleet.DeviceActions;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// M2 的临时对话框（实时模式才注册；演示数据里这些名字照旧给「对话框 … 未定义」，正式的 9 个对话框在 M3）：
/// <list type="bullet">
/// <item>connect（菜单 设备 › 连接…）与 add（资源管理器「添加」、设备 › 添加设备…，M3 换成添加设备对话框）：「连接设备」——
///   字段同旧 ConnectDialog（地址 / 端口 / 访问令牌 / 客户端名 / 记住令牌），新样式；连上一台真机（M2 只有一台）。</item>
/// <item>estop / stopall：全局停止格与设备菜单的「急停…」「全部停止…」。真机上它们必须能用（旧界面就能急停），所以 M2 先给确认对话框：
///   对象 = 全部能发停止类的设备（在线，或只是 WebSocket 在重连；estop:k = 只这一台），文字照抄样稿；M3 换成可勾选设备的多选对话框。</item>
/// <item>runall:k：检查器「全部运行」在真机上先列清单确认（D6：只运行本机下发过、没在运行的反应釜，绝不调 HMI 的 POST /run）；
///   演示数据照样稿直接运行。</item>
/// </list>
/// </summary>
public static class DialogSet
{
    public static void RegisterLive(ShellVm shell)
    {
        shell.Dialogs.Register("connect", _ => new ConnectDialogVm(shell));
        // 「添加」（资源管理器标题栏、空状态「点「添加」连接第一台设备」、设备 › 添加设备…）在 M3 是添加设备对话框；M2 先打开「连接设备」
        shell.Dialogs.Register("add", _ => new ConnectDialogVm(shell));
        shell.Dialogs.Register("estop", arg => new StopDialogVm(shell, estop: true, arg));
        shell.Dialogs.Register("stopall", arg => new StopDialogVm(shell, estop: false, arg));
        shell.Dialogs.Register("runall", arg => RunAllDialogVm.Create(shell, arg));
    }
}

/// <summary>
/// 「连接设备」（临时，M2）：新样式的表单（两列：地址 · 端口；访问令牌（可显示）；客户端名；记住令牌），底栏左侧是状态（未连接 / 连接中… / 连接失败：…），
/// 右侧「取消」+「连接」（主要按钮）。连上后关闭、提示「已连接 …」；失败时留在对话框里显示原因（不留下连不上的设备 —— 重连监督在 M3）。
/// </summary>
public sealed partial class ConnectDialogVm : DialogVm
{
    private readonly ShellVm _shell;

    public ConnectDialogVm(ShellVm shell)
    {
        _shell = shell;
        Name = "connect";
        Title = "连接设备";
        Sub = "REST + WebSocket · Bearer 令牌";
        Icon = "link";
        Width = 600;
        var cfg = shell.Fleet.Config;
        _host = cfg?.Host ?? "127.0.0.1";
        _port = (cfg?.Port ?? 8080).ToString(CultureInfo.InvariantCulture);
        _token = cfg?.Token ?? "";
        _clientName = cfg?.ClientName ?? shell.Fleet.ClientName;
        _rememberToken = cfg?.RememberToken ?? false;
        _status = shell.Fleet.LiveDevice is { IsOffline: false } d ? d.ConnText + " · " + d.Host + ":" + d.Port : "未连接";
        ConnectCommand = new AsyncRelayCommand(ConnectAsync);
        CancelCommand = new RelayCommand(() => _shell.Dialogs.Close());
        EyeCommand = new RelayCommand(() => ShowToken = !ShowToken);
        Body = this;
        Footer = new ConnectFooterVm(this);
        FooterLeft = new ConnectStatusVm(this);
    }

    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _token;
    [ObservableProperty] private string _clientName;
    [ObservableProperty] private bool _rememberToken;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(EyeIcon), nameof(EyeTip), nameof(TokenChar))] private bool _showToken;
    [ObservableProperty] private string _status;
    [ObservableProperty] private bool _hasError;
    [ObservableProperty] private bool _busy;

    public string EyeIcon => ShowToken ? "eyeoff" : "eye";
    public string EyeTip => ShowToken ? "隐藏令牌" : "显示令牌";
    public char TokenChar => ShowToken ? '\0' : '●';

    public ICommand ConnectCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand EyeCommand { get; }

    /// <summary>连接（同旧 MainViewModel.ConnectAsync 的校验与文字）。</summary>
    public async Task ConnectAsync()
    {
        if (!int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0) { Status = "端口无效"; HasError = true; return; }
        if (string.IsNullOrWhiteSpace(Token)) { Status = "请输入访问令牌"; HasError = true; return; }
        if (string.IsNullOrWhiteSpace(Host)) { Status = "请填写地址"; HasError = true; return; }
        Busy = true; HasError = false; Status = "连接中…";
        try
        {
            var d = await _shell.Fleet.ConnectAsync(Host, port, Token, ClientName, RememberToken).ConfigureAwait(true);
            Status = "已连接 · " + d.ModelLabel + " " + d.Sn;
            _shell.Dialogs.Close();
            _shell.Select(d.K);
            _shell.Toast("已连接 http://" + d.Host + ":" + d.Port, ToastKind.Ok);
        }
        catch (Exception ex)
        {
            Status = "连接失败：" + DeviceActions.Describe(ex);
            HasError = true;
        }
        finally { Busy = false; }
    }
}

/// <summary>「连接设备」的底栏按钮（DataTemplate 在 DialogHost）。</summary>
public sealed record ConnectFooterVm(ConnectDialogVm Dialog);

/// <summary>「连接设备」的底栏左侧状态（DataTemplate 在 DialogHost）。</summary>
public sealed record ConnectStatusVm(ConnectDialogVm Dialog);

/// <summary>急停 / 全部停止对话框里的一台设备（只读：M2 不能勾选，M3 换成多选）。</summary>
public sealed record StopTargetVm(string Alias, string Sub, string RunText);

/// <summary>
/// 急停 / 全部停止（临时，M2，实时模式）：对象 = 全部已连接的设备（estop:k 只这一台；已在急停的不再发），文字照抄样稿的急停 / 全部停止对话框；
/// 确认 = 对每台执行（急停 / 全部停止都是停止类：不需要控制权，执行前照样再查一次守卫）。急停的确认是整个界面唯一的红色实心按钮之外的另一个（对话框里唯一）。
/// </summary>
public sealed partial class StopDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly bool _estop;
    private readonly List<DeviceVm> _targets;

    public StopDialogVm(ShellVm shell, bool estop, string? arg)
    {
        _shell = shell;
        _estop = estop;
        Name = estop ? "estop" : "stopall";
        int? only = int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) ? k : null;
        // 能发停止类的设备：在线，或者只是 WebSocket 在重连（CanStop；急停 / 全部停止尽力发送，送不到的单独提示）
        _targets = shell.Fleet.Devices.Where(d => d.CanStop && (only == null || d.K == only) && !(estop && d.EstopActive)).ToList();
        Title = estop ? "急停" : "全部停止";
        Sub = estop ? "立即切断所选设备的加热、进气与搅拌，运行中的程序全部中止" : "停止所选设备上全部反应釜：中止程序、停止加热、关闭进气";
        Icon = estop ? "estop" : "stop";
        IsDanger = estop;
        Width = 640;
        Targets = _targets.Select(d => new StopTargetVm(d.Alias, d.ModelLabel + " · " + d.Host,
            estop ? "运行 " + d.Running + "/" + d.Installed : d.Running > 0 ? "运行 " + d.Running + "/" + d.Installed : "无运行中 · 仍会发送")).ToList();
        Note = estop
            ? "急停不需要控制权，连接正常就会发送。急停复位只能在设备触屏上操作，本机不能复位。"
            : "全部停止不需要控制权，连接正常就会发送（本机看到的状态可能滞后，所以不会因为「看起来没在运行」而跳过）。搅拌保持当前状态；需要彻底切断请用急停。";
        int nRun = _targets.Sum(d => d.Running);
        CountText = "已选 " + _targets.Count + " 台" + (estop ? "" : " · 共 " + nRun + " 个运行中的反应釜");
        GoText = (estop ? "急停所选 " : "停止所选 ") + _targets.Count + " 台";
        GoReason = _targets.Count == 0 ? "至少选择一台已连接的设备" : null;
        GoCommand = new AsyncRelayCommand(GoAsync);
        CancelCommand = new RelayCommand(() => _shell.Dialogs.Close());
        Body = this;
        Footer = new StopFooterVm(this);
        FooterLeft = CountText;
    }

    public bool IsEstop => _estop;
    public IReadOnlyList<StopTargetVm> Targets { get; }
    public bool HasTargets => Targets.Count > 0;
    public string Note { get; }
    public string CountText { get; }
    public string GoText { get; }
    public string? GoReason { get; }
    public ICommand GoCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task GoAsync()
    {
        if (_targets.Count == 0) return;
        var action = _estop ? DeviceAction.Estop : DeviceAction.StopAll;
        var results = await Task.WhenAll(_targets.Select(d => d.Actions.RunAsync(action, quiet: true))).ConfigureAwait(true);
        var ok = _targets.Where((d, i) => results[i].Ok).ToList();
        _shell.Dialogs.Close();
        var names = string.Join("、", ok.Select(d => d.Alias));
        if (_estop) _shell.Toast("急停已发送到 " + ok.Count + " 台：" + names + " · 复位需在触屏操作", ToastKind.Err);
        else _shell.Toast("全部停止已发送到 " + ok.Count + " 台：" + names, ToastKind.Warn);
        foreach (var f in results.Where(r => !r.Ok)) _shell.Toast(f.Message, ToastKind.Err);
    }
}

/// <summary>急停 / 全部停止的底栏按钮（DataTemplate 在 DialogHost）。</summary>
public sealed record StopFooterVm(StopDialogVm Dialog);

/// <summary>
/// 「全部运行」的确认（临时，M2，实时模式；D6「先列清单确认」）：清单 = 这台设备上「全部运行」会运行的反应釜（本机这次下发过程序、已启用、没在运行 ——
/// 与守卫同一个判断 ActionGuard.IsRunAllTarget），每行写程序名和程序状态。确认 = 对清单里的每个反应釜运行程序（program/run，逐个过守卫），
/// 运行的正是确认时看到的这些（打开之后才下发的不算）。绝不调 HMI 的 POST /run（触屏的「开始运行」，含义不同）。
/// 打开时守卫不让运行（没有可运行的反应釜、没有控制权…）：不打开，提示原因。
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
        Arg = d.K.ToString(CultureInfo.InvariantCulture);
        Title = "全部运行";
        Sub = "运行" + d.Alias + "上已下发程序的反应釜";
        Icon = "play";
        Width = 560;
        var facts = d.Facts;
        Targets = targets.Select(n =>
        {
            var name = live.Downloaded(n)?.Name ?? "";
            var st = facts.Channel(n)?.Program;
            return new StopTargetVm(d.Alias + " · RV" + n, "程序「" + name + "」", st is { } ps ? Core.Programs.Progress.StatusText(ps) : "");
        }).ToList();
        CountText = "已选 " + targets.Count + " 个反应釜";
        GoReason = targets.Count == 0 ? "没有可运行的反应釜" : null;
        GoCommand = new AsyncRelayCommand(GoAsync);
        CancelCommand = new RelayCommand(() => _shell.Dialogs.Close());
        Body = this;
        Footer = new RunAllFooterVm(this);
        FooterLeft = CountText;
    }

    /// <summary>arg = 设备位置 k。设备不在、不是真机、或守卫不让「全部运行」时不打开（守卫的原因 toast 出来）。</summary>
    public static RunAllDialogVm? Create(ShellVm shell, string? arg)
    {
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) || shell.Fleet.Device(k) is not { } d) return null;
        if (d.Source is not LiveDeviceSource live) return null;
        var why = d.Actions.Why(DeviceAction.RunAll);
        if (why.Length > 0) { shell.Toast(why, ToastKind.Warn); return null; }
        return new RunAllDialogVm(shell, d, live.RunAllTargets(), live);
    }

    public IReadOnlyList<StopTargetVm> Targets { get; }
    public bool HasTargets => Targets.Count > 0;
    public string CountText { get; }
    public string GoText => "全部运行";
    public string? GoReason { get; }
    public ICommand GoCommand { get; }
    public ICommand CancelCommand { get; }

    private async Task GoAsync()
    {
        if (_targets.Count == 0) return;
        _shell.Dialogs.Close();
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

/// <summary>「全部运行」的底栏按钮（DataTemplate 在 DialogHost）。</summary>
public sealed record RunAllFooterVm(RunAllDialogVm Dialog);
