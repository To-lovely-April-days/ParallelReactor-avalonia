using System;
using System.Collections.Generic;
using System.Linq;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.Programs;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 融合版样稿（research/main-fusion.html）的演示数据，数值逐个照抄（fusion=1，构建见 DemoFleet.Fusion.cs）：
/// <list type="bullet">
/// <item>设备与反应釜：DEVS（含给 3号机 填 16 路的 forEach、设 stir / inlets / sv 的 DEVS.forEach、设 c.vol 的 DEVS.forEach）、DEV_NET、ONLOST、VV / DEADV / CALD。</item>
/// <item>程序库：LIB（步骤名、类型、取值）+ progPlan（各步估计时长）。</item>
/// <item>报警 ALARMS、事件 ALOG（只取「今天」）、会话 SESS、曲线标记 MARKS、曲线 series(d, c)（0–90 min，RUN0 = 13:00）。</item>
/// </list>
/// 样稿里的状态字：react / heat / cool / press / alarm / ready / off / done。
/// </summary>
internal static class DemoFusion
{
    /// <summary>2026-11-15 当天的本地时刻（样稿的 '14:03:18'）。</summary>
    public static DateTime At(int h, int m, int s = 0) => new(2026, 11, 15, h, m, s, DateTimeKind.Local);

    /// <summary>已结束的通道（2号机 RV4、3号机 RV12）程序结束的时刻：曲线 70 min 处（14:10）已降回。</summary>
    public static readonly DateTime DoneAt = At(14, 10);

    /// <summary>样稿 ch(n, st, t, sp, p, psp, rpm, heat, step, f, left, gas, rate)：f = 当前步内进度，left = 本步剩余 min（设备给的），gas = 吸气量 mmol，rate = mmol/min。</summary>
    public sealed record Ch(int N, string St, double? T, double? Sp, double? P, double? Psp, double? Rpm, double? Heat, int Step, double F, double Left, double Gas, double Rate)
    {
        public bool Live => St != "off";
        /// <summary>样稿 RUN（react / heat / cool / press；报警不算）。</summary>
        public bool Run => St is "react" or "heat" or "cool" or "press";
    }

    /// <summary>一台设备（样稿 DEVS[k] + DEV_NET + ONLOST + VV + 搅拌）。</summary>
    public sealed record Dev(
        int K, string Alias, string ModelKey, double TMax, double PLim, LeaseStatus Lease, string ProgId, string? Session, DateTime? SessionAt,
        string Host, string HostName, double StirRpm, double StirAmp, string OnLost, double VesselMl, double LineMl,
        IReadOnlyList<Ch> Chs, IReadOnlyList<double?> Vol);

    public sealed record Alarm(int K, AlarmLevel Level, int? N, string Code, string Text, DateTime At);

    public sealed record Event(int K, DateTime At, EventLevel Level, int? N, string Text);

    public sealed record Marker(int K, DateTime At, string Text, int? N);

    private static Ch C(int n, string st, double? t, double? sp, double? p, double? psp, double? rpm, double? heat, int step, double f, double left, double gas, double rate) =>
        new(n, st, t, sp, p, psp, rpm, heat, step, f, left, gas, rate);

    /// <summary>样稿 DEVS[0].chs（1号机 HT-PR8，8 路）。</summary>
    private static readonly Ch[] Dev1 =
    {
        C(1, "react", 145.2, 150, 2.35, 2.50, 800, 45, 3, .40, 27, 18.4, .21),
        C(2, "heat", 96.8, 120, 1.10, 2.50, 600, 88, 1, .62, 7, 2.1, .03),
        C(3, "react", 60.0, 60, 1.38, 1.40, 800, 22, 3, .53, 21, 12.7, .12),
        C(4, "alarm", 158.4, 160, 3.55, 2.50, 0, 0, 3, .30, 0, 15.9, 0),
        C(5, "ready", 25.6, 25, 0.12, 0, 0, 0, 0, 0, 0, 0, 0),
        C(6, "react", 80.4, 80, 2.41, 2.40, 1000, 35, 3, .53, 21, 21.3, .26),
        C(7, "off", null, null, null, null, null, null, 0, 0, 0, 0, 0),
        C(8, "cool", 121.5, 40, 0.50, 0, 200, 0, 4, .7, 9, 16.2, 0),
    };

