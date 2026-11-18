using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 融合版样稿的数据集（fusion=1，数据见 <see cref="DemoFusion"/>）：1号机 HT-PR8（8 路全装，远程控制中，会话 PT-20261115-1305）、
/// 2号机 HT-PR4（本地，会话 PT-20261115-1250）、3号机 HT-PR16（本地，没有会话），时钟 2026-11-15 14:30:07（<see cref="FusionT0"/>）。
/// <list type="bullet">
/// <item>程序：每个在跑的通道停在样稿的第 step 步，本步剩余 = 样稿 left（min），步内进度 = 样稿 f（按 progPlan 的估计时长）——
///   Progress 算出的「本步剩余」与「预计结束」（现在 + TotalRemainMin）同样稿运行表的 etaOf。报警的通道 = 报警暂停（Held）。</item>
/// <item>停用的通道（样稿 off）：已安装，但没有读数（<see cref="DemoChannel.NoData"/>）、没有历史。</item>
/// <item>offline=1：1号机断线重连中，数据停在 14:28:41（现在之前 <see cref="FusionLostAgoS"/> s），已试 <see cref="FusionLostAttempts"/> 次（样稿「已试 3 次」+ 这一次），
///   读数停在断线前的值；程序整段往前挪同样的秒数（断线时刻看到的剩余时间 = 样稿的值）；控制权照样稿保持「远程」（设备按 ONLOST 保持运行）。</item>
/// </list>
/// 不冻结时照常每秒推进（同旧数据的物理模型与程序步进）。
/// </summary>
public sealed partial class DemoFleet
{
    /// <summary>融合版的时钟起点：2026-11-15 14:30:07（本地时间，样稿状态栏）。</summary>
    public static readonly DateTime FusionT0 = new(2026, 11, 15, 14, 30, 7, DateTimeKind.Local);

    /// <summary>融合版这次运行的开始：13:00（样稿 RUN0；曲线 0–90 min）。</summary>
    public static readonly DateTime FusionRun0 = new(2026, 11, 15, 13, 0, 0, DateTimeKind.Local);

    /// <summary>断线演示：数据停在现在之前 86 s（14:28:41，样稿 NET.since）。</summary>
    public const int FusionLostAgoS = 86;

    /// <summary>断线演示的重连次数（样稿「已试 3 次」，正在进行的是第 4 次）。</summary>
    public const int FusionLostAttempts = 4;

    private void BuildFusion()
    {
        foreach (var f in DemoFusion.Devices) _devices.Add(MakeFusionDevice(f));
    }

    private DemoDevice MakeFusionDevice(DemoFusion.Dev f)
    {
        var m = Models.Get(f.ModelKey) ?? Models.Get("PR8") ?? Models.All[0];
        var now = Now;
        var since = DemoFusion.At(8, 30);
        var rand = new DemoRng(31 + f.K * 7919);
        var lib = DemoFusion.Library(m);
        var dev = new DemoDevice(this, f.K, m)
        {
            Alias = f.Alias,
            Host = f.Host,
            Port = 8080,
            Token = "ht-" + (0x5a17 * f.K).ToString("x", CultureInfo.InvariantCulture) + "-" + m.Key.ToLowerInvariant() + "-a9c4",
            Sn = m.Key + "-2026-" + f.K.ToString(CultureInfo.InvariantCulture).PadLeft(4, '0'),
            AutoConnect = true,
            DataAt = now,
            DefaultLease = f.Lease,
            DefaultOffline = false,
            Plant = DemoFusion.Plant(f, m.Channels.Count),
        };
        var c = dev.Conn;
        c.State = ConnStatus.Online;
        c.Since = since;
        c.LostAt = null;
        c.RetryS = RetryS;
        c.ConnectS = 0;
        c.Forced = false;
        c.Attempts = 0;
        c.EverOnline = true;
        dev.Lease.State = LeaseStatus.Local;
        dev.Lease.Holder = DeviceTexts.TouchScreen;
        dev.Lease.Since = since;
        // 搅拌：每台一台电机经齿轮带动全部釜（2026-11-18 用户确认，内置表全部型号 shared）= 设备搅拌（样稿 d.stir）；
        // 覆盖表里若有各釜独立搅拌的型号 → 每釜同一转速（样稿把非停用通道的 rpm 都设成 d.stir.rpm）
        if (m.StirMode == StirMode.Shared) dev.Stir = new DemoStir { On = true, Rpm = f.StirRpm };
        // 气路：三台都是气体 A 开，其余与排空关（样稿 inlets / vent；接什么气由工艺决定，界面只写气路名；型号表的键见 DemoFusion.GasKey）
        string open = DemoFusion.GasKey(m, "气体 A");
        foreach (var key in m.GasKeys) dev.GasList.Add(new KeyValuePair<string, bool>(key, key == open));
        dev.ProgramList.AddRange(lib);
        var def = lib.FirstOrDefault(p => p.Id == f.ProgId);

        foreach (var slot in m.Channels)
        {
            var spec = f.Chs.FirstOrDefault(x => x.N == slot.N);
            DemoChannel ch;
            if (spec is null || !slot.Installed)
            {
                ch = new DemoChannel(slot.N, slot.Installed) { State = StateKey.Idle, TempC = 24, PressureMPa = 0.005, HeatRate = 0.1, PRate = 0.0005 };
                dev.ChannelList.Add(ch);
                continue;
            }
            double? vol = slot.N - 1 < f.Vol.Count ? f.Vol[slot.N - 1] : null;
            ch = MakeFusionChannel(dev, spec, spec.Live ? vol : null, f.StirRpm, def, now);
            dev.ChannelList.Add(ch);
            ch.HistoryList.AddRange(DemoFusion.History(dev, ch, spec, FusionRun0, now));
        }
        FusionAlarms(dev, restore: false);
        for (int i = 0; i < m.Comm.Count; i++)
        {
            var cm = m.Comm[i];
            dev.CommList.Add(new DemoComm(cm.Key, cm.Name, cm.Count) { Online = true, LastOk = AddMs(now, -JsNumber.Round(rand.Next() * 1500) - i * 120) });
        }
        SetLease(dev, DeviceTexts.LeaseCode(f.Lease), f.Lease == LeaseStatus.Remote ? DemoFusion.At(14, 5) : since);
        if (f.Session is { } sid && f.SessionAt is { } sat)
        {
            var s = new DemoSession(sid, sat);
            foreach (var mk in DemoFusion.Markers.Where(x => x.K == f.K)) s.MarkerList.Add(new DemoMarker(mk.At, mk.Text, mk.N));
            dev.Session = s;
        }
        foreach (var e in DemoFusion.Events.Where(x => x.K == f.K && x.At <= now).OrderByDescending(x => x.At))
            dev.EventList.Add(new DemoEvent("e" + _eventSeq++, e.At, e.Level, e.N, e.Text));
        return dev;
    }

