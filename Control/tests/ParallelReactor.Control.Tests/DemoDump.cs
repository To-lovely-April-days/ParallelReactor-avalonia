using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Guard;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 演示数据的规范文字摘要：与 MockVectors/gen-demo-vectors.js 的 dumpFleet / dumpDevice / dumpSteps 逐行同格式（改一边必须同时改另一边）。
/// 数字一律 JS 的 String(number)（JsNumber.ToString），布尔 1 / 0，缺失 '-'，时间 = 相对 DemoFleet.T0 的毫秒数。
/// 另有 <see cref="Replay"/>：按向量里的参数 + 脚本跑一遍 DemoFleet，得到与 JS 同样的整段文字。
/// </summary>
internal static class DemoDump
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static string S(double? v) => v is double d ? JsNumber.ToString(d) : "-";
    private static string S(int? v) => v is int i ? i.ToString(Inv) : "-";
    private static string S(bool b) => b ? "1" : "0";
    private static string S(string? s) => s ?? "-";
    private static string T(DateTime? t) => t is { } x ? JsNumber.ToString((x - DemoFleet.T0).Ticks / (double)TimeSpan.TicksPerMillisecond) : "-";

    public static string Fnv(IEnumerable<string> lines)
    {
        uint h = 0x811c9dc5;
        foreach (var line in lines)
        {
            foreach (char c in line) { h ^= c; h = unchecked(h * 0x01000193); }
            h ^= '\n';
            h = unchecked(h * 0x01000193);
        }
        return h.ToString("x8", Inv);
    }

    public static string Rle(IReadOnlyList<string> items)
    {
        var output = new List<string>();
        for (int i = 0; i < items.Count;)
        {
            int j = i;
            while (j < items.Count && items[j] == items[i]) j++;
            output.Add(j - i > 1 ? items[i] + "*" + (j - i) : items[i]);
            i = j;
        }
        return string.Join("|", output);
    }

    public static string Level(AlarmLevel l) => l switch { AlarmLevel.Err => "err", AlarmLevel.Warn => "warn", _ => "info" };
    public static string Level(EventLevel l) => l switch { EventLevel.Info => "info", EventLevel.Op => "op", EventLevel.Warn => "warn", _ => "err" };
    public static string Status(ProgramStatus s) => s switch
    {
        ProgramStatus.Running => "running", ProgramStatus.Held => "held", ProgramStatus.Loaded => "loaded", ProgramStatus.Done => "done",
        ProgramStatus.Aborted => "aborted", _ => "failed",
    };
    public static string ToneCode(Tone t) => t switch { Tone.Ok => "ok", Tone.Mute => "mute", Tone.Warn => "warn", Tone.Err => "err", _ => "info" };
    public static string HealthCode(DeviceHealth h) => h switch { DeviceHealth.Ok => "ok", DeviceHealth.Warn => "warn", DeviceHealth.Err => "err", DeviceHealth.Off => "off", _ => "mute" };
    public static string SegCode(SegmentState s) => s switch { SegmentState.Done => "done", SegmentState.Cur => "cur", SegmentState.Next => "next", _ => "abort" };
    public static string BarCode(StepBarState s) => s switch
    {
        StepBarState.Done => "done", StepBarState.Cur => "cur", StepBarState.Held => "held", StepBarState.Abort => "abort", _ => "pending",
    };
    public static string KindCode(DemoToastKind k) => k switch { DemoToastKind.Ok => "ok", DemoToastKind.Info => "info", DemoToastKind.Warn => "warn", _ => "err" };

    private static string SampleLine(DemoSample s) =>
        string.Join("|", T(s.T), S(s.TempC), S(s.PressureMPa), S(s.HeaterPct), S(s.Rpm), S(s.Mmol), S(s.SpC), S(s.PTargetMPa));

    /// <summary>样稿 payloadText：{n=1,sp=150}（按 n sp rpm id alias text 的顺序，没给的不写）。</summary>
    public static string PayloadText(ActionPayload p)
    {
        var parts = new List<string>();
        if (p.N != null) parts.Add("n=" + S(p.N));
        if (p.Sp != null) parts.Add("sp=" + S(p.Sp));
        if (p.Rpm != null) parts.Add("rpm=" + S(p.Rpm));
        if (p.Id != null) parts.Add("id=" + p.Id);
        if (p.Alias != null) parts.Add("alias=" + p.Alias);
        if (p.Text != null) parts.Add("text=" + p.Text);
        return "{" + string.Join(",", parts) + "}";
    }

    private static readonly DeviceAction[] ChActions =
    {
        DeviceAction.Enable, DeviceAction.Disable, DeviceAction.Start, DeviceAction.Stop, DeviceAction.HeatStart, DeviceAction.HeatStop, DeviceAction.SetSp,
        DeviceAction.ProgLoad, DeviceAction.ProgRun, DeviceAction.ProgAbort, DeviceAction.ChStirOn, DeviceAction.ChStirOff, DeviceAction.ChStirSet,
    };

    private static IEnumerable<(DeviceAction, ActionPayload)> ChWhy2(int n) => new[]
    {
        (DeviceAction.HeatStart, new ActionPayload(N: n, Sp: 999)), (DeviceAction.HeatStart, new ActionPayload(N: n)),
        (DeviceAction.SetSp, new ActionPayload(N: n, Sp: 999)), (DeviceAction.SetSp, new ActionPayload(N: n, Sp: -1)), (DeviceAction.SetSp, new ActionPayload(N: n)),
        (DeviceAction.ChStirSet, new ActionPayload(N: n, Rpm: 99999)), (DeviceAction.ChStirSet, new ActionPayload(N: n)),
        (DeviceAction.ProgLoad, new ActionPayload(N: n, Id: "nope")), (DeviceAction.ProgLoad, new ActionPayload(N: n)), (DeviceAction.ChStirOn, new ActionPayload(N: n, Rpm: 900)),
    };

    private static IEnumerable<(string Code, ActionPayload P)> DevWhy(DemoDevice d)
    {
        var list = new List<(string, ActionPayload)>
        {
            ("stirOn", ActionPayload.Empty), ("stirOff", ActionPayload.Empty), ("stirSet", new(Rpm: 600)), ("stirSet", new(Rpm: 99999)), ("stirSet", ActionPayload.Empty),
            ("runAll", ActionPayload.Empty), ("stopAll", ActionPayload.Empty), ("estop", ActionPayload.Empty), ("sessionStart", ActionPayload.Empty), ("sessionMark", new(Text: "x")),
            ("sessionEnd", ActionPayload.Empty), ("leaseRequest", ActionPayload.Empty), ("leaseCancel", ActionPayload.Empty), ("leaseRelease", ActionPayload.Empty),
            ("disconnect", ActionPayload.Empty), ("reconnect", ActionPayload.Empty),
            ("rename", new(Alias: d.Alias)), ("rename", new(Alias: "")), ("rename", new(Alias: "   ")), ("rename", new(Alias: " 新名字 ")), ("rename", new(Alias: "一二三四五六七八九")),
            ("rename", new(Alias: "一二三四五六七八")), ("rename", new(Alias: "ABCDEFGHIJKLMNOPQ")), ("rename", new(Alias: "ABCDEFGHIJKLMNOP")), ("rename", new(Alias: "1号机")),
            ("rename", new(Alias: "2号机")), ("remove", ActionPayload.Empty), ("enable", ActionPayload.Empty), ("enable", new(N: 99)), ("foo", ActionPayload.Empty),
            ("alarmAck", ActionPayload.Empty), ("alarmAck", new(Id: "nope")),
        };
        foreach (var a in d.Alarms) list.Add(("alarmAck", new ActionPayload(Id: a.Id)));
        return list;
    }

    private static readonly DemoReading[] Readings =
        { DemoReading.TempC, DemoReading.PressureMPa, DemoReading.HeaterPct, DemoReading.Rpm, DemoReading.Mmol, DemoReading.VolumeMl, DemoReading.SpC, DemoReading.PTargetMPa };

    public static void Device(DemoDevice d, List<string> l, bool brief)
    {
        var c = d.Conn;
        var le = d.Lease;
        l.Add("dev " + d.K + " " + d.Id + " " + d.Alias + " " + d.ModelKey + " " + d.Host + ":" + d.Port + " " + d.Token + " " + d.Sn + " " + d.HmiVersion + " " + d.ApiVersion + " auto=" + S(d.AutoConnect));
        l.Add(" conn " + DeviceTexts.ConnCode(c.State) + " since=" + T(c.Since) + " lost=" + T(c.LostAt) + " retry=" + c.RetryS + " connS=" + c.ConnectS + " forced=" + S(c.Forced) + " att=" + c.Attempts + " ever=" + S(c.EverOnline));
        l.Add(" lease " + DeviceTexts.LeaseCode(le.State) + " holder=" + le.Holder + " remain=" + le.RemainS + " grace=" + le.GraceS + " auto=" + T(le.AutoAt) + " since=" + T(le.Since));
        l.Add(" estop " + S(d.Estop.Active) + " at=" + T(d.Estop.At) + " by=" + S(d.Estop.By));
        l.Add(" session " + (d.Session is { } s
            ? S(s.Active) + " " + s.Id + " start=" + T(s.StartAt) + " end=" + T(s.EndAt) + " " + string.Join(";", s.Markers.Select(m => T(m.At) + "|" + S(m.N) + "|" + m.Text))
            : "-"));
        l.Add(" sessions " + string.Join(",", d.Sessions.Select(x => x.Id + "@" + T(x.StartAt) + "~" + T(x.EndAt) + "#" + x.Markers.Count)));
        l.Add(" stir " + (d.Stir is { } st ? S(st.On) + " " + S(st.Rpm) : "-"));
        l.Add(" gas " + string.Join(",", d.Gas.Select(g => g.Key + "=" + S(g.Value))));
        l.Add(" programs " + string.Join(",", d.Programs.Select(p => p.Id + ":" + p.Name + ":" + p.Steps.Count)));
        l.Add(" dataAt " + T(d.DataAt));
        foreach (var ch in d.Channels)
        {
            l.Add(" ch " + ch.N + " inst=" + S(ch.Installed) + " st=" + StateKeys.Code(ch.State) + " t=" + S(ch.TempC) + " sp=" + S(ch.SpC) + " p=" + S(ch.PressureMPa) + " pt=" + S(ch.PTargetMPa) +
                " h=" + S(ch.HeaterPct) + " heating=" + S(ch.Heating) + " cooling=" + S(ch.Cooling) + " mmol=" + S(ch.Mmol) + " vol=" + S(ch.VolumeMl) + " sv=" + S(ch.Sv) +
                " stirOn=" + S(ch.StirOn) + " rpm=" + S(ch.Rpm) + " hr=" + S(ch.HeatRate) + " pr=" + S(ch.PRate) + " alarm=" + S(ch.Alarm));
            if (ch.Program is { } p)
                l.Add(" prog " + ch.N + " " + p.Id + " " + p.Name + " note=" + S(p.Note) + " idx=" + p.StepIndex + " " + Status(p.Status) + " start=" + T(p.StartedAt) + " stepStart=" + T(p.StepStartedAt) +
                    " dur=" + S(p.StepDurS) + " end=" + T(p.EndedAt) + " log=" + string.Join(",", p.Log.Select(x => x.I + ":" + T(x.T0) + ":" + T(x.T1))));
            var h = ch.History;
            l.Add(" hist " + ch.N + " " + h.Count + " " + Fnv(h.Select(SampleLine)) + (h.Count > 0 ? " first=" + SampleLine(h[0]) + " last=" + SampleLine(h[^1]) : ""));
        }
        foreach (var a in d.Alarms)
            l.Add(" alarm " + a.Id + " " + Level(a.Level) + " ch=" + S(a.Ch) + " " + a.Code + " at=" + T(a.At) + " acked=" + S(a.Acked) + " by=" + S(a.AckBy) + " demo=" + S(a.Demo) + " " + a.Text);
        foreach (var e in d.Events) l.Add(" event " + e.Id + " " + T(e.At) + " " + Level(e.Level) + " n=" + S(e.N) + " " + e.Text);
        foreach (var m in d.Comm) l.Add(" comm " + m.Key + " " + m.Name + " " + m.Count + " online=" + S(m.Online) + " last=" + T(m.LastOk));

        var li = d.LeaseInfo;
        var ci = d.ConnInfo;
        l.Add(" lease-info " + string.Join("|", li.Code, li.Text, li.Short, ToneCode(li.Tone), li.Holder, li.BadgeShort));
        l.Add(" conn-info " + string.Join("|", ci.Code, ci.Text, ci.Short, ToneCode(ci.Tone), S(ci.First)));
        l.Add(" health " + HealthCode(d.Health) + " data=" + d.DataText + " estop=" + d.EstopText);
        var k = d.Counts;
        var t1 = d.AlarmCounts();
        var t2 = d.AlarmCounts(all: true);
        l.Add(" counts " + string.Join(" ", k.Installed, k.Running, k.Enabled, k.Alarms, k.Unacked, k.ErrAlarms) + " tally=" + t1.Err + "/" + t1.Warn + "/" + t1.Total +
            " all=" + t2.Err + "/" + t2.Warn + "/" + t2.Total + " text=" + d.AlarmText() + " allText=" + d.AlarmText(all: true, none: "无"));
        foreach (var g in new[] { GuardKind.None, GuardKind.Conn, GuardKind.Safe, GuardKind.Control }) l.Add(" guard " + DeviceActions.KindCode(g) + " " + d.GuardText(g));
        foreach (var ch in d.Channels)
        {
            l.Add(" chd " + ch.N + " key=" + StateKeys.Code(d.StateKeyOf(ch)) + " text=" + d.StateText(ch) + " word=" + d.StateWord(ch) + " run=" + S(ch.IsRunning) +
                " rpmOf=" + S(d.RpmOf(ch)) + " read=" + string.Join("|", Readings.Select(r => S(d.Reading(ch, r)))) +
                " series=" + d.Series(ch.N, DemoReading.TempC, 10).Count + "/" + d.Series(ch.N, DemoReading.PressureMPa).Count);
            var pr = d.Progress(ch);
            if (pr != null)
            {
                l.Add(" progress " + ch.N + " " + string.Join("|", pr.Name, Status(pr.Status), pr.StatusText, S(pr.Offline), S(pr.WaitGas), pr.GasName, pr.K, pr.N, pr.StepName, S(pr.Overdue),
                    pr.RemainText, pr.RemainShort, S(pr.StepRemainS), S(pr.StepRemainMin), S(pr.TotalRemainMin), S(pr.Within), S(pr.Frac)));
                l.Add(" progtext " + ch.N + " " + d.ProgText(ch) + " / " + d.ProgText(ch, shortForm: true));
                l.Add(" seg " + ch.N + " " + string.Join(";", d.Segments(ch).Select(sg => sg.I + ":" + SegCode(sg.State) + ":" + T(sg.T0) + ":" + T(sg.T1) + ":" + S(sg.WaitGas) + ":" + sg.Name)));
                var prog = ch.Program!;
                l.Add(" bar " + ch.N + " " + string.Join(",", Progress.Bar(prog, pr, d.Online).Select(b => BarCode(b.State) + ":" + JsNumber.ToFixed(b.FillPct, 0) + ":" + S(b.Pulse))));
                l.Add(" marks " + ch.N + " " + string.Join(";", Enumerable.Range(0, prog.Steps.Count).Select(i => SegCode(Progress.StepState(prog, i)) + ":" + Progress.StepMark(prog, i, pr))));
            }
            else l.Add(" progtext " + ch.N + " " + d.ProgText(ch) + " / " + d.ProgText(ch, shortForm: true));
            if (brief) continue;
            var pp = new ActionPayload(N: ch.N, Sp: 150, Rpm: 600, Id: "h2-std");
            l.Add(" why " + ch.N + " " + Rle(ChActions.Select(a => d.Why(a, pp)).ToArray()));
            l.Add(" why2 " + ch.N + " " + Rle(ChWhy2(ch.N).Select(x => d.Why(x.Item1, x.Item2)).ToArray()));
        }
        if (brief) return;
        int nx = d.Channels.Count + 1;
        l.Add(" why " + nx + " " + Rle(ChActions.Select(a => d.Why(a, new ActionPayload(N: nx, Sp: 150, Rpm: 600))).ToArray()));
        var facts = d.Facts();
        l.Add(" whyd " + Rle(DevWhy(d).Select(x => x.Code + PayloadText(x.P) + "=" + ActionGuard.Why(facts, x.Code, x.P)).ToArray()));
    }

    public static void Steps(DemoDevice d, List<string> l)
    {
        var first = d.Channels.FirstOrDefault(c => c.Installed);
        foreach (var p in d.Programs)
            for (int i = 0; i < p.Steps.Count; i++)
            {
                var s = p.Steps[i];
                var c1 = StepText.Cond(s, d.Model);
                var c0 = StepText.Cond(s);
                string Secs(int? n) => string.Join(";", StepText.Sections(d.Model, n, s).Select(x => string.Join("/", x.Dev, x.Mode, x.Label, x.Value, S(x.Tip))));
                l.Add("step " + d.ModelKey + " " + p.Id + "/" + i + " name=" + StepText.Name(s) + " text=" + StepText.Summary(d.Model, s) + " cond=" + c1.Label + "|" + c1.Value +
                    " cond0=" + c0.Label + "|" + c0.Value + " secs=" + Secs(first?.N) + " secs0=" + Secs(null));
            }
    }

    public static void Fleet(DemoFleet f, List<string> l, bool brief = false)
    {
        l.Add("now " + T(f.Now) + " tick " + f.TickCount + " frozen " + S(f.Frozen) + " stress " + f.Stress);
        foreach (var d in f.Devices) Device(d, l, brief);
        var t = f.Totals();
        l.Add("totals " + string.Join(" ", t.Devices, t.Connected, t.Remote, t.Grace, t.Alarms) + " " + string.Join(",", t.Sessions.Select(s => s.Device.K + ":" + s.Id)));
    }

    // ———————————————————— 回放向量里的场景 ————————————————————

    public static IReadOnlyList<KeyValuePair<string, string>> Pairs(JsonElement arr) =>
        arr.EnumerateArray().Select(kv => new KeyValuePair<string, string>(kv[0].GetString()!, kv[1].GetString()!)).ToArray();

    private static ActionPayload Payload(JsonElement o)
    {
        if (o.ValueKind != JsonValueKind.Object) return ActionPayload.Empty;
        int? n = o.TryGetProperty("n", out var nv) ? nv.GetInt32() : null;
        double? sp = o.TryGetProperty("sp", out var spv) ? spv.GetDouble() : null;
        double? rpm = o.TryGetProperty("rpm", out var rv) ? rv.GetDouble() : null;
        string? id = o.TryGetProperty("id", out var iv) ? iv.GetString() : null;
        string? alias = o.TryGetProperty("alias", out var av) ? av.GetString() : null;
        string? text = o.TryGetProperty("text", out var tv) ? tv.GetString() : null;
        return new ActionPayload(n, sp, rpm, id, alias, text);
    }

    private static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) ? v.GetString() : null;

    /// <summary>按向量里的初始参数 + 脚本跑一遍，返回与 JS 同格式的全部文字（含脚本日志与提示）。</summary>
    public static List<string> Replay(JsonElement scenario, out DemoFleet fleet)
    {
        var l = new List<string>();
        // 向量是样稿 JS 算出来的，样稿的「全部停止」不停搅拌：这里固定 false，不跟 ControlOptions.StopAllStopsStirring 走
        // （用户把默认改成连搅拌一起停时，向量照样对得上样稿，改默认仍然只改那一行）
        var f = new DemoFleet(DemoParams.FromPairs(Pairs(scenario.GetProperty("params")))) { StopAllStopsStirring = false };
        f.Toast += (_, e) => l.Add("toast " + KindCode(e.Kind) + " " + e.Text);
        foreach (var step in scenario.GetProperty("script").EnumerateArray())
        {
            string op = step[0].GetString()!;
            switch (op)
            {
                case "tick":
                    int n = step[1].GetInt32();
                    for (int i = 0; i < n; i++) f.Tick();
                    l.Add("ticked " + n);
                    break;
                case "act":
                {
                    int k = step[1].GetInt32();
                    string code = step[2].GetString()!;
                    var p = Payload(step[3]);
                    Assert(DeviceActions.TryParse(code, out var a), "unknown action " + code);
                    var r = f.Act(f.Device(k)!, a, p);
                    l.Add("act " + k + " " + code + " " + PayloadText(p) + " = " + S(r.Ok));
                    break;
                }
                case "actMany":
                {
                    var keys = step[1].EnumerateArray().Select(x => x.GetInt32()).ToArray();
                    string code = step[2].GetString()!;
                    Assert(DeviceActions.TryParse(code, out var a), "unknown action " + code);
                    int ok = f.ActMany(keys, a, Payload(step[3]));
                    l.Add("actMany " + string.Join(",", keys) + " " + code + " = " + ok);
                    break;
                }
                case "add":
                {
                    var o = step[1];
                    var d = f.AddDevice(new DemoNewDevice(Str(o, "host"), o.TryGetProperty("port", out var pv) ? pv.GetInt32() : null, Str(o, "token"), Str(o, "alias"),
                        o.TryGetProperty("autoConnect", out var acv) ? acv.GetBoolean() : null, Str(o, "modelKey")));
                    l.Add("add " + d.K + " " + d.Alias);
                    break;
                }
                case "edit":
                {
                    int k = step[1].GetInt32();
                    var o = step[2];
                    string r = f.EditDevice(f.Device(k)!, new DemoDeviceEdit(Str(o, "alias"), Str(o, "host"), o.TryGetProperty("port", out var pv) ? pv.GetInt32() : null, Str(o, "token"),
                        o.TryGetProperty("autoConnect", out var acv) ? acv.GetBoolean() : null));
                    l.Add("edit " + k + " = " + r);
                    break;
                }
                case "apply":
                    f.Apply(DemoParams.FromPairs(Pairs(step[1])));
                    l.Add("applied");
                    break;
                case "dump":
                    Fleet(f, l, step.GetArrayLength() > 1 && step[1].GetString() == "brief");
                    break;
                default:
                    throw new InvalidOperationException("unknown op " + op);
            }
        }
        Fleet(f, l);
        fleet = f;
        return l;
    }

    private static void Assert(bool ok, string what)
    {
        if (!ok) throw new InvalidOperationException(what);
    }
}
