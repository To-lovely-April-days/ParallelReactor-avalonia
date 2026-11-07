using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using ParallelReactor.Services;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Api.Hmi;

/// <summary>
/// <see cref="IApiSources"/> 的 HMI 实现：把 MainViewModel / ControlLoop / HardwareOptions 接到接口层。
/// 除 <see cref="PreflightAsync"/>（Dispatcher 投递，5 s 超时）外，所有成员都不进 UI 线程、不读 VM 的可变状态：
/// 设备信息来自静态配置，快照来自 ControlLoop.Snapshots，事件来自 Logger.Events。
/// </summary>
public sealed class HmiApiSources : IApiSources
{
    public static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(5);

    private readonly MainViewModel _vm;
    private readonly ControlLoop _loop;
    private readonly Lazy<bool?> _clockSynced = new(ProbeClockSynced);
    private ProgramSchemaDto? _schema;
    private long _schemaRevision = -1;

    public HmiApiSources(MainViewModel vm, ControlLoop loop)
    {
        _vm = vm;
        _loop = loop;
        BootId = Guid.NewGuid().ToString("N")[..8];
        BootTime = Process.GetCurrentProcess().StartTime.ToUniversalTime();
    }

    public SnapshotStore Snapshots => _loop.Snapshots;
    public EventStore Events => _vm.Logger.Events ?? throw new InvalidOperationException("DataLogger 未配置 EventStore");
    public string DataDir => _vm.Logger.Dir;
    public bool Simulated => HardwareOptions.Sim;
    public string BootId { get; }
    public DateTime BootTime { get; }
    public bool? ClockSynced => _clockSynced.Value;

    public ModeSnap Control => new(_vm.Arbiter.Mode == ControlMode.Remote ? "remote" : "local",
        _vm.Arbiter.Mode == ControlMode.Remote ? _vm.Arbiter.HolderName : null);

    /// <summary>任意线程：经 DataLogger.Event（线程安全）记一条「接口」事件（CSV + JSONL + 环）。</summary>
    public void Audit(string level, string title, string detail) => _vm.Logger.Event(DateTimeOffset.Now, level, "接口", title, detail);

    // ===================== /device =====================

    public DeviceInfoDto Device(ApiConfig cfg)
    {
        var installed = HardwareOptions.InstalledChannels;
        var snap = Snapshots.Current;
        double? fullScale = snap?.Settings.PressFullScaleMPa;
        return new DeviceInfoDto(
            ApiVersionInfo.Model, cfg.Serial, HmiVersion(), ApiVersionInfo.ApiVersion, ChannelRange.ChannelCount,
            Enumerable.Range(1, ChannelRange.ChannelCount).Select(i => new ChannelInstalledDto(i, Array.IndexOf(installed, i) >= 0)).ToArray(),
            installed,
            new HardwareDto(
                new HardwareModuleDto(HardwareOptions.UseRealTemp, HardwareOptions.Sim ? "sim" : HardwareOptions.TempPort, HardwareOptions.TempBaud, null),
                new HardwareModuleDto(HardwareOptions.UseRealAnalog, HardwareOptions.Sim ? "sim" : HardwareOptions.AnalogPort, HardwareOptions.AnalogBaud, fullScale),
                new HardwareModuleDto(HardwareOptions.UseRealStir, HardwareOptions.Sim ? "sim" : HardwareOptions.StirPort, HardwareOptions.StirBaud, null),
                new HardwareModuleDto(HardwareOptions.UseRealIo, HardwareOptions.Sim ? "sim" : HardwareOptions.IoPort, HardwareOptions.IoBaud, null)),
            new UnitsDto("C", "psi", "rpm"),
            HardwareOptions.Sim, HardwareOptions.SimRefused, BootTime, BootId, ClockSynced, Events.Head, Events.SeqEpoch);
    }

    /// <summary>程序集版本 + InformationalVersion（含 git 短哈希时一并给出，设计稿 §12）。</summary>
    public static string HmiVersion()
    {
        var asm = typeof(HmiApiSources).Assembly;
        var v = asm.GetName().Version;
        string s = v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return !string.IsNullOrWhiteSpace(info) && info != s ? info : s;
    }

