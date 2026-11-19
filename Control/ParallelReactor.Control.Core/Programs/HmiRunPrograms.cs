using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.History;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Core.Programs;

/// <summary>
/// 设备上的程序（融合版 F3）：不是本机下发的程序（触屏放上去的、别的客户端下发的、台账对不上的）也要有真实的步骤 —— 步骤条、本步剩余、预计结束。
/// HMI v0.6 的 GET /channels/{id}/program/run 给出这次执行下发时冻结的步骤（§6.9 格式 + 中文名称），这里按需取、缓存、转换成 <see cref="ProgramRecord"/>
/// （<see cref="ProgramSource.Hmi"/>，计时与估算同台账：<see cref="ProgramLedger.RunRecord"/>）。
/// <list type="bullet">
/// <item><b>什么时候取</b>：帧里这一路有 program（有开始时刻）、而缓存里没有这一次执行（启动号 + 开始时刻）的步骤 —— 新的执行；或者这一次执行换了步（步骤冻结、
///   换步再取一次只是为了核对：取回来之前照旧用已有的步骤）。<b>不是每帧</b>：同一次执行、同一步只取一次；同一路同时只有一个请求。</item>
/// <item><b>失败</b>（网络、HMI 忙、取回来的不是帧里这一次执行）：按 <see cref="RetryMin"/> 起翻倍、最长 <see cref="RetryMax"/> 退避，
///   到点之后下一次被问到时再取 —— 不刷屏、不打满 HMI。<b>404</b>（「本次开机以来没有执行过程序」，或没有这个端点的旧 HMI）：这一步不再取，换步 / 新的执行时再试。</item>
/// <item><b>R5</b>：只在缓存的执行与帧里报的这一次执行的启动号、开始时刻都对得上时给出记录；HMI 重启 / 换了一次执行之后旧的步骤立即不用。
///   台账对得上的程序（本机下发）优先，由调用方决定（LiveDeviceSource.Program）。</item>
/// </list>
/// 取数的结果在 <paramref name="fetch"/> 的续体线程上落地（App：UI 线程），落地后 <see cref="Version"/> 加一并触发 <see cref="Landed"/>（调用方刷新程序显示）。
/// 状态有锁，可以在任意线程上问。
/// </summary>
public sealed class HmiRunPrograms
{
    /// <summary>设备上的程序记录的名字：HMI 的运行记录里没有程序名。</summary>
    public const string DefaultName = "设备程序";

    /// <summary>一次执行的开始时刻对得上的容差（帧与 program/run 取自同一个 HMI 字段，序列化相同；容差只防序列化精度）。</summary>
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(1);

    private readonly Func<int, CancellationToken, Task<ProgramRunDto?>> _fetch;
    private readonly Func<DateTime> _utcNow;
    private readonly object _lock = new();
    private readonly Dictionary<int, Slot> _slots = new();
    private int _gen;

