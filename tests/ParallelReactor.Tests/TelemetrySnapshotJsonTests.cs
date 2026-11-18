using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>遥测快照的 JSON 形状（设计稿 §6.3）：字段名稳定（camelCase 金标准）、null 读数、小写枚举、UTC 时间。</summary>
public class TelemetrySnapshotJsonTests
{
    /// <summary>构造一份覆盖所有字段的假快照（纯逻辑，不依赖 VM）。</summary>
    public static SnapshotBundle Sample(long seq = 7, bool offline = false)
    {
        var ts = new DateTime(2026, 11, 6, 8, 12, 30, 120, DateTimeKind.Utc);
        var seg = new[] { new ProfileSegmentSnap(1, 80, 20, true, true), new ProfileSegmentSnap(2, 150, 60, true, false) };
        var profile = new ProfileSnap(true, 2, seg, 34.2, 80, false, "第 1 段");
        var prog2 = new ChannelProgramSnap(true, 3, 7, "running", "148.6 / 150.0 °C", "local", ts.AddMinutes(-20), false);
        var prog4 = new ChannelProgramSnap(true, 1, 5, "waitingGas", "等待气路（RV2 占用中）", "local", ts.AddMinutes(-5), true);
        var prog6 = new ChannelProgramSnap(false, 4, 5, "completed", "5 步全部完成", "local", ts.AddHours(-2), false);
        ChannelSnap Ch(int id, bool curve) => new(id, id <= 4, id == 3 ? "alarm" : "heating", id == 3 ? "overTemp" : null, id == 3, false, null,
            offline ? null : 148.6, 150, offline ? null : 147, offline ? null : 63, !offline, false, offline ? null : 0, curve ? "curve" : "fixed",
            new ApproachSnap("creep", 150, 147, "逼近中"), curve ? profile : null, offline ? null : 52.31, offline ? null : 1234, 50, null, false, id is 2 or 4,
            id == 2 ? prog2 : id == 4 ? prog4 : id == 6 ? prog6 : null,
            GasRate: id == 2 ? 0.125 : null, SolutionVolume: id == 2 ? 3.5 : 3);
        var full = Enumerable.Range(1, 8).Select(i => Ch(i, i == 2) with { Profile = profile }).ToArray();
        var frame = new TelemetrySnapshot("telemetry", seq, ts, false, 18233,
            new ModeSnap("local", null),
            new EstopSnap(false, "ok", "ok", "notConfigured", null, false, false),
            new CommSnap(!offline, !offline, true, true),
            Enumerable.Range(1, 8).Select(i => Ch(i, i == 2)).ToArray(),
            new StirSnap(true, 600, true, false, 1.0, 24.1, null, 0, 1000),
            new GasSnap(new[] { new GasInletSnap("inert", false), new GasInletSnap("A", false), new GasInletSnap("B", false) }, false, false, null, null),
            new ProgramSnap(true, 2, "运行中", 3, 7, "升温", "148.6 / 150.0 °C"),
            new ProgramsSnap(2, new[] { 2, 4 }, 2, new[] { 4 }, "正在运行：RV2 RV4"),
            new LeakSnap(false, 0, null, null, false, false),
            new SessionSnap(true, "TP-017", "TP-017", "TP-017-20261106-070000.csv"));
        var settings = new SettingsSnap(200, 500, 1000, 50, 510, 250, true, 20, 8, 50, 10, 30, 2, 7, null,
            5, 0.2, 1.0, 1.0, 0.2, 90, 1.0, 50, 1000, 10000, 4.137,
            new[] { new TempBandSnap(0, 0, 100), new TempBandSnap(1, 100, 200), new TempBandSnap(2, 200, 400) }, 42,
            new VolumeSettingsSnap(10, 0.4, 1.234, new double?[] { 10.62, null, null, null, null, null, null, null },
                new DateTime?[] { ts.AddDays(-1), null, null, null, null, null, null, null }),
            2.236842);
        var tick = new TickStats(1200, 100, 0, 1, 12.3, 350.1, 1350.2);
        var mod = new ModuleCommSnap(true, true, 100, 2, ts, ts);
        var runs = new ProgramRunSnap?[8];
        runs[1] = SampleRun(ts);
        return new SnapshotBundle(frame, full, settings, tick, mod, mod, mod, mod, 0, runs);
    }