    /// <summary>样稿 DEVS[1].chs（2号机 HT-PR4）。</summary>
    private static readonly Ch[] Dev2 =
    {
        C(1, "react", 150.0, 150, 5.20, 5.20, 800, 45, 3, .22, 90, 34.6, .31),
        C(2, "heat", 112.6, 150, 0.12, 5.20, 500, 100, 1, .6, 8, 0.4, .01),
        C(3, "cool", 68.3, 40, 0.35, 0, 300, 0, 4, .45, 12, 29.8, 0),
        C(4, "done", 31.6, 25, 0.10, 0, 0, 0, 4, 1, 0, 31.2, 0),
    };

    /// <summary>样稿给 DEVS[2].chs 填 16 路的 forEach（3号机 HT-PR16）：[状态, 温度, 压力, 本步剩余]。</summary>
    private static Ch[] Dev3()
    {
        (string St, double? T, double? P, double Left)[] rows =
        {
            ("react", 80.1, 1.20, 58), ("react", 79.8, 1.19, 59), ("react", 80.3, 1.21, 60), ("react", 80.0, 1.20, 61), ("react", 79.9, 1.18, 62), ("react", 80.2, 1.20, 63),
            ("heat", 58.2, 0.005, 6), ("heat", 63.5, 0.005, 4), ("press", 80.1, 0.64, 3), ("press", 79.9, 0.88, 2), ("alarm", 80.4, 3.12, 0), ("done", 35.1, 0.006, 0),
            ("ready", 23.9, 0.005, 0), ("ready", 24.1, 0.004, 0), ("off", null, null, 0), ("off", null, null, 0),
        };
        var list = new Ch[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            var (st, t, p, left) = rows[i];
            int step = st switch { "react" => 3, "heat" => 1, "press" => 2, "alarm" => 3, "done" => 4, _ => 0 };
            bool live = st != "off";
            bool run = st is "react" or "heat" or "cool" or "press";
            double? heat = st == "heat" ? 100 : run ? 30 : live ? 0 : null;
            double gas = step >= 2 ? DemoHistory.Fixed(4 + i * .37, 1) : step != 0 ? .1 : 0;
            double rate = run && step >= 2 ? DemoHistory.Fixed(0.04 + (i % 4) * .01, 2) : 0;
            list[i] = C(i + 1, st, t, live ? 80 : null, p, live ? 1.2 : null, live ? 400 : null, heat, step, st == "done" ? 1 : .45, left, gas, rate);
        }
        return list;
    }

    /// <summary>
    /// 三台设备（样稿 DEVS、DEV_NET、ONLOST、VV、搅拌 400 rpm（3号机 300）/ 1.2 A（3号机 0.9 A）；溶液体积同样稿 c.vol：
    /// 1号机 [5, 5, 6, 5, 5, 5, —, 4]，2号机 20，3号机 5，停用的为 null）。
    /// LineMl：未标定通道用的管路死体积 —— 1号机 0.7（样稿 DEADV 的 RV1），2号机 / 3号机 = 样稿 DEADV 公式各路的平均（2.2 / 1.9）。
    /// </summary>
    public static readonly IReadOnlyList<Dev> Devices = new[]
    {
        new Dev(1, "1号机", "PR8", 200, 3.50, LeaseStatus.Remote, "A", "PT-20261115-1305", At(13, 5),
            "192.168.1.21", "hmi-01", 400, 1.2, "keep", 10, 0.7, Dev1, new double?[] { 5, 5, 6, 5, 5, 5, null, 4 }),
        new Dev(2, "2号机", "PR4", 250, 9.5, LeaseStatus.Local, "B", "PT-20261115-1250", At(12, 50),
            "192.168.1.22", "hmi-02", 400, 1.2, "keep", 100, 2.2, Dev2, new double?[] { 20, 20, 20, 20 }),
        new Dev(3, "3号机", "PR16", 200, 3.0, LeaseStatus.Local, "C", null, null,
            "192.168.1.35", "hmi-16a", 300, 0.9, "stopHeat", 25, 1.9, Dev3(), Enumerable.Repeat<double?>(5, 16).ToArray()),
    };

