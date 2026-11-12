using System;
using System.Collections.Generic;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Control.Core.State;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 计算程序进度需要的现场情况（样稿 progress / segments 从设备读的那几样）：
/// <list type="bullet">
/// <item><see cref="Now"/>：当前时刻（演示 = DemoFleet.Now；真机 = 校正后的设备时间），与 <see cref="ProgramRecord"/> 里的时间同一种 Kind。</item>
/// <item><see cref="Online"/> / <see cref="DataAt"/>：设备断线时进度停在最后收到数据的时刻，不再倒计时。</item>
/// <item><see cref="ChannelState"/>：反应釜自己的状态（未按断线改写的那个）；= Wait 且当前步要进气时算「等待进气」。</item>
/// <item><see cref="Model"/>：气路名称、压力小数位。<see cref="GasOpen"/>：时间轴上「下一步需要进气、进气口关着」的提示（演示）；不给 = 不提示。</item>
/// <item><see cref="WaitGasWord"/>：等待进气时的文字。样稿 =「等待进气」（进气口关闭）；真机的 waitingGas 是排队等共用气路，传 StateMap.WaitGasWord「等待气路」（PORT-PLAN §3.5）。</item>
/// </list>
/// </summary>
public sealed record ProgressContext(DateTime Now, bool Online = true, DateTime? DataAt = null, StateKey ChannelState = StateKey.Idle,
    ModelProfile? Model = null, Func<string, bool>? GasOpen = null, string WaitGasWord = Progress.WaitGasWordDemo)
{
    /// <summary>计时用的时刻：断线且有最后数据时刻时 = DataAt，否则 = Now。</summary>
    public DateTime Clock => !Online && DataAt is { } d ? d : Now;
}

/// <summary>
/// 程序进度（样稿 PR.sim.progress 的返回值，字段同名）：状态文字、第 K/N 步、剩余时间（当前步 / 全程，分钟向上取整）、
/// 当前步内的进度 <see cref="Within"/> 与全程进度 <see cref="Frac"/>（0..1，按各步预计时长）、等待进气、按条件结束且预计时间已用完（<see cref="Overdue"/>）。
/// </summary>
public sealed record ProgressInfo(
    string Name, ProgramStatus Status, string StatusText, bool Offline, bool WaitGas, string GasName,
    int K, int N, ProgramStep? Step, string StepName, bool Overdue, string RemainText, string RemainShort,
    double StepRemainS, int StepRemainMin, int TotalRemainMin, double Within, double Frac, string WaitWord);

/// <summary>时间轴上的一段（样稿 PR.sim.segments）：步骤序号、步骤、名称、起止时刻、状态、是否在等进气。</summary>
public sealed record ProgramSegment(int I, ProgramStep? Step, string Name, DateTime T0, DateTime T1, SegmentState State, bool WaitGas);

/// <summary>时间轴分段 / 步骤卡片的状态：已完成、当前、未到、中止在这一步。</summary>
public enum SegmentState { Done, Cur, Next, Abort }

/// <summary>分段进度条（样稿 ui.stepBar）一段的样式：已完成（浅绿满格）、当前（按进度填充，运行中且在线时缓慢呼吸）、报警暂停、中止（灰斜纹）、未到。</summary>
public enum StepBarState { Pending, Done, Cur, Held, Abort }

/// <summary>分段进度条的一段：样式、填充百分比（0–100）、是否呼吸（运行中、在线、不在等进气）。</summary>
public readonly record struct StepBarSegment(StepBarState State, double FillPct, bool Pulse, string Label);

/// <summary>
/// 程序进度（样稿 PR.sim.progress / segments、ui.progText / stepBar / stepState / stepCard 标记的移植，文字逐字相同）。
/// 输入是 <see cref="ProgramRecord"/> + <see cref="ProgressContext"/>，演示数据与真机（程序台账）共用。
/// </summary>
public static class Progress
{
    /// <summary>样稿的等待进气文字（进气口关闭，需在触屏打开）。</summary>
    public const string WaitGasWordDemo = "等待进气";

    private const double MinMs = 60000;

