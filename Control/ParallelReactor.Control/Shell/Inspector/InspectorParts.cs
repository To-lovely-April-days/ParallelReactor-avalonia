using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;

namespace ParallelReactor.Control.Shell;

/// <summary>
/// 检查器一次刷新的上下文：外框、设备、守卫的输入（一次刷新取一次，再问各个按钮，同样稿每次 render 时的 PR.sim.why）、
/// 页面上已经有「确认」的报警（样稿 pageOwned()：键 = 「k:报警 id」，值 = 位置，如「顶部报警条」）、当前页签的对象（样稿 pageObj()）、
/// 检查器是否 300 px（样稿 .insp-n）。
/// </summary>
public sealed record InspCtx(ShellVm Shell, DeviceVm D, GuardFacts F, IReadOnlyDictionary<string, string> Owned, Route? PageObj, bool Narrow, DateTime Now)
{
    /// <summary>这个操作现在为什么不能做；能做返回 null（绑定 b:Pr.Reason）。</summary>
    public string? Why(DeviceAction action, ActionPayload? payload = null) => Nz(ActionGuard.Why(F, action, payload));

    /// <summary>只看连接 / 急停 / 控制权的原因（样稿 PR.sim.guard）；能做返回 null。</summary>
    public string? Guard(GuardKind kind) => Nz(ActionGuard.GuardText(F, kind));

    public bool Online => D.ConnStatus == ConnStatus.Online;

    /// <summary>页面上已经有这条报警的「确认」时返回位置（「顶部报警条」…），否则 null。</summary>
    public string? OwnedWhere(AlarmVm a) => Owned.TryGetValue(D.K + ":" + a.Id, out var w) ? w : null;

    internal static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// 检查器里的一个操作按钮（样稿 actBtn / ui.btn）：文字、图标、禁用原因（b:Pr.Reason，锁图标提示、点击 toast 原因）、普通提示。
/// 命令执行时由 Fleet/DeviceActions 再查一次守卫（界面上的原因可能已经过期）。
/// </summary>
public sealed partial class InspAct : ObservableObject
{
    public InspAct(string label, string? icon, Action run)
    {
        _label = label;
        _icon = icon;
        Command = new RelayCommand(run);
    }

    public ICommand Command { get; }

    [ObservableProperty] private string _label;
    [ObservableProperty] private string? _icon;
    /// <summary>不能做的原因（null = 能做）。</summary>
    [ObservableProperty] private string? _reason;
    /// <summary>可以做时的悬停提示（有原因时提示是原因）。</summary>
    [ObservableProperty] private string? _tip;
}

/// <summary>
/// 控制权卡（样稿 shell.js leaseBox）：「控制权 [徽标] [按钮]」一行 + 一到两行说明 +（本地只读 / 申请中）整行宽的操作。
/// 控制类按钮为什么不可用只在这里说一次。断线时卡片保持中性，只有徽标是红色（brief §4.3 不重复编码）。
/// </summary>
public sealed partial class LeaseBoxVm : ObservableObject
{
    private readonly ShellVm _shell;
    private DeviceVm? _d;
    private DeviceAction? _btnAction, _ctaAction;