    /// <summary>样稿 ALARMS（全部未确认）。文字 = 标题 · 说明（界面在第一个「 · 」处分开）。</summary>
    public static readonly IReadOnlyList<Alarm> Alarms = new[]
    {
        new Alarm(1, AlarmLevel.Err, 4, "P-HIGH", "压力超上限 · 已关闭进气阀 · 程序暂停", At(14, 3, 18)),
        new Alarm(1, AlarmLevel.Warn, 2, "HEAT-SLOW", "升温偏慢 · 第 1 步已用 18 min", At(14, 1, 30)),
        new Alarm(3, AlarmLevel.Err, 11, "P-HIGH", "压力超上限 · 已关闭 SV11 · 程序暂停", At(13, 58, 42)),
    };

    /// <summary>
    /// 样稿 ALOG 里「今天」的条目（报警、已清除的报警、操作 / 程序事件）。通道事件写「RV4 压力超上限」；设备级写「来源 · 标题」（「控制权 · PC-测试工位1 获得远程控制」）。
    /// 级别：报警 hi → Err、lo → Warn；操作（kind op）→ Op；程序（kind prog）→ Info。
    /// </summary>
    public static readonly IReadOnlyList<Event> Events = new[]
    {
        new Event(1, At(14, 5, 0), EventLevel.Op, null, "控制权 · " + DemoFleet.Client + " 获得远程控制"),
        new Event(1, At(14, 3, 18), EventLevel.Err, 4, "RV4 压力超上限"),
        new Event(1, At(14, 1, 30), EventLevel.Warn, 2, "RV2 升温偏慢"),
        new Event(1, At(13, 58, 0), EventLevel.Info, 2, "RV2 程序「平行合成程序 A」开始"),
        new Event(1, At(13, 41, 30), EventLevel.Info, 8, "RV8 进入第 4 步 降温"),
        new Event(1, At(13, 5, 0), EventLevel.Op, null, "会话 · 会话 PT-20261115-1305 开始"),
        new Event(1, At(12, 55, 41), EventLevel.Op, null, "设置 · 超压报警阈值 3.40 → 3.50 MPa"),
        new Event(1, At(11, 22, 10), EventLevel.Err, 6, "RV6 超温 · 切断加热"),
        new Event(2, At(13, 30, 12), EventLevel.Info, 1, "RV1 程序「高压加氢-150」开始"),
        new Event(2, At(12, 40, 5), EventLevel.Err, null, "通讯 · 压力采集模块离线"),
        new Event(3, At(13, 58, 42), EventLevel.Err, 11, "RV11 压力超上限"),
        new Event(3, At(10, 5, 33), EventLevel.Err, null, "搅拌 · 搅拌电机故障"),
        new Event(3, At(9, 12, 0), EventLevel.Op, null, "泄漏测试 · 泄漏测试通过（16/16）"),
    };

    /// <summary>样稿 MARKS（1号机会话 PT-20261115-1305 的 3 个标记，m = 距 13:00 的分钟）。</summary>
    public static readonly IReadOnlyList<Marker> Markers = new[]
    {
        new Marker(1, At(13, 41, 30), "RV3 加料", 3),
        new Marker(1, At(13, 52, 0), "换气源 · 气体 A", null),
        new Marker(1, At(14, 3, 18), "RV4 超压", 4),
    };

