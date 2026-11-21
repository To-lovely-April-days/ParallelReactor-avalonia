using System;
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

    /// <summary>停止加热（只关这一路的加热输出，HMI heat/stop：仪表 At=4）。</summary>
    public static IReadOnlyList<StopRow> HeatStop { get; } = new[]
    {
        R("加热", T("该路停止加热 · 温度自然下降")),
        R("程序", T("不中止 · 要停下整路用「停止该通道」")),
        R("搅拌", T("不变")),
        R("气路", T("不变")),
        R("釜内压力", B("保留，不放空")),
    };

    /// <summary>停用通道（HMI disable = 停止该通道 + 转为停用）。</summary>
    public static IReadOnlyList<StopRow> Disable { get; } = new[]
    {
        R("程序", T("该路程序中止")),
        R("加热", T("该路停止加热")),
        R("搅拌", T("不变 · 电机全部釜共用，其它路还在用")),
        R("气路", T("关该路进气阀 SV；正在给该路升压的序列中止")),
        R("釜内压力", B("保留，不放空"), T(" · 温度自然下降")),
        R("之后", T("该路停用，要先「启用通道」才能再启动")),
    };

    /// <summary>关共用搅拌（PUT /stir {on:false}）。</summary>
    public static IReadOnlyList<StopRow> StirOff { get; } = new[]
    {
        R("搅拌", T("电机停止 · "), B("所有釜一起停")),
        R("程序", T("不中止")),
        R("加热", T("不变")),
        R("气路", T("不变")),
        R("之后", T("可以重新打开搅拌")),
    };

    /// <summary>中止性能测试（PerfRunner.Abort → SafeStop）。</summary>
    public static IReadOnlyList<StopRow> PerfAbort { get; } = new[]
    {
        R("程序", T("本项测试通道的程序中止，测试开的自整定取消")),
        R("加热", T("本项测试通道停止加热")),
        R("搅拌", T("测试开的搅拌停止，其它不变")),
        R("泄漏测试", T("测试开的一并中止")),
        R("报告", T("照常生成：已测部分 + 中止原因")),
        R("釜内压力", B("保留，不放空")),
    };

    /// <summary>中止泄漏测试（DELETE /leak-tests/current：HMI 全关阀，不泄压）。</summary>
    public static IReadOnlyList<StopRow> LeakAbort { get; } = new[]
    {
        R("气路", T("阀门全部关闭")),
        R("釜内压力", B("保留，不放空"), T(" · 已充压的釜拆前先泄压")),
        R("结果", T("本次不出结论")),
    };

    /// <summary>其它停止类确认的琥珀说明（不提「三种停止」）。</summary>
    public static IReadOnlyList<TextSeg> NoteNoVent { get; } = new[]
    {
        T("停止"), B("不会自动放空"), T("。要泄压：程序最后一步「结束方式」选「放空」，或降温后在触屏上手动放空。"),
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
            FootKind.PrimaryStop, "stop");
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

/// <summary>
/// 其它停止类操作的确认（融合版 F9，用户：「停止一律先确认」）：停止加热、停用通道、关共用搅拌、中止性能测试、中止泄漏测试。
/// 外观同「停止一路」（开头一句 + 「做什么」表 + 琥珀说明，见 StopInfoView）；取消 / 主按钮（红，停止图标）。
/// 主按钮的禁用原因每次刷新重新问（<c>reason</c>，可用时 null）；确认 = 关掉对话框再执行 <c>go</c>。打开时不注册名字（直接 Show）。
/// </summary>
public sealed partial class StopConfirmDialogVm : DialogVm
{
    private readonly ShellVm _shell;
    private readonly Func<string?> _reason;
    private readonly Func<Task> _go;
    private bool _closed;

    public StopConfirmDialogVm(ShellVm shell, string name, string title, string goLabel,
        IReadOnlyList<TextSeg> lead, IReadOnlyList<StopRow> rows, Func<string?> reason, Func<Task> go)
    {
        _shell = shell;
        _reason = reason;
        _go = go;
        Name = name;
        Title = title;
        Sub = null;
        Icon = null;
        Width = 518;
        Body = this;
        Lead = lead;
        Rows = rows;
        var cancel = new FootButton("取消", new RelayCommand(() => shell.Dialogs.Close(this)));
        GoButton = new FootButton(goLabel, new AsyncRelayCommand(GoAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions), FootKind.PrimaryStop, "stop");
        Footer = new FootBar(cancel, GoButton);
        Refresh();
    }

    public FootButton GoButton { get; }
    public IReadOnlyList<TextSeg> Lead { get; }
    public IReadOnlyList<StopRow> Rows { get; }
    public IReadOnlyList<TextSeg> StopNote => StopTexts.NoteNoVent;

