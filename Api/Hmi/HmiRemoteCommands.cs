using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using ParallelReactor.ViewModels;

namespace ParallelReactor.Api.Hmi;

/// <summary>
/// <see cref="IRemoteCommands"/> 的 HMI 实现：每个方法 = 经 <see cref="UiCommandBridge.RunAsync"/> 在 UI 线程上调用一个
/// <c>Ops.TryX(CommandSource.Remote(令牌名, 客户端名), …)</c>（命令层设计 §12.1 映射表）。控制类走信号量 + Background 优先级，
/// 停止 / 记录类直接 Normal 优先级。急停（<see cref="EstopAsync"/>）不进 UI 线程：TryEstop 任意线程可调。
/// 本文件不出现 Dispatcher（架构测试）；也不读 VM 的可变状态，除非在 UI 线程回调里。
/// </summary>
public sealed class HmiRemoteCommands : IRemoteCommands
{
    private readonly MainViewModel _vm;
    public HmiRemoteCommands(MainViewModel vm) => _vm = vm;

    private static CommandSource Src(RemoteCaller c) => CommandSource.Remote(c.TokenName, c.ClientName);

    private Task<CommandOutcome> Control(Func<OperatorCommands, CommandSource, Task<OpResult>> call, RemoteCaller c, CancellationToken ct)
        => UiCommandBridge.RunAsync(() => call(_vm.Ops, Src(c)), control: true, ct);

    private Task<CommandOutcome> Stop(Func<OperatorCommands, CommandSource, Task<OpResult>> call, RemoteCaller c, CancellationToken ct)
        => UiCommandBridge.RunAsync(() => call(_vm.Ops, Src(c)), control: false, ct);

    // —— 急停 / 全局停止 ——
    public Task<CommandOutcome> EstopAsync(RemoteCaller c, string? note)
        => Task.FromResult(CommandOutcome.Done(_vm.Ops.TryEstop(Src(c), note).Result));   // TryEstop 同步完成（快速路径），任意线程
    public Task<CommandOutcome> StopAllAsync(RemoteCaller c, bool stopStir, CancellationToken ct) => Stop((o, s) => o.TryStopAll(s, stopStir), c, ct);

    // —— 运行 / 通道 ——
    public Task<CommandOutcome> RunAsync(RemoteCaller c, CancellationToken ct) => Control((o, s) => o.TryRun(s), c, ct);
    public Task<CommandOutcome> EnableChannelAsync(RemoteCaller c, int id, CancellationToken ct) => Control((o, s) => o.TryEnableChannel(s, id, defaultVolume: true), c, ct);
    public Task<CommandOutcome> StartChannelAsync(RemoteCaller c, int id, double? tempC, CancellationToken ct)
        => Control((o, s) =>
        {
            // /channels/{id}/start = 启用 + 启动加热（同一次 UI 线程调用，§7.4）：启用失败直接返回；加热结果为主结果
            var en = o.TryEnableChannel(s, id, defaultVolume: true);
            if (!en.IsCompletedSuccessfully || !en.Result.Ok) return en;
            return o.TryStartHeat(s, id, tempC);
        }, c, ct);
    public Task<CommandOutcome> StopChannelAsync(RemoteCaller c, int id, bool disable, CancellationToken ct) => Stop((o, s) => o.TryStopChannel(s, id, disable), c, ct);
    public Task<CommandOutcome> CopyChannelConfigAsync(RemoteCaller c, int from, IReadOnlyList<int>? to, CancellationToken ct) => Control((o, s) => o.TryCopyChannelConfig(s, from, to), c, ct);
    public Task<CommandOutcome> ResetGasCounterAsync(RemoteCaller c, int id, CancellationToken ct) => Control((o, s) => o.TryResetGasCounter(s, id), c, ct);

    // —— 温度 ——
    public Task<CommandOutcome> StartHeatAsync(RemoteCaller c, int id, double? tempC, CancellationToken ct) => Control((o, s) => o.TryStartHeat(s, id, tempC), c, ct);
    public Task<CommandOutcome> StopHeatAsync(RemoteCaller c, int id, CancellationToken ct) => Stop((o, s) => o.TryStopHeat(s, id), c, ct);
    public Task<CommandOutcome> SetSetpointAsync(RemoteCaller c, int id, double tempC, CancellationToken ct)
        => Control(async (o, s) =>
        {
            // §7.5：SP 由逼近在下一个 Tick 写入仪表（不在这里等硬件），响应 result 带 applied=false / coalesced=false（§14.1 偏差）
            var r = await o.TrySetChannelTemp(s, id, tempC);
            bool replanned = _vm.FindReactor(id)?.Approach.Active ?? false;   // UI 线程回调内读取
            return r.Ok ? r with { Message = $"RV{id} 目标温度已设为 {tempC:0.#} ℃", Data = new { channel = id, tempSpC = tempC, applied = false, coalesced = false, approachReplanned = replanned } } : r;
        }, c, ct);
    public Task<CommandOutcome> SetSpModeAsync(RemoteCaller c, int id, string mode, CancellationToken ct) => Control((o, s) => o.TrySetSpMode(s, id, mode), c, ct);
    public Task<CommandOutcome> ReplaceProfileAsync(RemoteCaller c, int id, IReadOnlyList<(double TempC, double Minutes)> segments, CancellationToken ct)
        => Control((o, s) => o.TryReplaceProfile(s, id, segments.Select(x => new ProfileSegmentDto(x.TempC, x.Minutes)).ToList()), c, ct);
    public Task<CommandOutcome> StartProfileAsync(RemoteCaller c, int id, CancellationToken ct) => Control((o, s) => o.TryStartProfile(s, id), c, ct);
    public Task<CommandOutcome> StopProfileAsync(RemoteCaller c, int id, CancellationToken ct) => Stop((o, s) => o.TryStopProfile(s, id), c, ct);

