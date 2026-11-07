using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Commands;

namespace ParallelReactor.ViewModels;

/// <summary>
/// 远程控制的触屏侧（设计稿 §5.1 / §5.3 / §13.5）：顶栏「远程控制中（PC-x）」+「切回本地」、申请弹窗「PC-x 申请远程控制 允许 / 拒绝」（60 s 超时 = 拒绝）。
/// 切到远程时关闭本地键盘与各确认框（H2 / H4）。租约状态机在任意线程触发 Changed，这里一律切回 UI 线程再改绑定属性。
/// 远程控制中触屏写操作被拒的 Toast 由命令层 Admit → MODE_REMOTE（warn）→ Local/Show 给出，不在这里。
/// </summary>
public partial class MainViewModel
{
    /// <summary>申请弹窗是否打开（autoGrantRemote 时不弹）。</summary>
    [ObservableProperty] private bool _isControlRequestOpen;
    /// <summary>弹窗正文：「PC-x 申请远程控制」。</summary>
    [ObservableProperty] private string _controlRequestText = "";
    /// <summary>弹窗倒计时文字（每秒刷新）。</summary>
    [ObservableProperty] private string _controlRequestCountdown = "";
    /// <summary>顶栏徽章：当前处于远程控制（含宽限期）。</summary>
    [ObservableProperty] private bool _isRemoteControl;
    /// <summary>顶栏文字：「远程控制中（PC-x）」/「远程连接丢失（PC-x）· 设备继续运行」。</summary>
    [ObservableProperty] private string _remoteControlText = "";

    private string _pendingRequestId = "";

    private void InitControl()
    {
        ControlLease = new ControlLease(Arbiter, () => MonoNow);
        ControlLease.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(OnControlChanged);
    }

    /// <summary>UI 线程：按租约快照刷新顶栏 / 弹窗；进入远程时关闭本地键盘与确认框（§13.5 H2 / H4）。</summary>
    private void OnControlChanged()
    {
        var s = ControlLease.Snapshot(MonoNow);
        bool wasRemote = IsRemoteControl;
        IsRemoteControl = s.State is LeaseState.Remote or LeaseState.RemoteLost;
        RemoteControlText = s.State switch
        {
            LeaseState.Remote => $"远程控制中（{Who(s)}）",
            LeaseState.RemoteLost => $"远程连接丢失（{Who(s)}）· 设备继续运行",
            _ => "",
        };
        if (s.State == LeaseState.Requested)
        {
            _pendingRequestId = s.RequestId ?? "";
            ControlRequestText = $"{Who(s)} 申请远程控制";
            UpdateControlCountdown(s.RequestDeadline);
            IsControlRequestOpen = true;
        }
        else
        {
            _pendingRequestId = "";
            IsControlRequestOpen = false;
        }
        if (IsRemoteControl && !wasRemote)
        {
            // 切到远程：本地已打开的键盘 / 确认框一律关掉且不执行回调，避免确认时按旧模式写入
            Keyboard.IsOpen = false;
            IsPreRunOpen = false;
            IsExitConfirmOpen = false;
            Program.CancelPendingRun();
            Toast("warn", $"{s.ClientName} 已取得远程控制 · 触屏只能执行停止类操作");
        }
        else if (!IsRemoteControl && wasRemote)
            Toast("ok", $"已回到本地控制（{Arbiter.LastReturnReason}）");
    }

    /// <summary>弹窗 / 顶栏里的申请方：「客户端名 · 令牌 xxx」（客户端名已由状态机清洗；令牌名来自 api.json，不能被远程伪造）。</summary>
    private static string Who(LeaseSnapshot s)
        => s.TokenName.Length > 0 && s.TokenName != s.ClientName ? $"{s.ClientName} · 令牌 {s.TokenName}" : s.ClientName;

    /// <summary>每秒（ClockTick）刷新弹窗倒计时；状态机自己负责超时迁移（Changed 会关框）。</summary>
    internal void ControlClockTick()
    {
        if (!IsControlRequestOpen) return;
        UpdateControlCountdown(ControlLease.Snapshot(MonoNow).RequestDeadline);
    }

    private void UpdateControlCountdown(TimeSpan? deadline)
    {
        double left = deadline is { } d ? Math.Max(0, (d - MonoNow).TotalSeconds) : 0;
        ControlRequestCountdown = $"{left:0} 秒后自动拒绝";
    }

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryGrantControl))]
    private void AllowControlRequest() => Local(Ops.TryGrantControl(Src, _pendingRequestId));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryDenyControl))]
    private void DenyControlRequest() => Local(Ops.TryDenyControl(Src, _pendingRequestId));

    [RelayCommand]
    [WriteEntry(nameof(OperatorCommands.TryTakeBackControl))]
    private void TakeBackControl() => Local(Ops.TryTakeBackControl(Src));
}
