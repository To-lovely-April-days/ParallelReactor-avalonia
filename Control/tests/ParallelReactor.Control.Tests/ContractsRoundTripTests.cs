using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Client;
using ParallelReactor.Commands;
using ParallelReactor.Models;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Contracts 线上类型 ↔ 实测 JSON：HMI 侧用同一个 ApiJsonContext 序列化，PC 侧反序列化，字段名（camelCase）与形状必须与抓到的样本一致。
/// </summary>
public class ContractsRoundTripTests
{
    [Fact]
    public void Sample_ControlRequest_Granted()
    {
        var dto = ApiJson.Deserialize<CommandResponseDto>(Samples.Read("req.json"))!;
        Assert.True(dto.Ok);
        Assert.Equal("accepted", dto.Status);
        Assert.Equal(11, dto.OpEventSeq);
        var r = ClientJson.FromElement<ControlRequestResultDto>(dto.Result!.Value)!;
        Assert.Equal("granted", r.Result);
        Assert.Equal("a7269bdf8f151c29", r.RequestId);
        Assert.Equal("4bda5369236bc3094016de28dacea828", r.LeaseId);
        Assert.Equal("8ee7d1b6723413c79fb9f510a28c576d", r.RequestKey);
        Assert.Equal(3, r.HeartbeatTimeoutSec);
        Assert.Equal(3, r.LeaseGraceSec);
        Assert.Equal(DateTimeKind.Utc, r.LeaseExpiresAt!.Value.Kind);
        // 泛型 CommandResult 包装
        var cr = CommandResult<ControlRequestResultDto>.From(dto, replayed: false, 202);
        Assert.True(cr.IsAccepted);
        Assert.Equal(r, cr.Result);
    }

    [Fact]
    public void Sample_ControlRequestStatus_And_ControlState()
    {
        var st = ApiJson.Deserialize<ControlRequestStatusDto>(Samples.Read("reqq_key.json"))!;
        Assert.Equal("granted", st.Result);
        Assert.Equal("PC-测试工位1 （请点允许）", st.ClientName);   // 控制符已被服务端清洗为空格
        Assert.NotNull(st.LeaseId);

        var ctl = ApiJson.Deserialize<ControlStateDto>(Samples.Read("ctl1.json"))!;
        Assert.Equal("remote", ctl.Mode);
        Assert.Equal("remote", ctl.State);
        Assert.Equal("PC-测试工位1", ctl.HolderToken);
        Assert.True(ctl.AutoGrant);
        Assert.Equal(60, ctl.RequestTimeoutSec);
        Assert.Null(ctl.Pending);

        var lost = ApiJson.Deserialize<ControlStateDto>(Samples.Read("ctl_lost.json"))!;
        Assert.Equal(ControlModes.RemoteLost, lost.State);
    }

    [Fact]
    public void Sample_Heartbeat_Result()
    {
        var dto = ApiJson.Deserialize<CommandResponseDto>(Samples.Read("hb1.json"))!;
        var hb = ClientJson.FromElement<HeartbeatResultDto>(dto.Result!.Value)!;
        Assert.Equal("remote", hb.Mode);
        Assert.False(hb.Resumed);
        Assert.Equal(3, hb.HeartbeatTimeoutSec);
        Assert.True(hb.LeaseHardExpiresAt > hb.LeaseExpiresAt);
    }

    [Fact]
    public void Sample_CommandError_ModeLocal()
    {
        var body = Samples.Read("nolease1.json");
        var dto = ApiJson.Deserialize<CommandErrorDto>(body)!;
        Assert.False(dto.Ok);
        Assert.Equal(OpCodes.ModeLocal, dto.Error.Code);
        Assert.Equal(8, dto.OpEventSeq);
        var ex = ApiException.Parse(409, body, "x");
        Assert.Equal(OpCodes.ModeLocal, ex.Code);
        Assert.Equal(409, ex.Status);
        Assert.True(ex.IsLeaseError);
        Assert.Equal(dto.OpId, ex.OpId);
        Assert.Contains("本地控制中", ex.Message);
        Assert.Equal(409, ApiErrorStatus.Of(OpCodes.ModeLocal));
    }