    /// <param name="fetch">取第 n 路的运行记录（GET /channels/{n}/program/run；没执行过返回 null）。</param>
    /// <param name="utcNow">退避用的时钟（UTC）。</param>
    public HmiRunPrograms(Func<int, CancellationToken, Task<ProgramRunDto?>> fetch, Func<DateTime>? utcNow = null)
    {
        _fetch = fetch ?? throw new ArgumentNullException(nameof(fetch));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>失败后的第一次等待（之后翻倍）。</summary>
    public TimeSpan RetryMin { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>失败后等待的上限。</summary>
    public TimeSpan RetryMax { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>取到的步骤变了（新的一次执行的步骤落地 / 清空）加一：调用方的程序记录缓存按它失效。</summary>
    public int Version { get { lock (_lock) return _ver; } }
    private int _ver;

    /// <summary>某一路取到了这一次执行的步骤（参数 = 反应釜号）。在取数的续体线程上触发。</summary>
    public event Action<int>? Landed;

    /// <summary>发出过的请求数（测试 / 诊断）。</summary>
    public int Requests { get { lock (_lock) return _requests; } }
    private int _requests;

    private sealed class Slot
    {
        public string Boot = "";
        public DateTime StartedAt;
        /// <summary>已经为哪一步取过（成功落地的）；int.MinValue = 这一次执行还没取到过。</summary>
        public int FetchedStep = int.MinValue;
        public ProgramRunSnap? Run;
        public IReadOnlyList<ProgramStepDto>? Dto;
        /// <summary>转换好的步骤（按 <see cref="Model"/>；不认识的步骤类型为 null）。</summary>
        public IReadOnlyList<ProgramStep>? Steps;
        public ModelProfile? Model;
        public bool InFlight;
        public int Failures;
        public DateTime RetryAt = DateTime.MinValue;
    }

    /// <summary>
    /// 第 n 路的程序记录（帧里这一次执行 + 取到的冻结步骤 + 步骤计时 + 估算）；还没取到 / 对不上 / 有不认识的步骤类型为 null。
    /// 需要时发起一次取数（<paramref name="allowFetch"/> = 现在能发请求：已连接）。<paramref name="frame"/> = 最新一帧（估算时刻 = 它的 Ts）；
    /// <paramref name="tracker"/> = 台账的步骤计时（每帧观察全部通道，见 ProgramLedger.ObserveFrame）。
    /// </summary>
    public ProgramRecord? RecordOf(int n, TelemetrySnapshot? frame, string? bootId, ModelProfile? model, StepTracker? tracker,
        ChannelHistory? history = null, EtaRates? rates = null, bool allowFetch = true)
    {
        ChannelSnap? ch = null;
        if (frame?.Channels is { } list) foreach (var c in list) if (c?.Id == n) { ch = c; break; }
        var steps = StepsFor(n, ch?.Program, bootId, model, allowFetch);
        if (steps is null || ch?.Program is not { } hmi) return null;
        var rec = new ProgramRecord
        {
            Name = DefaultName, Steps = steps, Source = ProgramSource.Hmi, StartedByTouch = hmi.StartedBy == "local",
            EndReason = RunOf(n, hmi, bootId)?.EndReason is { Length: > 0 } er ? er : null,
        };
        return ProgramLedger.RunRecord(rec, ch, tracker?.TrackOf(n, hmi, bootId), frame!.Ts, history, rates);
    }

    /// <summary>
    /// 第 n 路这一次执行（<paramref name="p"/> 的启动号 + 开始时刻）的步骤（没有估算的原始步骤）；还没取到 / 对不上为 null。需要时发起取数（见类说明）。
    /// </summary>
    public IReadOnlyList<ProgramStep>? StepsFor(int n, ChannelProgramSnap? p, string? bootId, ModelProfile? model, bool allowFetch = true)
    {
        if (p?.StartedAt is not { } s0 || string.IsNullOrEmpty(bootId)) return null;
        var startedAt = Utc(s0);
        Slot slot;
        bool start = false;
        int gen;
        lock (_lock)
        {
            if (!_slots.TryGetValue(n, out slot!) || slot.Boot != bootId || slot.StartedAt != startedAt)
            {
                // 新的一次执行（或 HMI 重启）：之前取到的步骤不再用（R5）；在途的请求落地时按执行的身份丢弃
                bool had = slot?.Steps != null || slot?.Run != null;
                slot = new Slot { Boot = bootId, StartedAt = startedAt };
                _slots[n] = slot;
                if (had) _ver++;
            }
            if (allowFetch && !slot.InFlight && slot.FetchedStep != p.StepIndex && _utcNow() >= slot.RetryAt)
            {
                slot.InFlight = true;
                _requests++;
                start = true;
            }
            gen = _gen;
            if (!ReferenceEquals(slot.Model, model)) Convert(slot, model);   // 型号换了（气路键按型号表）：重新转换
        }
        if (start) _ = FetchAsync(n, bootId, startedAt, p.StepIndex, gen);
        // 步数与帧里报的不同（不该发生）：步骤序号对不上，不显示（同台账）
        lock (_lock) return slot.Steps is { Count: > 0 } st && st.Count == p.StepCount ? st : null;
    }

    /// <summary>这一路取到的运行记录（HMI 的阶段文字、发起者…）；只给与 <paramref name="p"/> 同一次执行的。</summary>
    public ProgramRunSnap? RunOf(int n, ChannelProgramSnap? p, string? bootId)
    {
        if (p?.StartedAt is not { } s0 || string.IsNullOrEmpty(bootId)) return null;
        lock (_lock)
            return _slots.TryGetValue(n, out var s) && s.Boot == bootId && s.StartedAt == Utc(s0) ? s.Run : null;
    }

    /// <summary>第 n 路有请求在途（测试 / 诊断）。</summary>
    public bool Pending(int n)
    {
        lock (_lock) return _slots.TryGetValue(n, out var s) && s.InFlight;
    }

    /// <summary>全部作废（HMI 重启、换了设备 / 地址）：在途的请求落地时丢弃。</summary>
    public void Reset()
    {
        lock (_lock)
        {
            if (_slots.Count > 0) _ver++;
            _slots.Clear();
            _gen++;
        }
    }

    private static void Convert(Slot s, ModelProfile? model)
    {
        s.Model = model;
        s.Steps = s.Dto is { } d ? ProgramDocs.FromRun(d, model) : null;
    }

    private async Task FetchAsync(int n, string boot, DateTime startedAt, int step, int gen)
    {
        ProgramRunDto? dto = null;
        Exception? error = null;
        bool none = false;
        try { dto = await _fetch(n, CancellationToken.None).ConfigureAwait(true); none = dto is null; }
        catch (ApiException ex) when (ex.Status == 404) { none = true; }   // 没有这个端点的旧 HMI（HTTP_404）同样
        catch (Exception ex) { error = ex; }
        bool landed = false;
        lock (_lock)
        {
            if (gen != _gen || !_slots.TryGetValue(n, out var s) || s.Boot != boot || s.StartedAt != startedAt) return;   // 换了一次执行 / 作废了：丢弃
            s.InFlight = false;
            if (none)
            {
                // 设备说这一路没有运行记录：这一步不再问（不退避重试 —— 旧 HMI 每帧都会是 404），换步 / 新的执行时再试
                s.Failures = 0;
                s.RetryAt = DateTime.MinValue;
                s.FetchedStep = step;
                return;
            }
            var run = dto?.Run;
            bool same = error is null && run is not null && (run.Channel == n || run.Channel == 0)
                && (Utc(run.StartedAt) - startedAt).Duration() <= StartTolerance;
            if (!same)
            {
                // 失败 / 没有记录 / 取回来的不是这一次执行（HMI 的记录还没跟上）：退避，到点后下一次被问到时再取
                s.Failures++;
                double sec = Math.Min(RetryMax.TotalSeconds, RetryMin.TotalSeconds * Math.Pow(2, Math.Min(16, s.Failures - 1)));
                s.RetryAt = _utcNow() + TimeSpan.FromSeconds(sec);
                return;
            }
            s.Failures = 0;
            s.RetryAt = DateTime.MinValue;
            s.FetchedStep = step;
            s.Run = run;
            if (!SameSteps(s.Dto, run!.Steps))
            {
                s.Dto = run.Steps;
                Convert(s, s.Model);
                _ver++;
                landed = true;
            }
        }
        if (landed) Landed?.Invoke(n);
    }

    /// <summary>两份冻结步骤一样（同一次执行换步后再取：一般完全一样，不必重新转换、不触发刷新）。</summary>
    private static bool SameSteps(IReadOnlyList<ProgramStepDto>? a, IReadOnlyList<ProgramStepDto>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i].Type != b[i].Type || a[i].Name != b[i].Name) return false;
            if (a[i].Params.ValueKind != b[i].Params.ValueKind) return false;
            if (a[i].Params.ValueKind != System.Text.Json.JsonValueKind.Undefined && a[i].Params.GetRawText() != b[i].Params.GetRawText()) return false;
        }
        return true;
    }

    private static DateTime Utc(DateTime t) => t.Kind == DateTimeKind.Utc ? t : t.Kind == DateTimeKind.Local ? t.ToUniversalTime() : DateTime.SpecifyKind(t, DateTimeKind.Utc);
}
