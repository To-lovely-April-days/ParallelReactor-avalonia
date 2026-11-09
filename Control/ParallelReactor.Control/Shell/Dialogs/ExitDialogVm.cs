using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>
/// 退出确认（真机，M3 复核新加；样稿没有退出流程）：关窗 / 文件 › 退出 / 系统的退出时，本机的控制权申请（或已取消、还在等结果的申请）还在某台设备的触屏上
/// （<see cref="ShellVm.PendingLeaseRequests"/>）—— 直接退出的话，触屏之后若批准，这份控制权没人心跳：HMI 心跳超时后进入宽限，宽限期满按它的 onRemoteLost 设置处理
/// （可能停止加热甚至急停，PORT-PLAN R7）。HMI 没有取消申请的端点（A5），退出前的 3 s 预算里等不到结果。两种选择：
/// <list type="bullet">
/// <item>「等结果再退出」（主要）：先取消这些申请（Client 的 CancelRequestAsync：后台守卫继续查，触屏批准了就立即释放），然后等到每条申请都有了结局
///   （批准 → 已释放、拒绝、超时）或者过了 HMI 给的到期时刻再加 <see cref="Margin"/>（最多 <see cref="MaxWait"/>），再退出。说明里倒计时。</item>
/// <item>「仍然退出」：照常退出（退出流程里照样取消申请、在 3 s 预算里等一下）。</item>
/// </list>
/// 关掉这个对话框（×、Esc、点遮罩）= 不退出（已经取消的申请照样由后台守卫收尾）。
/// 外观照样稿的断开 / 移除确认（说明 + warn 说明 + 两个按钮）；新加的文字都在这里（标题、副标题、说明、等待说明、两个按钮）。
/// </summary>
public sealed partial class ExitDialogVm : DialogVm
{
    /// <summary>过了 HMI 给的到期时刻之后再等这么久（后台守卫每秒查一次申请的结局）。</summary>
    public static readonly TimeSpan Margin = TimeSpan.FromSeconds(5);

    /// <summary>最多等这么久（HMI 申请超时默认 60 s；不知道到期时刻时按 70 s 算）。</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(80);

    private readonly ShellVm _shell;
    private bool _closed, _done;
    private DateTime? _until;

    private ExitDialogVm(ShellVm shell)
    {
        _shell = shell;
        Name = "exit";
        Title = "退出控制软件？";
        Icon = "key";   // 讲的是控制权申请（同申请控制权对话框）；不用「退出」菜单的 ×：标题旁再一个 × 像第二个关闭按钮
        Width = 560;
        ExitButton = new FootButton("仍然退出", new RelayCommand(Exit));
        WaitButton = new FootButton("等结果再退出", new AsyncRelayCommand(WaitAsync), FootKind.Primary, "clock");
        Footer = new FootBar(ExitButton, WaitButton);
        Body = this;
        Refresh();
    }

    /// <summary>没有申请在等时不打开（返回 null）。</summary>
    public static ExitDialogVm? Create(ShellVm shell) => shell.PendingLeaseRequests.Count == 0 ? null : new ExitDialogVm(shell);

    public FootButton ExitButton { get; }
    public FootButton WaitButton { get; }

    [ObservableProperty] private string _text = "";
    /// <summary>warn 说明的纯文字（核对用）；界面显示 <see cref="WarnSegs"/>（同样的字，按意思分成不折断的两段）。</summary>
    [ObservableProperty] private string _warn = "";
    [ObservableProperty] private IReadOnlyList<TextSeg> _warnSegs = Array.Empty<TextSeg>();
    /// <summary>在等结果（点了「等结果再退出」）。</summary>
    [ObservableProperty] private bool _isWaiting;

    public override void Refresh()
    {
        if (_closed || _done) return;
        var pending = _shell.PendingLeaseRequests;
        if (IsWaiting)
        {
            var now = Now(pending);
            int left = _until is { } u && u > now ? (int)Math.Ceiling((u - now).TotalSeconds - 1e-9) : 0;
            if (pending.Count == 0 || left <= 0) { Finish(); return; }
            SetWarn("已取消申请，正在等触屏的结果 · 剩 " + left + " s", "（触屏批准的会立即释放）");
            return;
        }
        if (pending.Count > 0) Sub = string.Join("、", pending.Select(d => d.Alias)) + "的控制权申请还在触屏上";
        Text = "退出后本机不再发心跳：触屏之后若批准，这份控制权没人持有 —— 设备在心跳超时后进入宽限，宽限期满按它的设置处理（可能停止加热甚至急停）。";
        if (pending.Count == 0) SetWarn("申请已经有了结果，可以退出。", "");
        else SetWarn("选「等结果再退出」会先取消申请、", "等触屏的结果（批准的立即释放）再退出。");
    }

    /// <summary>warn 说明：两段各自不折断（样稿 .dlg-nw 的做法：一行放不下时在两段之间换行，不留下孤零零的几个字）。</summary>
    private void SetWarn(string a, string b)
    {
        Warn = a + b;
        WarnSegs = b.Length == 0 ? new[] { new TextSeg(a) } : new[] { new TextSeg(a, NoWrap: 1), new TextSeg(b, NoWrap: 2) };
    }

    /// <summary>「等结果再退出」：取消正在申请的（后台守卫继续查），算出最晚等到哪一刻，之后每秒的刷新里看结局。</summary>
    private async Task WaitAsync()
    {
        if (IsWaiting || _closed) return;
        var pending = _shell.PendingLeaseRequests;
        foreach (var d in pending.Where(d => d.LeaseStatus == LeaseStatus.Requested).ToList())
            await d.Actions.RunAsync(DeviceAction.LeaseCancel, quiet: true).ConfigureAwait(true);
        var now = Now(pending);
        DateTime until = now;
        foreach (var d in pending)
        {
            var due = (d.Source as LiveDeviceSource)?.LeaseRequestDue ?? now + TimeSpan.FromSeconds(70);
            if (due + Margin > until) until = due + Margin;
        }
        if (until > now + MaxWait) until = now + MaxWait;
        _until = until;
        IsWaiting = true;
        WaitButton.IsVisible = false;
        Refresh();
    }

    private static DateTime Now(IReadOnlyList<DeviceVm> pending) =>
        pending.Select(d => d.Source).OfType<LiveDeviceSource>().FirstOrDefault()?.ClockNow ?? DateTime.UtcNow;

    private void Exit()
    {
        if (_closed || _done) return;
        Finish();
    }

    private void Finish()
    {
        _done = true;
        _shell.Dialogs.Close(this);
        _shell.ConfirmExit();
    }

    public override void OnClosed() => _closed = true;
}
