using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ParallelReactor.Control.Core.Format;

namespace ParallelReactor.Control.Core.Activity;

/// <summary>活动的语气（图标块、胶囊、设备栏行的颜色）：进行中品牌蓝 / 等人操作琥珀 / 通过绿 / 不通过红 / 中止或失败灰。</summary>
public enum ActTone { Run, Amb, Ok, No, Ab }

/// <summary>圆环的一段（样稿 ax2Ring）：权重（按阶段时长）、样子、当前段的进度。</summary>
public enum RingSegKind { Done, Cur, Next, CurAmb, DoneSoft, Ok, No, Amb, Ab }

/// <summary>圆环 / 阶段条的一段。</summary>
public readonly record struct RingSeg(double Weight, RingSegKind Kind, double Fill);

/// <summary>通道小块的样子（样稿 .a2chip.k-*）：进行中 / 通过 / 不通过 / 初始压力过高 / 中性（中止、没测完）。</summary>
public enum ChipKind { Run, Ok, No, Amb, Neutral }

/// <summary>通道小块：RVn、右边的数值 + 单位、下面一行的状态字。</summary>
public sealed record ActChip(int N, ChipKind Kind, string Value, string Unit, string Word);

/// <summary>
/// 设备活动的文字与图形规则（ACT-SPEC §3.1 / §3.2，样稿 mock-src/act.js + act2.js 的方向 A）：纯函数，单元测试直接调。
/// 压力按本机设置的单位（<see cref="PressureDisplay"/>，MPa 3 位）；泄漏率一律 psi/h（设备的判据就是 psi/h）。
/// </summary>
public static class ActivityText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static readonly IReadOnlyList<string> LeakStages = new[] { "充压", "平衡", "测量", "判读 · 泄压" };
    public static readonly IReadOnlyList<string> CalibStages = new[] { "第一遍 · 空釜", "加水", "第二遍 · 读数", "算出容积" };

    /// <summary>充压、判读 · 泄压两段的估计时长（样稿 ax2Stg：160 s / 120 s）；平衡 / 测量按设备设置。</summary>
    public const double FillSec = 160, VentSec = 120;

    /// <summary>判据（设备设置没有时）：HMI 出厂 2 psi/h。</summary>
    public const double DefaultThreshold = 2;

    /// <summary>结束了、PC 又没看着它进行的活动，超过这么久不再显示（打开软件时不翻出几小时前的结果）。</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    public static IReadOnlyList<string> Stages(DeviceActivity a) => a.Kind == ActKind.Leak ? LeakStages : CalibStages;

    public static string StageName(DeviceActivity a) => Stages(a)[Math.Clamp(a.Stage, 0, 3)];

    /// <summary>「RV1–4」（连续）/「RV1、RV3」。</summary>
    public static string Range(IReadOnlyList<int> chs)
    {
        if (chs.Count == 0) return "";
        var l = chs.OrderBy(n => n).ToList();
        if (l.Count > 1 && l[^1] - l[0] == l.Count - 1) return "RV" + l[0].ToString(Inv) + "–" + l[^1].ToString(Inv);
        return string.Join("、", l.Select(n => "RV" + n.ToString(Inv)));
    }

    /// <summary>「37:20」（分钟可以超过 60）。</summary>
    public static string Mmss(double sec)
    {
        long t = (long)Math.Round(Math.Max(0, sec), MidpointRounding.AwayFromZero);
        return (t / 60).ToString("00", Inv) + ":" + (t % 60).ToString("00", Inv);
    }

    /// <summary>「泄漏测试」/「容积标定 RV3」。</summary>
    public static string Name(DeviceActivity a) => a.Kind == ActKind.Leak ? "泄漏测试" : "容积标定" + (a.Channel is int c ? " RV" + c.ToString(Inv) : "");

    /// <summary>面板标题「1号机 · 泄漏测试」/「1号机 · 容积标定 · RV3」。</summary>
    public static string Title(string alias, DeviceActivity a) =>
        alias + " · " + (a.Kind == ActKind.Leak ? "泄漏测试" : "容积标定" + (a.Channel is int c ? " · RV" + c.ToString(Inv) : ""));

    /// <summary>
    /// 发起人：「触屏发起」/「本机发起」（远程来源是本机的客户端名）/「PC-2 发起」；不知道为空串。
    /// </summary>
    public static string Source(DeviceActivity a, string? clientName)
    {
        var s = a.StartedBy?.Trim();
        if (string.IsNullOrEmpty(s)) return a.Kind == ActKind.Calib ? "触屏发起" : "";
        if (s.Contains("触屏", StringComparison.Ordinal) || s.Equals("local", StringComparison.OrdinalIgnoreCase)) return "触屏发起";
        if (clientName is { Length: > 0 } me && s.Contains(me, StringComparison.Ordinal)) return "本机发起";
        int i = s.IndexOf('（'), j = s.LastIndexOf('）');
        if (i >= 0 && j > i + 1) return s[(i + 1)..j] + " 发起";
        return s + " 发起";
    }

    /// <summary>测试压力：「0.34 MPa（50 psi）」；单位是 psi 时「50 psi（≈ 0.34 MPa）」。</summary>
    public static string Nominal(double psi)
    {
        var mpa = Units.PsiToMpa(psi);
        var p = Math.Round(psi).ToString(Inv);
        return PressureDisplay.Primary switch
        {
            PressureUnit.Psi => p + " psi（≈ " + mpa.ToString("0.00", Inv) + " MPa）",
            PressureUnit.Bar => PressureUnits.FromMpa(mpa, PressureUnit.Bar).ToString("0.00", Inv) + " bar（" + p + " psi）",
            _ => mpa.ToString("0.00", Inv) + " MPa（" + p + " psi）",
        };
    }

    /// <summary>压力（psi）按本机单位：MPa 3 位（bar 2 位、psi 1 位）+ 单位。</summary>
    public static (string Value, string Unit) Pressure(double? psi, int mpaDecimals = 3) =>
        (PressureDisplay.Fixed(Units.PsiToMpa(psi), mpaDecimals), PressureDisplay.Unit);

    /// <summary>「0.12」psi/h（负数按 0）。</summary>
    public static string Rate(double r) => Math.Max(0, r).ToString("0.00", Inv);

    /// <summary>判据「≤ 0.5 psi/h」。</summary>
    public static string Threshold(DeviceActivity a) => "判据 ≤ " + Num(a.ThresholdPsiH ?? DefaultThreshold) + " psi/h";

    private static string Num(double v) => v.ToString("0.###", Inv);

    /// <summary>「约 15:07 结束」的时刻：现在 + 全程剩余；不知道剩余为 null。</summary>
    public static DateTime? EndAt(DeviceActivity a, DateTime now) => a.TotalLeftSec is double s ? now.AddSeconds(s) : null;

    /// <summary>等人操作已经等了多少秒（不知道从什么时候开始等为 0）。</summary>
    public static double WaitedSec(DeviceActivity a, DateTime now) => a.WaitingSince is { } t ? Math.Max(0, (now - t).TotalSeconds) : 0;

    /// <summary>已完成百分比（整数）。</summary>
    public static int Pct(DeviceActivity a) => (int)Math.Round(Math.Clamp(a.Progress, 0, 100), MidpointRounding.AwayFromZero);

    public static ActTone Tone(DeviceActivity a) => a.Phase switch
    {
        ActPhase.Waiting => ActTone.Amb,
        ActPhase.Failed => ActTone.No,
        ActPhase.Passed => ActTone.Ok,
        ActPhase.Aborted or ActPhase.Error => ActTone.Ab,
        _ => ActTone.Run,
    };

    /// <summary>标题前的图标（Fluent 图标名）。</summary>
    public static string Icon(DeviceActivity a) => a.Kind == ActKind.Calib
        ? (a.Phase == ActPhase.Error ? "f-no-f" : "f-beaker")
        : a.Phase switch { ActPhase.Failed => "f-no-f", ActPhase.Passed => "f-ok-f", ActPhase.Aborted or ActPhase.Error => "f-stop", _ => "f-timer" };

    /// <summary>胶囊 / 收起行的说明（样稿 ax2Stage1）：「平衡 · 剩余 37:20」「等待触屏操作 · 已等 03:12」「已结束 · 2 路不通过」。</summary>
    public static string Meta(DeviceActivity a, DateTime now)
    {
        if (a.Kind == ActKind.Calib)
            return a.Phase switch
            {
                ActPhase.Waiting => "等待触屏操作 · 已等 " + Mmss(WaitedSec(a, now)),
                ActPhase.Running => StageName(a) + " · " + Pct(a).ToString(Inv) + " %",
                ActPhase.Passed => "已完成" + (a.ResultMl is double r ? " · " + r.ToString("0.00", Inv) + " mL" : ""),
                _ => "标定失败",
            };
        return a.Phase switch
        {
            ActPhase.Running => StageName(a) + (a.TotalLeftSec is double t ? " · 剩余 " + Mmss(t) : " · " + Pct(a).ToString(Inv) + " %"),
            ActPhase.Failed => "已结束 · " + a.Counts.No.ToString(Inv) + " 路不通过",
            ActPhase.Aborted => HoldChannels(a).Count > 0 ? "已中止 · 釜内带压" : "已中止",
            ActPhase.Error => "没有完成",
            _ => "已结束 · 全部通过",
        };
    }

    /// <summary>
    /// 设备栏的活动行（样稿 axLines）：语气 + 图标 + 文字。「泄漏测试 · 平衡 · 16 %」「容积标定 RV4 · 等待触屏操作」「泄漏测试结束 · 2 路不通过」。
    /// </summary>
    public static (ActTone Tone, string Icon, string Text) DevLine(DeviceActivity a)
    {
        if (a.Kind == ActKind.Calib)
            return a.Phase switch
            {
                ActPhase.Waiting => (ActTone.Amb, "f-warn-f", Name(a) + " · 等待触屏操作"),
                ActPhase.Running => (ActTone.Run, "f-beaker", Name(a) + " · " + StageName(a)),
                ActPhase.Passed => (ActTone.Run, "f-ok-f", Name(a) + " · 已完成"),
                _ => (ActTone.Amb, "f-warn-f", Name(a) + " · 标定失败"),
            };
        return a.Phase switch
        {
            ActPhase.Running => (ActTone.Run, "f-timer", "泄漏测试 · " + StageName(a) + " · " + Pct(a).ToString(Inv) + " %"),
            ActPhase.Failed => (ActTone.Amb, "f-warn-f", "泄漏测试结束 · " + a.Counts.No.ToString(Inv) + " 路不通过"),
            ActPhase.Aborted => (ActTone.Amb, "f-warn-f", HoldChannels(a).Count > 0 ? "泄漏测试已中止 · 釜内带压" : "泄漏测试已中止"),
            ActPhase.Error => (ActTone.Amb, "f-warn-f", "泄漏测试没有完成"),
            _ => (ActTone.Run, "f-ok-f", "泄漏测试结束 · 全部通过"),
        };
    }

    /// <summary>状态栏的一项：宽屏「1号机 · 泄漏测试 · 平衡 · 16 %」；窄屏「1号机 泄漏测试 16 %」。</summary>
    public static string Status(string alias, DeviceActivity a, bool narrow)
    {
        string sep = narrow ? " " : " · ";
        if (a.Kind == ActKind.Calib)
            return alias + sep + Name(a) + sep + (a.Waiting ? (narrow ? "等待触屏" : "等待触屏操作") : a.Live ? StageName(a) : a.Phase == ActPhase.Passed ? "已完成" : "标定失败");
        if (a.Live) return alias + sep + "泄漏测试" + (narrow ? " " : " · " + StageName(a) + " · ") + Pct(a).ToString(Inv) + " %";
        return alias + sep + "泄漏测试" + (narrow ? "已结束" : " · " + Meta(a, default));
    }

    // ———————————————————— 面板 ————————————————————

    /// <summary>面板标题下的一行：「RV1–4 · 测试压力 0.34 MPa（50 psi）· 触屏发起」/「RV1–4 · 14:28 完成 · 用时 44 min · 触屏发起」/「第二遍 · 加水后再读一遍 · 触屏发起」。</summary>
    public static string HeadSub(DeviceActivity a, string? clientName)
    {
        var src = Source(a, clientName);
        string tail = src.Length > 0 ? " · " + src : "";
        if (a.Kind == ActKind.Calib)
        {
            string what = a.Phase switch
            {
                ActPhase.Waiting => "第二遍 · 加水后再读一遍",
                ActPhase.Running => a.Stage == 0 ? "第一遍 · 空釜读一遍" : "第二遍 · 加水后再读一遍",
                ActPhase.Passed => "已完成" + (a.FinishedAt is { } f ? " · " + Fmt.Hm(f) : ""),
                _ => "标定没有完成",
            };
            return what + tail;
        }
        var rng = Range(a.Chs);
        if (a.Live) return rng + (a.TargetPsi is double p ? " · 测试压力 " + Nominal(p) : "") + tail;
        if (a.Phase == ActPhase.Aborted || a.Phase == ActPhase.Error)
            return rng + (a.StartedAt is { } t0 ? " · " + Fmt.Hm(t0) + " 开始" : "") + tail;
        var parts = new List<string> { rng };
        if (a.FinishedAt is { } t1) parts.Add(Fmt.Hm(t1) + " 完成");
        if (a.FinishedAt is { } e && a.StartedAt is { } s && e > s) parts.Add("用时 " + Math.Max(1, (int)Math.Round((e - s).TotalMinutes)).ToString(Inv) + " min");
        return string.Join(" · ", parts.Where(x => x.Length > 0)) + tail;
    }

    /// <summary>结果的大字：「2 路不通过」「4 路全部通过」「已中止」「没有完成」。</summary>
    public static string Verdict(DeviceActivity a)
    {
        var (ok, no, _) = a.Counts;
        return a.Phase switch
        {
            ActPhase.Aborted => "已中止",
            ActPhase.Error => "没有完成",
            ActPhase.Failed => no.ToString(Inv) + " 路不通过",
            _ => ok > 0 ? ok.ToString(Inv) + " 路全部通过" : "全部通过",
        };
    }

    /// <summary>结果下面一行：「1 路通过 · 1 路未测」「14:30 中止 · 阀门全关」「没有通道充压成功」。</summary>
    public static string VerdictSub(DeviceActivity a)
    {
        var (ok, _, amb) = a.Counts;
        return a.Phase switch
        {
            ActPhase.Aborted => (a.FinishedAt is { } t ? Fmt.Hm(t) + " 中止 · " : "") + "阀门全关",
            ActPhase.Error => (a.FailReason is { Length: > 0 } r ? r : "测试没有完成") + " · 阀门全关",
            _ => ok.ToString(Inv) + " 路通过" + (amb > 0 ? " · " + amb.ToString(Inv) + " 路未测" : ""),
        };
    }

    /// <summary>
    /// 结束后釜内还有压力的通道（≥ 2 psi，同 HMI 判断泄压没降下来的下限；充压不上的不算）：泄压没成功（vented = false）时是参加的通道；
    /// 中止 / 失败（没做到泄压那一步）时按读数。
    /// </summary>
    public static IReadOnlyList<ActChannel> HoldChannels(DeviceActivity a)
    {
        if (a.Kind != ActKind.Leak || a.Live || a.Vented == true) return Array.Empty<ActChannel>();
        // 充压不上的那一路本来就没到测试压力（样稿只列 RV1、RV3、RV4）
        var hold = a.Channels.Where(c => c.State != LeakChState.FillFail && c.PressurePsi is >= 2).ToList();
        if (a.Vented == false && hold.Count == 0) hold = a.Channels.Where(c => c.State != LeakChState.FillFail).ToList();
        return hold;
    }

    /// <summary>
    /// 琥珀带压提示（样稿 .a2warn）：「釜内仍有测试压力，拆釜前请先泄压」+「泄压阶段压力没有降下来 · RV1、RV3、RV4 仍约 0.30 MPa · 在触屏上手动放空」。没有为 null。
    /// </summary>
    public static (string Title, string Body)? HoldWarn(DeviceActivity a)
    {
        var hold = HoldChannels(a);
        if (hold.Count == 0) return null;
        var ps = hold.Where(c => c.PressurePsi is double).Select(c => c.PressurePsi!.Value).ToList();
        string about = ps.Count > 0 ? "约 " + PressureDisplay.Fixed(Units.PsiToMpa(ps.Max()), 2) + " " + PressureDisplay.Unit : "";
        var names = string.Join("、", hold.Select(c => "RV" + c.N.ToString(Inv)));
        string body = a.Vented == false
            ? "泄压阶段压力没有降下来 · " + names + (about.Length > 0 ? " 仍" + about : " 仍有压力") + " · 在触屏上手动放空"
            : (a.Phase == ActPhase.Aborted ? "中止不放空 · " : "没有泄压 · ") + names + (about.Length > 0 ? " " + about : "") + " · 在触屏上手动放空";
        return ("釜内仍有测试压力，拆釜前请先泄压", body);
    }

    /// <summary>一路的小块（样稿 ax2Chips）：进行中 = 压力 + 触屏文字；结束 = 通过 / 不通过的泄漏率、充压不上的压力、初始压力过高「未测 · 先泄压」。</summary>
    public static ActChip Chip(DeviceActivity a, ActChannel c)
    {
        var (pv, pu) = Pressure(c.PressurePsi);
        if (a.Live) return new ActChip(c.N, ChipKind.Run, pv, pu, c.Detail.Length > 0 ? c.Detail : "进行中");
        // 结束后按每路的结果；中止 / 失败时没出结果的路（HMI 报 untested「未完成」）写「未完成」，压力照写（釜里可能还有测试压力）
        return c.State switch
        {
            LeakChState.Pass => new ActChip(c.N, ChipKind.Ok, c.RatePsiH is double r ? Rate(r) : "—", c.RatePsiH is null ? "" : "psi/h", "通过"),
            LeakChState.Fail => new ActChip(c.N, ChipKind.No, c.RatePsiH is double r2 ? Rate(r2) : "—", c.RatePsiH is null ? "" : "psi/h", "不通过"),
            LeakChState.FillFail => new ActChip(c.N, ChipKind.No, Pressure(c.PressurePsi, 2).Value, pu, "充压不上"),
            LeakChState.TooHigh => new ActChip(c.N, ChipKind.Amb, "未测 · 先泄压", "", "初始压力过高"),
            _ => new ActChip(c.N, ChipKind.Neutral, pv, pu, "未完成"),
        };
    }

    /// <summary>「RV5–8 不测」；全部参加时为空串。</summary>
    public static string OffLine(DeviceActivity a) => a.Untested.Count > 0 ? Range(a.Untested) + " 不测" : "";

    // ———————————————————— 圆环 / 阶段条 ————————————————————

    /// <summary>四个阶段的时长（秒）：泄漏测试 = 充压 160 / 平衡 / 测量（设备设置）/ 判读 · 泄压 120；标定按样稿的权重 15 / 6 / 15 / 3（分钟，这里也写成秒）。</summary>
    public static double[] StageWeights(DeviceActivity a, double settleMin, double measureMin) => a.Kind == ActKind.Leak
        ? new[] { FillSec, Math.Max(1, settleMin) * 60, Math.Max(1, measureMin) * 60, VentSec }
        : new double[] { 900, 360, 900, 180 };

    /// <summary>当前阶段做了多少（0–1）：平衡 / 测量 = 1 − 本阶段剩余 / 阶段时长；充压按进度（0–10 %）；判读 · 泄压按一半；标定等人操作算满。</summary>
    public static double StageFill(DeviceActivity a, double settleMin, double measureMin)
    {
        if (a.Kind == ActKind.Calib) return a.Waiting ? 1 : a.Stage == 2 ? Math.Clamp((a.Progress - 55) / 40, 0.05, 1) : Math.Clamp(a.Progress / 45, 0.05, 1);
        var w = StageWeights(a, settleMin, measureMin);
        return a.Stage switch
        {
            0 => Math.Clamp(a.Progress / 10, 0, 1),
            1 or 2 => a.StageLeftSec is double l ? Math.Clamp(1 - l / w[a.Stage], 0, 1) : Math.Clamp((a.Progress - (a.Stage == 1 ? 12 : 35)) / (a.Stage == 1 ? 23 : 62), 0, 1),
            _ => 0.5,
        };
    }

    /// <summary>
    /// 大圆环的段（样稿 ax2Ring）：进行中 = 四段按阶段时长，已完成品牌蓝、当前浅蓝底 + 蓝色进度（等人操作 = 琥珀）、未开始灰；
    /// 标定的第一遍完成后用浅品牌蓝（--brand-line）；结束 = 参加的每一路一段（通过绿、不通过 / 充压不上红、初始压力过高琥珀）；中止 / 没完成 = 一整圈灰。
    /// </summary>
    public static IReadOnlyList<RingSeg> Ring(DeviceActivity a, double settleMin, double measureMin)
    {
        if (a.Kind == ActKind.Leak && a.Finished)
        {
            if (a.Phase is ActPhase.Aborted or ActPhase.Error || a.Channels.Count == 0) return new[] { new RingSeg(1, RingSegKind.Next, 0) };
            return a.Channels.Select(c => new RingSeg(1, c.State switch
            {
                LeakChState.Pass => RingSegKind.Ok,
                LeakChState.Fail or LeakChState.FillFail => RingSegKind.No,
                LeakChState.TooHigh => RingSegKind.Amb,
                _ => RingSegKind.Ab,
            }, 1)).ToArray();
        }
        var w = StageWeights(a, settleMin, measureMin);
        int cur = a.Finished ? 4 : a.Stage;
        double f = StageFill(a, settleMin, measureMin);
        var list = new RingSeg[4];
        for (int i = 0; i < 4; i++)
        {
            var k = i < cur ? (a.Kind == ActKind.Calib ? RingSegKind.DoneSoft : RingSegKind.Done)
                : i == cur ? (a.Waiting ? RingSegKind.CurAmb : RingSegKind.Cur) : RingSegKind.Next;
            list[i] = new RingSeg(w[i], k, k is RingSegKind.Cur or RingSegKind.CurAmb ? f : 0);
        }
        return list;
    }

    /// <summary>胶囊里的小圆环（样稿 ax2Mini）：总进度一段 + 轨道；结束 = 一整圈（不通过红、通过绿、中止灰），等人操作琥珀。</summary>
    public static (double Fraction, ActTone Tone) Mini(DeviceActivity a) => a.Finished
        ? (1, Tone(a))
        : (a.Kind == ActKind.Calib && a.Waiting ? 0.45 : Math.Clamp(a.Progress / 100, 0.02, 1), Tone(a));
}
