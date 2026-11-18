using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Core.Pid;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Shell;
using ParallelReactor.Control.Shell.Dialogs;

namespace ParallelReactor.Control.Pages.Settings.Pid;

/// <summary>
/// 「取消整定」的确认（停止一律先确认，2026-11-18 用户）：标题「取消 1号机 的自整定？」，说明「RV5 停止加热、PID 不改」；
/// 继续整定 / 取消整定（主要停止样式）。确认 = 每路 DELETE /channels/{n}/autotune（停止类，不要控制权；只要连接）。参数 = 设备位置 k。
/// </summary>
public sealed partial class PidCancelDialogVm : DialogVm
{
    public const string Name0 = "pidcancel";
    private readonly ShellVm _shell;
    private readonly PidSectionVm _pid;
    private readonly int _k;
    private bool _closed;

    private PidCancelDialogVm(ShellVm shell, PidSectionVm pid, DeviceVm d, PidSectionVm.TuneRun run)
    {
        _shell = shell;
        _pid = pid;
        _k = d.K;
        Name = Name0;
        Arg = d.K.ToString(CultureInfo.InvariantCulture);
        Icon = null;
        Width = 518;
        var chs = run.Ch.Where(n => !run.Ended.ContainsKey(n)).ToList();
        Title = "取消 " + d.Alias + " 的自整定？";
        Sub = null;
        Text = PidTexts.Rvs(chs) + " 的自整定（" + run.BandName + " · " + PidTexts.Num(run.Sp) + " °C）会中止：通道停止加热，自然降温。";
        Warn = "PID 不改：" + run.BandName + "保持原来的值。以后要整定，需要从头再来一次（约 20–40 min）。";
        GoButton = new FootButton("取消整定", new AsyncRelayCommand(GoAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions), FootKind.PrimaryStop, "f-stop");
        Footer = new FootBar(new FootButton("继续整定", new RelayCommand(() => shell.Dialogs.Close(this))), GoButton);
        Body = new Views.Pages.Settings.PidCancelView { DataContext = this };
        Refresh();
    }

    /// <summary>参数「k」；这台设备没有在整定时 null（不打开）。</summary>
    public static PidCancelDialogVm? Create(ShellVm shell, PidSectionVm pid, string? arg)
    {
        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) || shell.Fleet.Device(k) is not { } d) return null;
        return pid.RunOf(d) is { Done: false } run ? new PidCancelDialogVm(shell, pid, d, run) : null;
    }

    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _warn = "";
    public FootButton GoButton { get; }

    public override void Refresh()
    {
        if (_closed) return;
        var d = _shell.Fleet.Device(_k);
        if (d == null || _pid.RunOf(d) is not { Done: false }) { _shell.Dialogs.Close(this); return; }
        GoButton.Reason = d.Source.Conn.CanStop ? null : d.Alias + "未连接";
    }

    private async Task GoAsync()
    {
        var d = _shell.Fleet.Device(_k);
        if (d == null || _closed) return;
        _shell.Dialogs.Close(this);
        await _pid.CancelAsync(d).ConfigureAwait(true);
    }

    public override void OnClosed() => _closed = true;
}
