using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// 急停 / 全部停止对话框里的一台设备（样稿 dialogs.js devPick / .dlg-pick）：复选框 + 别名（15/600）+「型号 · 地址」（14 t-3）+ 右侧运行情况（14 t-2）+ 徽标
/// （在线：控制权短徽标；断线：连接徽标）。不能选时（断线「连接中断 · 无法发送」、急停对话框里已在急停）灰底、复选框凹陷、带原因（锁图标提示 + 点击 toast）。
/// </summary>
public sealed partial class PickRowVm : ObservableObject
{
    public PickRowVm(int k, Action<int> toggle)
    {
        K = k;
        ToggleCommand = new RelayCommand(() => toggle(K));
    }

    public int K { get; }
    public ICommand ToggleCommand { get; }
    public DeviceBadgeVm Badge { get; } = new();

    [ObservableProperty] private string _alias = "";
    [ObservableProperty] private string _sub = "";
    [ObservableProperty] private string _runText = "";
    /// <summary>勾上了（样稿 is-on：checked && !reason）。</summary>
    [ObservableProperty] private bool _isOn;
    /// <summary>不能选的原因（样稿 is-dis + data-reason）；null = 能选。</summary>
    [ObservableProperty] private string? _reason;
}

/// <summary>
/// 多选设备的对话框（样稿 pickInit / picked / pickAction / pickBar）：急停与全部停止共用。
/// <list type="bullet">
/// <item>打开时预选：已连接（能发停止类，DeviceVm.CanStop）且这个对话框不排除的设备；参数 k（estop:k）时只预选那一台。之后加进来的设备默认不勾。</item>
/// <item>「全选」= 全部能选的；「全不选」= 全不勾。勾选状态按设备位置 K 记，设备断线 / 重连时照样保留（断线时不算进「已选」）。</item>
/// <item>每秒刷新（样稿 live: true）：行的文字、能不能选、说明、计数、按钮文字跟着数据变；设备增减时行就地增删。</item>
/// </list>
/// </summary>
public abstract partial class PickDialogVm : DialogVm
{
    protected readonly ShellVm Shell;
    protected readonly Dictionary<int, bool> Sel = new();
    private bool _closed;

