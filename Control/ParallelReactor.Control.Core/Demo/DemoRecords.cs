using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Records;

namespace ParallelReactor.Control.Core.Demo;

/// <summary>
/// 记录页的演示数据（融合版 F7，样稿 main-fusion.html 的 SESS / rcGas / rcData 历史分支 / 标记 / 数据文件，逐项移植）：
/// <list type="bullet">
/// <item>七条会话（今天 3 条、昨天 1 条、更早 3 条），设备号、名字、程序、操作员、创建者、通道、开始、时长、状态、标记数、报警数、说明、提醒同样稿。</item>
/// <item>进行中的两条（1号机 PT-20261115-1305、2号机 PT-20261115-1250）的曲线不在这里：用设备的历史缓存（与主页同一份演示曲线，见 DemoFusion.History）。</item>
/// <item>历史会话的曲线按会话号确定性生成（样稿的线性同余 (k × 9301 + 49297) mod 233280，种子 = 会话号长度 × 7 + 设备号 × 13 + 5）：
///   吸气类为饱和曲线 G = g × (1 − e^(−(m − t0)/τ)) / 归一（t0 = 时长 × 0.22–0.28，τ = 时长 × 0.12–0.30），温度 / 压力按程序的目标值分段；
///   保压类按压降线性下降（0.15–1.05 psi/h）+ 噪声。各路吸气终值同样稿 rcGas（种子 = 会话号长度 × 7 + 设备号，14–24 mmol）。</item>
/// </list>
/// 运算顺序同 JS（随机数的取用顺序一致，结果逐点相同）。
/// </summary>
public static class DemoRecords
{
    /// <summary>样稿 SESS 的一项（day = 「11-15」，start = 「13:05」，dur = 「1 h 25 min」）。</summary>
    private sealed record S(string Id, int Dev, string Name, string Prog, string Op, string By, int[] Chs, string Day, string Start, string Dur,
        RecState State, int Marks, int Alarms, string? Note = null, string? Warn = null, bool Leak = false);

    private static int[] Range(int n) => Enumerable.Range(1, n).ToArray();

    private static readonly S[] Sess =
    {
        new("PT-20261115-1305", 1, "平行合成 · 批次 7", "平行合成程序 A", "张工", DemoFleet.Client, new[] { 1, 2, 3, 4, 5, 6, 8 }, "11-15", "13:05", "1 h 25 min", RecState.Active, 3, 2),
        new("PT-20261115-1250", 2, "加氢放大 · 第 2 轮", "高压加氢-150", "王工", "触屏", new[] { 1, 2, 3, 4 }, "11-15", "12:50", "1 h 40 min", RecState.Active, 1, 1),
        new("LK-20261115-0912", 3, "泄漏测试", "—", "李工", "触屏", Range(16), "11-15", "09:12", "42 min", RecState.Ended, 0, 0, Leak: true),
        new("PT-20261114-0915", 1, "平行合成 · 批次 6", "平行合成程序 A", "张工", DemoFleet.Client, Range(8), "11-14", "09:15", "4 h 05 min", RecState.Ended, 5, 0),
        new("PT-20261113-1402", 3, "筛选 · 80 ℃ 第 1 组", "筛选-80 ℃", "李工", "触屏", Range(12), "11-13", "14:02", "2 h 18 min", RecState.Interrupted, 2, 1,
            Note: "断电重启 · 会话中断，数据保留到 16:20"),
        new("PT-20261112-0930", 1, "催化加氢筛选 · 120 ℃", "催化加氢筛选 · 120 ℃", "李工", DemoFleet.Client, new[] { 1, 2, 3, 4 }, "11-12", "09:30", "2 h 51 min", RecState.Ended, 2, 1,
            Warn: "RV3 升温超时（提醒）"),
        new("PT-20261110-1600", 1, "气密预检", "气密预检", "张工", "触屏", Range(8), "11-10", "16:00", "36 min", RecState.Ended, 0, 0),
    };

    /// <summary>样稿 LIB 里各程序的升温目标 ℃ 和升压目标 MPa（rcData 的 tgtT / tgtP；没有升温步 25 ℃，没有程序 0.35 MPa）。</summary>
    private static (double T, double P) Targets(string prog) => prog switch
    {
        "平行合成程序 A" => (150, 2.5),
        "催化加氢筛选 · 120 ℃" => (120, 3.0),
        "气密预检" => (25, 1.0),
        "低温氧化 · 60 ℃" => (60, 3.4),
        "高压加氢-150" => (150, 5.2),
        "筛选-80 ℃" => (80, 1.2),
        _ => (25, .35),
    };

    private static DateTime Parse(string day, string hm) =>
        DateTime.ParseExact("2026-" + day + " " + hm, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal);

    /// <summary>样稿 rcMin：「4 h 05 min」→ 245。</summary>
    public static int Minutes(string dur)
    {
        var m = System.Text.RegularExpressions.Regex.Match(dur, @"(?:(\d+) h )?(\d+) min");
        return m.Success ? (m.Groups[1].Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 0) * 60 + int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 60;
    }

    private static RecSession ToSession(S s)
    {
        var start = Parse(s.Day, s.Start);
        DateTime? end = s.State == RecState.Active ? null : start.AddMinutes(Minutes(s.Dur));
        var hold = s.Leak ? RecHold.Leak : s.Prog == "气密预检" ? RecHold.Tight : RecHold.None;
        return new RecSession(s.Id, s.Dev, s.Name, s.Prog, s.Op, s.By, s.Chs, start, end, s.State, s.Marks, s.Alarms, s.Note, s.Warn, hold);
    }

