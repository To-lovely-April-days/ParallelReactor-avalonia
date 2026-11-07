using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Control.Tests.Fakes;
using ParallelReactor.PerfTest.Engine;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 性能测试引擎的假设备：记录每条命令；内部维护 8 通道的一阶温度响应（τ 可调）、恒压 225 psi、搅拌状态；
/// <see cref="Tick"/> 按假时钟推进一帧并通过 <see cref="ITelemetryFeed.Frame"/> 推出。
/// </summary>
public sealed class FakeReactor : IReactorApi, ITelemetryFeed
{
    private readonly FakeClock _clock;
    private readonly double[] _temp = new double[9];
    private readonly double[] _sp = new double[9];
    private readonly bool[] _heating = new bool[9];
    private readonly string[] _state = new string[9];
    private bool _stirOn; private int _rpm;
    private long _seq;
    private bool _sessionActive; private string? _sessionId;

    public FakeReactor(FakeClock clock, double ambient = 25, double tauSec = 60)
    {
        _clock = clock; TauSec = tauSec;
        for (int i = 1; i <= 8; i++) { _temp[i] = ambient; _sp[i] = 25; _state[i] = i <= 4 ? "idle" : "idle"; }
    }

    public double TauSec { get; set; }
    public bool CanControl { get; set; } = true;
    public bool EstopActive { get; set; }
    public List<string> Commands { get; } = new();
    public Func<string, ApiOutcome?>? Intercept { get; set; }
    public SettingsDto Settings { get; set; } = new(new LimitsDto(250, 500, 1500, 500), new AlarmSettingsDto(510, 250, true, 0, 0), new LeakTestSettingsDto(100, 10, 30, 2, 30, null),
        new ApproachSettingsDto(5, 1, 1, 2, 0.5, 60), new StirSettingsDto(1, 50, 1000, 16), new PressureSettingsDto(4.137), Array.Empty<TempBandSnap>(), new SettingsNotesDto(Array.Empty<string>()), 7);
    public bool SessionAlreadyActive { get; set; }

    public event Action<TelemetrySnapshot>? Frame;
    public event Action<SafetyEvent>? Safety;
    public TelemetrySnapshot? Latest { get; private set; }

    public void RaiseSafety(SafetyEvent e) => Safety?.Invoke(e);
    public double Temp(int rv) => _temp[rv];
    public bool Heating(int rv) => _heating[rv];
    public bool StirOn => _stirOn;

    /// <summary>推进 dt 秒的物理模型并推一帧。</summary>
    public void Tick(double dtSec = 1)
    {
        for (int i = 1; i <= 8; i++)
        {
            double target = _heating[i] ? _sp[i] : 25;
            _temp[i] += (target - _temp[i]) * (1 - Math.Exp(-dtSec / TauSec));
        }
        var chans = new ChannelSnap[8];
        for (int i = 1; i <= 8; i++)
            chans[i - 1] = new ChannelSnap(i, i <= 4, _state[i], null, false, false, null, Math.Round(_temp[i], 1), _sp[i], _sp[i], _heating[i] ? Math.Clamp((_sp[i] - _temp[i]) * 10, 0, 100) : 0, true, false, null, "fixed",
                new ApproachSnap("off", null, null, ""), null, 225.0 + (i * 0.1), 1234 + i, 0, null, false, false, null);
        var f = new TelemetrySnapshot("telemetry", ++_seq, _clock.UtcNow, true, 0, new ModeSnap("remote", "PC"), new EstopSnap(EstopActive, "ok", "ok", "ok", null, false, false),
            new CommSnap(true, true, true, true), chans, new StirSnap(_stirOn, _rpm, true, false, 0.3, 24.1, null, null, 1000), new GasSnap(Array.Empty<GasInletSnap>(), false, false, null, null),
            new ProgramSnap(false, null, "idle", 0, 0, "", ""), new ProgramsSnap(0, Array.Empty<int>(), null, Array.Empty<int>(), ""), new LeakSnap(false, 0, null, null, false, false),
            new SessionSnap(_sessionActive, _sessionId, _sessionId, null));
        Latest = f;
        Frame?.Invoke(f);
    }

    private Task<ApiOutcome> Cmd(string text, Action? apply = null)
    {
        Commands.Add(text);
        var hit = Intercept?.Invoke(text);
        if (hit != null) return Task.FromResult(hit);
        apply?.Invoke();
        return Task.FromResult(new ApiOutcome(true, Guid.NewGuid().ToString("N"), "confirmed", 200, null, null, Commands.Count));
    }