    /// <summary>一步的计划时长（秒，样稿 stepPlanS）：反应 / 等待按 minutes；其它按预估分钟（至少 60 s）。null → 60。</summary>
    public static double StepPlanS(ProgramStep? step)
    {
        if (step is null) return 60;
        if (step.Type is StepKind.React or StepKind.Wait) return (step.Minutes ?? double.NaN) * 60;
        return Math.Max(60, (step.EstMin != 0 && !double.IsNaN(step.EstMin) ? step.EstMin : 1) * 60);
    }

    /// <summary>程序状态文字：运行中 / 报警暂停 / 已下发 · 待运行 / 已结束 / 已中止 / 报警中止（真机）。</summary>
    public static string StatusText(ProgramStatus status) => status switch
    {
        ProgramStatus.Running => "运行中",
        ProgramStatus.Held => "报警暂停",
        ProgramStatus.Loaded => "已下发 · 待运行",
        ProgramStatus.Done => "已结束",
        ProgramStatus.Aborted => "已中止",
        ProgramStatus.Failed => "报警中止",
        _ => status.ToString(),
    };

    /// <summary>程序进度（样稿 PR.sim.progress）。没有程序返回 null。断线时停在 <see cref="ProgressContext.DataAt"/>。</summary>
    public static ProgressInfo? Of(ProgramRecord? p, ProgressContext ctx)
    {
        if (p is null) return null;
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        bool off = !ctx.Online;
        var now = ctx.Clock;
        int n = p.Steps.Count;
        var step = p.CurrentStep;
        bool running = p.Status is ProgramStatus.Running or ProgramStatus.Held;
        double el = running && p.StepStartedAt is { } ss ? Ms(now, ss) / 1000 : 0;
        double stepRemainS = running ? Math.Max(0, p.StepDurS - el) : 0;
        double after = 0;
        if (running) for (int i = p.StepIndex + 1; i < n; i++) after += StepPlanS(p.Steps[i]);
        double totalS = 0;
        foreach (var st in p.Steps) totalS += StepPlanS(st);
        double doneS;
        if (p.Status == ProgramStatus.Done) doneS = totalS;
        else
        {
            doneS = 0;
            for (int i = 0; i < p.StepIndex && i < n; i++) doneS += StepPlanS(p.Steps[i]);
            doneS += Math.Min(el, StepPlanS(step));
        }
        bool waitGas = running && ctx.ChannelState == StateKey.Wait && !string.IsNullOrEmpty(step?.Gas);
        var endK = step?.End?.Kind;
        bool overdue = p.Status == ProgramStatus.Running && stepRemainS <= 0 && endK is EndKind.Temp or EndKind.Press;
        int remainMin = (int)Math.Ceiling(stepRemainS / 60);
        string target = !overdue ? "" : endK == EndKind.Temp
            ? JsNumber.ToString(step!.End!.V) + " ℃"
            : PressureDisplay.WithUnit(ctx.Model, step!.End!.V);
        string remain = "剩 " + remainMin + " min";
        double within = running && p.StepDurS != 0 && !double.IsNaN(p.StepDurS) ? Clamp(el / p.StepDurS, 0, 1) : p.Status == ProgramStatus.Done ? 1 : 0;
        return new ProgressInfo(
            Name: p.Name, Status: p.Status, StatusText: StatusText(p.Status), Offline: off, WaitGas: waitGas,
            GasName: waitGas ? StepText.GasName(ctx.Model, step!.Gas) : "",
            K: p.StepIndex + 1, N: n, Step: step, StepName: StepText.Name(step), Overdue: overdue,
            RemainText: overdue ? "等" + StepText.Cond(step, ctx.Model).Value : remain,
            RemainShort: overdue ? "→ " + target : remain,
            StepRemainS: stepRemainS, StepRemainMin: remainMin, TotalRemainMin: (int)Math.Ceiling((stepRemainS + after) / 60),
            Within: within, Frac: Clamp(doneS / (totalS != 0 && !double.IsNaN(totalS) ? totalS : 1), 0, 1), WaitWord: ctx.WaitGasWord);
    }

