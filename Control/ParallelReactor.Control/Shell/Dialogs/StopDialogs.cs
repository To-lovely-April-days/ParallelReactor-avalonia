using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using ParallelReactor.Control.Controls.Pr;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Fleet;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Shell.Dialogs;

/// <summary>「对气路做什么」表的一行（样稿 .stopt：左列 92 宽 600 t-2，右列 t-1；粗体段 600）。</summary>
public sealed record StopRow(string Key, IReadOnlyList<TextSeg> Value);

/// <summary>三种停止分别做什么（融合版 F3，样稿 STOPK / stopTbl / STOP_NOTE，逐字照抄；依据接口文档 §5.2、§7.3、通道 stop）。</summary>
public static class StopTexts
{
    private static TextSeg T(string s) => new(s);
    private static TextSeg B(string s) => new(s, Bold: true);
    private static StopRow R(string k, params TextSeg[] v) => new(k, v);

    /// <summary>停止该通道。</summary>
    public static IReadOnlyList<StopRow> Channel { get; } = new[]
    {
        R("程序", T("该路程序中止")),
        R("加热", T("该路停止加热")),
        R("搅拌", T("不变 · 电机全部釜共用，其它路还在用")),
        R("气路", T("关该路进气阀 SV；正在给该路升压的序列中止")),
        R("釜内压力", B("保留，不放空"), T(" · 温度自然下降")),
        R("之后", T("可以重新启动该路")),
    };

    /// <summary>全部停止（<paramref name="stopsStirring"/> = 全部停止连搅拌一起停，用户已定：是）。</summary>
    public static IReadOnlyList<StopRow> All(bool stopsStirring) => new[]
    {
        R("程序", T("所有通道的程序中止")),
        R("加热", T("全部停止加热")),
        R("搅拌", T(stopsStirring ? "停止" : "不变")),
        R("气路", T("进气阀、气源阀全部关闭；正在进行的吹扫、升压中止")),
        R("釜内压力", B("保留，不放空"), T(" · 各釜密封降温")),
        R("之后", T("控制权不变，可以重新下发")),
    };

    /// <summary>急停。</summary>
    public static IReadOnlyList<StopRow> Estop { get; } = new[]
    {
        R("程序", T("全部中止，加热、搅拌锁定")),
        R("加热 / 搅拌", T("立即切断并锁定，复位前不能再启动")),
        R("气路", T("所有阀门关闭，不再进气")),
        R("釜内压力", B("保留，不放空"), T(" · 降温后在触屏上手动泄压")),
        R("控制权", T("强制回到本地，远程要重新申请")),
        R("复位", B("只能在设备触屏上")),
    };

    /// <summary>琥珀说明（样稿 STOP_NOTE）。</summary>
    public static IReadOnlyList<TextSeg> Note { get; } = new[]
    {
        T("三种停止都"), B("不会自动放空"), T("。要泄压：程序最后一步「结束方式」选「放空」，或降温后在触屏上手动放空。"),
    };

    /// <summary>开头一句。</summary>
    public static IReadOnlyList<TextSeg> LeadAll { get; } = new[] { T("停止所选设备的全部通道。不需要控制权。") };
    public static IReadOnlyList<TextSeg> LeadEstop { get; } = new[] { B("这是经网络发出的软件停机请求，不能代替设备上的急停按钮。"), T("急停不需要控制权。") };
    public static IReadOnlyList<TextSeg> LeadChannel { get; } = new[] { T("只停这一路，其它通道不受影响。停止不需要控制权。") };
}

/// <summary>
/// 停止一路（融合版 F3，样稿 stopChDlg）：「停止 1号机 RV1」+ 开头一句 + 「对气路做什么」表 + 琥珀说明；取消 / 停止 RV1（主要）。
/// 参数 k:n。停止不需要控制权（每次照样再查守卫：只要连接）。
/// </summary>
public sealed partial class StopChannelDialogVm : DialogVm
{
    private readonly ShellVm _shell;

    private StopChannelDialogVm(ShellVm shell, DeviceVm d, int n, string? arg)
    {
        _shell = shell;
        Name = "stopch";
        Arg = arg;
        K = d.K;
        N = n;
        Title = "停止 " + d.Alias + " RV" + n.ToString(CultureInfo.InvariantCulture);
        Sub = null;
        Icon = null;
        Width = 518;
        Body = this;
        var cancel = new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this)));
        GoButton = new FootButton("停止 RV" + n.ToString(CultureInfo.InvariantCulture), new AsyncRelayCommand(GoAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions),
            FootKind.PrimaryStop, "f-stop");
        Footer = new FootBar(cancel, GoButton);
        Refresh();
    }

    /// <summary>参数「k:n」；设备或通道不在时 null（不打开）。</summary>
    public static StopChannelDialogVm? Create(ShellVm shell, string? arg)
    {
        var parts = (arg ?? "").Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return null;
        var d = shell.Fleet.Device(k);
        if (d?.Reactor(n) is not { Installed: true }) return null;
        return new StopChannelDialogVm(shell, d, n, arg);
    }

    public int K { get; }
    public int N { get; }
    public FootButton GoButton { get; }
    public IReadOnlyList<TextSeg> Lead => StopTexts.LeadChannel;
    public IReadOnlyList<StopRow> Rows => StopTexts.Channel;
    public IReadOnlyList<TextSeg> StopNote => StopTexts.Note;

    public override void Refresh()
    {
        var d = _shell.Fleet.Device(K);
        if (d == null) { _shell.Dialogs.Close(this); return; }
        GoButton.Reason = FootButton.Nz(d.Actions.Why(DeviceAction.Stop, ActionPayload.Ch(N)));
    }

    private async Task GoAsync()
    {
        var d = _shell.Fleet.Device(K);
        _shell.Dialogs.Close(this);
        if (d != null) await d.Actions.RunAsync(DeviceAction.Stop, ActionPayload.Ch(N)).ConfigureAwait(true);
    }
}
