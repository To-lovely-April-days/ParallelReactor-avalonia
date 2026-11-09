using ParallelReactor.Api;

namespace ParallelReactor.Control.Core.State;

/// <summary>报警原因（HMI 只有超压 / 超温两种；对不上的记 Unknown，状态字写「报警」）。</summary>
public enum AlarmCause { None, OverPressure, OverTemp, Unknown }

/// <summary>一个反应釜的显示状态：键（决定样式）+ 状态字（界面上绝不省略）+ 报警原因。</summary>
public readonly record struct ChannelState(StateKey Key, string Word, AlarmCause Cause = AlarmCause.None)
{
    public StateKind Kind => StateKeys.KindOf(Key);
    public bool IsRunning => StateKeys.IsRunning(Key);
}

/// <summary>
/// 真机遥测 → 样稿状态（PORT-PLAN §3.1 通道级 / §3.2 第 3 条 / §3.5）。输入是 Contracts 的 ChannelSnap（旧 ChannelVm.Apply 用的同一份数据）
/// 加上设备级的连接与急停。按顺序判断：
/// <list type="number">
/// <item>未安装：ChannelSnap.Installed = false（还没收到过遥测时用调用方给的型号表结果）→ 未安装。</item>
/// <item>设备断线 → offline：连上过「离线」，从未连上「未连接」。在线但还没有这个通道的数据 →「未连接」。</item>
/// <item>报警：State = "alarm"，或超温 / 超压切断未解除（OverTempTripped / OverPressureTripped；HMI 在切断期间会把通道重新置成报警）。
///   状态字按 AlarmCause：overPressure「超压报警」、overTemp「超温报警」；AlarmCause 为空时看切断标志；都没有写「报警」。</item>
/// <item>等待气路：程序在跑且 Program.WaitingGas（排队等共用气路，不是样稿的「进气口关闭」）→ wait「等待气路」。
///   这时 HMI 的 State 还是排队前的状态。占用者见 ProgramsSnap.GasHolder（<see cref="WaitGasText"/>）。</item>
/// <item>done：HMI 没有「已停止」（停止、全部停止、急停之后都是 done）。程序为 aborted / failed，或设备正在急停 →「已停止」，否则「已结束」。
///   没有程序时手动停止无法区分，显示「已结束」。</item>
/// <item>其余按代码：heating→heat、pressing→press、cooling→cool，idle / ready / react / wait / stir 同名（也接受样稿的代码）。
///   不认识的代码：中性样式 + 原样的代码作状态字，让异常一眼可见。</item>
/// </list>
/// HMI 不提供、这里不推导的：报警暂停 held（HMI 报警时中止该通道程序 → 程序状态 failed，键仍是 alarm，程序文字「报警中止」由后续里程碑给出）、
/// 冷却水（cooling 只是程序结束降温段，PR8 没有冷却水）、每釜独立搅拌转速、液位、耗气 mmol、急停来源。
/// </summary>
public static class StateMap
{
    /// <summary>真机「等待气路」的状态字（样稿的「等待进气」是进气口关闭，含义不同，§3.5）。</summary>
    public const string WaitGasWord = "等待气路";

    /// <summary>HMI 通道状态码（Contracts.ChannelStates）→ 样稿键；也接受样稿代码（heat / press / cool / stopped）。不认识返回 null。</summary>
    public static StateKey? KeyOfHmi(string? state) => state switch
    {
        ChannelStates.Idle => StateKey.Idle,
        ChannelStates.Ready => StateKey.Ready,
        ChannelStates.Heating or "heat" => StateKey.Heat,
        ChannelStates.Pressing or "press" => StateKey.Press,
        ChannelStates.React => StateKey.React,
        ChannelStates.Wait => StateKey.Wait,
        ChannelStates.Stir => StateKey.Stir,
        ChannelStates.Cooling or "cool" => StateKey.Cool,
        ChannelStates.Done => StateKey.Done,
        ChannelStates.Alarm => StateKey.Alarm,
        "stopped" => StateKey.Stopped,
        _ => null,
    };