    /// <summary>Linux：timedatectl show -p NTPSynchronized --value（2 s 超时）；其他平台 / 失败为 null。只在首次访问时探测一次。</summary>
    private static bool? ProbeClockSynced()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("timedatectl", "show -p NTPSynchronized --value")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (p == null) return null;
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return null; }
            var out1 = p.StandardOutput.ReadToEnd().Trim();
            return out1 switch { "yes" => true, "no" => false, _ => null };
        }
        catch { return null; }
    }

    // ===================== /preflight（唯一进 UI 线程的读取）=====================

    public async Task<PreflightDto> PreflightAsync(string clientName, CancellationToken ct)
    {
        var src = CommandSource.Remote(clientName, clientName);
        var op = Dispatcher.UIThread.InvokeAsync(() =>
        {
            // dryRun：只做预运行检查、不启动（Record 类，任何控制模式都允许，见 OperatorCommands.TryRun）。
            // 检查项由 MainViewModel.ComputePreRun 纯计算得到，不写 PreRunChecks：远程轮询不会闪动操作员正打开的确认框
            var t = _vm.Ops.TryRun(src, dryRun: true);
            var r = t.IsCompletedSuccessfully ? t.Result : throw new InvalidOperationException("TryRun(dryRun) 未同步完成");
            var pre = _vm.ComputePreRun();
            var checks = pre.Checks.Select(c => new PreflightCheckDto(c.Kind, c.Title, c.Detail)).ToArray();
            var channels = _vm.Reactors.Where(x => RunSelection.CanStartHeat(_vm.RunChannelOf(x))).Select(x => x.Id).ToArray();
            bool canRun = r.Ok && pre.CanRun;   // 被仲裁 / 其他守卫拒绝时同样不可运行
            return new PreflightDto(canRun, r.Ok ? pre.Summary : r.Message, checks, channels);
        }, DispatcherPriority.Background);
        var task = op.GetTask();
        var done = await Task.WhenAny(task, Task.Delay(UiTimeout, ct)).ConfigureAwait(false);
        if (done != task)
        {
            ct.ThrowIfCancellationRequested();
            // 还没开始执行：撤销，确定未执行（§13.1）；已开始执行则再等一个超时，仍没回来按 UI 线程卡住处理（不无限等）
            if (op.Status == DispatcherOperationStatus.Pending && op.Abort()) throw new UiThreadTimeoutException();
            done = await Task.WhenAny(task, Task.Delay(UiTimeout, ct)).ConfigureAwait(false);
            if (done != task) { ct.ThrowIfCancellationRequested(); throw new UiThreadTimeoutException(); }
        }
        return await task.ConfigureAwait(false);
    }

    // ===================== /program-schema =====================

    /// <summary>按当前设置修订号缓存：StepDef.Registry 是静态表，只有生效范围随设置变化。</summary>
    public ProgramSchemaDto ProgramSchema()
    {
        var snap = Snapshots.Current;
        long rev = snap?.Settings.Revision ?? 0;
        var cached = _schema;
        if (cached != null && Volatile.Read(ref _schemaRevision) == rev && snap != null) return cached;
        var s = snap?.Settings;
        var limits = s == null
            ? new ProgramLimitSettings(200, 250, 1000, 500, 510)
            : new ProgramLimitSettings(s.TMaxSp, s.OverTemp, s.RpmMax, s.PMaxSp, s.OverPressure);
        var dto = BuildSchema(limits, rev);
        if (snap != null) { _schema = dto; Volatile.Write(ref _schemaRevision, rev); }
        return dto;
    }

    public static ProgramSchemaDto BuildSchema(ProgramLimitSettings limits, long revision)
    {
        var steps = new List<StepSchemaDto>();
        foreach (var type in Enum.GetValues<StepType>())
        {
            if (!StepDef.Registry.TryGetValue(type, out var def)) continue;
            string tname = type.ToString().ToLowerInvariant();
            var fields = new List<FieldSchemaDto>();
            if (type == StepType.Heat)
            {
                fields.Add(new FieldSchemaDto("mode", "升温方式", "sel", null, null, null, null, null, null,
                    ProgramSchemaCodes.Options["heat.mode"], true, null, null, null, null));
            }
            foreach (var f in def.Fields)
            {
                string key = $"{tname}.{f.Key}";
                bool available = !ProgramSchemaCodes.UnavailableFields.Contains(key);
                if (f.Kind == "sel")
                {
                    var opts = ProgramSchemaCodes.Options.TryGetValue(key, out var o) ? o
                        : (f.Options ?? Array.Empty<string>()).Select(l => new SchemaOption(l, l)).ToArray();
                    fields.Add(new FieldSchemaDto(f.Key, f.Label, "sel", null, null, null, null, null, null, opts, true, null, null, null, null));
                    continue;
                }
                var (lo, hi) = ProgramLimits.FieldRange(type, f.Key, f.Min, f.Max, limits);
                double? dflt = f.Default is double d ? d : f.Default is int n ? n : null;
                fields.Add(new FieldSchemaDto(f.Key, f.Label, "num", f.Unit, dflt, lo, hi, f.Min, f.Max, null,
                    available, available ? null : ProgramSchemaCodes.GasUnavailableReason, null, null, null));
            }
            if (type == StepType.Heat)
            {
                // 曲线段：ProgramView 曲线编辑器的键盘范围；段温度同样受 TMaxSp / OverTemp 封顶
                var segFields = ProgramSchemaCodes.SegmentFields.Select(sf =>
                {
                    double hi = sf.Key == "temp" ? Math.Min(sf.Max, ProgramLimits.HeatTempMax(limits)) : sf.Max;
                    return new FieldSchemaDto(sf.Key, sf.Label, "num", sf.Unit, null, sf.Min, hi, sf.Min, sf.Max, null, true, null, null, null, null);
                }).ToArray();
                fields.Add(new FieldSchemaDto("segments", "曲线段（mode=curve 时使用）", "array", null, null, null, null, null, null, null, true, null,
                    segFields, ProgramSchemaCodes.SegmentsMin, ProgramSchemaCodes.SegmentsMax));
            }
            steps.Add(new StepSchemaDto(tname, def.Name, def.Chip, def.Help, fields.ToArray()));
        }
        return new ProgramSchemaDto(ApiVersionInfo.ApiVersion, revision, steps.Select(x => x.Type).ToArray(), steps.ToArray());
    }
}