    protected PickDialogVm(ShellVm shell, string name, string? arg)
    {
        Shell = shell;
        Name = name;
        Arg = arg;
        Width = 640;
        AllCommand = new RelayCommand(() => { foreach (var d in shell.Fleet.Devices) Sel[d.K] = d.CanStop && Excluded(d).Length == 0; Refresh(); });
        NoneCommand = new RelayCommand(() => { foreach (var d in shell.Fleet.Devices) Sel[d.K] = false; Refresh(); });
        CancelButton = new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this)));
        Body = this;
    }

    /// <summary>预选（样稿 pickInit）：<paramref name="only"/> = 只预选这一台。子类构造完再调。</summary>
    protected void InitSelection(int? only)
    {
        foreach (var d in Shell.Fleet.Devices)
            Sel[d.K] = (only == null || d.K == only) && d.CanStop && Excluded(d).Length == 0;
        Refresh();
    }

    public ObservableCollection<PickRowVm> Rows { get; } = new();
    public ICommand AllCommand { get; }
    public ICommand NoneCommand { get; }
    public FootButton CancelButton { get; }

    /// <summary>勾选条左边的说明（样稿 pickBar 的 label）。</summary>
    [ObservableProperty] private string _caption = "";
    /// <summary>说明（样稿 ui.note，中性；粗体段见 TextSeg）。</summary>
    [ObservableProperty] private IReadOnlyList<TextSeg> _note = Array.Empty<TextSeg>();

    /// <summary>这个对话框不让选这台的原因（急停：已在急停）；'' = 不排除。</summary>
    protected abstract string Excluded(DeviceVm d);

    /// <summary>勾选行右侧的运行情况（全部停止：没在运行时「无运行中 · 仍会发送」）。</summary>
    protected virtual string RunTextOf(DeviceVm d) => DialogText.RunText(d);

    /// <summary>样稿 picked：勾了、已连接、不被排除的设备（按列表顺序）。</summary>
    public IReadOnlyList<DeviceVm> Picked => Shell.Fleet.Devices.Where(d => Sel.TryGetValue(d.K, out var on) && on && d.CanStop && Excluded(d).Length == 0).ToList();

    private void Toggle(int k)
    {
        var d = Shell.Fleet.Device(k);
        if (d == null || RowReason(d) != null) return;
        Sel[k] = !(Sel.TryGetValue(k, out var on) && on);
        Refresh();
    }

    /// <summary>这一行为什么不能选（样稿 devPick 的 reason）：断线「连接中断 · 无法发送」，或子类的排除原因。</summary>
    private string? RowReason(DeviceVm d) => !d.CanStop ? d.ConnShort + " · 无法发送" : FootButton.Nz(Excluded(d));

    public override void Refresh()
    {
        if (_closed) return;
        var ds = Shell.Fleet.Devices;
        // 行：按设备列表就地同步
        for (int i = Rows.Count - 1; i >= 0; i--) if (ds.All(d => d.K != Rows[i].K)) Rows.RemoveAt(i);
        for (int i = 0; i < ds.Count; i++)
        {
            var d = ds[i];
            var row = Rows.FirstOrDefault(r => r.K == d.K);
            if (row == null) { row = new PickRowVm(d.K, Toggle); Rows.Insert(Math.Min(i, Rows.Count), row); }
            else if (Rows.IndexOf(row) != i) Rows.Move(Rows.IndexOf(row), Math.Min(i, Rows.Count - 1));
            var reason = RowReason(d);
            row.Alias = d.Alias;
            row.Sub = d.ModelLabel + " · " + d.Host;
            row.Reason = reason;
            row.IsOn = Sel.TryGetValue(d.K, out var on) && on && reason == null;
            row.RunText = reason ?? RunTextOf(d);
            row.Badge.Apply(d, shortLease: true);
        }
        RefreshTexts();
    }

    /// <summary>说明、计数、按钮文字（子类）。</summary>
    protected abstract void RefreshTexts();

    public override void OnClosed() => _closed = true;

    /// <summary>发出之后最多等这么久再汇总提示（<see cref="SendAsync"/>）。</summary>
    public static readonly TimeSpan DefaultSummaryWait = TimeSpan.FromSeconds(1.5);

    /// <summary>这个对话框发出之后最多等多久再汇总提示（测试可以改短 / 改长）。</summary>
    public TimeSpan SummaryWait { get; set; } = DefaultSummaryWait;

    /// <summary>
    /// 发送（样稿 actMany → closeDialog → toast，M3 复核）：并行发给所选设备（每台照样再查一次守卫），<b>发出后立即关掉对话框</b> —— 慢的 / 不通的设备不能把对话框
    /// 拖住（停止类命令遇到网络错误会同键重试，最长约 1 min），急停按钮也不能在这期间一直灰着。然后最多等 <see cref="SummaryWait"/>：
    /// 已经成功的汇总成一条（样稿的写法 <paramref name="okText"/>），已经失败的各一条（err）；到时还没结果的设备，结果出来时各自再提示一条
    /// （成功同样稿的写法、只列这一台；失败写原因）。
    /// </summary>
    protected async Task SendAsync(IReadOnlyList<DeviceVm> list, DeviceAction action, Func<IReadOnlyList<string>, string> okText, ToastKind okKind)
    {
        var tasks = list.Select(d => d.Actions.RunAsync(action, quiet: true)).ToArray();
        Shell.Dialogs.Close(this);
        var all = Task.WhenAll(tasks);
        if (!all.IsCompleted && SummaryWait > TimeSpan.Zero)
        {
            using var cts = new System.Threading.CancellationTokenSource();
            await Task.WhenAny(all, Task.Delay(SummaryWait, cts.Token)).ConfigureAwait(true);
            cts.Cancel();
        }
        var ok = new List<string>();
        var failed = new List<string>();
        for (int i = 0; i < tasks.Length; i++)
        {
            if (!tasks[i].IsCompleted) { _ = ReportLateAsync(list[i].Alias, tasks[i], okText, okKind); continue; }
            var o = Outcome(list[i].Alias, tasks[i]);
            if (o.Ok) ok.Add(list[i].Alias); else failed.Add(o.Message);
        }
        if (ok.Count > 0) Shell.Toast(okText(ok), okKind);
        foreach (var f in failed) Shell.Toast(f, ToastKind.Err);
    }

    /// <summary>汇总之后才有结果的一台：成功 = 样稿的写法只列这一台，失败 = 原因（err）。</summary>
    private async Task ReportLateAsync(string alias, Task<ActionOutcome> t, Func<IReadOnlyList<string>, string> okText, ToastKind okKind)
    {
        try { await t.ConfigureAwait(true); } catch { }
        var o = Outcome(alias, t);
        Shell.Toast(o.Ok ? okText(new[] { alias }) : o.Message, o.Ok ? okKind : ToastKind.Err);
    }

    /// <summary>已完成的一次发送的结果（DeviceActions.RunAsync 自己不抛：异常已经翻译成失败；万一抛了也翻译）。</summary>
    private static ActionOutcome Outcome(string alias, Task<ActionOutcome> t) =>
        t.IsCompletedSuccessfully ? t.Result
        : ActionOutcome.Failed(alias + " · " + (t.Exception?.GetBaseException() is { } ex ? Fleet.DeviceActions.Describe(ex) : "已取消"), ToastKind.Err);
}