    public LeaseBoxVm(ShellVm shell)
    {
        _shell = shell;
        BtnCommand = new RelayCommand(() => Run(_btnAction));
        CtaCommand = new RelayCommand(() =>
        {
            // 「申请控制权」打开控制权对话框（倒计时环，M3）；没注册之前直接申请（同菜单「申请控制权…」）
            if (_ctaAction == DeviceAction.LeaseRequest && _d != null && _shell.Dialogs.IsRegistered("lease")) _shell.OpenDialog("lease", _d.K.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else Run(_ctaAction);
        });
    }

    public ICommand BtnCommand { get; }
    public ICommand CtaCommand { get; }

    /// <summary>写「控制权」三个字（检查器 300 px 时不写，样稿 .insp-n .pr-insp-lease-k）。</summary>
    [ObservableProperty] private bool _showKey = true;
    /// <summary>卡片边框：申请中 a-line；正在连接（从未连上）line；其余 line-2。</summary>
    [ObservableProperty] private bool _isRequested;
    [ObservableProperty] private bool _isConnecting;
    /// <summary>在线：控制权徽标（完整文字；心跳中断时写「心跳中断」）；断线：连接徽标（短文字）。</summary>
    [ObservableProperty] private bool _showLease;
    [ObservableProperty] private LeaseState _leaseState;
    [ObservableProperty] private string _leaseText = "";
    [ObservableProperty] private string? _leaseTip;
    [ObservableProperty] private ConnState _connState;
    [ObservableProperty] private string _connText = "";
    [ObservableProperty] private string _connTip = "";
    /// <summary>行内的小按钮（释放 / 立即重连 / 连接）；null = 没有。</summary>
    [ObservableProperty] private string? _btnLabel;
    [ObservableProperty] private string _btnIcon = "unlock";
    [ObservableProperty] private string? _btnReason;
    /// <summary>说明下面整行宽的操作（取消申请 / 申请控制权）；null = 没有。</summary>
    [ObservableProperty] private string? _ctaLabel;
    [ObservableProperty] private string _ctaIcon = "key";
    /// <summary>整行操作是主要按钮（本地只读的「申请控制权」：这一块唯一的主要操作）。</summary>
    [ObservableProperty] private bool _ctaPrimary;
    [ObservableProperty] private string? _ctaReason;
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private string _noteTip = "";

    public bool HasBtn => BtnLabel != null;
    public bool HasCta => CtaLabel != null;
    public bool HasCtaPrimary => CtaLabel != null && CtaPrimary;
    public bool HasCtaPlain => CtaLabel != null && !CtaPrimary;
    partial void OnBtnLabelChanged(string? value) => OnPropertyChanged(nameof(HasBtn));
    partial void OnCtaLabelChanged(string? value) { OnPropertyChanged(nameof(HasCta)); OnPropertyChanged(nameof(HasCtaPrimary)); OnPropertyChanged(nameof(HasCtaPlain)); }
    partial void OnCtaPrimaryChanged(bool value) { OnPropertyChanged(nameof(HasCtaPrimary)); OnPropertyChanged(nameof(HasCtaPlain)); }

    private void Run(DeviceAction? a)
    {
        if (_d is { } d && a is { } action) _ = d.Actions.RunAsync(action);
    }

    internal void Refresh(InspCtx c)
    {
        var d = c.D;
        _d = d;
        var src = d.Source;
        var conn = src.Conn;
        var cd = conn.Display;
        var lease = src.Lease;
        bool online = conn.Online;
        string? btn = null, cta = null;
        DeviceAction? btnA = null, ctaA = null;
        string btnIcon = "unlock", ctaIcon = "key";
        string? btnReason = null, ctaReason = null;
        bool primary = false, connecting = false;
        string note;
        if (!online && cd.First)
        {
            connecting = true;
            // 真机从未连上、但 REST 已经通了（例如 WS 503 TOO_MANY_CLIENTS，DeviceVm.CanStop）：停止类照样能发 —— 不能写「连上后才能操作」
            // （M3 复核；后半句取自样稿本地只读时的说明「停止类操作随时可用」）
            note = "正在连接 " + d.Host + ":" + d.Port + "\n尚未收到数据 · " + (d.CanStop ? "停止类操作随时可用" : "连上后才能操作");
        }
        else if (!online)
        {
            // 打开的页签就是这台设备（设备页 / 它的反应釜页）、页面上有断线说明条时，「立即重连」在页面上 —— 一个动作只放一个地方
            // （PageVm.ShowsReconnect：设备页 M4 起有说明条；反应釜页的说明条在 M5，之前按钮留在这里）
            if (!(c.PageObj is { } po && po.K == d.K && c.Shell.Page is { ShowsReconnect: true }))
            {
                btn = conn.State == ConnStatus.Disconnected ? "连接" : "立即重连";
                btnIcon = "refresh";
                btnA = DeviceAction.Reconnect;
                btnReason = c.Why(DeviceAction.Reconnect);
            }
            var retry = conn.State == ConnStatus.Disconnected ? "已断开" : conn.State == ConnStatus.Connecting ? "正在重连…" : "重连中 " + conn.RetryS + " s";
            // 真机只是 WebSocket 在重连（REST 还在，DeviceVm.CanStop）：停止类照样能发 —— 说明不能写「不能操作」（措辞取自样稿申请中的说明）
            note = retry + " · " + d.DataText + "\n" + (lease.State == LeaseStatus.Lost ? "控制权宽限 " + lease.GraceS + " s · 超时交回触屏"
                : d.CanStop ? "重连前只能查看；停止类操作随时可用" : "断线时只能查看，不能操作");
        }
        else if (lease.State == LeaseStatus.Remote)
        {
            btn = "释放"; btnA = DeviceAction.LeaseRelease;
            // 真机连续 ≥ 2 次心跳没送到（HMI 还没判 remoteLost）：不写「心跳正常」—— 写旧界面的「心跳异常」（样稿没有这一种）
            // 持有者按 HMI 认的名字（Lease.Holder：真机这条连接用的客户端名）写
            note = (lease.Holder.Length > 0 ? lease.Holder : c.Shell.Fleet.ClientName) + (lease.HeartbeatDegraded ? " · 心跳异常" : " · 心跳正常");
        }
        else if (lease.State == LeaseStatus.Requested)
        {
            cta = "取消申请"; ctaIcon = "x"; ctaA = DeviceAction.LeaseCancel;
            note = "等待" + d.Alias + "触屏确认 · 超时自动取消\n确认前只能查看；停止类操作随时可用";
        }
        else if (lease.State == LeaseStatus.Lost)
        {
            btn = "释放"; btnA = DeviceAction.LeaseRelease;
            note = "宽限 " + lease.GraceS + " s · 心跳恢复后继续\n超时交回触屏";
        }
        else
        {
            // 本地只读（以及真机的「被其他客户端持有」）：「申请控制权」是这一块唯一的主要操作
            cta = "申请控制权"; ctaIcon = "key"; ctaA = DeviceAction.LeaseRequest; primary = true;
            ctaReason = c.Why(DeviceAction.LeaseRequest);
            note = "本机只读 · 启动、设定等需要控制权\n停止类操作随时可用";
        }
        _btnAction = btnA;
        _ctaAction = ctaA;
        ShowKey = !c.Narrow;
        IsRequested = online && lease.State == LeaseStatus.Requested;
        IsConnecting = connecting;
        ShowLease = online;
        LeaseState = d.Lease;
        LeaseText = lease.State == LeaseStatus.Lost ? "心跳中断" : d.LeaseText;
        LeaseTip = lease.State == LeaseStatus.Lost ? d.LeaseText : null;
        ConnState = d.Conn;
        ConnText = d.ConnShort;
        ConnTip = d.ConnText;
        BtnIcon = btnIcon;
        BtnReason = btnReason;
        BtnLabel = btn;
        CtaIcon = ctaIcon;
        CtaReason = ctaReason;
        CtaPrimary = primary;
        CtaLabel = cta;
        Note = note;
        NoteTip = note;
    }
}

/// <summary>
/// 一张报警 / 提醒卡片（样稿 alarmCard）：图标 + 文字占满一行，下面一行「时间 · 报警 / 提醒（· 已确认）… 确认」。
/// 同一条报警屏幕上只有一个「确认」：页面（顶部报警条 / 「报警与事件」）有它时，卡片中性、不放按钮，最后一行写「确认在 …」。
/// </summary>
public sealed partial class InspAlarmVm : ObservableObject
{
    public InspAlarmVm(AlarmVm a) => A = a;