    /// <summary>
    /// 一个反应釜（样稿 ch(...) + DEVS.forEach 的 sv / rpm / vol）：状态按样稿字（off = 停用且没有读数）；目标压力 0 → null；
    /// 进气阀 SV = 升压中，或在运行且在耗气（rate &gt; 0）；转速 = 设备搅拌转速（非停用通道）。
    /// </summary>
    private static DemoChannel MakeFusionChannel(DemoDevice dev, DemoFusion.Ch c, double? vol, double stirRpm, ProgramDef? def, DateTime now)
    {
        var m = dev.Model;
        var st = DemoFusion.State(c.St);
        bool live = c.Live;
        double? sp = live ? c.Sp : null;
        double? pt = live && DemoHistory.Truthy(c.Psp) ? c.Psp : null;
        var ch = new DemoChannel(c.N, true)
        {
            State = st,
            TempC = c.T ?? 24,
            SpC = sp,
            PressureMPa = c.P ?? 0.005,
            PTargetMPa = pt,
            HeaterPct = c.Heat ?? 0,
            Heating = live && Array.IndexOf(HeatingStates, st) >= 0 && sp != null,
            Cooling = st == StateKey.Cool && m.HasCooling,
            Mmol = live ? c.Gas : null,
            Rate = c.Rate > 0 ? c.Rate : null,
            VolumeMl = vol ?? 0,
            Sv = c.St == "press" || (c.Run && c.Rate > 0),
            StirOn = live && m.StirMode == StirMode.Independent,
            Rpm = live ? stirRpm : 0,
            HeatRate = Clamp(3.5 / 60, 0.03, 0.2),
            PRate = Math.Max(0.0005, (pt ?? 0.35) / 300),
            Alarm = st == StateKey.Alarm,
            BlankWhenIdle = true,
        };
        if (def != null && live && c.Step > 0 && st != StateKey.Ready) ch.Program = FusionProgram(def, c, now);
        return ch;
    }

