using System;
using System.Globalization;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 「程序」画布顶部的状态行（样稿 reactor.js progStatus / statusHTML）：状态点的键、状态字、宽画布的说明、窄画布的短说明、短说明前要不要状态字。
/// 宽画布（程序面板 ≥ 460 px，容器 rvprog）显示「● 运行中 · 第 3/4 步 · 预计 11:09 结束」（<see cref="Long"/>），窄的显示「● 预计 11:09 结束」/「● 报警暂停 · 2/4」（<see cref="Short"/>），
/// 悬停提示都是 <see cref="Title"/>。
/// </summary>
public sealed record ProgramStatusLine(StateKey Key, string Word, string Full, string ShortText, bool NoWord = false, string? Note = null)
{
    /// <summary>
    /// 悬停提示：'运行中 · 第 3/4 步 · 预计 11:09 结束'（有 <see cref="Note"/> 时再加上它）；说明后面的短提醒在悬停里换成全文时（<see cref="TitleLong"/>）用那一句。
    /// </summary>
    public string Title => (TitleLong ?? Long) + (Note is { Length: > 0 } ? " · " + Note : "");
    /// <summary>悬停里代替 <see cref="Long"/> 的一句（真机的「触屏接管过控制」：说明行写短的，悬停写全文）；null = 同 <see cref="Long"/>。</summary>
    public string? TitleLong { get; init; }
    /// <summary>宽画布：'运行中 · 第 3/4 步 · 预计 11:09 结束'。</summary>
    public string Long => Word + (Full.Length > 0 ? " · " + Full : "");
    /// <summary>窄画布：'预计 11:09 结束'（运行中不写状态字）/ '报警暂停 · 2/4' / '已下发 · 待运行'。</summary>
    public string Short => (NoWord ? "" : Word + (ShortText.Length > 0 ? " · " : "")) + ShortText;
    /// <summary>宽画布才显示 <see cref="Long"/>（程序面板宽 ≥ 460，reactor.css @container rvprog）。</summary>
    public const double LongMinWidth = 460;
}

/// <summary>安全联锁框的语气：正常、红（急停 / 报警）、灰（断线不知道）。</summary>
public enum SafetyTone { Ok, Err, Mute }

/// <summary>
/// 画布底部的安全联锁框（样稿 reactor.js safetyHTML，原稿「RV1 : 硬件自检 = 通过」）：「RV1 :」+「安全联锁」+「= 正常 / 急停 / 报警 / 未知」+ 说明（量程取自型号表）。
/// <see cref="Note"/> 是原文；显示时按样稿做断行保护（Text.Keep：「4.14 MPa」不在数字和单位之间折行）。
/// </summary>
public sealed record SafetyBox(int N, string Value, SafetyTone Tone, string Note)
{
    public const string Label = "安全联锁";
    public string RvText => "RV" + N.ToString(CultureInfo.InvariantCulture) + " :";
    public string ValueText => "= " + Value;
}

/// <summary>反应釜页「程序」面板上的文字（样稿 reactor.js：状态行、安全联锁、面包屑、条件框的悬停说明），文字逐字相同。</summary>
public static class ProgramPanelText
{
    /// <summary>没有程序时面包屑的最后一段。</summary>
    public const string NoProgram = "无程序";

    /// <summary>
    /// 状态行（样稿 progStatus）。<paramref name="online"/> = 设备在线；<paramref name="dataText"/> = 断线时的「数据停在 10:11:30」（DeviceTexts.DataText）；
    /// <paramref name="now"/> = 现在（与记录同一种时间），ETA = now + 全程剩余（按绝对时刻，<see cref="EtaAt"/>）。没有程序返回 null。
    /// 真机多出的两种（样稿没有）：报警中止（Failed）同「已中止」的写法；等待气路（排队等共用气路，不是进气口关闭）不写「需在触屏开阀」。
    /// 真机的两种提醒（样稿没有，宽画布的说明后面、悬停写全）：触屏启动（<see cref="TouchStarted"/>）、下发之后触屏端接管过控制（<see cref="LocalControl"/>）。
    /// </summary>
    public static ProgramStatusLine? Status(ProgramRecord? p, ProgressInfo? pr, bool online, string dataText, DateTime now)
    {
        var line = StatusCore(p, pr, online, dataText, now);
        if (line is null || p is null) return line;
        // 真机：本机下发之后由触屏启动的执行（台账照规则认它，但改没改过程序看不出来）—— 宽画布的说明后面写「触屏启动」，悬停写全
        if (p.StartedByTouch && p.Status != ProgramStatus.Loaded)
            return line with { Full = line.Full + (line.Full.Length > 0 ? " · " : "") + TouchStarted, Note = TouchStartedNote };
        // 真机：下发之后触屏端接管过控制（复核）—— 之后开始的执行、还没跑的已下发程序：说明后面写「触屏接管过控制」，悬停写全文（两样不同时写，触屏启动优先）
        if (p.AfterLocalControl)
            return line with
            {
                Full = line.Full + (line.Full.Length > 0 ? " · " : "") + LocalControl,
                TitleLong = line.Long + " · " + (p.Status == ProgramStatus.Loaded ? LocalControlLoadedNote : LocalControlNote),
            };
        return line;
    }