    /// <summary>样稿状态字 → 状态键（off = 停用，alarm = 超压报警，done = 已结束）。</summary>
    public static StateKey State(string st) => st switch
    {
        "react" => StateKey.React,
        "heat" => StateKey.Heat,
        "cool" => StateKey.Cool,
        "press" => StateKey.Press,
        "alarm" => StateKey.Alarm,
        "ready" => StateKey.Ready,
        "done" => StateKey.Done,
        _ => StateKey.Idle,
    };

    // ———————————————————— 程序库（样稿 LIB + progPlan） ————————————————————

    /// <summary>
    /// 样稿的气路名 → 型号表里的键：内置表全部型号都是 inert / a / b（惰性气体 / 气体 A / 气体 B，接什么气由工艺决定）。
    /// 覆盖表（配置目录的 models.json）没有这几个键时按名称里的化学式找（惰性气体 → N₂，其余 → H₂），再找不到用第一路。
    /// </summary>
    public static string GasKey(ModelProfile m, string mockName)
    {
        string want = mockName switch { "惰性气体" => "inert", "气体 B" => "b", _ => "a" };
        if (m.GasInlets.Any(g => g.Key == want)) return want;
        string formula = want == "inert" ? "N₂" : "H₂";
        return (m.GasInlets.FirstOrDefault(g => g.Name.Contains(formula, StringComparison.Ordinal)) ?? m.GasInlets[0]).Key;
    }

    /// <summary>
    /// 样稿 progPlan 的写法逐步排程序：估计时长（分钟）—— 升温 |目标 − 当前温度| / 3.5 + 等待平衡；升压 3；吹扫 次数 × (充 + 排) / 60；
    /// 搅拌 / 等待 / 反应 = 设定时长；结束 max(0, 当前温度 − 先降温至) / 5。温度从 25 ℃ 起。
    /// </summary>
    private sealed class Plan
    {
        private readonly ModelProfile _m;
        private double _t = 25;
        private bool _heated;
        private double? _p;
        private string? _gas;

        public Plan(ModelProfile m) => _m = m;

        public List<ProgramStep> Steps { get; } = new();

        private double? Sp => _heated ? _t : null;

        public Plan Purge(string name, int cycles, double fillS = 3, double ventS = 5)
        {
            Steps.Add(new ProgramStep(StepKind.Purge) { Name = name, Gas = GasKey(_m, "惰性气体"), Cycles = cycles, EstMin = cycles * (fillS + ventS) / 60 });
            return this;
        }

        public Plan Heat(string name, double temp, double wait)
        {
            double est = Math.Abs(temp - _t) / 3.5 + wait;
            _t = temp;
            _heated = true;
            Steps.Add(new ProgramStep(StepKind.Heat) { Name = name, Sp = temp, End = new StepEnd(EndKind.Temp, temp, "≥"), EstMin = est });
            return this;
        }

        public Plan Stir(string name, double rpm, double min)
        {
            Steps.Add(new ProgramStep(StepKind.Stir) { Name = name, Sp = Sp, Rpm = rpm, Minutes = min, End = new StepEnd(EndKind.Time, min), EstMin = min });
            return this;
        }

        public Plan Press(string name, double p, string gas = "气体 A", bool hold = true)
        {
            _p = p;
            _gas = GasKey(_m, gas);
            Steps.Add(new ProgramStep(StepKind.Press) { Name = name, Sp = Sp, Target = p, Gas = _gas, End = new StepEnd(EndKind.Press, p, "≥"), EstMin = 3, Regulate = hold });
            return this;
        }

        public Plan Wait(string name, double max)
        {
            Steps.Add(new ProgramStep(StepKind.Wait) { Name = name, Sp = Sp, Minutes = max, End = new StepEnd(EndKind.Time, max), EstMin = max });
            return this;
        }

        public Plan React(string name, double max)
        {
            Steps.Add(new ProgramStep(StepKind.React) { Name = name, Sp = Sp, Target = _p, Gas = _gas, Minutes = max, End = new StepEnd(EndKind.Time, max), EstMin = max });
            return this;
        }

