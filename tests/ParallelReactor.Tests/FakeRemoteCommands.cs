using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;

namespace ParallelReactor.Tests;

/// <summary>
/// <see cref="IRemoteCommands"/> 的脚本化假实现：记录每次调用（方法名 + 参数摘要 + 调用方），按 <see cref="Next"/> 返回预设结局（默认 Done(OpResult.Done)）。
/// 集成测试用它验证写端点的公共管线（角色 / 幂等 / 租约 / 响应映射 / 审计），不依赖 UI 线程。
/// </summary>
public sealed class FakeRemoteCommands : IRemoteCommands
{
    public readonly List<(string Method, string Args, RemoteCaller Caller)> Calls = new();
    /// <summary>下一次调用返回的结局（取走后恢复默认）。</summary>
    public Func<CommandOutcome>? Next;
    public Func<CommandOutcome> Default = () => CommandOutcome.Done(OpResult.Done("ok", "ok", new { done = true }));

    private Task<CommandOutcome> Rec(string method, string args, RemoteCaller c)
    {
        lock (Calls) Calls.Add((method, args, c));
        var f = Next ?? Default;
        Next = null;
        return Task.FromResult(f());
    }

    public Task<CommandOutcome> EstopAsync(RemoteCaller c, string? note) => Rec("Estop", note ?? "", c);
    public Task<CommandOutcome> StopAllAsync(RemoteCaller c, bool stopStir, CancellationToken ct) => Rec("StopAll", stopStir.ToString(), c);
    public Task<CommandOutcome> RunAsync(RemoteCaller c, CancellationToken ct) => Rec("Run", "", c);
    public Task<CommandOutcome> EnableChannelAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("EnableChannel", id.ToString(), c);
    public Task<CommandOutcome> StartChannelAsync(RemoteCaller c, int id, double? tempC, CancellationToken ct) => Rec("StartChannel", $"{id},{tempC}", c);
    public Task<CommandOutcome> StopChannelAsync(RemoteCaller c, int id, bool disable, CancellationToken ct) => Rec("StopChannel", $"{id},{disable}", c);
    public Task<CommandOutcome> CopyChannelConfigAsync(RemoteCaller c, int from, IReadOnlyList<int>? to, CancellationToken ct) => Rec("CopyChannelConfig", $"{from}->{(to == null ? "all" : string.Join("/", to))}", c);
    public Task<CommandOutcome> ResetGasCounterAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("ResetGasCounter", id.ToString(), c);
    public Task<CommandOutcome> StartHeatAsync(RemoteCaller c, int id, double? tempC, CancellationToken ct) => Rec("StartHeat", $"{id},{tempC}", c);
    public Task<CommandOutcome> StopHeatAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("StopHeat", id.ToString(), c);
    public Task<CommandOutcome> SetSetpointAsync(RemoteCaller c, int id, double tempC, CancellationToken ct) => Rec("SetSetpoint", $"{id},{tempC}", c);
    public Task<CommandOutcome> SetSpModeAsync(RemoteCaller c, int id, string mode, CancellationToken ct) => Rec("SetSpMode", $"{id},{mode}", c);
    public Task<CommandOutcome> ReplaceProfileAsync(RemoteCaller c, int id, IReadOnlyList<(double TempC, double Minutes)> segments, CancellationToken ct) => Rec("ReplaceProfile", $"{id},{segments.Count}", c);
    public Task<CommandOutcome> StartProfileAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("StartProfile", id.ToString(), c);
    public Task<CommandOutcome> StopProfileAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("StopProfile", id.ToString(), c);
    public Task<CommandOutcome> SetPidAsync(RemoteCaller c, int id, int band, double p, double i, double d, CancellationToken ct) => Rec("SetPid", $"{id},{band},{p},{i},{d}", c);
    public Task<CommandOutcome> StartAutotuneAsync(RemoteCaller c, int id, double tempC, CancellationToken ct) => Rec("StartAutotune", $"{id},{tempC}", c);
    public Task<CommandOutcome> CancelAutotuneAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("CancelAutotune", id.ToString(), c);
    public Task<CommandOutcome> SetStirAsync(RemoteCaller c, bool? on, int? rpm, CancellationToken ct) => Rec("SetStir", $"{on},{rpm}", c);
    public Task<CommandOutcome> StartLeakTestAsync(RemoteCaller c, IReadOnlyList<int> channels, double? testPsi, CancellationToken ct) => Rec("StartLeakTest", $"{string.Join("/", channels)},{testPsi}", c);
    public Task<CommandOutcome> AbortLeakTestAsync(RemoteCaller c, CancellationToken ct) => Rec("AbortLeakTest", "", c);
    public Task<CommandOutcome> ReplaceProgramAsync(RemoteCaller c, int id, JsonElement steps, CancellationToken ct) => Rec("ReplaceProgram", $"{id},{steps.GetArrayLength()}", c);
    public Task<CommandOutcome> RunProgramAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("RunProgram", id.ToString(), c);
    public Task<CommandOutcome> AbortProgramAsync(RemoteCaller c, int id, CancellationToken ct) => Rec("AbortProgram", id.ToString(), c);
    public Task<CommandOutcome> CopyProgramAsync(RemoteCaller c, int from, IReadOnlyList<int> to, CancellationToken ct) => Rec("CopyProgram", $"{from}->{string.Join("/", to)}", c);
    public Task<CommandOutcome> AckAlarmAsync(RemoteCaller c, string alarmId, CancellationToken ct) => Rec("AckAlarm", alarmId, c);
    public Task<CommandOutcome> BeginSessionAsync(RemoteCaller c, string testId, string? note, CancellationToken ct) => Rec("BeginSession", $"{testId},{note}", c);
    public Task<CommandOutcome> EndSessionAsync(RemoteCaller c, CancellationToken ct) => Rec("EndSession", "", c);
    public Task<CommandOutcome> AddMarkerAsync(RemoteCaller c, string text, CancellationToken ct) => Rec("AddMarker", text, c);
}