    [Fact]
    public void Sample_WriteResults_AnonymousShapes()
    {
        var sess = CommandResult<SessionBeginResult>.From(ApiJson.Deserialize<CommandResponseDto>(Samples.Read("sess.json"))!, false, 200).Result!;
        Assert.Equal("TP-017-20261107-171054", sess.Id);
        Assert.Equal("TP-017", sess.TestId);
        Assert.Equal(34, sess.StartEventSeq);
        var mk = CommandResult<MarkerResult>.From(ApiJson.Deserialize<CommandResponseDto>(Samples.Read("mark2r.json"))!, true, 200);
        Assert.True(mk.Replayed);
        Assert.Equal(2, mk.Result!.Id);
        Assert.Equal("保温段", mk.Result.Text);
        var run = CommandResult<ProgramRunResult>.From(ApiJson.Deserialize<CommandResponseDto>(Samples.Read("run1.json"))!, false, 202).Result!;
        Assert.Equal(1, run.Rv);
        Assert.Equal(2, run.Steps);
        var sp = CommandResult<SetpointResult>.From(ApiJson.Deserialize<CommandResponseDto>(Samples.Read("sp.json"))!, false, 200).Result!;
        Assert.Equal(3, sp.Channel);
        Assert.Equal(90, sp.TempSpC);
        Assert.True(sp.ApproachReplanned);
        var heat = CommandResult<JsonElement>.From(ApiJson.Deserialize<CommandResponseDto>(Samples.Read("heat.json"))!, false, 202);
        Assert.True(heat.IsAccepted);
        Assert.Equal(80, heat.Result.GetProperty("tempC").GetDouble());
        var estop = CommandResult<JsonElement>.From(ApiJson.Deserialize<CommandResponseDto>(Samples.Read("estop.json"))!, false, 202);
        Assert.Contains("远程", estop.Result.GetProperty("source").GetString());
        var stop = CommandResult<JsonElement>.From(ApiJson.Deserialize<CommandResponseDto>(Samples.Read("afterexp.json"))!, false, 200);
        Assert.True(stop.IsConfirmed);
        Assert.Equal(JsonValueKind.Undefined, stop.Result.ValueKind);   // result:null
    }

    [Fact]
    public void Sample_SessionDetail_And_Manifest()
    {
        var cur = ApiJson.Deserialize<SessionDetailDto>(Samples.Read("cur.json"))!;
        Assert.True(cur.Active);
        Assert.Equal("TP-017", cur.TestId);
        Assert.NotNull(cur.Manifest);
        Assert.Equal(SessionManifest.Active, cur.Manifest!.State);
        Assert.True(cur.Manifest.Markers.Length >= 2);
        Assert.Equal("开始升温", cur.Manifest.Markers[0].Text);
        Assert.True(cur.Manifest.Simulated);

        var m = SessionManifest.FromJson(Samples.Read("session-manifest.json"))!;
        Assert.Equal("TP-017-20261107-171054", m.Id);
        var again = SessionManifest.FromJson(m.ToJson())!;
        Assert.Equal(m.Markers.Length, again.Markers.Length);
        Assert.Equal(m.StartedAt, again.StartedAt);

        var list = ApiJson.Deserialize<SessionsDto>(Samples.Read("sesslist.json"))!;
        Assert.True(list.Count >= 1);
    }

    [Fact]
    public void Sample_WsControlAndOpEvent()
    {
        var controls = Samples.Lines("ws-control.jsonl").Select(l => ApiJson.Deserialize<ControlMsg>(l)!).ToList();
        Assert.Contains(controls, c => c.Event == "requested" && c.Result == "pending");
        Assert.Contains(controls, c => c.Event == "granted" && c.Result == "granted");
        Assert.All(controls, c => Assert.Null(c.LeaseId));   // §14.2：WS 不带 leaseId
        Assert.All(controls, c => Assert.Equal("control", c.Type));
        var ops = Samples.Lines("ws-opevent.jsonl").Select(l => ApiJson.Deserialize<OpEventMsg>(l)!).ToList();
        Assert.Contains(ops, o => o.Action == "control.request" && o.HttpStatus == 202 && o.Status == "accepted");
        Assert.Contains(ops, o => o.Action == "channel.enable" && o.Code == "OK");
    }

