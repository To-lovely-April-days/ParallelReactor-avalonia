using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

public sealed partial class DemoFleet
{
    /// <summary>
    /// 执行操作（样稿 act）：先 <see cref="ActionGuard.Why"/>，不可用时提示原因（quiet 时不提示）并返回 Ok = false；
    /// 可用时改状态、记一条事件、提示「1号机 · …」、触发 <see cref="Changed"/>（State）。停止类在「本来就没在运行」时照样执行，结果 Info = true
    /// （提示为 info 语气：「RV4 未在运行 · 已发送停止」）。
    /// </summary>
    public DemoActResult Act(DemoDevice d, DeviceAction action, ActionPayload? payload = null, bool quiet = false)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        var p = payload ?? ActionPayload.Empty;
        string r = d.Why(action, p);
        if (r.Length > 0)
        {
            if (!quiet) RaiseToast(r, DemoToastKind.Warn);
            return new DemoActResult(false, r);
        }
        var (msg, info) = Run(d, action, p);
        var lvl = info ? EventLevel.Op
            : action is DeviceAction.Estop or DeviceAction.StopAll or DeviceAction.Stop or DeviceAction.ProgAbort or DeviceAction.Disable ? EventLevel.Warn : EventLevel.Op;
        if (action != DeviceAction.Remove) Log(d, lvl, p.N, msg);
        if (!quiet)
            RaiseToast((action == DeviceAction.Remove || msg.StartsWith(d.Alias, StringComparison.Ordinal) ? "" : d.Alias + " · ") + msg,
                info ? DemoToastKind.Info : action == DeviceAction.Estop ? DemoToastKind.Err : lvl == EventLevel.Warn ? DemoToastKind.Warn : DemoToastKind.Ok);
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d, action));
        if (action == DeviceAction.Remove) Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.Devices));
        return new DemoActResult(true, msg, info);
    }

    /// <summary>对多台设备执行同一操作（样稿 actMany：急停 / 全部停止对话框），不逐台提示；返回执行了的台数。</summary>
    public int ActMany(IEnumerable<int> keys, DeviceAction action, ActionPayload? payload = null)
    {
        int ok = 0;
        foreach (int k in keys.ToArray())
            if (Device(k) is { } d && Act(d, action, payload, quiet: true).Ok) ok++;
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, null, action));
        return ok;
    }

    /// <summary>
    /// 添加设备（样稿 addDevice）：从未连上的新设备（没有读数、程序、历史和事件，只有「已添加 …」），状态「正在连接…」，2 s 后视为连上。
    /// 别名不给时取第一个没被占用的「k号机」；型号不给时 PR8。
    /// </summary>
    public DemoDevice AddDevice(DemoNewDevice o)
    {
        if (o is null) throw new ArgumentNullException(nameof(o));
        int k = _devices.Count == 0 ? 1 : _devices.Max(x => x.K) + 1;
        var d = MakeDevice(k, string.IsNullOrEmpty(o.ModelKey) ? "PR8" : o.ModelKey, 23,
            new MakeOpts(Alias: string.IsNullOrEmpty(o.Alias) ? FreeAlias() : o.Alias, Host: o.Host, Port: o.Port is int port && port != 0 ? port : 8080,
                Token: o.Token, AutoConnect: o.AutoConnect, Offline: false, Blank: true));
        var c = d.Conn;
        c.State = ConnStatus.Connecting;
        c.Since = Now;
        c.LostAt = null;
        c.RetryS = RetryS;
        c.ConnectS = ConnectS;
        c.Forced = false;
        c.Attempts = 0;
        c.EverOnline = false;
        d.DataAt = null;
        d.AlarmList.Clear();
        d.Session = null;
        d.EventList.Clear();
        foreach (var m in d.CommList) { m.Online = false; m.LastOk = null; }
        foreach (var ch in d.ChannelList) ch.HistoryList.Clear();
        SetLease(d, "local");
        _devices.Add(d);
        Log(d, EventLevel.Info, null, "已添加 " + d.Host + ":" + d.Port + " · 正在连接");
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.Devices, d));
        return d;
    }

    /// <summary>编辑设备（样稿 editDevice）：返回 '' 或别名错误；地址或端口变了且在线 → 重新连接。</summary>
    public string EditDevice(DemoDevice d, DemoDeviceEdit o)
    {
        if (d is null) throw new ArgumentNullException(nameof(d));
        if (o is null) throw new ArgumentNullException(nameof(o));
        string err = AliasError(o.Alias, d);
        if (err.Length > 0) return err;
        bool moved = (!string.IsNullOrEmpty(o.Host) && o.Host != d.Host) || (o.Port is int pt && pt != 0 && pt != d.Port);
        d.Alias = ActionGuard.JsTrim(o.Alias ?? "");
        if (!string.IsNullOrEmpty(o.Host)) d.Host = o.Host;
        if (o.Port is int port && port != 0) d.Port = port;
        if (!string.IsNullOrEmpty(o.Token)) d.Token = o.Token;
        if (o.AutoConnect is bool ac) d.AutoConnect = ac;
        Log(d, EventLevel.Op, null, "设备设置已修改" + (moved ? " · 新地址 " + d.Host + ":" + d.Port : ""));
        if (moved && d.Conn.State == ConnStatus.Online) { d.Conn.State = ConnStatus.Connecting; d.Conn.ConnectS = ConnectS; d.Conn.Forced = false; }
        Changed?.Invoke(this, new DemoChangedEventArgs(DemoChangeKind.State, d));
        return "";
    }

    /// <summary>别名检查（样稿 aliasError）：'' = 可用。<paramref name="except"/> = 正在改名的那台（不跟自己比）。</summary>
    public string AliasError(string? alias, DemoDevice? except) =>
        ActionGuard.AliasError(alias, _devices.Where(x => x != except).Select(x => new PeerDevice(x.Alias, x.Host)));

    /// <summary>第一个没被占用的「k号机」（样稿 freeAlias）。</summary>
    public string FreeAlias()
    {
        for (int i = 1; ; i++)
        {
            string a = i + "号机";
            if (!_devices.Any(d => d.Alias == a)) return a;
        }
    }

    // ———————————————————— 各操作的效果（样稿 RUN） ————————————————————

    private static readonly Regex SnYear = new("-[0-9]{4}-", RegexOptions.CultureInvariant);

    private (string Msg, bool Info) Run(DemoDevice d, DeviceAction action, ActionPayload p)
    {
        var now = Now;
        var ch = p.N is int n ? d.Channel(n) : null;
        string rv = "RV" + p.N;
        switch (action)
        {
            case DeviceAction.Enable:
                ch!.State = StateKey.Ready;
                return (rv + " 已启用", false);
            case DeviceAction.Disable:
            {
                var was = ch!.State;
                StopChannel(ch);
                ch.State = StateKey.Idle;
                return was == StateKey.Idle ? (rv + " 本来就是停用 · 已发送停用", true) : (rv + " 已停用", false);
            }
            case DeviceAction.Start:
                if (ActionGuard.IsRerunnable(ch!.Program?.Status))
                {
                    RunProgramNow(d, ch);
                    return (rv + " 已启动 · 程序「" + ch.Program!.Name + "」", false);
                }
                ch.Heating = true;
                ch.SpC ??= 80;
                ch.State = StateKey.Heat;
                return (rv + " 已启动 · 加热至 " + JsNumber.ToString(ch.SpC.Value) + " ℃", false);
            case DeviceAction.Stop:
            {
                bool idle = !ch!.IsRunning && !ch.Heating;
                StopChannel(ch);
                return idle ? (rv + " 未在运行 · 已发送停止", true) : (rv + " 已停止", false);
            }
            case DeviceAction.HeatStart:
                if (p.Sp is double sp) ch!.SpC = sp;
                ch!.Heating = true;
                if (!ch.IsRunning) ch.State = StateKey.Heat;
                return (rv + " 开始加热 · 设定 " + Num(ch.SpC) + " ℃", false);
            case DeviceAction.HeatStop:
                if (!ch!.Heating) return (rv + " 未在加热 · 已发送停止加热", true);
                ch.Heating = false;
                ch.HeaterPct = 0;
                if (ch.Program is { Status: ProgramStatus.Running } hp) ch.Program = hp with { Status = ProgramStatus.Held };
                if (ch.State == StateKey.Heat) ch.State = StateKey.Ready;
                return (rv + " 已停止加热", false);
            case DeviceAction.SetSp:
                ch!.SpC = p.Sp;
                return (rv + " 设定温度 " + Num(ch.SpC) + " ℃", false);
            case DeviceAction.ProgLoad:
            {
                string? id = !string.IsNullOrEmpty(p.Id) ? p.Id : ch!.Program?.Id;
                var def = d.ProgramList.FirstOrDefault(x => x.Id == id) ?? d.ProgramList[0];
                ch!.Program = MakeProgram(def, new ProgSpec(def.Id, Status: ProgramStatus.Loaded), null, now);
                if (ch.State == StateKey.Idle) ch.State = StateKey.Ready;
                return ("程序「" + def.Name + "」已下发到 " + rv, false);
            }
            case DeviceAction.ProgRun:
                RunProgramNow(d, ch!);
                return (rv + " 程序「" + ch!.Program!.Name + "」开始运行", false);
            case DeviceAction.ProgAbort:
                if (ch!.Program is not { Status: ProgramStatus.Running or ProgramStatus.Held }) return (rv + " 没有运行中的程序 · 已发送中止", true);
                StopChannel(ch);
                return (rv + " 程序已中止", false);
            case DeviceAction.StirOn:
                d.Stir!.On = true;
                return ("共用搅拌已启动 · " + JsNumber.ToString(d.Stir.Rpm) + " rpm", false);
            case DeviceAction.StirOff:
                if (!d.Stir!.On) return ("共用搅拌未运行 · 已发送停止", true);
                d.Stir.On = false;
                return ("共用搅拌已停止", false);
            case DeviceAction.StirSet:
                d.Stir!.Rpm = JsNumber.Round(p.Rpm ?? double.NaN);
                return ("共用搅拌转速 " + JsNumber.ToString(d.Stir.Rpm) + " rpm", false);
            case DeviceAction.ChStirOn:
                ch!.StirOn = true;
                if (p.Rpm is double r1) ch.Rpm = JsNumber.Round(r1);
                return (rv + " 搅拌已启动 · " + JsNumber.ToString(ch.Rpm) + " rpm", false);
            case DeviceAction.ChStirOff:
                if (!ch!.StirOn) return (rv + " 搅拌未运行 · 已发送停止", true);
                ch.StirOn = false;
                return (rv + " 搅拌已停止", false);
            case DeviceAction.ChStirSet:
                ch!.Rpm = JsNumber.Round(p.Rpm ?? double.NaN);
                return (rv + " 转速 " + JsNumber.ToString(ch.Rpm) + " rpm", false);
            case DeviceAction.RunAll:
            {
                int k = 0;
                foreach (var c in d.ChannelList)
                    if (c.Installed && c.State != StateKey.Idle && !c.IsRunning && ActionGuard.IsRerunnable(c.Program?.Status)) { RunProgramNow(d, c); k++; }
                return ("已启动 " + k + " 个反应釜", false);
            }
            case DeviceAction.StopAll:
            {
                int k = 0;
                foreach (var c in d.ChannelList)
                    if (c.Installed && (c.IsRunning || c.Heating)) { StopChannel(c); k++; }
                return k > 0 ? ("已停止 " + k + " 个反应釜", false) : ("没有运行中的反应釜 · 已发送全部停止", true);
            }
            case DeviceAction.Estop:
                DoEstop(d);
                return ("急停已发送 · 复位需在触屏操作", false);
            case DeviceAction.SessionStart:
            {
                // 会话号带设备后缀（型号 + 序号），同一分钟在多台设备上开始也不会重号：PT-20261108-1012-PR4-0003
                string id = "PT-" + Fmt.Date(now).Replace("-", "") + "-" + Fmt.Pad2(now.Hour) + Fmt.Pad2(now.Minute) + "-" + SnYear.Replace(d.Sn, "-", 1);
                d.Session = new DemoSession(id, now);
                return ("会话 " + id + " 开始记录", false);
            }
            case DeviceAction.SessionMark:
            {
                string text = !string.IsNullOrEmpty(p.Text) ? p.Text : p.N is int mn && mn != 0 ? "RV" + mn + " 标记" : "标记";
                d.Session!.MarkerList.Add(new DemoMarker(now, text, p.N is int nn && nn != 0 ? nn : null));
                return ("已打标记「" + text + "」", false);
            }
            case DeviceAction.SessionEnd:
            {
                var s = d.Session!;
                s.Active = false;
                s.EndAt = now;
                d.SessionList.Insert(0, s);
                d.Session = null;
                return ("会话 " + s.Id + " 已结束", false);
            }
            case DeviceAction.AlarmAck:
            {
                var a = d.AlarmList.First(x => x.Id == p.Id);
                a.Acked = true;
                a.AckBy = Client;
                string what = a.Level != AlarmLevel.Err ? "提醒" : a.Code == "P-HIGH" ? "超压报警" : a.Code == "ESTOP" ? "急停报警" : "报警";
                return ((a.Ch is int ac ? "RV" + ac + " " : "") + what + "已确认", false);
            }
            case DeviceAction.LeaseRequest:
                SetLease(d, "requested");
                d.Lease.RemainS = LeaseWaitS;
                if (AutoConfirmLease) d.Lease.AutoAt = AddMs(now, AutoConfirmS * 1000);
                return ("已申请控制权 · 等待触屏确认", false);
            case DeviceAction.LeaseCancel:
                SetLease(d, "local");
                return ("已取消控制权申请", false);
            case DeviceAction.LeaseRelease:
                SetLease(d, "local");
                return ("控制权已释放 · 回到本地只读", false);
            case DeviceAction.Disconnect:
            {
                var held = d.Lease.State;
                if (held != LeaseStatus.Local) SetLease(d, "local");
                d.Conn.State = ConnStatus.Disconnected;
                d.Conn.Forced = false;
                d.Conn.LostAt = now;
                if (d.Conn.EverOnline) d.DataAt = now;
                foreach (var m in d.CommList) m.Online = false;
                return ("已断开连接" + (held is LeaseStatus.Remote or LeaseStatus.Lost ? " · 控制权已释放" : held == LeaseStatus.Requested ? " · 控制权申请已取消" : ""), false);
            }
            case DeviceAction.Reconnect:
                d.Conn.State = ConnStatus.Connecting;
                d.Conn.ConnectS = ConnectS;
                d.Conn.Forced = false;
                return ("正在重连…", false);
            case DeviceAction.Rename:
            {
                string old = d.Alias;
                d.Alias = ActionGuard.JsTrim(p.Alias ?? "");
                return ("已重命名（原名 " + old + "）", false);
            }
            case DeviceAction.Remove:
                if (d.Lease.State != LeaseStatus.Local) SetLease(d, "local");
                _devices.Remove(d);
                return ("已移除 " + d.Alias, false);
            default:
                return ("", false);
        }
    }

    /// <summary>样稿 stopChannel：程序中止、关加热 / 进气 / 冷却；运行中（含报警）→ 已停止。</summary>
    private void StopChannel(DemoChannel ch)
    {
        if (ch.Program is { Status: ProgramStatus.Running or ProgramStatus.Held } p) ch.Program = p with { Status = ProgramStatus.Aborted, EndedAt = Now };
        ch.Heating = false;
        ch.HeaterPct = 0;
        ch.Sv = false;
        ch.Cooling = false;
        if (ch.IsRunning) ch.State = StateKey.Stopped;
        if (ch.State == StateKey.Alarm) ch.State = StateKey.Stopped;
    }

    /// <summary>样稿 runProgramNow：从第 1 步重新开始这个程序。</summary>
    private void RunProgramNow(DemoDevice d, DemoChannel ch)
    {
        var cur = ch.Program!;
        var def = d.ProgramList.FirstOrDefault(x => x.Id == cur.Id) ?? new ProgramDef(cur.Id ?? "", cur.Name, cur.Note ?? "", cur.Steps);
        ch.Program = MakeProgram(def, new ProgSpec(def.Id, Status: ProgramStatus.Running), null, Now);
        var first = ch.Program.Steps.Count > 0 ? ch.Program.Steps[0] : null;
        if (first is null) return;
        ch.State = StateForStep(first);
        if (first.Sp is double sp) { ch.SpC = sp; ch.Heating = true; }
    }

    private static string Num(double? v) => v is double d ? JsNumber.ToString(d) : "null";
}
