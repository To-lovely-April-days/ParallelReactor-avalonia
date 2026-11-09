using ParallelReactor.Api;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.State;
using ParallelReactor.Control.Services;

namespace ParallelReactor.Control.Fleet;

/// <summary>
/// 两种数据源 → <see cref="ChannelReading"/> 的投影（旧 ItemVms.cs ChannelVm.Apply 的去处，PORT-PLAN §1.3）。
/// 实时：Contracts 的 ChannelSnap 经 Core 的 StateMap（heating→heat、alarm + overPressure →「超压报警」、done 按程序 / 急停分「已停止 / 已结束」、
/// 等待气路）与 Units（psi ÷ 145.037738 → MPa）；演示：Core 的 DemoDevice / DemoChannel 的帮助方法（同样稿 PR.sim.reading / stateKey / stateText）。
/// 纯函数，不碰界面（测试直接调用）。
/// </summary>
public static class ChannelProjection
{
    /// <summary>
    /// 实时：一帧遥测里的第 n 路。<paramref name="ch"/> = null 表示还没收到这一路的数据（<paramref name="installed"/> 用 /device 的已安装通道）。
    /// <paramref name="online"/> / <paramref name="everOnline"/> 是设备的连接；<paramref name="estop"/> 取该帧的 Estop.Active；<paramref name="stir"/> 是共用搅拌（转速 = 设定值）。
    /// </summary>
    public static ChannelReading FromSnap(ChannelSnap? ch, int n, bool online, bool everOnline, bool estop = false, bool installed = true, StirSnap? stir = null)
    {
        if (!(ch?.Installed ?? installed)) return ChannelReading.Uninstalled(n);
        var shown = StateMap.Map(ch, online, everOnline, estop, installed);
        var own = ch is null ? shown : StateMap.Map(ch, online: true, everOnline: true, estopActive: estop);
        if (ch is null)
            return new ChannelReading(n, true, shown.Key, shown.Word, own.Key, own.Word, own.Cause, null, null, null, null, null, null, false, false);
        var p = ch.Program;
        return new ChannelReading(n, true, shown.Key, shown.Word, own.Key, own.Word, own.Cause,
            ch.TempC, ch.TempSpC, Units.PsiToMpa(ch.PressurePsi), ch.PressureSpPsi > 0 ? Units.PsiToMpa(ch.PressureSpPsi) : null,
            ch.HeatOutputPct, stir is null ? null : stir.On ? stir.RpmSp : 0,
            StateMap.IsHeating(ch), ch.ValveOpen,
            p is null ? null : new HmiProgram(p.Running, p.StepIndex, p.StepCount, p.State, p.Message, p.StartedAt, p.WaitingGas, p.StartedBy));
    }

    /// <summary>实时：从一帧里取第 n 路（Id = n）再投影；帧为空（从未收到数据）按 <paramref name="installed"/>。</summary>
    public static ChannelReading FromFrame(TelemetrySnapshot? frame, int n, bool online, bool everOnline, bool installed = true)
    {
        ChannelSnap? ch = null;
        if (frame?.Channels is { } list)
            foreach (var c in list)
                if (c.Id == n) { ch = c; break; }
        return FromSnap(ch, n, online, everOnline, frame?.Estop?.Active ?? false, installed, frame?.Stir);
    }

    /// <summary>演示：第 n 路（同样稿 PR.sim.reading / stateKey / stateText；演示的报警只有超压）。</summary>
    public static ChannelReading FromDemo(DemoDevice d, int n)
    {
        var ch = d.Channel(n);
        if (ch is null || !ch.Installed) return ChannelReading.Uninstalled(n);
        return new ChannelReading(n, true, d.StateKeyOf(ch), d.StateWord(ch), ch.State, d.StateText(ch),
            ch.State == StateKey.Alarm ? AlarmCause.OverPressure : AlarmCause.None,
            d.Reading(ch, DemoReading.TempC), d.Reading(ch, DemoReading.SpC), d.Reading(ch, DemoReading.PressureMPa), d.Reading(ch, DemoReading.PTargetMPa),
            d.Reading(ch, DemoReading.HeaterPct), d.Reading(ch, DemoReading.Rpm), ch.Heating, ch.Sv);
    }

    /// <summary>
    /// 资源管理器 / 时间轴行的读数格（样稿 shell.js keyReading）：两列固定（℃ 列、MPa 列），按反应釜<b>自己的</b>状态决定显示哪列 ——
    /// 升压 / 等待进气只写压力，升压 / 等待 / 反应 / 报警写压力；超压报警时温度格窄一些（is-tn），温度 ≥ 100 ℃（5 位）放不下就只写压力。
    /// </summary>
    public static (bool ShowT, bool NarrowT, bool ShowP) KeyColumns(StateKey own, string tempText)
    {
        bool alarm = own == StateKey.Alarm;
        bool showT = alarm ? tempText.Length <= 4 : own is not (StateKey.Press or StateKey.Wait);
        bool showP = own is StateKey.Press or StateKey.Wait or StateKey.React or StateKey.Alarm;
        return (showT, alarm, showP);
    }
}