    [Fact]
    public void TelemetrySnapshot_RoundTrip_CamelCase_NullKept()
    {
        var ch = new ChannelSnap(1, true, ChannelStates.Heating, null, false, false, "remote", 25.5, 80, 80, 12.5, true, false, null, "fixed",
            new ApproachSnap("settle", 80, 60, "逼近中"), null, null, 1200, 0, null, false, false,
            new ChannelProgramSnap(true, 1, 3, ChannelProgramStates.Running, "升温", "PC", new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc), false));
        var snap = new TelemetrySnapshot("telemetry", 42, new DateTime(2026, 11, 7, 6, 0, 1, DateTimeKind.Utc), true, 7,
            new ModeSnap("remote", "PC-1"), new EstopSnap(false, "ok", "ok", "ok", null, false, false), new CommSnap(true, true, true, true),
            new[] { ch }, new StirSnap(true, 300, true, false, 0.4, 24.1, null, null, 500), new GasSnap(new[] { new GasInletSnap("inert", false) }, false, false, null, null),
            new ProgramSnap(false, null, "", 0, 0, "", ""), new ProgramsSnap(1, new[] { 1 }, null, Array.Empty<int>(), "1 个程序"),
            new LeakSnap(false, 0, null, null, false, false), new SessionSnap(false, null, null, null));
        var json = ApiJson.Serialize(snap);
        Assert.Contains("\"tempSpC\":80", json);
        Assert.Contains("\"pressurePsi\":null", json);          // 无效读数 = null 不省略
        Assert.Contains("\"eventSeqApplied\":7", json);
        Assert.Contains("\"ts\":\"2026-11-07T06:00:01Z\"", json);
        Assert.DoesNotContain("TempSpC", json);
        var back = ApiJson.Deserialize<TelemetrySnapshot>(json)!;
        Assert.Equal(json, ApiJson.Serialize(back));
        Assert.Equal(42, back.Seq);
        Assert.Null(back.Channels[0].PressurePsi);
        Assert.Equal(1200, back.Channels[0].PressureRaw);
        Assert.Equal(ChannelProgramStates.Running, back.Channels[0].Program!.State);
    }

    [Fact]
    public void EventRecord_To_EventMsg_To_Json()
    {
        var r = new EventRecord(100, 1, new DateTime(2026, 11, 7, 6, 0, 0, DateTimeKind.Utc), "2026-11-07 14:00:00.000", "info", "op.heat.start", 1, "remote", "RV1", "PC", "op-1", "远程命令", "ok");
        var msg = EventMsg.From(r);
        var json = ApiJson.Serialize(msg);
        Assert.Contains("\"type\":\"event\"", json);
        Assert.Contains("\"seq\":100", json);
        Assert.Contains("\"opId\":\"op-1\"", json);
        var back = ApiJson.Deserialize<EventMsg>(json)!;
        Assert.Equal(msg, back);
        var page = ApiJson.Deserialize<EventsPageDto>(ApiJson.Serialize(new EventsPageDto(1, 100, 99, 1, false, new[] { r })))!;
        Assert.Single(page.Items);
        Assert.Equal(r, page.Items[0]);
    }

    [Fact]
    public void Headers_Status_Codes_States()
    {
        Assert.Equal("X-Control-Lease", ApiHeaders.Lease);
        Assert.Equal("Idempotency-Key", ApiHeaders.Idempotency);
        Assert.Equal("X-Control-Request-Key", ApiHeaders.RequestKey);
        Assert.Equal("/api/v1/ws", ApiHeaders.WsPath);
        Assert.Equal(428, ApiErrorStatus.Of(OpCodes.PreconditionRequired));
        Assert.Equal(504, ApiErrorStatus.Of(OpCodes.OutcomeUnknown));
        Assert.Equal(503, ApiErrorStatus.Of(OpCodes.Starting));
        Assert.Equal(409, ApiErrorStatus.Of(OpCodes.IdempotencyInProgress));
        Assert.Equal(409, ApiErrorStatus.Of("SOMETHING_NEW"));
        Assert.Equal("升温中", ChannelStates.Zh("heating"));
        Assert.Equal("超压", ChannelStates.Zh("alarm", "overPressure"));
        Assert.Equal("等待气路", ChannelProgramStates.Zh("waitingGas"));
        Assert.Equal("heat", StepType.Heat.ToCode());
        Assert.True(StepTypeCodes.TryParse("PURGE", out var t) && t == StepType.Purge);
        Assert.False(StepTypeCodes.TryParse("charge", out _));
        Assert.Equal(new[] { 1, 2, 3, 4 }, ChannelRange.Parse("1-4"));
        Assert.Equal("inert", ProgramSchemaCodes.CodeOf("purge", "gas", "惰性气体"));
    }

    [Fact]
    public void ProgramDoc_Json_Shape()
    {
        var p = new ProgramDoc { Name = "加氢测试-01" }
            .Add(StepType.Wait, new { mode = "fixed", time = 0.2 })
            .Add(StepType.Heat, new { mode = "fixed", temp = 50, wait = 0.1 });
        var json = p.ToJson();
        using var d = JsonDocument.Parse(json);
        var steps = d.RootElement.GetProperty("steps");
        Assert.Equal(2, steps.GetArrayLength());
        Assert.Equal("wait", steps[0].GetProperty("type").GetString());
        Assert.Equal(0.2, steps[0].GetProperty("params").GetProperty("time").GetDouble());
        Assert.Equal("heat", steps[1].GetProperty("type").GetString());
        Assert.Equal(50, steps[1].GetProperty("params").GetProperty("temp").GetDouble());
        var back = ProgramDoc.FromJson(json, p.Name);
        Assert.Equal(json, back.ToJson());
        Assert.Equal(StepType.Heat, back.Steps[1].StepType);
    }
}