    /// <summary>演示会话（样稿 SESS 的顺序：新的在前），只给存在的设备（<paramref name="hasDevice"/>）。</summary>
    public static IReadOnlyList<RecSession> Sessions(Func<int, bool> hasDevice) =>
        Sess.Where(s => hasDevice(s.Dev)).Select(ToSession).ToList();

    /// <summary>是不是演示数据里的会话。</summary>
    public static bool Has(string id) => Sess.Any(s => s.Id == id);

    /// <summary>样稿 rcGas 的历史分支：各路吸气终值（保压类 0）。</summary>
    public static IReadOnlyList<double> Gas(RecSession s)
    {
        long k = s.Id.Length * 7 + s.K;
        double R() { k = (k * 9301 + 49297) % 233280; return k / 233280.0; }
        return s.Channels.Select(_ => s.Hold == RecHold.Leak ? 0 : DemoHistory.Fixed(14 + R() * 10, 1)).ToList();
    }

    /// <summary>
    /// 历史会话的各路曲线（样稿 rcData 的历史分支）。进行中的会话不在这里（用设备的历史缓存）；不是演示会话返回 null。
    /// </summary>
    public static RecSeriesSet? Series(RecSession s)
    {
        var src = Sess.FirstOrDefault(x => x.Id == s.Id);
        if (src == null || s.State == RecState.Active) return null;
        var (tgtT, tgtP) = Targets(src.Prog);
        bool hold = s.Hold != RecHold.None;
        long k = s.Id.Length * 7 + s.K * 13 + src.Start.Length;
        double R() { k = (k * 9301 + 49297) % 233280; return k / 233280.0; }
        double D = Minutes(src.Dur), step = Math.Max(.5, D / 180);
        var gs = Gas(s);
        var chs = new List<RecChannelSeries>(s.Channels.Count);
        for (int i = 0; i < s.Channels.Count; i++)
        {
            int n = s.Channels[i];
            var pts = new List<RecPoint>();
            if (hold)
            {
                double drop = DemoHistory.Fixed(.15 + R() * .9, 2), p0 = tgtP;
                for (double m = 0; m <= D; m += step)
                {
                    double P = m < 6 ? p0 * m / 6 : p0 - drop / 145.04 * (m - 6) / 60;
                    double T = 24.6 + R() * .4;
                    pts.Add(new RecPoint(m, T, Math.Max(0, P + (R() - .5) * .00025), 0));
                }
            }
            else
            {
                double g = gs[i], t0 = D * (.22 + R() * .06), tau = D * (.12 + R() * .18);
                double Sat(double m) => m < t0 ? 0 : 1 - Math.Exp(-(m - t0) / tau);
                double norm = Sat(D);
                if (norm == 0) norm = 1;
                for (double m = 0; m <= D; m += step)
                {
                    double T = m < D * .2 ? 25 + (tgtT - 25) * Math.Min(1, m / (D * .2)) : m > D * .9 ? tgtT - (tgtT - 40) * (m - D * .9) / (D * .1) : tgtT + (R() - .5) * .8;
                    double P = m < D * .2 ? .05 : m < D * .25 ? tgtP * (m - D * .2) / (D * .05) : tgtP + (R() - .5) * .02;
                    pts.Add(new RecPoint(m, T, P, g * Sat(m) / norm));
                }
                // 步长累加停在终点之前时补一个会话结束时刻的采样（温度 / 压力同最后一个，吸气量 = 终值；不取随机数，后面各路的曲线不变）
                if (pts.Count > 0 && pts[^1].M < D - 1e-9) pts.Add(pts[^1] with { M = D, G = g });
            }
            chs.Add(new RecChannelSeries(n, pts));
        }
        return new RecSeriesSet(chs, D, false);
    }

    /// <summary>标记（样稿：1号机进行中的会话用设备的会话标记；其余按标记数取「09:41 RV1 加料完成」「10:58 全部 取样」）：时刻、来源、文字。</summary>
    public static IReadOnlyList<(DateTime At, string Src, string Text)> Marks(RecSession s)
    {
        var all = new (string Hm, string Src, string Text)[] { ("09:41", "RV1", "加料完成"), ("10:58", "全部", "取样") };
        return all.Take(Math.Min(s.Marks, all.Length)).Select(x => (Parse(RecTexts.Day(s.Start), x.Hm), x.Src, x.Text)).ToList();
    }

    /// <summary>数据文件（样稿 files）：文件名、内容、大小。</summary>
    public static IReadOnlyList<(string Name, string What, string Size)> Files(RecSession s)
    {
        string day = s.Start.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var list = new List<(string, string, string)>
        {
            ("telemetry-" + day + ".csv", "每 1.2 s 一行 · 温度 / 压力 / 阀态 / 搅拌", s.Active ? "11.8 MB（写入中）" : "24.6 MB"),
            ("events-" + day + ".csv", "时间 / 级别 / 来源 / 标题 / 详情", "46 KB"),
            (s.Id + ".json", "会话清单：通道、操作员、标记", "3 KB"),
        };
        if (s.Hold == RecHold.Leak) list.Add(("leak/" + s.Id + "-summary.json", "泄漏测试汇总", "8 KB"));
        return list;
    }
}
