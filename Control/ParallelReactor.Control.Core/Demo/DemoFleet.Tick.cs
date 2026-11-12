using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

public sealed partial class DemoFleet
{
    /// <summary>
    /// 推进 1 s（样稿 tick）：冻结时什么都不做、返回 false。否则时钟 +1 s，每台设备：断线倒计时 / 重连、控制权倒计时（申请 5 s 后自动批准、
    /// 申请超时、宽限期结束）、在线时的物理模型与程序步进（压力测试数据只把数值钉回最坏值）、每 10 s 追加一个历史采样（保留 4 h）、模块通讯时刻。
    /// 推进后触发 <see cref="Changed"/>（Tick）。
    /// </summary>
    public bool Tick()
    {
        if (Frozen) return false;
        Now = AddMs(Now, 1000);
        TickCount++;
        foreach (var d in _devices.ToArray()) TickDevice(d);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.Tick));
        return true;
    }

    private double Nz(double a) => (_liveRand.Next() - 0.5) * 2 * a;

    private void TickDevice(DemoDevice dev)
    {
        var now = Now;
        var c = dev.Conn;
        var l = dev.Lease;
        // 连接：断线 → 倒计时重连；演示强制断线时重连失败，再倒计时
        if (c.State == ConnStatus.Offline)
        {
            c.RetryS = Math.Max(0, c.RetryS - 1);
            if (c.RetryS == 0) { c.State = ConnStatus.Connecting; c.ConnectS = ConnectS; }
        }
        else if (c.State == ConnStatus.Connecting)
        {
            c.ConnectS = Math.Max(0, c.ConnectS - 1);
            if (c.ConnectS == 0)
            {
                if (c.Forced) { c.State = ConnStatus.Offline; c.RetryS = RetryS; c.Attempts++; }
                else GoOnline(dev);
            }
        }
        // 控制权
        if (l.State == LeaseStatus.Requested)
        {
            l.RemainS = Math.Max(0, l.RemainS - 1);
            if (l.AutoAt is { } auto && now >= auto && c.State == ConnStatus.Online)
            {
                SetLease(dev, "remote");
                Log(dev, EventLevel.Op, null, "触屏已确认 · 远程控制中（" + Client + "）");
                RaiseToast(dev.Alias + "触屏已确认 · 远程控制中", DemoToastKind.Ok);
            }
            else if (l.RemainS == 0)
            {
                SetLease(dev, "local");
                Log(dev, EventLevel.Warn, null, "控制权申请超时（触屏未确认）");
                RaiseToast(dev.Alias + "控制权申请超时 · 触屏未确认", DemoToastKind.Warn);
            }
        }
        else if (l.State == LeaseStatus.Lost)
        {
            l.GraceS = Math.Max(0, l.GraceS - 1);
            if (l.GraceS == 0)
            {
                SetLease(dev, "local");
                Log(dev, EventLevel.Warn, null, "宽限期结束 · 控制权已交回触屏");
                RaiseToast(dev.Alias + "宽限期结束 · 控制权已交回触屏", DemoToastKind.Warn);
            }
        }
        if (c.State != ConnStatus.Online) return;
        dev.DataAt = now;
        if (Stress != 0) StressPin(dev);   // 压力测试数据：不跑物理模型，数值钉回最坏值
        else
        {
            foreach (var ch in dev.ChannelList)
            {
                if (!ch.Installed) continue;
                if (ch.State == StateKey.Alarm)
                {
                    if (ch.SpC is double sp) HoldTemp(ch, sp);
                    ch.PressureMPa += Nz(0.002);
                    continue;
                }
                if (!dev.Estop.Active && ch.Program is { Status: ProgramStatus.Running }) RunProgram(dev, ch);
                else IdlePhysics(ch);
            }
        }
        if (TickCount % SampleS == 0)
        {
            var cut = AddMs(now, -KeepMin * MinMs);
            foreach (var ch in dev.ChannelList)
            {
                if (!ch.Installed) continue;
                var h = ch.HistoryList;
                h.Add(DemoHistory.Sample(dev, ch, now));
                int drop = 0;
                while (drop < h.Count && h[drop].T < cut) drop++;
                if (drop > 0) h.RemoveRange(0, drop);
            }
        }
        foreach (var m in dev.CommList) if (m.Online) m.LastOk = AddMs(now, -JsNumber.Round(_liveRand.Next() * 900));
    }

    private void NextStep(DemoDevice dev, DemoChannel ch)
    {
        var p = ch.Program!;
        var now = Now;
        var log = p.Log.Append(new StepLogEntry(p.StepIndex, p.StepStartedAt ?? now, now)).ToArray();
        if (p.StepIndex >= p.Steps.Count - 1)
        {
            ch.Program = p with { Log = log, Status = ProgramStatus.Done, EndedAt = now };
            ch.State = StateKey.Done;
            ch.Heating = false;
            ch.Cooling = false;
            ch.HeaterPct = 0;
            ch.Sv = false;
            ch.StirOn = false;
            Log(dev, EventLevel.Info, ch.N, "RV" + ch.N + " 程序「" + p.Name + "」已结束");
            return;
        }
        int idx = p.StepIndex + 1;
        ch.Program = p with { Log = log, StepIndex = idx, StepStartedAt = now, StepDurS = Progress.StepPlanS(p.Steps[idx]) };
        ch.State = StateForStep(p.Steps[idx]);
        Log(dev, EventLevel.Info, ch.N, "RV" + ch.N + " 进入第 " + (idx + 1) + " 步 " + StepText.Name(p.Steps[idx]));
    }

    private void HoldTemp(DemoChannel ch, double sp)
    {
        ch.TempC += (sp - ch.TempC) * 0.05 + Nz(0.06);
        double hold = ch.HeaterPct > 0 && ch.HeaterPct < 100 ? ch.HeaterPct : 38;
        ch.HeaterPct = Clamp(hold + (sp - ch.TempC) * 8 + Nz(1.2), 5, 95);
    }

    private void RunProgram(DemoDevice dev, DemoChannel ch)
    {
        var p = ch.Program!;
        var st = p.Steps[p.StepIndex];
        double el = Ms(Now, p.StepStartedAt ?? Now) / 1000;
        switch (st.Type)
        {
            case StepKind.Heat:
            {
                double sp = st.Sp ?? double.NaN;
                ch.State = StateKey.Heat; ch.Heating = true; ch.Cooling = false; ch.SpC = st.Sp; ch.Sv = false;
                if (ch.TempC < sp - 5) { ch.TempC += ch.HeatRate + Nz(0.02); ch.HeaterPct = 100; }
                else { ch.TempC += (sp - ch.TempC) * 0.04 + 0.01 + Nz(0.03); ch.HeaterPct = Clamp(40 + (sp - ch.TempC) * 12, 20, 100); }
                if (ch.TempC >= (st.End?.V ?? double.NaN)) NextStep(dev, ch);
                break;
            }
            case StepKind.Press:
            case StepKind.Purge:
            {
                ch.Heating = st.Sp != null;
                if (st.Sp is double sp) { ch.SpC = sp; HoldTemp(ch, sp); }
                double target = st.Target ?? double.NaN;
                ch.PTargetMPa = st.Target;
                bool gasOk = st.Gas == null || dev.GasOpen(st.Gas);
                ch.Sv = gasOk;
                ch.State = gasOk ? StateKey.Press : StateKey.Wait;
                if (gasOk) ch.PressureMPa = Math.Min(target * 1.003, ch.PressureMPa + ch.PRate + Nz(ch.PRate * 0.1));
                if (st.Type == StepKind.Purge ? el >= st.EstMin * 60 : ch.PressureMPa >= target * 0.999) NextStep(dev, ch);
                break;
            }
            case StepKind.React:
            {
                double sp = st.Sp ?? double.NaN, target = st.Target ?? double.NaN;
                ch.State = StateKey.React; ch.Heating = true; ch.SpC = st.Sp; HoldTemp(ch, sp);
                ch.PTargetMPa = st.Target; ch.Sv = true;
                ch.PressureMPa += (target - ch.PressureMPa) * 0.08 - target * 0.0004 + Nz(target * 0.0012);
                ch.Mmol = DemoHistory.Fixed(DemoHistory.Or(ch.Mmol, 0) + 0.0009 * Math.Max(0.2, target / 0.35) * (0.8 + _noise.Next() * 0.4), 4);
                if (el >= (st.Minutes ?? double.NaN) * 60) NextStep(dev, ch);
                break;
            }
            case StepKind.Cool:
            {
                double sp = st.Sp ?? double.NaN;
                ch.State = StateKey.Cool; ch.Heating = false; ch.Cooling = dev.Model.HasCooling; ch.HeaterPct = 0; ch.SpC = st.Sp; ch.Sv = false;
                ch.TempC -= (ch.TempC - (sp - 12)) * (ch.Cooling ? 0.0011 : 0.0005) + Nz(0.02);
                if (ch.TempC <= sp) NextStep(dev, ch);
                break;
            }
            case StepKind.End:
            {
                ch.State = StateKey.Cool; ch.Heating = false; ch.HeaterPct = 0; ch.Sv = false; ch.SpC = null; ch.PTargetMPa = null;
                if (st.Vent == true) ch.PressureMPa = Math.Max(0.004, ch.PressureMPa * 0.985);
                ch.TempC -= (ch.TempC - 24) * (dev.Model.HasCooling ? 0.0011 : 0.0006) + Nz(0.02);
                bool doneT = st.CoolTo is not double coolTo || ch.TempC < coolTo;
                bool doneP = st.End is not { Kind: EndKind.Press } e || ch.PressureMPa < e.V;
                if ((doneT && doneP) || el > DemoHistory.Or(st.EstMin, 1) * 60 * 2) NextStep(dev, ch);
                break;
            }
            case StepKind.Wait:
            {
                ch.State = StateKey.Wait;
                if (el >= DemoHistory.Or(st.Minutes, 1) * 60) NextStep(dev, ch);
                break;
            }
            default:
                NextStep(dev, ch);
                break;
        }
    }

    private void IdlePhysics(DemoChannel ch)
    {
        if (ch.Heating && ch.SpC is double sp)
        {
            if (ch.TempC < sp - 5) { ch.TempC += ch.HeatRate; ch.HeaterPct = 100; }
            else HoldTemp(ch, sp);
        }
        else
        {
            ch.HeaterPct = 0;
            ch.TempC -= (ch.TempC - 24) * (ch.Cooling ? 0.0011 : 0.0004) + Nz(0.015);
        }
    }
}