    /// <summary>RV2 正在执行的程序（GET /channels/2/program/run）：两步，第 2 步升温中。</summary>
    public static ProgramRunSnap SampleRun(DateTime ts)
    {
        using var p1 = JsonDocument.Parse("{\"gas\":\"inert\",\"cycles\":10,\"fill\":3,\"vent\":5}");
        using var p2 = JsonDocument.Parse("{\"mode\":\"fixed\",\"temp\":150,\"wait\":15}");
        var steps = new[]
        {
            new ProgramStepDto("purge", p1.RootElement.Clone(), "吹扫", "惰性气体 × 10 次"),
            new ProgramStepDto("heat", p2.RootElement.Clone(), "升温", "定值 150°C · 平衡 15 min"),
        };
        return new ProgramRunSnap(2, true, "running", 1, 2, "执行第 2 步 · 升温", "148.6 / 150.0 °C", "远程（PC-测试工位1）", ts.AddMinutes(-20), false, steps);
    }

    [Fact]
    public void Frame_HasGoldenTopLevelAndChannelKeys_CamelCase()
    {
        var json = ApiJson.Serialize(Sample().Frame);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var top = root.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "type", "seq", "ts", "simulated", "eventSeqApplied", "mode", "estop", "comm", "channels", "stir", "gas", "program", "programs", "leakTest", "session" }, top);
        var ch = root.GetProperty("channels")[0].EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[]
        {
            "id", "installed", "state", "alarmCause", "overTempTripped", "overPressureTripped", "startedBy", "tempC", "tempSpC", "tempSpDeviceC",
            "heatOutputPct", "tempOnline", "autotuning", "atMode", "spMode", "approach", "profile", "pressurePsi", "pressureRaw", "pressureSpPsi",
            "gasConsumed", "valveOpen", "programOwned", "program", "gasRate", "solutionVolume",
        }, ch);
        Assert.Equal(new[] { "running", "stepIndex", "stepCount", "state", "message", "startedBy", "startedAt", "waitingGas" },
            root.GetProperty("channels")[1].GetProperty("program").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "running", "channels", "gasHolder", "waitingGas", "summary" }, root.GetProperty("programs").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "active", "heat", "stir", "valves", "since", "latched", "restoredOnBoot" }, root.GetProperty("estop").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "on", "rpmSp", "online", "locked", "motorCurrentA", "busVoltageV", "fault", "alarmCode", "rampMsPer1000Rpm" }, root.GetProperty("stir").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "inlets", "ventOpen", "manualValve", "task", "owner" }, root.GetProperty("gas").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "running", "channel", "status", "stepIndex", "stepCount", "phase", "detail" }, root.GetProperty("program").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "running", "progress", "phase", "runId", "done", "failed" }, root.GetProperty("leakTest").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "active", "id", "testId", "file" }, root.GetProperty("session").EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "control", "holder" }, root.GetProperty("mode").EnumerateObject().Select(p => p.Name));
        // 无字段被 PascalCase 泄漏
        Assert.DoesNotContain(top.Concat(ch), k => char.IsUpper(k[0]));
    }

    [Fact]
    public void Frame_ValuesAndEnums()
    {
        var json = ApiJson.Serialize(Sample().Frame);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("telemetry", root.GetProperty("type").GetString());
        Assert.Equal("2026-11-06T08:12:30.12Z", root.GetProperty("ts").GetString());   // UTC，Z 结尾
        Assert.Equal("heating", root.GetProperty("channels")[0].GetProperty("state").GetString());
        Assert.Equal("alarm", root.GetProperty("channels")[2].GetProperty("state").GetString());
        Assert.Equal("overTemp", root.GetProperty("channels")[2].GetProperty("alarmCause").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("channels")[0].GetProperty("alarmCause").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("channels")[0].GetProperty("profile").ValueKind);   // fixed 模式不带曲线
        Assert.Equal(JsonValueKind.Object, root.GetProperty("channels")[1].GetProperty("profile").ValueKind);   // curve 模式带
        Assert.Equal("creep", root.GetProperty("channels")[0].GetProperty("approach").GetProperty("phase").GetString());
        Assert.Equal("notConfigured", root.GetProperty("estop").GetProperty("valves").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("estop").GetProperty("since").ValueKind);
        Assert.True(root.GetProperty("channels")[1].GetProperty("programOwned").GetBoolean());
        // 多程序并行：每通道 program 子块 + 总览 programs；没跑过程序的通道 program 为 null
        Assert.Equal("running", root.GetProperty("channels")[1].GetProperty("program").GetProperty("state").GetString());
        Assert.Equal("waitingGas", root.GetProperty("channels")[3].GetProperty("program").GetProperty("state").GetString());
        Assert.True(root.GetProperty("channels")[3].GetProperty("program").GetProperty("waitingGas").GetBoolean());
        Assert.False(root.GetProperty("channels")[5].GetProperty("program").GetProperty("running").GetBoolean());   // 已完成的终态仍可见
        Assert.Equal(JsonValueKind.Null, root.GetProperty("channels")[0].GetProperty("program").ValueKind);
        Assert.Equal("2026-11-06T07:52:30.12Z", root.GetProperty("channels")[1].GetProperty("program").GetProperty("startedAt").GetString());
        Assert.Equal(new[] { 2, 4 }, root.GetProperty("programs").GetProperty("channels").EnumerateArray().Select(x => x.GetInt32()));
        Assert.Equal(2, root.GetProperty("programs").GetProperty("gasHolder").GetInt32());
        Assert.Equal("正在运行：RV2 RV4", root.GetProperty("programs").GetProperty("summary").GetString());
        Assert.False(root.GetProperty("channels")[4].GetProperty("installed").GetBoolean());
        Assert.Equal("inert", root.GetProperty("gas").GetProperty("inlets")[0].GetProperty("id").GetString());
        Assert.Contains("运行中", json);   // 中文不转义
    }

    [Fact]
    public void OfflineReadings_AreNullNotZero()
    {
        var json = ApiJson.Serialize(Sample(offline: true).Frame);
        using var doc = JsonDocument.Parse(json);
        var c = doc.RootElement.GetProperty("channels")[0];
        Assert.Equal(JsonValueKind.Null, c.GetProperty("tempC").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("pressurePsi").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("heatOutputPct").ValueKind);
        Assert.Equal(JsonValueKind.Null, c.GetProperty("atMode").ValueKind);
        Assert.False(c.GetProperty("tempOnline").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("comm").GetProperty("temp").GetBoolean());
    }

    [Fact]
    public void Frame_RoundTrips()
    {
        var frame = Sample().Frame;
        var back = ApiJson.Deserialize<TelemetrySnapshot>(ApiJson.Serialize(frame));
        Assert.NotNull(back);
        Assert.Equal(frame.Seq, back!.Seq);
        Assert.Equal(frame.Ts, back.Ts);
        Assert.Equal(frame.Channels.Length, back.Channels.Length);
        Assert.Equal(frame.Channels[1].Profile!.Segments.Length, back.Channels[1].Profile!.Segments.Length);
        Assert.Equal(frame.Stir, back.Stir);
        Assert.Equal(frame.Program, back.Program);
    }

    [Fact]
    public void SettingsDto_Shape_PerDesignDoc()
    {
        var json = ApiJson.Serialize(SettingsDto.From(Sample().Settings));
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(new[] { "limits", "alarms", "leakTest", "approach", "stir", "pressure", "tempBands", "notes", "revision", "volume" }, root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(new[] { "peakCurrentA", "standbyPct", "rampMsPer1000Rpm", "microstep", "stirGearRatio" }, root.GetProperty("stir").EnumerateObject().Select(p => p.Name));
        Assert.Equal(2.236842, root.GetProperty("stir").GetProperty("stirGearRatio").GetDouble(), 6);
        // v0.6 容积（只读）：8 项数组，未标定为 null；标定时间 UTC
        var vol = root.GetProperty("volume");
        Assert.Equal(new[] { "vesselVolume", "gasLineVolume", "manifoldVolume", "effectiveVolume", "volumeCalibratedAt" }, vol.EnumerateObject().Select(p => p.Name));
        Assert.Equal(10, vol.GetProperty("vesselVolume").GetDouble());
        Assert.Equal(0.4, vol.GetProperty("gasLineVolume").GetDouble());
        Assert.Equal(1.234, vol.GetProperty("manifoldVolume").GetDouble());
        Assert.Equal(8, vol.GetProperty("effectiveVolume").GetArrayLength());
        Assert.Equal(10.62, vol.GetProperty("effectiveVolume")[0].GetDouble());
        Assert.Equal(JsonValueKind.Null, vol.GetProperty("effectiveVolume")[1].ValueKind);
        Assert.Equal(8, vol.GetProperty("volumeCalibratedAt").GetArrayLength());
        Assert.EndsWith("Z", vol.GetProperty("volumeCalibratedAt")[0].GetString());
        Assert.Equal(JsonValueKind.Null, vol.GetProperty("volumeCalibratedAt")[7].ValueKind);
        Assert.Equal(200, root.GetProperty("limits").GetProperty("tempSpMaxC").GetDouble());
        Assert.Equal(510, root.GetProperty("alarms").GetProperty("overPressurePsi").GetDouble());
        Assert.Equal(42, root.GetProperty("revision").GetInt64());
        Assert.Equal(4, root.GetProperty("notes").GetProperty("unused").GetArrayLength());
        Assert.Equal(3, root.GetProperty("tempBands").GetArrayLength());
    }

    [Fact]
    public void Channel_GasRateAndSolutionVolume()
    {
        using var doc = JsonDocument.Parse(ApiJson.Serialize(Sample().Frame));
        var chs = doc.RootElement.GetProperty("channels");
        Assert.Equal(0.125, chs[1].GetProperty("gasRate").GetDouble());
        Assert.Equal(3.5, chs[1].GetProperty("solutionVolume").GetDouble());      // 0.1 mL 分辨率：小数原样输出
        Assert.Equal(JsonValueKind.Null, chs[0].GetProperty("gasRate").ValueKind); // 未在计量 → null
        Assert.Equal(3, chs[0].GetProperty("solutionVolume").GetDouble());
        // 旧 HMI 的帧没有这两个字段：反序列化为 null（带默认值的尾部参数）
        var old = ApiJson.Deserialize<ChannelSnap>("{\"id\":1,\"installed\":true,\"state\":\"idle\",\"tempSpC\":0,\"spMode\":\"fixed\",\"approach\":{\"phase\":\"off\",\"statusText\":\"\"},\"pressureSpPsi\":0}");
        Assert.NotNull(old);
        Assert.Null(old!.GasRate);
        Assert.Null(old.SolutionVolume);
    }

    [Fact]
    public void ProgramRunDto_Shape()
    {
        var b = Sample();
        Assert.Null(b.ProgramRun(1));
        Assert.Null(b.ProgramRun(9));
        var run = b.ProgramRun(2)!;
        using var doc = JsonDocument.Parse(ApiJson.Serialize(new ProgramRunDto(b.Frame.Seq, b.Frame.Ts, run)));
        var root = doc.RootElement;
        Assert.Equal(new[] { "seq", "ts", "run" }, root.EnumerateObject().Select(p => p.Name));
        var r = root.GetProperty("run");
        Assert.Equal(new[] { "channel", "running", "state", "stepIndex", "stepCount", "phase", "message", "startedBy", "startedAt", "waitingGas", "steps" },
            r.EnumerateObject().Select(p => p.Name));
        Assert.Equal("running", r.GetProperty("state").GetString());
        Assert.Equal(1, r.GetProperty("stepIndex").GetInt32());
        Assert.Equal("远程（PC-测试工位1）", r.GetProperty("startedBy").GetString());
        var st = r.GetProperty("steps");
        Assert.Equal(2, st.GetArrayLength());
        Assert.Equal(new[] { "type", "params", "name", "detail" }, st[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("inert", st[0].GetProperty("params").GetProperty("gas").GetString());
        Assert.Equal(150, st[1].GetProperty("params").GetProperty("temp").GetDouble());
    }

    [Fact]
    public void ErrorBody_Shape()
    {
        var json = ApiErrors.Body("CHANNEL_NOT_FOUND", "通道 9 不存在", new Dictionary<string, string> { ["id"] = "9" });
        using var doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("CHANNEL_NOT_FOUND", doc.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal("9", doc.RootElement.GetProperty("error").GetProperty("detail").GetProperty("id").GetString());
        Assert.Equal(404, ApiErrors.StatusOf("CHANNEL_NOT_FOUND"));
        Assert.Equal(401, ApiErrors.StatusOf("UNAUTHORIZED"));
        Assert.Equal(403, ApiErrors.StatusOf("IP_NOT_ALLOWED"));
        Assert.Equal(429, ApiErrors.StatusOf("RATE_LIMITED"));
        Assert.Equal(503, ApiErrors.StatusOf("TOO_MANY_CLIENTS"));
        Assert.Equal(503, ApiErrors.StatusOf("UI_THREAD_TIMEOUT"));
        Assert.Equal(503, ApiErrors.StatusOf("STARTING"));
        Assert.Equal(400, ApiErrors.StatusOf("BAD_REQUEST"));
    }

    [Fact]
    public void WsMessages_CarryType()
    {
        Assert.StartsWith("{\"type\":\"hello\"", ApiJson.Serialize(new HelloMsg("hello", "1.0", DateTime.UtcNow, null, "abcd1234", true, 1, 10, 5, new ModeSnap("local", null), 5000)));
        Assert.StartsWith("{\"type\":\"gap\"", ApiJson.Serialize(new GapMsg("gap", "gap_too_large", 100, 50)));
        Assert.StartsWith("{\"type\":\"pong\"", ApiJson.Serialize(new PongMsg("pong", DateTime.UtcNow)));
    }
}