    /// <summary>触屏启动的执行在状态行上的说明（样稿没有这一种，复核 R5）。</summary>
    public const string TouchStarted = "触屏启动";
    /// <summary>触屏启动的执行在状态行悬停里的补充说明。</summary>
    public const string TouchStartedNote = "按本机下发的程序显示";
    /// <summary>下发之后触屏端接管过控制（样稿没有这一种，复核）：状态行说明后面的短提醒。</summary>
    public const string LocalControl = "触屏接管过控制";
    /// <summary>同上，运行中 / 已结束的执行：悬停里的全文（代替短提醒）。</summary>
    public const string LocalControlNote = "触屏端接管过控制 · 按本机下发的程序显示，设备上的程序可能已修改";
    /// <summary>同上，还没跑的已下发程序：悬停里的全文（「启动」此时禁用，要重新下发）。</summary>
    public const string LocalControlLoadedNote = "触屏端接管过控制 · 设备上的程序可能已修改 · 重新下发后才能从这里启动";

    private static ProgramStatusLine? StatusCore(ProgramRecord? p, ProgressInfo? pr, bool online, string dataText, DateTime now)
    {
        if (p is null || pr is null) return null;
        string kn = pr.K + "/" + pr.N;
        if (!online) return new ProgramStatusLine(StateKey.Offline, "离线", "停在第 " + kn + " 步 · " + dataText, "停在 " + kn);
        switch (p.Status)
        {
            case ProgramStatus.Running:
                if (pr.WaitGas)
                    return pr.WaitWord == Progress.WaitGasWordDemo
                        ? new ProgramStatusLine(StateKey.Wait, pr.WaitWord, "第 " + kn + " 步 · 需在触屏开阀", kn)
                        : new ProgramStatusLine(StateKey.Wait, pr.WaitWord, "第 " + kn + " 步", kn);
                string eta = Fmt.Hm(EtaAt(now, pr.TotalRemainMin));
                return new ProgramStatusLine(StateKey.React, "运行中", "第 " + kn + " 步 · 预计 " + eta + " 结束", "预计 " + eta + " 结束", NoWord: true);
            case ProgramStatus.Held:
                return new ProgramStatusLine(StateKey.Stopped, "报警暂停", "第 " + kn + " 步", kn);
            case ProgramStatus.Loaded:
                return new ProgramStatusLine(StateKey.Ready, "已下发", "待运行", "待运行");
            case ProgramStatus.Done:
            {
                string t = p.EndedAt is { } e ? Fmt.Hm(e) : "";
                return new ProgramStatusLine(StateKey.Done, "已结束", "共 " + pr.N + " 步" + (t.Length > 0 ? " · " + t + " 完成" : ""), t.Length > 0 ? t + " 完成" : "共 " + pr.N + " 步");
            }
            case ProgramStatus.Aborted:
                return new ProgramStatusLine(StateKey.Stopped, "已中止", "第 " + kn + " 步", kn);
            case ProgramStatus.Failed:
                return new ProgramStatusLine(StateKey.Stopped, "报警中止", "第 " + kn + " 步", kn);
            default:
                return new ProgramStatusLine(StateKey.Idle, pr.StatusText, "", "");
        }
    }

    /// <summary>
    /// 预计结束的时刻（UTC）：现在 + 全程剩余，按绝对时刻相加（同样稿 new Date(+now + min * 60000)），显示时再换回本地时间（Fmt.Hm）——
    /// 复核：在本地时间上直接加，跨夏令时切换时差 1 小时（中国没有夏令时，欧洲等地有）。<paramref name="now"/> 是本地时间或 UTC。
    /// </summary>
    public static DateTime EtaAt(DateTime now, int remainMin) =>
        (now.Kind == DateTimeKind.Utc ? now : now.ToUniversalTime()).AddTicks((long)remainMin * TimeSpan.TicksPerMinute);

