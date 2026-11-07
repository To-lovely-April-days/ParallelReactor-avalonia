using System.Text.Json;
using System.Threading.Tasks;
using ParallelReactor.Api;
using ParallelReactor.Commands;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>§7.1 / §11 / §13.1：OpResult / CommandOutcome → HTTP 状态码与正文形状；§11 状态码表补齐项。</summary>
public class CommandResultsTests
{
    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Confirmed_200_WithResultAndWarnings()
    {
        var r = OpResult.Done("已启用", "ok", new { rv = 1, channels = new[] { 1, 2 } }).WithWarnings("RV3 已跳过");
        var h = CommandResults.Build(CommandOutcome.Done(r), "op-1", 42);
        Assert.Equal(200, h.Status);
        Assert.True(h.Ok);
        var j = Root(h.Body);
        Assert.True(j.GetProperty("ok").GetBoolean());
        Assert.Equal("op-1", j.GetProperty("opId").GetString());
        Assert.Equal("confirmed", j.GetProperty("status").GetString());
        Assert.Equal(1, j.GetProperty("result").GetProperty("rv").GetInt32());
        Assert.Equal(2, j.GetProperty("result").GetProperty("channels").GetArrayLength());   // 匿名对象 → camelCase JsonElement
        Assert.Equal("RV3 已跳过", j.GetProperty("warnings")[0].GetString());
        Assert.Equal(42, j.GetProperty("opEventSeq").GetInt64());
    }

    [Fact]
    public void Accepted_202_AndSilentHasNullResult()
    {
        var h = CommandResults.Build(CommandOutcome.Done(OpResult.Accepted("加热已启动", "ok", new { tempC = 80.0 })), "op", 1);
        Assert.Equal(202, h.Status);
        Assert.Equal("accepted", Root(h.Body).GetProperty("status").GetString());
        Assert.Equal(80.0, Root(h.Body).GetProperty("result").GetProperty("tempC").GetDouble());

        var silent = CommandResults.Build(CommandOutcome.Done(OpResult.Silent), "op", 1);
        Assert.Equal(200, silent.Status);
        Assert.Equal(JsonValueKind.Null, Root(silent.Body).GetProperty("result").ValueKind);   // Quiet 不影响 HTTP
    }

    [Fact]
    public void Failure_MapsStatusByCode_WithDetail()
    {
        var h = CommandResults.Build(CommandOutcome.Done(OpResult.Fail(OpCodes.ParamOutOfRange, "温度越界", "err", new { min = 10, max = 200, value = 300 })), "op", 7);
        Assert.Equal(422, h.Status);
        Assert.False(h.Ok);
        var j = Root(h.Body);
        Assert.False(j.GetProperty("ok").GetBoolean());
        Assert.Equal("PARAM_OUT_OF_RANGE", j.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("温度越界", j.GetProperty("error").GetProperty("message").GetString());
        Assert.Equal(300, j.GetProperty("error").GetProperty("detail").GetProperty("value").GetInt32());
        Assert.Equal(7, j.GetProperty("opEventSeq").GetInt64());
        Assert.Equal(409, CommandResults.Build(CommandOutcome.Done(OpResult.Fail(OpCodes.GasBusy, "忙")), "op", 0).Status);
        Assert.Equal(502, CommandResults.Build(CommandOutcome.Done(OpResult.Fail(OpCodes.HardwareWriteFailed, "x")), "op", 0).Status);
        Assert.Equal(403, CommandResults.Build(CommandOutcome.Done(OpResult.Fail(OpCodes.SessionNotOwner, "x")), "op", 0).Status);
    }

    [Fact]
    public void Followups_FailuresBecomeWarnings()
    {
        var r = OpResult.Done("已停止", "warn").Then(OpResult.Fail(OpCodes.HardwareWriteFailed, "关阀失败")).Then(OpResult.Done("ok"));
        var h = CommandResults.Build(CommandOutcome.Done(r), "op", 1);
        Assert.Equal(200, h.Status);
        var w = Root(h.Body).GetProperty("warnings");
        Assert.Equal(1, w.GetArrayLength());
        Assert.Equal("关阀失败", w[0].GetString());
    }

    [Fact]
    public void HwUnconfirmed_UiTimeout_OutcomeUnknown()
    {
        var pending = new TaskCompletionSource<OpResult>().Task;
        var hw = CommandResults.Build(CommandOutcome.Unconfirmed(pending), "op", 3);
        Assert.Equal(202, hw.Status);
        Assert.Equal("accepted", Root(hw.Body).GetProperty("status").GetString());
        Assert.Equal("HW_UNCONFIRMED", Root(hw.Body).GetProperty("warnings")[0].GetString());

        var ui = CommandResults.Build(CommandOutcome.UiTimeout, "op", 0);
        Assert.Equal(503, ui.Status);
        Assert.Equal("UI_THREAD_TIMEOUT", Root(ui.Body).GetProperty("error").GetProperty("code").GetString());

        var unk = CommandResults.Build(CommandOutcome.Unknown(pending), "op", 0);
        Assert.Equal(504, unk.Status);
        Assert.Equal("OUTCOME_UNKNOWN", Root(unk.Body).GetProperty("error").GetProperty("code").GetString());
    }

    [Theory]
    [InlineData(OpCodes.PreconditionRequired, 428)]
    [InlineData(OpCodes.ParamOutOfRange, 422)]
    [InlineData(OpCodes.ParamCrossCheck, 422)]
    [InlineData(OpCodes.PreflightFailed, 422)]
    [InlineData(OpCodes.ProgramInvalid, 422)]
    [InlineData(OpCodes.HardwareWriteFailed, 502)]
    [InlineData(OpCodes.SessionNotOwner, 403)]
    [InlineData(OpCodes.ForbiddenRole, 403)]
    [InlineData(OpCodes.ClientTooOld, 426)]
    [InlineData(OpCodes.ModeLocal, 409)]
    [InlineData(OpCodes.NotControlHolder, 409)]
    [InlineData(OpCodes.ControlLeaseExpired, 409)]
    [InlineData(OpCodes.ControlRequestPending, 409)]
    [InlineData(OpCodes.IdempotencyMismatch, 409)]
    [InlineData(OpCodes.IdempotencyInProgress, 409)]
    [InlineData(OpCodes.SessionActive, 409)]
    [InlineData(OpCodes.UiThreadTimeout, 503)]
    [InlineData(OpCodes.OutcomeUnknown, 504)]
    [InlineData(OpCodes.ChannelNotFound, 404)]
    public void StatusOf_MatchesSection11(string code, int status) => Assert.Equal(status, ApiErrors.StatusOf(code));

    [Fact]
    public void ToElement_AnonymousAndNull()
    {
        Assert.Null(ApiJson.ToElement(null));
        var e = ApiJson.ToElement(new { approachReplanned = true, list = new[] { 1 } })!.Value;
        Assert.True(e.GetProperty("approachReplanned").GetBoolean());
        Assert.Equal(1, e.GetProperty("list")[0].GetInt32());
    }
}