    // —— PID / 自整定 ——
    public Task<CommandOutcome> SetPidAsync(RemoteCaller c, int id, int band, double p, double i, double d, CancellationToken ct) => Control((o, s) => o.TrySetPid(s, id, band, p, i, d), c, ct);
    public Task<CommandOutcome> StartAutotuneAsync(RemoteCaller c, int id, double tempC, CancellationToken ct) => Control((o, s) => o.TryStartAutotune(s, id, tempC), c, ct);
    public Task<CommandOutcome> CancelAutotuneAsync(RemoteCaller c, int id, CancellationToken ct) => Stop((o, s) => o.TryCancelAutotune(s, id), c, ct);

    // —— 搅拌 ——
    public Task<CommandOutcome> SetStirAsync(RemoteCaller c, bool? on, int? rpm, CancellationToken ct)
    {
        bool control = !(on == false && rpm == null);
        return UiCommandBridge.RunAsync(async () =>
        {
            var o = _vm.Ops; var s = Src(c);
            OpResult? r1 = null;
            if (rpm is { } v) { r1 = await o.TrySetStirRpm(s, v); if (!r1.Ok) return r1; }
            if (on is { } b)
            {
                var r2 = await o.TrySetStir(s, b);
                return r1 == null ? r2 : (r2.WithWarnings([.. r1.Warnings]) with { Data = new { on = b, rpm = _vm.StirRpm } });
            }
            return r1!;
        }, control, ct);
    }

    // —— 泄漏测试 ——
    public Task<CommandOutcome> StartLeakTestAsync(RemoteCaller c, IReadOnlyList<int> channels, double? testPsi, CancellationToken ct) => Control((o, s) => o.TryStartLeakTest(s, channels, testPsi), c, ct);
    public Task<CommandOutcome> AbortLeakTestAsync(RemoteCaller c, CancellationToken ct) => Stop((o, s) => o.TryAbortLeakTest(s), c, ct);

    // —— 程序 ——
    public Task<CommandOutcome> ReplaceProgramAsync(RemoteCaller c, int id, JsonElement steps, CancellationToken ct)
    {
        // JSON → 快照的转换与范围校验在 Kestrel 线程做（StepDef.Registry 是静态表），只有替换本身进 UI 线程
        var (snap, err) = ProgramJson.Parse(steps);
        if (err != null) return Task.FromResult(CommandOutcome.Done(err));
        return Control((o, s) => o.TryReplaceProgram(s, id, snap!), c, ct);
    }
    public Task<CommandOutcome> RunProgramAsync(RemoteCaller c, int id, CancellationToken ct)
        => Control((o, s) =>
        {
            // 运行当前草稿的冻结副本（UI 线程取；与触屏 RunProgram 同一取法）
            IReadOnlyList<StepSnapshot> steps = _vm.FindReactor(id) == null ? Array.Empty<StepSnapshot>() : _vm.Program.StepsOf(id).Select(x => x.ToSnapshot()).ToList();
            return o.TryRunProgram(s, id, steps);
        }, c, ct);
    public Task<CommandOutcome> AbortProgramAsync(RemoteCaller c, int id, CancellationToken ct) => Stop((o, s) => o.TryAbortProgram(s, id), c, ct);
    public Task<CommandOutcome> CopyProgramAsync(RemoteCaller c, int from, IReadOnlyList<int> to, CancellationToken ct) => Control((o, s) => o.TryCopyProgram(s, from, to), c, ct);

    // —— 报警 / 会话 ——
    public Task<CommandOutcome> AckAlarmAsync(RemoteCaller c, string alarmId, CancellationToken ct) => Stop((o, s) => o.TryAckAlarm(s, alarmId), c, ct);
    public Task<CommandOutcome> BeginSessionAsync(RemoteCaller c, string testId, string? note, CancellationToken ct) => Stop((o, s) => o.TryBeginSession(s, testId, note), c, ct);
    public Task<CommandOutcome> EndSessionAsync(RemoteCaller c, CancellationToken ct) => Stop((o, s) => o.TryEndSession(s), c, ct);
    public Task<CommandOutcome> AddMarkerAsync(RemoteCaller c, string text, CancellationToken ct) => Stop((o, s) => o.TryAddMarker(s, text), c, ct);
}