/// <summary>
/// 急停（样稿 dialogs.js 'estop'，final-dlg-estop）：多选设备，已连接的默认全选；estop:k（总览 «⋯» / 右键「急停 1号机…」）只预选这一台。
/// 说明跟着真正的勾选状态走（样稿第 3 轮）：「已预选 1号机 · 可再勾选其它设备」/「1号机 未勾选 · 勾选后才会急停」/
/// 「1号机 连接中断 · 无法预选 · 可勾选其它设备」/「1号机 已在急停 · 可勾选其它设备」。确认按钮是对话框里唯一的红色实心按钮（全局「急停」之外）；
/// 已在急停的设备不能选（「已在急停 · 需在触屏复位」）。急停不需要控制权：每台照样再查一次守卫（DeviceActions，停止类只要连接），并行发送。
/// </summary>
public sealed partial class EstopDialogVm : PickDialogVm
{
    private readonly int? _one;

    public EstopDialogVm(ShellVm shell, string? arg) : base(shell, "estop", arg)
    {
        _one = DialogText.ParseK(arg) is int k && shell.Fleet.Device(k) != null ? k : null;
        Title = "急停";
        Sub = "立即切断所选设备的加热、进气与搅拌，运行中的程序全部中止";
        Icon = "estop";
        IsDanger = true;
        GoButton = new FootButton("急停所选 0 台", new AsyncRelayCommand(GoAsync), FootKind.Danger, "estop", large: true);
        Footer = new FootBar(CancelButton, GoButton);
        // 样稿：急停<b>不需要控制权</b>，连接正常就会发送。<span nw>急停<b>复位只能在设备触屏上操作</b>，</span><span nw>本机不能复位。</span>
        Note = new[]
        {
            new TextSeg("急停"), new TextSeg("不需要控制权", Bold: true), new TextSeg("，连接正常就会发送。"),
            new TextSeg("急停", NoWrap: 1), new TextSeg("复位只能在设备触屏上操作", Bold: true, NoWrap: 1), new TextSeg("，", NoWrap: 1),
            new TextSeg("本机不能复位。", NoWrap: 2),
        };
        InitSelection(_one);
    }

    public FootButton GoButton { get; }

    protected override string Excluded(DeviceVm d) => d.EstopActive ? "已在急停 · 需在触屏复位" : "";

    protected override void RefreshTexts()
    {
        var one = _one is int k ? Shell.Fleet.Device(k) : null;
        if (one == null) Caption = "选择设备（已连接的默认全选）";
        else
        {
            string why = !one.CanStop ? one.ConnShort + " · 无法预选" : one.EstopActive ? "已在急停" : "";
            Caption = why.Length > 0 ? one.Alias + " " + why + " · 可勾选其它设备"
                : Sel.TryGetValue(one.K, out var on) && on ? "已预选 " + one.Alias + " · 可再勾选其它设备"
                : one.Alias + " 未勾选 · 勾选后才会急停";
        }
        int n = Picked.Count;
        FooterLeft = "已选 " + n + " 台";
        GoButton.Label = "急停所选 " + n + " 台";
        GoButton.Reason = n > 0 ? null : "至少选择一台已连接的设备";
    }