        public Plan End(string name, double cool, bool vent = false)
        {
            double est = Math.Max(0, _t - cool) / 5;
            _t = Math.Min(_t, cool);
            Steps.Add(new ProgramStep(StepKind.End) { Name = name, CoolTo = cool, Vent = vent, End = new StepEnd(EndKind.Temp, cool, "<"), EstMin = est });
            return this;
        }
    }

    private static string Note(ModelProfile m, double temp, double p, string gas, string tail) =>
        JsNumber.ToString(temp) + " ℃ · " + JsNumber.ToFixed(p, 2) + " MPa " + m.GasName(GasKey(m, gas)) + " · " + tail;

    /// <summary>样稿 LIB 按型号：HT-PR8 = A、H1、LK、OX；HT-PR4 = B；HT-PR16 = C（别的型号同 HT-PR8）。</summary>
    public static List<ProgramDef> Library(ModelProfile m)
    {
        var list = new List<ProgramDef>();
        switch (m.Key)
        {
            case "PR4":
                list.Add(new ProgramDef("B", "高压加氢-150", Note(m, 150, 5.2, "气体 A", "反应 120 min"),
                    new Plan(m).Heat("升温", 150, 10).Press("升压", 5.2).React("反应", 120).End("降温", 40).Steps));
                break;
            case "PR16":
                list.Add(new ProgramDef("C", "筛选-80 ℃", Note(m, 80, 1.2, "气体 A", "反应 60 min"),
                    new Plan(m).Heat("升温", 80, 5).Press("升压", 1.2).React("反应", 60).End("降温", 25).Steps));
                break;
            default:
                list.Add(new ProgramDef("A", "平行合成程序 A", Note(m, 150, 2.5, "气体 A", "反应 45 min"),
                    new Plan(m).Heat("升温", 150, 15).Press("升压", 2.5).React("恒温反应", 45).End("降温", 40).Steps));
                list.Add(new ProgramDef("H1", "催化加氢筛选 · 120 ℃", Note(m, 120, 3.0, "气体 A", "反应 90 min"),
                    new Plan(m).Purge("吹扫置换", 8).Heat("升温", 120, 10).Stir("搅拌", 400, 10).Press("升压", 3.0).React("反应", 90).End("降温", 35).Steps));
                list.Add(new ProgramDef("LK", "气密预检", "1.00 MPa " + m.GasName(GasKey(m, "气体 A")) + " · 保压观察 30 min",
                    new Plan(m).Purge("吹扫", 5).Press("充压", 1.0, hold: false).Wait("保压观察", 30).End("排空", 40, vent: true).Steps));
                list.Add(new ProgramDef("OX", "低温氧化 · 60 ℃", Note(m, 60, 3.4, "气体 B", "反应 240 min"),
                    new Plan(m).Heat("升温", 60, 10).Press("升压", 3.4, "气体 B").React("反应", 240).End("降温", 30).Steps));
                break;
        }
        return list;
    }

    // ———————————————————— 整机参数（样稿 plim / tmax / VV / DEADV / CALD / ONLOST / DEV_NET） ————————————————————

    /// <summary>样稿 DEADV：第 k 台第 n 路的管路死体积 mL（3号机 RV15–16 未标定 = null）。</summary>
    public static double? DeadVolume(int k, int n) => k == 3 && n > 14 ? null
        : DemoHistory.Fixed(k == 1 ? 0.4 + (n * 3 % 5) / 10.0 : 1.6 + ((k * 7 + n * 3) % 9) / 10.0, 1);