    public AlarmVm A { get; }
    public string Id => A.Id;

    /// <summary>页面上的「确认」在哪里（null = 检查器自己放确认按钮）。</summary>
    [ObservableProperty] private string? _where;
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _levelText = "";
    [ObservableProperty] private string _icon = "warn";
    [ObservableProperty] private bool _isErr;
    /// <summary>错误报警且没有被页面拿走「确认」：浅红底 + r-line 边。</summary>
    [ObservableProperty] private bool _isErrCard;
    [ObservableProperty] private bool _showAck;
    [ObservableProperty] private string? _whereText;

    /// <summary>按当前情况刷新（页面拿走了「确认」时写「确认在 …」）。</summary>
    public void Refresh(InspCtx c)
    {
        var a = A;
        var where = a.Acked ? null : c.OwnedWhere(a);
        Where = where;
        Text = a.Text;
        LevelText = (a.IsErr ? "报警" : a.Level == AlarmLevel.Warn ? "提醒" : "提醒") + (a.Acked ? " · 已确认" : "");
        Icon = a.Level == AlarmLevel.Err ? "alert" : a.Level == AlarmLevel.Warn ? "warn" : "info";
        IsErr = a.IsErr;
        IsErrCard = a.IsErr && where == null;
        ShowAck = !a.Acked && where == null;
        WhereText = where == null ? null : "确认在" + where;
    }
}

/// <summary>最近事件的一行（样稿 .pr-insp-events li）：时间 13 px · 错误红 ⚠ · 文字两行（报警 / 提醒 t-1 500）。</summary>
public sealed class InspEventVm
{
    public InspEventVm(EventVm e)
    {
        E = e;
        Text = e.Text;
    }