    /// <summary>样稿：actMany(estop) → 关闭 → 「急停已发送到 N 台：… · 复位需在触屏操作」（发出即关，见 <see cref="PickDialogVm.SendAsync"/>）。</summary>
    private Task GoAsync()
    {
        var list = Picked;
        if (list.Count == 0) return Task.CompletedTask;
        return SendAsync(list, DeviceAction.Estop, names => "急停已发送到 " + names.Count + " 台：" + string.Join("、", names) + " · 复位需在触屏操作", ToastKind.Err);
    }
}

/// <summary>
/// 全部停止（样稿 dialogs.js 'stopall'）：多选设备，已连接的默认全选（参数忽略，同样稿）。停止类「永远可用」：只有断线的设备不能选；
/// 本机看到没有运行中的反应釜时照样可以发送（「无运行中 · 仍会发送」）。确认 = 主要按钮 + 白色实心方块（不用红色）。
/// 停不停搅拌看 FleetVm.StopAllStopsStirring（= Core 的 ControlOptions.StopAllStopsStirring）：副标题、说明、发送后的提示都跟着它（Core 的 StopAllText；
/// 不停搅拌时逐字同样稿）。打开时取一次，确认时照这个值提示。
/// </summary>
public sealed partial class StopAllDialogVm : PickDialogVm
{
    private readonly bool _stopsStirring;

    public StopAllDialogVm(ShellVm shell, string? arg) : base(shell, "stopall", arg)
    {
        _stopsStirring = shell.Fleet.StopAllStopsStirring;
        Title = "全部停止";
        Sub = StopAllText.Sub(_stopsStirring);
        Icon = "stop";
        GoButton = new FootButton("停止所选 0 台", new AsyncRelayCommand(GoAsync), FootKind.PrimaryStop, "stop", large: true);
        Footer = new FootBar(CancelButton, GoButton);
        Caption = "选择设备（已连接的默认全选）";
        // 样稿：全部停止<b>不需要控制权</b>，连接正常就会发送（…）。搅拌也会一并停止；需要立即切断并锁定请用急停。（最后一句跟着设置，StopAllText.StirNote）
        Note = new[]
        {
            new TextSeg("全部停止"), new TextSeg("不需要控制权", Bold: true),
            new TextSeg("，连接正常就会发送（本机看到的状态可能滞后，所以不会因为「看起来没在运行」而跳过）。" + StopAllText.StirNote(_stopsStirring)),
        };
        InitSelection(null);
    }

    public FootButton GoButton { get; }

    /// <summary>这次打开时的设置（测试用）。</summary>
    public bool StopsStirring => _stopsStirring;

    protected override string Excluded(DeviceVm d) => "";

    protected override string RunTextOf(DeviceVm d) => d.Running > 0 ? DialogText.RunText(d) : "无运行中 · 仍会发送";

    protected override void RefreshTexts()
    {
        var list = Picked;
        int n = list.Count, nRun = list.Sum(d => d.Running);
        FooterLeft = "已选 " + n + " 台 · 共 " + nRun + " 个运行中的反应釜";
        GoButton.Label = "停止所选 " + n + " 台";
        GoButton.Reason = n > 0 ? null : "至少选择一台已连接的设备";
    }

    /// <summary>样稿：actMany(stopAll) → 关闭 → 「全部停止已发送到 N 台：…」（发出即关，见 <see cref="PickDialogVm.SendAsync"/>；搅拌后缀跟着打开时的设置）。</summary>
    private Task GoAsync()
    {
        var list = Picked;
        if (list.Count == 0) return Task.CompletedTask;
        return SendAsync(list, DeviceAction.StopAll, names => StopAllText.Sent(names.ToList(), _stopsStirring), ToastKind.Warn);
    }
}