    /// <summary>
    /// 真机的程序对不上台账（R5：不显示步骤与预计结束，只写 HMI 报的 k/n、状态与消息）时的状态行，写法同 <see cref="Status"/>：
    /// 断线「离线 · 停在第 k/n 步 · 数据停在 …」；运行中「运行中 · 第 k/n 步 · 消息」（窄：「运行中 · k/n」）；等待气路；已结束「共 n 步」；已中止 / 报警中止「第 k/n 步」。
    /// </summary>
    public static ProgramStatusLine HmiStatus(ParallelReactor.Api.ChannelProgramSnap p, bool online, string dataText)
    {
        ArgumentNullException.ThrowIfNull(p);
        int n = Math.Max(0, p.StepCount);
        int k = Math.Clamp(p.StepIndex + 1, n > 0 ? 1 : 0, n);
        string kn = k + "/" + n;
        if (!online) return new ProgramStatusLine(StateKey.Offline, "离线", "停在第 " + kn + " 步 · " + dataText, "停在 " + kn);
        string msg = string.IsNullOrWhiteSpace(p.Message) ? "" : " · " + p.Message.Trim();
        return p.State switch
        {
            ParallelReactor.Api.ChannelProgramStates.Completed => new ProgramStatusLine(StateKey.Done, "已结束", "共 " + n + " 步", "共 " + n + " 步"),
            ParallelReactor.Api.ChannelProgramStates.Aborted => new ProgramStatusLine(StateKey.Stopped, "已中止", "第 " + kn + " 步", kn),
            ParallelReactor.Api.ChannelProgramStates.Failed => new ProgramStatusLine(StateKey.Stopped, "报警中止", "第 " + kn + " 步", kn),
            ParallelReactor.Api.ChannelProgramStates.WaitingGas => new ProgramStatusLine(StateKey.Wait, StateMap.WaitGasWord, "第 " + kn + " 步" + msg, kn),
            _ => new ProgramStatusLine(StateKey.React, "运行中", "第 " + kn + " 步" + msg, kn),
        };
    }

    /// <summary>
    /// 安全联锁框（样稿 safetyHTML）：断线 = 未知（「连接中断 · 状态停在 10:11:30」）；急停中 = 急停；反应釜自己的状态是报警、或这个反应釜有错误报警 = 报警
    /// （说明 = 第一条错误报警的文字，没有时「超压报警」）；否则正常（「急停未触发 · 量程内 ≤ 4.14 MPa · ≤ 200 ℃」，数字按 JS 的 String）。
    /// </summary>
    public static SafetyBox Safety(int n, ModelProfile model, bool online, DateTime? dataAt, bool estop, bool channelAlarm, string? errAlarmText)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (!online) return new SafetyBox(n, "未知", SafetyTone.Mute, "连接中断 · 状态停在 " + (dataAt is { } t ? Fmt.Time(t) : Fmt.Dash));
        if (estop) return new SafetyBox(n, "急停", SafetyTone.Err, "急停已触发 · 加热 / 进气 / 搅拌已切断 · 需在触屏复位");
        if (channelAlarm || errAlarmText is not null) return new SafetyBox(n, "报警", SafetyTone.Err, errAlarmText ?? "超压报警");
        return new SafetyBox(n, "正常", SafetyTone.Ok,
            "急停未触发 · 量程内 ≤ " + JsNumber.ToString(model.Limits.PressureMaxMPa) + " MPa · ≤ " + JsNumber.ToString(model.Limits.TempMaxC) + " ℃");
    }

    /// <summary>面包屑的全文（悬停提示）：'1号机 › RV1 › 加氢-标准'；没有程序时最后一段「无程序」。</summary>
    public static string CrumbTitle(string alias, int n, string? programName) =>
        alias + " › RV" + n.ToString(CultureInfo.InvariantCulture) + " › " + (string.IsNullOrEmpty(programName) ? NoProgram : programName);

    /// <summary>程序面板关闭提示等用的面板名：'程序 · 加氢-标准'。</summary>
    public static string PanelName(string? programName) => "程序 · " + (string.IsNullOrEmpty(programName) ? NoProgram : programName);

    /// <summary>
    /// 条件框的悬停说明（样稿 condHTML 的 title）：'1号机 · RV1 · 步骤时间 > 72 min'；当前步再加「 · 已进行 42 %」和「 · 剩 30 min」（等待进气 / 报警暂停时写那个）；
    /// 已完成的步骤（记下了起止）加「 · 用时 12 min（09:40–09:52）」。
    /// </summary>
    public static string CondTip(string alias, int n, ProgramRecord p, int i, ProgressInfo? pr, ModelProfile? model)
    {
        ArgumentNullException.ThrowIfNull(p);
        var c = StepText.Cond(p.Steps[i], model);
        string tip = alias + " · RV" + n.ToString(CultureInfo.InvariantCulture) + " · " + c.Label + " " + c.Value;
        var s = Progress.StepState(p, i);
        if (s == SegmentState.Cur)
        {
            if (pr is not null)
            {
                double f = pr.Within;
                tip += " · 已进行 " + JsNumber.ToString(JsNumber.Round(f * 100)) + " %" + (p.Status == ProgramStatus.Running ? (pr.WaitGas ? " · " + pr.WaitWord : " · " + pr.RemainText) : " · 暂停");
            }
        }
        else if (s == SegmentState.Done)
        {
            foreach (var l in p.Log)
                if (l.I == i)
                {
                    tip += " · 用时 " + Fmt.Minutes((l.T1 - l.T0).TotalMilliseconds / 60000) + "（" + Fmt.Hm(l.T0) + "–" + Fmt.Hm(l.T1) + "）";
                    break;
                }
        }
        return tip;
    }

    /// <summary>条件框底部进度线的宽度（0–1，样稿 max(0.04, within)）：只有当前步有；运行中、在线、不在等进气时缓慢呼吸。</summary>
    public static double CondFill(ProgressInfo? pr) => Math.Max(0.04, pr?.Within ?? 0);
}