    public Task<DeviceInfoDto?> GetDeviceAsync(CancellationToken ct) => Task.FromResult<DeviceInfoDto?>(new DeviceInfoDto("PR8", "PR8-FAKE", "1.0.0+fake", "v1", 8, Array.Empty<ChannelInstalledDto>(), new[] { 1, 2, 3, 4 },
        new HardwareDto(new HardwareModuleDto(false, null, null, null), new HardwareModuleDto(false, null, null, null), new HardwareModuleDto(false, null, null, null), new HardwareModuleDto(false, null, null, null)),
        new UnitsDto("C", "psi", "rpm"), true, false, _clock.UtcNow, "fakeboot", true, 0, 1));
    public Task<SettingsDto?> GetSettingsAsync(CancellationToken ct) => Task.FromResult<SettingsDto?>(Settings);
    public Task<TelemetrySnapshot?> GetSnapshotAsync(CancellationToken ct) { if (Latest == null) Tick(0); return Task.FromResult(Latest); }
    public Task<CommDto?> GetCommAsync(CancellationToken ct) => Task.FromResult<CommDto?>(null);

    public Task<ApiOutcome> EnableChannelAsync(int rv, CancellationToken ct) => Cmd($"enable {rv}", () => _state[rv] = "ready");
    public Task<ApiOutcome> DisableChannelAsync(int rv, CancellationToken ct) => Cmd($"disable {rv}", () => { _state[rv] = "idle"; _heating[rv] = false; });
    public Task<ApiOutcome> StopChannelAsync(int rv, CancellationToken ct) => Cmd($"stopChannel {rv}", () => { _state[rv] = "done"; _heating[rv] = false; });
    public Task<ApiOutcome> StartHeatAsync(int rv, double? tempC, CancellationToken ct) => Cmd($"heatStart {rv} {tempC}", () => { _heating[rv] = true; _sp[rv] = tempC ?? _sp[rv]; _state[rv] = "heating"; });
    public Task<ApiOutcome> SetSetpointAsync(int rv, double tempC, CancellationToken ct) => Cmd($"setpoint {rv} {tempC}", () => _sp[rv] = tempC);
    public Task<ApiOutcome> StopHeatAsync(int rv, CancellationToken ct) => Cmd($"heatStop {rv}", () => { _heating[rv] = false; _state[rv] = "done"; });
    public Task<ApiOutcome> SetStirAsync(bool? on, int? rpm, CancellationToken ct) => Cmd($"stir on={on} rpm={rpm}", () => { _stirOn = on ?? true; if (rpm.HasValue) _rpm = rpm.Value; });
    public Task<ApiOutcome> StopStirAsync(CancellationToken ct) => Cmd("stirStop", () => _stirOn = false);
    public Task<ApiOutcome> ReplaceProgramAsync(int rv, string programJson, CancellationToken ct) => Cmd($"programPut {rv} {programJson}");
    public Task<ApiOutcome> RunProgramAsync(int rv, string? sessionId, string idem, CancellationToken ct) => Cmd($"programRun {rv}");
    public Task<ApiOutcome> AbortProgramAsync(int rv, CancellationToken ct) => Cmd($"programAbort {rv}");
    public Task<ApiOutcome> StartAutotuneAsync(int rv, double tempC, CancellationToken ct) => Cmd($"autotuneStart {rv} {tempC}");
    public Task<ApiOutcome> CancelAutotuneAsync(int rv, CancellationToken ct) => Cmd($"autotuneCancel {rv}");
    public Task<ApiOutcome> StartLeakTestAsync(int[] channels, double? testPsi, string idem, CancellationToken ct) => Cmd($"leakTestStart {string.Join(",", channels)} {testPsi}");
    public Task<ApiOutcome> AbortLeakTestAsync(CancellationToken ct) => Cmd("leakTestAbort");
    public Task<ApiOutcome> EstopAsync(string? note, CancellationToken ct) => Cmd("estop", () => { EstopActive = true; for (int i = 1; i <= 8; i++) _heating[i] = false; _stirOn = false; });
    public Task<ApiOutcome> StopAllAsync(bool stopStir, CancellationToken ct) => Cmd("stopAll", () => { for (int i = 1; i <= 8; i++) _heating[i] = false; if (stopStir) _stirOn = false; });
    public Task<ApiOutcome> RequestControlAsync(CancellationToken ct) => Cmd("controlRequest", () => CanControl = true);