    /// <summary>
    /// 进度文字（样稿 ui.progText）。长：'第 3/4 步 · 剩 42 min' / '已结束 · 共 4 步' / '已下发 · 待运行' / '已中止 · 第 3/4 步' /
    /// '离线 · 停在第 3/4 步' / '第 2/4 步 · 报警暂停' / '第 2/4 步 · 等待进气' / '第 4/4 步 · 等温度 &lt; 50 ℃'；
    /// 短（窄卡片）：'3/4 · 42 min' / '已结束 · 4 步' / '待运行' / '中止 · 3/4' / '3/4 · 离线' / '3/4 · 暂停' / '2/4 · 等待进气' / '4/4 · → 50 ℃'。
    /// 没有程序：'无程序'。真机的报警中止：'报警中止 · 第 2/4 步' / '中止 · 2/4'。
    /// </summary>
    public static string Text(ProgressInfo? pr, bool shortForm = false)
    {
        if (pr is null) return "无程序";
        bool s = shortForm;
        string kn = pr.K + "/" + pr.N;
        switch (pr.Status)
        {
            case ProgramStatus.Done: return s ? "已结束 · " + pr.N + " 步" : "已结束 · 共 " + pr.N + " 步";
            case ProgramStatus.Loaded: return s ? "待运行" : "已下发 · 待运行";
            case ProgramStatus.Aborted: return s ? "中止 · " + kn : "已中止 · 第 " + kn + " 步";
            case ProgramStatus.Failed: return s ? "中止 · " + kn : "报警中止 · 第 " + kn + " 步";
        }
        if (pr.Offline) return s ? kn + " · 离线" : "离线 · 停在第 " + kn + " 步";
        if (pr.Status == ProgramStatus.Held) return s ? kn + " · 暂停" : "第 " + kn + " 步 · 报警暂停";
        if (pr.WaitGas) return s ? kn + " · " + pr.WaitWord : "第 " + kn + " 步 · " + pr.WaitWord;
        if (pr.Overdue) return s ? kn + " · " + pr.RemainShort : "第 " + kn + " 步 · " + pr.RemainText;
        return s ? kn + " · " + pr.StepRemainMin + " min" : "第 " + kn + " 步 · 剩 " + pr.StepRemainMin + " min";
    }

    /// <summary>
    /// 时间轴分段（样稿 PR.sim.segments）：已完成的步骤（log）+ 当前步 + 后面的步骤（按计划时长排下去；当前步至少延到「现在 + 30 s」）。
    /// 已结束只有已完成段；中止（真机的报警中止同样）再加一段 Abort（到结束时刻或现在）。没有程序或从未开始返回空表。
    /// </summary>
    public static IReadOnlyList<ProgramSegment> Segments(ProgramRecord? p, ProgressContext ctx)
    {
        if (p?.StartedAt is null) return Array.Empty<ProgramSegment>();
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        var now = ctx.Clock;
        var output = new List<ProgramSegment>(p.Log.Count + p.Steps.Count);
        foreach (var l in p.Log)
        {
            var st = l.I >= 0 && l.I < p.Steps.Count ? p.Steps[l.I] : null;
            output.Add(new ProgramSegment(l.I, st, StepText.Name(st), l.T0, l.T1, SegmentState.Done, false));
        }
        if (p.Status is ProgramStatus.Done or ProgramStatus.Aborted or ProgramStatus.Failed)
        {
            if (p.Status != ProgramStatus.Done && p.StepStartedAt is { } s0)
                output.Add(new ProgramSegment(p.StepIndex, p.CurrentStep, StepText.Name(p.CurrentStep), s0, p.EndedAt ?? now, SegmentState.Abort, false));
            return output;
        }
        if (p.StepStartedAt is not { } t) return output;
        for (int i = p.StepIndex; i < p.Steps.Count; i++)
        {
            var st = p.Steps[i];
            bool cur = i == p.StepIndex;
            var t1 = cur ? Max(AddMs(t, p.StepDurS * 1000), AddMs(now, 30000)) : AddMs(t, StepPlanS(st) * 1000);
            bool needGas = st.Type is StepKind.Press or StepKind.Purge && st.Gas != null;
            bool wait = needGas && (cur ? ctx.ChannelState == StateKey.Wait : ctx.GasOpen != null && !ctx.GasOpen(st.Gas!));
            output.Add(new ProgramSegment(i, st, StepText.Name(st), t, t1, cur ? SegmentState.Cur : SegmentState.Next, wait));
            t = t1;
        }
        return output;
    }

