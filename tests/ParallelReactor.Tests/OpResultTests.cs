using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using ParallelReactor.Commands;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>C2：命令结果与错误码。</summary>
public class OpResultTests
{
    [Fact]
    public void Done_DefaultsToConfirmedOk()
    {
        var r = OpResult.Done("好", "warn", 7);
        Assert.True(r.Ok);
        Assert.Equal(OpCodes.Ok, r.Code);
        Assert.Equal("好", r.Message);
        Assert.Equal("warn", r.Kind);
        Assert.Equal(OpStatus.Confirmed, r.Status);
        Assert.Equal(7, r.Data);
        Assert.Empty(r.Warnings);
        Assert.False(r.Quiet);
    }

    [Fact]
    public void Accepted_IsOkWithAcceptedStatus()
    {
        var r = OpResult.Accepted("已下发");
        Assert.True(r.Ok);
        Assert.Equal(OpStatus.Accepted, r.Status);
        Assert.Equal("ok", r.Kind);
    }

    [Fact]
    public void Fail_IsNotOk_DefaultsToErr()
    {
        var r = OpResult.Fail(OpCodes.EstopActive, "急停", detail: new { a = 1 });
        Assert.False(r.Ok);
        Assert.Equal("ESTOP_ACTIVE", r.Code);
        Assert.Equal("err", r.Kind);
        Assert.NotNull(r.Data);
        Assert.Equal("warn", OpResult.Fail(OpCodes.ModeRemote, "x", "warn").Kind);
    }

    [Fact]
    public void Silent_IsOkQuietAndEmpty()
    {
        Assert.True(OpResult.Silent.Ok);
        Assert.True(OpResult.Silent.Quiet);
        Assert.Equal("", OpResult.Silent.Message);
    }

    [Fact]
    public async System.Threading.Tasks.Task AsTask_IsCompletedWithSameInstance()
    {
        var r = OpResult.Done("x");
        var t = r.AsTask();
        Assert.True(t.IsCompletedSuccessfully);
        Assert.Same(r, await t);
    }

    [Fact]
    public void WithWarnings_Appends()
    {
        var r = OpResult.Done("x").WithWarnings("a").WithWarnings("b", "c");
        Assert.Equal(new[] { "a", "b", "c" }, r.Warnings);
        var same = OpResult.Done("y");
        Assert.Same(same, same.WithWarnings());
    }

    /// <summary>对外通讯接口设计 §11 的全部错误码（防拼写漂移）。</summary>
    public static readonly string[] Section11 =
    {
        "BAD_REQUEST", "UNAUTHORIZED", "FORBIDDEN_ROLE", "IP_NOT_ALLOWED", "NOT_EXPOSED", "SESSION_NOT_OWNER",
        "NOT_FOUND", "CHANNEL_NOT_FOUND", "MODE_LOCAL", "NOT_CONTROL_HOLDER", "CONTROL_LEASE_EXPIRED",
        "CONTROL_REQUEST_PENDING", "CHANNEL_NOT_INSTALLED", "ESTOP_ACTIVE", "GAS_BUSY", "GAS_OWNED_BY_OTHER",
        "MANUAL_VALVE_ACTIVE", "CHANNEL_BUSY", "CHANNEL_STATE_INVALID", "CHANNEL_PRESSURIZED", "CHANNEL_ALARM",
        "CHANNEL_PROGRAM_OWNED", "PROGRAM_RUNNING", "CURVE_RUNNING", "AUTOTUNE_ACTIVE", "STIR_PROGRAM_OWNED",
        "LEAK_TEST_RUNNING", "SESSION_ACTIVE", "SYSTEM_BUSY", "REVISION_CONFLICT", "LOCAL_EDITING",
        "IDEMPOTENCY_MISMATCH", "IDEMPOTENCY_IN_PROGRESS", "HW_NOT_CONFIGURED", "PARAM_OUT_OF_RANGE",
        "PARAM_CROSS_CHECK", "PREFLIGHT_FAILED", "PROGRAM_INVALID", "CLIENT_TOO_OLD", "PRECONDITION_REQUIRED",
        "RATE_LIMITED", "DOWNLOAD_BUSY", "HARDWARE_WRITE_FAILED", "HW_UNCONFIRMED", "HW_OFFLINE",
        "TOO_MANY_CLIENTS", "STARTING", "UI_THREAD_TIMEOUT", "OUTCOME_UNKNOWN", "INTERNAL",
    };

    private static string[] Declared() => typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!).ToArray();

    [Fact]
    public void OpCodes_CoverSection11()
    {
        var declared = Declared();
        foreach (var code in Section11) Assert.Contains(code, declared);
    }

    [Fact]
    public void OpCodes_OnlySection11PlusLocalOnes_NoDuplicates()
    {
        var declared = Declared();
        Assert.Equal(declared.Length, declared.Distinct().Count());
        var extra = declared.Except(Section11).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "MODE_REMOTE", "OK", "VALVE_BUSY" }, extra);
    }

    [Fact]
    public void Then_AppendsFollowupsInOrder_NullIsNoOp()
    {
        var a = OpResult.Fail(OpCodes.HardwareWriteFailed, "SV1 关闭失败");
        var b = OpResult.Done("x");
        var r = OpResult.Done("停止", "warn").Then(null).Then(a).Then(b);
        Assert.Equal(new[] { a, b }, r.Followups);
        Assert.True(r.Ok);   // 主结果不变
        Assert.Empty(OpResult.Silent.Followups);
        Assert.Empty(OpResult.Silent.Then(a).Then(null).Followups.Skip(1));
    }

    [Fact]
    public async Task Guard_TurnsSyncAndAsyncThrowsIntoInternal()
    {
        // 远程适配层的约定：同步头抛出（例如不在 UI 线程时 VerifyAccess）也不能冒泡到 HTTP 线程
        var sync = await OpResult.Guard(() => throw new InvalidOperationException("不在 UI 线程"));
        Assert.Equal(OpCodes.Internal, sync.Code);
        Assert.Equal("命令执行异常 · 不在 UI 线程", sync.Message);
        var tail = await OpResult.Guard(async () => { await Task.Yield(); throw new TimeoutException("超时"); });
        Assert.Equal(OpCodes.Internal, tail.Code);
        Assert.Contains("超时", tail.Message);
        var ok = OpResult.Done("好");
        Assert.Same(ok, await OpResult.Guard(() => ok.AsTask()));
    }
}