    /// <summary>
    /// 整机参数。容积：1号机 / 2号机按未标定（有效容积 null，用 LineMl）；3号机 RV1–14 已标定（样稿 CALD 2026-10-28）：有效容积 = 釜容积 + DEADV，
    /// 气相体积 = 有效容积 − 溶液，与样稿 gasV 相同。转速上限 450（样稿 STEPT.STIR），传动比 1。
    /// </summary>
    public static DemoPlant Plant(Dev f, int channelCount)
    {
        var eff = new double?[channelCount];
        var cal = new DateTime?[channelCount];
        if (f.K == 3)
        {
            for (int n = 1; n <= channelCount; n++)
            {
                if (DeadVolume(f.K, n) is not double dv) continue;
                eff[n - 1] = DemoHistory.Fixed(f.VesselMl + dv, 1);
                cal[n - 1] = new DateTime(2026, 10, 28, 0, 0, 0, DateTimeKind.Local);
            }
        }
        return new DemoPlant(f.PLim, f.TMax, 450, f.StirAmp, 1, f.VesselMl, f.LineMl, eff, cal, f.OnLost, f.HostName);
    }

    // ———————————————————— 曲线（样稿 series(d, c)） ————————————————————

    /// <summary>样稿 series 的一个点（m = 距 13:00 的分钟）：温度、压力、累计吸气量（停用的通道没有点）。</summary>
    public readonly record struct Point(double M, double T, double P, double G);

    /// <summary>
    /// 样稿 series(d, c)：由当前状态倒推 0–90 min（每 0.5 min）的温度 / 压力 / 累计吸气量，确定性随机（种子 n × 17 + k × 5 的线性同余）。
    /// 运算顺序同 JS（结果逐位相同）。停用（t = null）返回空表。
    /// </summary>
    public static List<Point> Series(int k, Ch c)
    {
        var output = new List<Point>(181);
        if (c.T is not double ct) return output;
        long s = c.N * 17 + k * 5;
        double Rnd() { s = (s * 9301 + 49297) % 233280; return s / 233280.0 - .5; }
        const double T0 = 25;
        double sp = c.Sp ?? ct;
        double cp = c.P ?? 0;
        double gs = c.Step >= 2 ? 24 + (c.N % 4) * 2 : 76;
        double tau = c.St is "cool" or "done" ? 10 : c.St == "alarm" ? 16 : 22 + (c.N % 3) * 10;
        double end = c.St == "alarm" ? 63.3 : c.St == "cool" ? 72 : 90;
        double U(double mm) => mm < gs ? 0 : 1 - Math.Exp(-(mm - gs) / tau);
        for (int j = 0; j <= 180; j++)
        {
            double m = j * .5;
            double t = ct, p = cp, g;
            switch (c.St)
            {
                case "react":
                case "alarm":
                {
                    t = m < 4 ? T0 : T0 + (sp - T0) * Math.Min(1, (1 - Math.Exp(-(m - 4) / 9)) / (1 - Math.Exp(-32.0 / 9)));
                    if (m > 36) t = sp + (ct - sp) * Math.Min(1, (m - 36) / 50);
                    double pl = c.St == "alarm" ? (DemoHistory.Truthy(c.Psp) ? c.Psp!.Value : cp) : cp;
                    p = m < 26 ? .5 : .5 + (pl - .5) * Math.Min(1, (m - 26) / 14);
                    if (c.St == "alarm" && m > 60) p = pl + (cp - pl) * Math.Min(1, (m - 60) / 3.3);
                    break;
                }
                case "heat":
                    t = m < 70 ? T0 : T0 + (ct - T0) * (m - 70) / 20;
                    p = cp * Math.Min(1, m / 90);
                    break;
                case "cool":
                    t = m < 30 ? T0 + 125 * m / 30 : m < 72 ? 150 : 150 - (150 - ct) * (m - 72) / 18;
                    p = m < 30 ? .05 : m < 72 ? 2.4 : 2.4 - (2.4 - cp) * (m - 72) / 18;
                    break;
                case "press":
                    t = m < 30 ? T0 + (sp - T0) * m / 30 : sp;
                    p = m < 60 ? .01 : cp * (m - 60) / 30;
                    break;
                case "done":
                {
                    double top = Math.Max(sp, ct);
                    t = m < 40 ? T0 + (top - T0) * Math.Min(1, m / 20) : top - (top - ct) * Math.Min(1, (m - 40) / 30);
                    p = m < 50 ? cp + .3 : cp;
                    break;
                }
            }
            g = c.Gas != 0 && U(end) > 0 ? c.Gas * U(Math.Min(m, end)) / U(end) : 0;
            double tn = t + Rnd() * .8;
            double pn = Math.Max(0, p + Rnd() * .02);
            output.Add(new Point(m, tn, pn, g));
        }
        return output;
    }