    /// <summary>
    /// 一个通道的显示状态。<paramref name="online"/> / <paramref name="everOnline"/> 是设备的连接状态；<paramref name="estopActive"/> 取遥测 Estop.Active；
    /// <paramref name="installed"/> 只在没有 ChannelSnap 时使用（型号表的已安装通道；不给则按已安装）。
    /// 断线时 <paramref name="ch"/> 传最后一帧也可以：结果是 offline，要「断线前 反应中」这类提示时再用 online: true 调一次。
    /// </summary>
    public static ChannelState Map(ChannelSnap? ch, bool online = true, bool everOnline = true, bool estopActive = false, bool? installed = null)
    {
        if (!(ch?.Installed ?? installed ?? true)) return new ChannelState(StateKey.Uninstalled, StateKeys.Word(StateKey.Uninstalled));
        if (!online) return new ChannelState(StateKey.Offline, StateKeys.OfflineText(everOnline));
        if (ch is null) return new ChannelState(StateKey.Offline, StateKeys.NeverConnectedWord);

        if (ch.State == ChannelStates.Alarm || ch.OverTempTripped || ch.OverPressureTripped)
        {
            var cause = ch.AlarmCause switch
            {
                "overPressure" => AlarmCause.OverPressure,
                "overTemp" => AlarmCause.OverTemp,
                _ when ch.OverPressureTripped => AlarmCause.OverPressure,
                _ when ch.OverTempTripped => AlarmCause.OverTemp,
                _ => AlarmCause.Unknown,
            };
            return new ChannelState(StateKey.Alarm, AlarmWord(cause), cause);
        }

        var p = ch.Program;
        if (p is { Running: true, WaitingGas: true }) return new ChannelState(StateKey.Wait, WaitGasWord);

        var key = KeyOfHmi(ch.State);
        if (key == StateKey.Done)
        {
            bool stopped = estopActive || p?.State is ChannelProgramStates.Aborted or ChannelProgramStates.Failed;
            return stopped ? new ChannelState(StateKey.Stopped, StateKeys.Word(StateKey.Stopped)) : new ChannelState(StateKey.Done, StateKeys.Word(StateKey.Done));
        }
        if (key is not { } k) return new ChannelState(StateKey.Idle, string.IsNullOrEmpty(ch.State) ? "—" : ch.State);
        return new ChannelState(k, StateKeys.Word(k));
    }

    /// <summary>从一帧遥测里取第 n 路（Id = n）再 <see cref="Map"/>；急停取该帧的 Estop.Active。帧为空（从未收到数据）按 installed 判断。</summary>
    public static ChannelState FromSnapshot(TelemetrySnapshot? snap, int n, bool online = true, bool everOnline = true, bool? installed = null)
    {
        ChannelSnap? ch = null;
        if (snap?.Channels is { } list)
            foreach (var c in list)
                if (c.Id == n) { ch = c; break; }
        return Map(ch, online, everOnline, snap?.Estop?.Active ?? false, installed);
    }

    /// <summary>报警状态字：超压报警 / 超温报警 / 报警。</summary>
    public static string AlarmWord(AlarmCause cause) => cause switch
    {
        AlarmCause.OverPressure => "超压报警",
        AlarmCause.OverTemp => "超温报警",
        _ => "报警",
    };

    /// <summary>等待气路的说明：有占用者时「等待气路（RV3 占用中）」，否则「等待气路」。</summary>
    public static string WaitGasText(int? holder) => holder is int h ? WaitGasWord + "（RV" + h + " 占用中）" : WaitGasWord;

    /// <summary>
    /// 加热中（釜体夹套 / 火焰图形用，PORT-PLAN §3.1 的 heating）：温控回读 At ≠ 4 即在加热（At=0 自动控制、自整定）；
    /// At 未知（温控离线）时看逼近相位（settle / creep / hold）或升温曲线是否在跑。
    /// </summary>
    public static bool IsHeating(ChannelSnap? ch)
    {
        if (ch is not { Installed: true }) return false;
        if (ch.AtMode is int at) return at != 4;
        return ch.Approach is { Phase: { Length: > 0 } phase } && phase != "off" || ch.Profile is { Running: true };
    }
}