    public override void Refresh()
    {
        if (_closed) return;
        GoButton.Reason = FootButton.Nz(_reason() ?? "");
    }

    private async Task GoAsync()
    {
        if (_closed || GoButton.Reason != null) return;
        _shell.Dialogs.Close(this);
        await _go().ConfigureAwait(true);
    }

    public override void OnClosed() => _closed = true;

    // ---------------- 五种确认 ----------------

    /// <summary>
    /// 按名字打开（DialogSet 注册，截图 / 地址参数 dialog=heatstop:k:n、disable:k:n、stiroff:k 用；界面上由各按钮直接 Show）。
    /// 设备、通道不在时 null（不打开）。
    /// </summary>
    public static StopConfirmDialogVm? Create(ShellVm shell, string name, string? arg)
    {
        var parts = (arg ?? "").Split(':');
        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) || shell.Fleet.Device(k) is not { } d) return null;
        if (name == "stiroff") return d.Stir != null ? StirOff(shell, d, () => Pages.Home.ResPaneVm.StirLockReason(d)) : null;
        if (parts.Length < 2 || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || d.Reactor(n) is not { Installed: true }) return null;
        return name == "disable" ? Disable(shell, d, n) : HeatStop(shell, d, n);
    }

    private static string Rv(int n) => "RV" + n.ToString(CultureInfo.InvariantCulture);

    /// <summary>停止一路的加热（检查器「停止加热」）：只关加热输出（仪表 At=4），程序不中止。</summary>
    public static StopConfirmDialogVm HeatStop(ShellVm shell, DeviceVm d, int n) => new(shell, "heatstop",
        "停止 " + d.Alias + " " + Rv(n) + " 加热", "停止加热",
        new[] { new TextSeg("只关这一路的加热输出，其它通道不受影响。停止不需要控制权。") },
        StopTexts.HeatStop,
        () => d.Actions.Why(DeviceAction.HeatStop, ActionPayload.Ch(n)),
        async () => await d.Actions.RunAsync(DeviceAction.HeatStop, ActionPayload.Ch(n)).ConfigureAwait(true));

    /// <summary>停用一路（检查器「停用通道」）：同停止该通道，再设为停用。启用不确认。</summary>
    public static StopConfirmDialogVm Disable(ShellVm shell, DeviceVm d, int n) => new(shell, "disable",
        "停用 " + d.Alias + " " + Rv(n), "停用 " + Rv(n),
        new[] { new TextSeg("先像「停止该通道」一样停下这一路，再把它设为停用。其它通道不受影响。停用不需要控制权。") },
        StopTexts.Disable,
        () => d.Actions.Why(DeviceAction.Disable, ActionPayload.Ch(n)),
        async () => await d.Actions.RunAsync(DeviceAction.Disable, ActionPayload.Ch(n)).ConfigureAwait(true));

    /// <summary>关共用搅拌（主页「共用资源」的搅拌开关）：一台电机带全部釜，关掉就全部停。开搅拌不确认。</summary>
    public static StopConfirmDialogVm StirOff(ShellVm shell, DeviceVm d, Func<string?> lockReason) => new(shell, "stiroff",
        "停止搅拌 · " + d.Alias, "停止搅拌",
        new[] { new TextSeg("搅拌电机"), new TextSeg("全部釜共用", Bold: true), new TextSeg("：关掉后所有釜一起停止搅拌。") },
        StopTexts.StirOff,
        () => lockReason() ?? d.Actions.Why(DeviceAction.StirOff, new ActionPayload(Rpm: d.Stir?.Rpm)),
        async () => await d.Actions.RunAsync(DeviceAction.StirOff, new ActionPayload(Rpm: d.Stir?.Rpm)).ConfigureAwait(true));

    /// <summary>中止性能测试（性能测试页工具条「中止」）。</summary>
    public static StopConfirmDialogVm PerfAbort(ShellVm shell, Func<string?> reason, Action go) => new(shell, "perfabort",
        "中止性能测试", "中止测试",
        new[] { new TextSeg("中止正在进行的性能测试。不需要控制权。") },
        StopTexts.PerfAbort, reason, () => { go(); return Task.CompletedTask; });

    /// <summary>中止泄漏测试（性能测试页「泄漏测试」工具条「中止」）：阀门全关，不放空。</summary>
    public static StopConfirmDialogVm LeakAbort(ShellVm shell, DeviceVm d, Func<string?> reason, Func<Task> go) => new(shell, "leakabort",
        "中止泄漏测试 · " + d.Alias, "中止测试",
        new[] { new TextSeg("中止" + d.Alias + "正在进行的泄漏测试。不需要控制权。") },
        StopTexts.LeakAbort, reason, go);
}