    public Task<ApiOutcome> BeginSessionAsync(string testId, string? note, string idem, CancellationToken ct)
    {
        Commands.Add($"sessionBegin {testId}");
        if (SessionAlreadyActive) return Task.FromResult(new ApiOutcome(false, null, "failed", 409, "SESSION_ACTIVE", "已有会话"));
        _sessionActive = true; _sessionId = testId;
        return Task.FromResult(new ApiOutcome(true, "op-sess", "confirmed", 200, null, testId));
    }
    public Task<ApiOutcome> EndSessionAsync(CancellationToken ct) { Commands.Add("sessionEnd"); _sessionActive = false; SessionAlreadyActive = false; return Task.FromResult(new ApiOutcome(true, "op-end", "confirmed", 200, null, null)); }
    public Task<ApiOutcome> AddMarkerAsync(string text, string idem, CancellationToken ct) { Commands.Add("marker " + text); return Task.FromResult(new ApiOutcome(true, "op-mk", "confirmed", 200, null, null)); }
    public async Task<bool> DownloadSessionCsvAsync(string sessionId, Stream dest, CancellationToken ct)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes("ts,rv,tempC\n2026-11-07T06:00:00Z,1,25\n");
        await dest.WriteAsync(bytes, ct);
        return true;
    }
}

/// <summary>自动应答的操作员：确认一律 yes（可脚本化），录入按字段返回预设值（默认 600），安全事件按 <see cref="SafetyAnswer"/>。</summary>
public sealed class AutoPrompt : IOperatorPrompt
{
    public List<ConfirmRequest> Confirms { get; } = new();
    public List<EntryRequest> Entries { get; } = new();
    public List<SafetyPrompt> Safeties { get; } = new();
    public Func<ConfirmRequest, ConfirmResult>? OnConfirm { get; set; }
    public Func<EntryRequest, EntryResult>? OnEntry { get; set; }
    public SafetyDecision SafetyAnswer { get; set; } = SafetyDecision.Continue;
    public double DefaultValue { get; set; } = 600;
    /// <summary>非空时：录入 / 确认挂起直到它完成（模拟操作员慢）。</summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<ConfirmResult> ConfirmAsync(ConfirmRequest request, CancellationToken ct)
    {
        Confirms.Add(request);
        if (Gate != null) await Gate.Task.WaitAsync(ct);
        if (OnConfirm != null) return OnConfirm(request);
        return new ConfirmResult(true, request.Checklist?.Select(_ => true).ToList(), request.TwoSigners ? "甲" : null, request.TwoSigners ? "乙" : null);
    }

    public async Task<EntryResult> EnterAsync(EntryRequest request, CancellationToken ct)
    {
        Entries.Add(request);
        if (Gate != null) await Gate.Task.WaitAsync(ct);
        if (OnEntry != null) return OnEntry(request);
        return Fill(request, DefaultValue);
    }

    public static EntryResult Fill(EntryRequest request, double value)
    {
        var values = new Dictionary<string, IReadOnlyList<double>>();
        var texts = new Dictionary<string, string>();
        foreach (var f in request.Fields)
        {
            if (f.IsText) { texts[f.Key] = "正常"; continue; }
            int n = f.Repeat * (f.PerChannel ? Math.Max(1, request.Channels.Count) : 1);
            var v = Math.Clamp(value, f.Min ?? double.MinValue, f.Max ?? double.MaxValue);
            values[f.Key] = Enumerable.Repeat(v, n).ToList();
        }
        return new EntryResult(false, values, texts);
    }

    public Task<SafetyDecision> SafetyAsync(SafetyPrompt prompt, CancellationToken ct)
    {
        Safeties.Add(prompt);
        return Task.FromResult(SafetyAnswer);
    }
}

/// <summary>驱动：让引擎在假时钟上跑完，每步推进 1 s 并 Tick 一帧。</summary>
public static class PerfDrive
{
    public static async Task<T> RunAsync<T>(Task<T> run, FakeClock clock, FakeReactor dev, int maxSteps = 20000, double dtSec = 1)
    {
        int n = 0;
        while (!run.IsCompleted)
        {
            if (++n > maxSteps) throw new TimeoutException("引擎未在 " + maxSteps + " 步内完成");
            dev.Tick(dtSec);
            await clock.AdvanceAsync(TimeSpan.FromSeconds(dtSec), 2);
        }
        return await run;
    }
}