    /// <summary>步骤卡片的状态（样稿 ui.stepState）：已结束或在当前步之前 = Done；当前步运行中 / 暂停 = Cur、中止 = Abort；其余 Next。</summary>
    public static SegmentState StepState(ProgramRecord p, int i)
    {
        if (p is null) throw new ArgumentNullException(nameof(p));
        if (p.Status == ProgramStatus.Done || i < p.StepIndex) return SegmentState.Done;
        if (i == p.StepIndex)
        {
            if (p.Status is ProgramStatus.Running or ProgramStatus.Held) return SegmentState.Cur;
            if (p.Status is ProgramStatus.Aborted or ProgramStatus.Failed) return SegmentState.Abort;
        }
        return SegmentState.Next;
    }

    /// <summary>
    /// 步骤卡片标题右侧的标记（样稿 ui.stepCard）：'已完成'（✓）/ '已中止'（✕）/ '报警暂停'（⚠）/ 当前步：'当前' / '当前（离线）' /
    /// 等待进气的文字 / '当前 · 等条件' / '当前 · 剩 42 min'；未到的步骤为 ''。图标由界面按 <see cref="StepState"/> 画。
    /// </summary>
    public static string StepMark(ProgramRecord p, int i, ProgressInfo? pr)
    {
        var state = StepState(p, i);
        if (state == SegmentState.Done) return "已完成";
        if (state == SegmentState.Abort) return "已中止";
        if (state != SegmentState.Cur) return "";
        if (p.Status == ProgramStatus.Held) return "报警暂停";
        if (pr is null) return "当前";
        if (pr.Offline) return "当前（离线）";
        if (pr.WaitGas) return pr.WaitWord;
        if (pr.Overdue) return "当前 · 等条件";
        return "当前 · 剩 " + pr.StepRemainMin + " min";
    }

    /// <summary>
    /// 分段进度条（样稿 ui.stepBar）：每步一段。已完成 100 %；当前步按 Within 填充（至少 8 %），运行中且在线且不在等进气时呼吸；
    /// 报警暂停同样按进度、不呼吸；中止（含报警中止）满格灰斜纹；未到 0。没有程序返回空表（界面画一段空轨道）。
    /// </summary>
    public static IReadOnlyList<StepBarSegment> Bar(ProgramRecord? p, ProgressInfo? pr, bool online)
    {
        if (p is null) return Array.Empty<StepBarSegment>();
        var list = new List<StepBarSegment>(p.Steps.Count);
        for (int i = 0; i < p.Steps.Count; i++)
        {
            var state = StepBarState.Pending;
            double w = 0;
            bool pulse = false;
            if (p.Status == ProgramStatus.Done || i < p.StepIndex) { state = StepBarState.Done; w = 100; }
            else if (i == p.StepIndex)
            {
                double within = pr?.Within ?? 0;
                if (p.Status == ProgramStatus.Running) { state = StepBarState.Cur; pulse = online && !(pr?.WaitGas ?? false); w = Math.Max(8, within * 100); }
                else if (p.Status == ProgramStatus.Held) { state = StepBarState.Held; w = Math.Max(8, within * 100); }
                else if (p.Status is ProgramStatus.Aborted or ProgramStatus.Failed) { state = StepBarState.Abort; w = 100; }
            }
            list.Add(new StepBarSegment(state, w, pulse, StepText.Name(p.Steps[i])));
        }
        return list;
    }

    /// <summary>进度条的无障碍文字（样稿 stepBar 的 aria-label）：'加氢-标准 第 3/4 步'。</summary>
    public static string BarLabel(ProgramRecord p) => p.Name + " 第 " + (p.StepIndex + 1) + "/" + p.Steps.Count + " 步";

    private static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));

    private static double Ms(DateTime a, DateTime b) => (a - b).Ticks / (double)TimeSpan.TicksPerMillisecond;

    private static DateTime AddMs(DateTime t, double ms) => t.AddTicks((long)Math.Round(ms * TimeSpan.TicksPerMillisecond));

    private static DateTime Max(DateTime a, DateTime b) => a >= b ? a : b;
}