    public EventVm E { get; }
    public string Id => E.Id;
    public string Text { get; }
    public string Tip => E.Text;
    public string AtText => E.AtText;
    public bool IsErr => E.Level == EventLevel.Err;
    /// <summary>报警 / 提醒：字 t-1 500。</summary>
    public bool Emph => E.Level is EventLevel.Err or EventLevel.Warn;
}

/// <summary>按 Id 就地同步一个视图模型列表（不重建集合：已有的移动 / 更新，没有的插入，多余的去掉）。</summary>
internal static class ListSync
{
    public static void Sync<TSrc, TVm>(ObservableCollection<TVm> list, IReadOnlyList<TSrc> want, Func<TSrc, string> key, Func<TVm, string> vmKey,
        Func<TSrc, TVm> make, Action<TVm>? update = null)
    {
        var keys = new HashSet<string>(want.Select(key), StringComparer.Ordinal);
        for (int i = list.Count - 1; i >= 0; i--) if (!keys.Contains(vmKey(list[i]))) list.RemoveAt(i);
        for (int i = 0; i < want.Count; i++)
        {
            var k = key(want[i]);
            int at = -1;
            for (int j = i; j < list.Count; j++) if (vmKey(list[j]) == k) { at = j; break; }
            TVm vm;
            if (at < 0) { vm = make(want[i]); list.Insert(i, vm); }
            else { vm = list[at]; if (at != i) list.Move(at, i); }
            update?.Invoke(vm);
        }
    }
}

/// <summary>步骤条的段（Controls.Pr.StepSeg）按 Core 的 <see cref="Core.Programs.StepBarSegment"/> 就地更新；段数变了换一个新列表（StepBar.SegmentsSource 重新同步）。</summary>
internal static class StepSegs
{
    public static IReadOnlyList<StepSeg> Update(IReadOnlyList<StepSeg>? have, IReadOnlyList<Core.Programs.StepBarSegment> bar)
    {
        var list = have != null && have.Count == bar.Count ? have : Enumerable.Range(0, bar.Count).Select(_ => new StepSeg()).ToArray();
        for (int i = 0; i < bar.Count; i++)
        {
            var b = bar[i];
            var s = list[i];
            s.Label = b.Label;
            s.State = b.State switch
            {
                Core.Programs.StepBarState.Done => StepSegState.Done,
                Core.Programs.StepBarState.Cur => StepSegState.Current,
                Core.Programs.StepBarState.Held => StepSegState.Held,
                Core.Programs.StepBarState.Abort => StepSegState.Aborted,
                _ => StepSegState.Pending,
            };
            s.Fraction = b.FillPct / 100.0;
        }
        return list;
    }
}