    /// <summary>曲线上的温度设定值（运行中的那几段才有）：反应 / 报警 4 min 起、升温 70 min 起、升压全程、降温 72 min 前 150 ℃ 之后 = 设定值、已结束 40 min 前。</summary>
    private static double? SpAt(Ch c, double m) => c.St switch
    {
        "react" or "alarm" => m >= 4 ? c.Sp : null,
        "heat" => m >= 70 ? c.Sp : null,
        "press" => c.Sp,
        "cool" => m < 72 ? 150 : c.Sp,
        "done" => m < 40 ? Math.Max(c.Sp ?? 0, c.T ?? 0) : null,
        _ => null,
    };

    /// <summary>曲线上的目标压力：反应 / 报警 26 min 起、升压 60 min 起（样稿 psp，0 = 没有）。</summary>
    private static double? PtAt(Ch c, double m)
    {
        double? pt = DemoHistory.Truthy(c.Psp) ? c.Psp : null;
        return c.St switch
        {
            "react" or "alarm" => m >= 26 ? pt : null,
            "press" => m >= 60 ? pt : null,
            _ => null,
        };
    }

    /// <summary>曲线上的加热输出 %（演示：升温段 100，之后保持当前输出）。</summary>
    private static double HeaterAt(Ch c, double m) => c.St switch
    {
        "react" or "alarm" => m < 4 ? 0 : m < 30 ? 100 : c.Heat ?? 0,
        "heat" => m < 70 ? 0 : 100,
        "cool" => m < 30 ? 100 : m < 72 ? 40 : 0,
        "press" => m < 30 ? 100 : c.Heat ?? 0,
        "done" => m < 20 ? 100 : m < 40 ? 30 : 0,
        _ => 0,
    };

    /// <summary>
    /// 一路的历史（DemoChannel.History）：样稿 series 的 0.5 min 点按 10 s 一格线性插值（13:00:00 – 14:30:00，541 个采样），
    /// 最后再加一个「现在」的采样 = 当前读数（同旧样稿 genHistory 的最后一个采样）。停用的通道为空表。
    /// </summary>
    public static List<DemoSample> History(DemoDevice dev, DemoChannel ch, Ch c, DateTime run0, DateTime now)
    {
        var pts = Series(dev.K, c);
        var output = new List<DemoSample>(pts.Count * 3);
        if (pts.Count == 0) return output;
        double rpm = dev.RpmOf(ch);
        int cells = (pts.Count - 1) * 3;
        for (int i = 0; i <= cells; i++)
        {
            int j = i / 3;
            double w = (i % 3) / 3.0;
            var a = pts[j];
            var b = w > 0 ? pts[j + 1] : a;
            double m = i / 6.0;
            var t = DemoHistory.AddMs(run0, i * 10000.0);
            if (t > now) break;
            output.Add(new DemoSample(t,
                DemoHistory.Fixed(a.T + (b.T - a.T) * w, 2),
                DemoHistory.Fixed(a.P + (b.P - a.P) * w, 4),
                HeaterAt(c, m), rpm,
                DemoHistory.Fixed(a.G + (b.G - a.G) * w, 3),
                SpAt(c, m), PtAt(c, m)));
        }
        var last = DemoHistory.Sample(dev, ch, now);
        if (output.Count > 0 && output[^1].T >= now) output[^1] = last;
        else output.Add(last);
        return output;
    }
}