    /// <summary>
    /// 样稿的程序进度 → 程序记录：当前步 = step − 1；步内已用 = f × 该步计划时长（Progress.StepPlanS，取整到秒）；本步计划 = 已用 + left（报警暂停的通道 = 该步计划时长）；
    /// 之前各步按计划时长往前排（Log）。已结束（done）：全部步骤排到 <see cref="DemoFusion.DoneAt"/> 为止。
    /// 这样 Progress 的 StepRemainMin = left、TotalRemainMin = left + 后面各步的估计时长（样稿 etaOf）。
    /// </summary>
    internal static ProgramRecord FusionProgram(ProgramDef def, DemoFusion.Ch c, DateTime now)
    {
        var steps = def.Steps;
        var rec = new ProgramRecord { Id = def.Id, Name = def.Name, Note = def.Note, Steps = steps };
        var log = new List<StepLogEntry>();
        if (c.St == "done")
        {
            var end = DemoFusion.DoneAt;
            var t1 = end;
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                var t0 = AddMs(t1, -Progress.StepPlanS(steps[i]) * 1000);
                log.Insert(0, new StepLogEntry(i, t0, t1));
                t1 = t0;
            }
            var lastStart = log[^1].T0;
            return rec with
            {
                StepIndex = steps.Count - 1, Status = ProgramStatus.Done, Log = log.ToArray(), StartedAt = t1, StepStartedAt = lastStart,
                StepDurS = Ms(end, lastStart) / 1000, EndedAt = end,
            };
        }
        int idx = Math.Max(0, Math.Min(c.Step, steps.Count) - 1);
        bool held = c.St == "alarm";
        double planS = Progress.StepPlanS(steps[idx]);
        double elS = Math.Round(c.F * planS);
        var stepStartedAt = AddMs(now, -elS * 1000);
        double durS = c.Left > 0 ? elS + c.Left * 60 : Math.Max(elS, planS);
        var t = stepStartedAt;
        for (int i = idx - 1; i >= 0; i--)
        {
            var t0 = AddMs(t, -Progress.StepPlanS(steps[i]) * 1000);
            log.Insert(0, new StepLogEntry(i, t0, t));
            t = t0;
        }
        return rec with
        {
            StepIndex = idx, Status = held ? ProgramStatus.Held : ProgramStatus.Running, Log = log.ToArray(), StartedAt = t,
            StepStartedAt = stepStartedAt, StepDurS = durS,
        };
    }

    /// <summary>
    /// 融合版的报警（样稿 ALARMS，全部未确认）。<paramref name="restore"/> = 参数 alarm=k:… 恢复场景默认：报警的通道回到超压报警（关进气阀、程序暂停、压力 = 样稿值）。
    /// </summary>
    private void FusionAlarms(DemoDevice dev, bool restore)
    {
        var f = DemoFusion.Devices.FirstOrDefault(x => x.K == dev.K);
        foreach (var a in DemoFusion.Alarms.Where(x => x.K == dev.K).OrderByDescending(x => x.At))
        {
            dev.AlarmList.Add(NewAlarm(a.Level, a.N, a.Code, a.Text, a.At, false));
            if (!restore || f is null || a.N is not int n) continue;
            var spec = f.Chs.FirstOrDefault(x => x.N == n);
            var ch = dev.Channel(n);
            if (spec is null || ch is null || spec.St != "alarm") continue;
            ch.State = StateKey.Alarm;
            ch.Alarm = true;
            ch.Sv = false;
            ch.PressureMPa = spec.P ?? ch.PressureMPa;
            if (ch.Program is { Status: ProgramStatus.Running } pr) ch.Program = pr with { Status = ProgramStatus.Held };
        }
    }

    /// <summary>
    /// 融合版的断线（offline=k）：断线时刻 = 现在 − <see cref="FusionLostAgoS"/> s，已试 <see cref="FusionLostAttempts"/> 次，演示强制断线（重连一直失败）；
    /// 数据停在断线时刻（读数不清零）；程序整段往前挪同样的时长（断线时刻的剩余时间 = 样稿的值）；断线之后的历史采样去掉、最后一个 = 停住的读数。
    /// 控制权不转宽限（样稿断线横条上仍是「远程」：设备按 ONLOST 保持运行）；不记事件（样稿的事件列表里没有，断线横条另外显示）。
    /// </summary>
    private void FusionOffline(DemoDevice dev)
    {
        var c = dev.Conn;
        double shiftMs = -FusionLostAgoS * 1000.0;
        var lostAt = AddMs(Now, shiftMs);
        c.State = ConnStatus.Offline;
        c.LostAt = lostAt;
        c.RetryS = RetryS;
        c.ConnectS = 0;
        c.Forced = true;
        c.Attempts = FusionLostAttempts;
        dev.DataAt = lostAt;
        foreach (var m in dev.CommList)
        {
            m.Online = false;
            if (m.LastOk is { } t && t > lostAt) m.LastOk = lostAt;
        }
        foreach (var ch in dev.ChannelList)
        {
            if (!ch.Installed) continue;
            if (ch.Program is { StartedAt: not null } p) ch.Program = ShiftProgram(p, shiftMs);
            if (ch.NoData) continue;
            var h = ch.HistoryList;
            h.RemoveAll(s => s.T >= lostAt);
            h.Add(DemoHistory.Sample(dev, ch, lostAt));
        }
    }

    /// <summary>程序的全部时刻平移 <paramref name="ms"/> 毫秒（计划时长不变）。</summary>
    private static ProgramRecord ShiftProgram(ProgramRecord p, double ms) => p with
    {
        StartedAt = p.StartedAt is { } a ? AddMs(a, ms) : null,
        StepStartedAt = p.StepStartedAt is { } s ? AddMs(s, ms) : null,
        EndedAt = p.EndedAt is { } e ? AddMs(e, ms) : null,
        Log = p.Log.Select(l => new StepLogEntry(l.I, AddMs(l.T0, ms), AddMs(l.T1, ms))).ToArray(),
    };
}
