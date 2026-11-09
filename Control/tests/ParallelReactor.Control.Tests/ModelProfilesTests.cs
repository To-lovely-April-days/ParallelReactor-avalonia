using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Api;
using ParallelReactor.Control.Core.Models;
using Xunit;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// Core.Models：内置型号表与样稿 js/models.js 逐字段一致（期望值来自样稿 PR.models.* 在 JavaScriptCore 里的结果）；
/// 用实测样本 live-device.json / live-settings.json 推出真机型号；覆盖文件的合并与容错。
/// </summary>
public class ModelProfilesTests
{
    private static ModelProfiles T => ModelProfiles.Embedded;

    [Fact]
    public void Embedded_KeysInMockOrder_AllHypothetical()
    {
        Assert.Equal(new[] { "PR4", "PR8", "PR8-4", "PR16" }, T.Keys);
        Assert.Equal(MockVectorData.Get("models").EnumerateArray().Select(m => m.GetProperty("key").GetString()), T.Keys);
        Assert.Null(T.LoadError);
        Assert.All(T.All, p => Assert.True(p.Hypothetical));     // 样稿参数都是假设值，等用户给真实规格
        Assert.Equal(ModelProfiles.SampleNote, MockVectorData.Get("sampleNote").GetString());
        Assert.Equal("PR8", T.FallbackKey);
        Assert.Equal("PR8", T.KeyForDeviceModel("ParallelReactor-8"));
        Assert.Null(T.Get("PR32"));
        Assert.Null(T.Get(null));
    }

    /// <summary>每个型号的每个字段、每个帮助函数都与样稿相同。</summary>
    [Fact]
    public void Embedded_MatchesMockProfiles()
    {
        foreach (var m in MockVectorData.Get("models").EnumerateArray())
        {
            string key = m.GetProperty("key").GetString()!;
            var p = T.Get(key)!;
            var j = m.GetProperty("profile");
            string at = key + "：";
            Assert.True(j.GetProperty("key").GetString() == p.Key, at + "key");
            Assert.True(j.GetProperty("name").GetString() == p.Name, at + "name");
            Assert.True(j.GetProperty("label").GetString() == p.Label, at + "label");
            Assert.True(j.GetProperty("variant").GetString() == p.Variant, at + "variant");
            Assert.True(j.GetProperty("channelCount").GetInt32() == p.ChannelCount, at + "channelCount");
            Assert.Equal(Ints(j.GetProperty("installed")), p.Installed);
            var v = j.GetProperty("vessel");
            Assert.True(v.GetProperty("volumeMl").GetDouble() == p.Vessel.VolumeMl, at + "volumeMl");
            Assert.True(v.GetProperty("maxFillMl").GetDouble() == p.Vessel.MaxFillMl, at + "maxFillMl");
            Assert.Equal(v.GetProperty("kind").GetString(), p.Vessel.Kind);
            Assert.Equal(v.GetProperty("note").GetString(), p.Vessel.Note);
            var l = j.GetProperty("limits");
            Assert.True(l.GetProperty("tempMaxC").GetDouble() == p.Limits.TempMaxC, at + "tempMaxC");
            Assert.True(l.GetProperty("pressureMaxMPa").GetDouble() == p.Limits.PressureMaxMPa, at + "pressureMaxMPa");
            Assert.True(l.GetProperty("rpmMin").GetDouble() == p.Limits.RpmMin, at + "rpmMin");
            Assert.True(l.GetProperty("rpmMax").GetDouble() == p.Limits.RpmMax, at + "rpmMax");
            Assert.Equal(j.GetProperty("stirMode").GetString(), p.StirMode == StirMode.Shared ? "shared" : "independent");
            Assert.Equal(j.GetProperty("stirKind").GetString(), p.StirKind);
            Assert.Equal(j.GetProperty("stirNote").GetString(), p.StirNote);
            Assert.Equal(j.GetProperty("gasInlets").EnumerateArray().Select(g => g.GetProperty("key").GetString() + "=" + g.GetProperty("name").GetString()),
                p.GasInlets.Select(g => g.Key + "=" + g.Name));
            Assert.Equal(j.GetProperty("vent").GetProperty("key").GetString() + "=" + j.GetProperty("vent").GetProperty("name").GetString(), p.Vent!.Key + "=" + p.Vent.Name);
            Assert.True(j.GetProperty("hasCooling").GetBoolean() == p.HasCooling, at + "hasCooling");
            Assert.Equal(j.GetProperty("layout").GetString(), p.Layout switch { LayoutKind.Wide4 => "wide4", LayoutKind.Row8 => "row8", _ => "grid16" });
            Assert.Equal(j.GetProperty("comm").EnumerateArray().Select(c => c.GetProperty("key").GetString() + "=" + c.GetProperty("name").GetString() + "×" + c.GetProperty("count").GetInt32()),
                p.Comm.Select(c => c.Key + "=" + c.Name + "×" + c.Count));
            Assert.Equal(j.GetProperty("note").GetString(), p.Note);

            // 帮助函数
            Assert.Equal(m.GetProperty("installedText").GetString(), p.InstalledText);
            Assert.Equal(m.GetProperty("uninstalledRuns").EnumerateArray().Select(r => r[0].GetInt32() + "-" + r[1].GetInt32()),
                p.UninstalledRuns.Select(r => r.From + "-" + r.To));
            Assert.Equal(m.GetProperty("channels").EnumerateArray().Select(c => c.GetProperty("n").GetInt32() + ":" + c.GetProperty("installed").GetBoolean()),
                p.Channels.Select(c => c.N + ":" + c.Installed));
            Assert.Equal(m.GetProperty("stirLabel").GetString(), p.StirLabel);
            Assert.Equal(m.GetProperty("stirShort").GetString(), p.StirShort);
            Assert.Equal(m.GetProperty("gasKeys").EnumerateArray().Select(x => x.GetString()), p.GasKeys);
            Assert.Equal(m.GetProperty("rangeNote").GetString(), p.RangeNote);
            foreach (var pair in m.GetProperty("isInstalled").EnumerateArray())
                Assert.True(pair[1].GetBoolean() == p.IsInstalled(pair[0].GetInt32()), at + "isInstalled " + pair[0].GetInt32());
            foreach (var pair in m.GetProperty("gasNames").EnumerateArray())
            {
                string gk = pair[0].GetString()!, want = pair[1].GetString()!;
                // 样稿之外的一层：HMI 线上代码（GasSnap / program-schema 的 "A" "B"）也能找到名称；样稿没有这层，原样返回键
                if (p.GasByHmi(gk) is { } hmi && !p.GasKeys.Contains(gk)) want = hmi.Name;
                Assert.True(want == p.GasName(gk), at + "gasName " + gk + " = " + p.GasName(gk) + "，期望 " + want);
            }
        }
    }

    [Fact]
    public void RangeText_MatchesMock()
    {
        foreach (var r in MockVectorData.Get("rangeText").EnumerateArray())
        {
            string? prefix = r.GetProperty("prefix").ValueKind == JsonValueKind.Null ? null : r.GetProperty("prefix").GetString();
            Assert.Equal(r.GetProperty("out").GetString(), ModelProfile.RangeText(Ints(r.GetProperty("list")), prefix));
        }
        Assert.Equal("—", ModelProfile.RangeText(null));
    }

    [Fact]
    public void Variant_InheritsBase()
    {
        var v = T.Get("PR8-4")!;
        Assert.Equal("PR8", v.BaseKey);
        Assert.Equal("PR8", T.Get("PR8")!.BaseKey);
        Assert.Equal("HT-PR8", v.Name);
        Assert.Equal("HT-PR8 · 现场", v.Label);
        Assert.Equal(new[] { new ChannelRun(5, 8) }, v.UninstalledRuns);
        Assert.Equal("搅拌电机（全局 RV1–RV8 共用）", v.StirLabel);
        Assert.Equal(T.Get("PR8")!.Comm, v.Comm);
    }

    [Fact]
    public void Resolve_LiveSample_Pr8WithDeviceLimits()
    {
        var dev = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!;
        var settings = ApiJson.Deserialize<SettingsDto>(Samples.Read("live-settings.json"))!;
        var p = T.Resolve(dev, settings);
        Assert.Equal("PR8", p.Key);
        Assert.Equal("PR8", p.BaseKey);
        Assert.Equal("HT-PR8", p.Label);
        Assert.Equal("", p.Variant);
        Assert.Equal("ParallelReactor-8", p.DeviceModel);
        Assert.Equal(8, p.ChannelCount);
        Assert.Equal(Enumerable.Range(1, 8), p.Installed);
        Assert.Empty(p.UninstalledRuns);
        Assert.Equal(LayoutKind.Row8, p.Layout);
        // 量程取设备：温度上限 200（与表内相同）、搅拌上限 1000、溶液上限 50、压力满量程 4.137
        Assert.Equal(200, p.Limits.TempMaxC);
        Assert.Equal(1000, p.Limits.RpmMax);
        Assert.Equal(4.137, p.Limits.PressureMaxMPa);
        Assert.Equal(3, p.PressureDecimals);
        Assert.Equal(50, p.Vessel.MaxFillMl);
        Assert.Equal(50, p.Vessel.VolumeMl);                // 接口没有釜容积：仍是表内假设值
        Assert.Equal("溶液 ≤ 50 mL", p.Vessel.Note);
        Assert.Equal("全釜共用磁力搅拌 0–1000 rpm", p.StirNote);
        Assert.Equal("≤ 200 ℃ · ≤ 4.137 MPa", p.RangeNote);
        Assert.Equal(new[] { "pressureMaxMPa", "rpmMax", "maxFillMl" }, p.LiveOverrides);
        Assert.True(p.Hypothetical);                         // 气路名称、搅拌方式、模块仍来自表
        Assert.Equal("气体 A", p.GasName("A"));             // 遥测 gas.inlets[].id
        Assert.Equal("惰性气体", p.GasByHmi("inert")!.Name);
        Assert.Null(p.GasByHmi("C"));
    }

    [Fact]
    public void Resolve_WithoutSettings_UsesHardwareFullScale()
    {
        var dev = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!;
        var p = T.Resolve(dev);
        Assert.Equal(4.137, p.Limits.PressureMaxMPa);       // hardware.pressure.fullScaleMPa
        Assert.Equal(200, p.Limits.TempMaxC);
        Assert.Equal(1500, p.Limits.RpmMax);                // 表内假设值
        Assert.Equal("全釜共用磁力搅拌 0–1500 rpm", p.StirNote);
        Assert.Equal(new[] { "pressureMaxMPa" }, p.LiveOverrides);
    }

    [Fact]
    public void Resolve_PartialInstall_IsFieldVariant()
    {
        var dev = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!;
        dev = dev with
        {
            Channels = Enumerable.Range(1, 8).Select(n => new ChannelInstalledDto(n, n <= 3 || n == 6)).ToArray(),
            InstalledChannels = new[] { 1, 2, 3, 6 },
        };
        var p = T.Resolve(dev);
        Assert.Equal("PR8", p.Key);
        Assert.Equal("HT-PR8 · 现场", p.Label);
        Assert.Equal("现场", p.Variant);
        Assert.Equal(new[] { 1, 2, 3, 6 }, p.Installed);
        Assert.Equal("RV1–RV3、RV6", p.InstalledText);
        Assert.Equal(new[] { new ChannelRun(4, 5), new ChannelRun(7, 8) }, p.UninstalledRuns);
        Assert.Contains("installed", p.LiveOverrides);
        Assert.Equal("当前在开发的机型", p.Note);            // 备注取基础型号，不取演示用的 PR8-4

        // 没有 channels[] 时用 installedChannels
        var p2 = T.Resolve(dev with { Channels = Array.Empty<ChannelInstalledDto>(), InstalledChannels = new[] { 4, 1, 2, 3, 9 } });
        Assert.Equal(new[] { 1, 2, 3, 4 }, p2.Installed);
        Assert.Equal("HT-PR8 · 现场", p2.Label);
    }

    [Fact]
    public void Resolve_UnknownModel_ByChannelCount()
    {
        var dev = ApiJson.Deserialize<DeviceInfoDto>(Samples.Read("live-device.json"))!;
        DeviceInfoDto With(string model, int count) => dev with
        {
            Model = model, ChannelCount = count,
            Channels = Enumerable.Range(1, count).Select(n => new ChannelInstalledDto(n, true)).ToArray(),
            InstalledChannels = Enumerable.Range(1, count).ToArray(),
        };
        var p16 = T.Resolve(With("ParallelReactor-16", 16));
        Assert.Equal("PR16", p16.Key);
        Assert.Equal("HT-PR16", p16.Label);
        Assert.Equal(LayoutKind.Grid16, p16.Layout);
        var p4 = T.Resolve(With("Foo", 4));
        Assert.Equal("PR4", p4.Key);                         // 通道数相同的非变体型号
        var p12 = T.Resolve(With("Foo", 12));                // 表里没有 12 路：用 fallbackModel，通道数取设备
        Assert.Equal("PR8", p12.Key);
        Assert.Equal(12, p12.ChannelCount);
        Assert.Equal(LayoutKind.Grid16, p12.Layout);
        Assert.Equal("搅拌电机（全局 RV1–RV12 共用）", p12.StirLabel);
        Assert.Contains("channelCount", p12.LiveOverrides);
        Assert.Equal("PR8", T.Resolve(With("parallelreactor-8", 8)).Key);   // 型号名大小写不敏感
    }

    [Fact]
    public void Load_MissingOrEmptyPath_IsEmbedded()
    {
        Assert.Same(ModelProfiles.Embedded, ModelProfiles.Load(null));
        Assert.Same(ModelProfiles.Embedded, ModelProfiles.Load(""));
        Assert.Same(ModelProfiles.Embedded, ModelProfiles.Load(Path.Combine(AppContext.BaseDirectory, "no-such-dir", "models.json")));
    }

    [Fact]
    public void Load_OverrideFile_MergesByKey()
    {
        // 用户给出真实规格后的样子：只写要改的型号；可写注释和多余逗号
        const string json = """
        {
          // 真实规格（测试用的假数据）
          "deviceModels": { "ParallelReactor-12": "PR12" },
          "profiles": [
            { "key": "PR8", "name": "HT-PR8", "hypothetical": false, "channelCount": 8,
              "vessel": { "volumeMl": 60, "maxFillMl": 25, "kind": "{volumeMl} mL 釜", "note": "溶液 ≤ {maxFillMl} mL" },
              "limits": { "tempMaxC": 220, "pressureMaxMPa": 6, "rpmMin": 0, "rpmMax": 1200 },
              "stirMode": "shared", "stirKind": "磁力搅拌", "stirNote": "全釜共用磁力搅拌 {rpmMin}–{rpmMax} rpm",
              "gasInlets": [ { "key": "a", "name": "氢气", "hmi": "A" } ], "vent": { "key": "vent", "name": "排空" },
              "layout": "row8", "comm": [ { "key": "temp", "name": "温控", "count": 1 } ], },
            { "key": "PR12", "base": "PR8", "name": "HT-PR12", "label": "HT-PR12", "channelCount": 12, "layout": "grid16" },
          ],
        }
        """;
        string path = Path.Combine(AppContext.BaseDirectory, "models-override-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        try
        {
            var t = ModelProfiles.Load(path);
            Assert.Null(t.LoadError);
            Assert.Equal(path, t.Source);
            Assert.Equal(new[] { "PR4", "PR8", "PR8-4", "PR16", "PR12" }, t.Keys);   // 同键原位替换，新键追加
            var pr8 = t.Get("PR8")!;
            Assert.False(pr8.Hypothetical);
            Assert.Equal(2, pr8.PressureDecimals);           // 6 MPa ≥ 5 → 2 位
            Assert.Equal("全釜共用磁力搅拌 0–1200 rpm", pr8.StirNote);
            Assert.Equal("60 mL 釜", pr8.Vessel.Kind);
            Assert.Equal("", pr8.Note);
            var v = t.Get("PR8-4")!;                         // 内置的变体继承新的 PR8
            Assert.Equal(1200, v.Limits.RpmMax);
            Assert.Equal("HT-PR8 · 现场", v.Label);
            Assert.Equal(new[] { 1, 2, 3, 4 }, v.Installed);
            Assert.Equal("氢气", v.GasName("A"));
            var p12 = t.Get("PR12")!;
            Assert.Equal(12, p12.ChannelCount);
            Assert.Equal(Enumerable.Range(1, 12), p12.Installed);  // 新 PR8 没写 installed（= 全部），继承后按 12 路全装
            Assert.Empty(p12.UninstalledRuns);
            Assert.Equal(LayoutKind.Grid16, p12.Layout);
            Assert.Equal("氢气", p12.GasName("a"));
            Assert.Equal("PR8", p12.BaseKey);
            Assert.Equal("PR12", t.KeyForDeviceModel("ParallelReactor-12"));
            Assert.Equal("PR8", t.KeyForDeviceModel("ParallelReactor-8"));   // 内置的映射保留
            Assert.Equal(250, t.Get("PR4")!.Limits.TempMaxC);                 // 没写的型号不变
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_BadOverride_FallsBackWithReason()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "models-bad-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{ "profiles": [ { "key": "PR8", "base": "PR4", "layout": "hex" } ] }""");
        try
        {
            var t = ModelProfiles.Load(path);
            Assert.NotNull(t.LoadError);
            Assert.Contains("layout", t.LoadError);
            Assert.Equal(ModelProfiles.Embedded.Keys, t.Keys);
            Assert.Equal(1500, t.Get("PR8")!.Limits.RpmMax);
            File.WriteAllText(path, "{ not json");
            Assert.Contains("无效", ModelProfiles.Load(path).LoadError);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("""{ "profiles": [] }""", "profiles 为空")]
    [InlineData("""{ "profiles": [ { "key": "A", "base": "B" }, { "key": "B", "base": "A" } ] }""", "成环")]
    [InlineData("""{ "profiles": [ { "key": "A", "base": "Z" } ] }""", "不存在")]
    [InlineData("""{ "foo": 1 }""", "profiles")]
    public void Parse_RejectsBadFiles(string json, string why)
    {
        var ex = Assert.Throws<ModelFileException>(() => ModelProfiles.Parse(json));
        Assert.Contains(why, ex.Message);
    }

    [Fact]
    public void Parse_FieldErrorsNameTheField()
    {
        string pr8 = Embedded8Json();
        var ex = Assert.Throws<ModelFileException>(() => ModelProfiles.Parse(pr8.Replace("\"installed\": [1, 2, 3, 4, 5, 6, 7, 8]", "\"installed\": [1, 9]")));
        Assert.Contains("installed[1]", ex.Message);
        ex = Assert.Throws<ModelFileException>(() => ModelProfiles.Parse(pr8.Replace("\"stirMode\": \"shared\"", "\"stirMode\": \"both\"")));
        Assert.Contains("stirMode", ex.Message);
        ex = Assert.Throws<ModelFileException>(() => ModelProfiles.Parse(pr8.Replace("\"channelCount\": 8", "\"channelCount\": 8.5")));
        Assert.Contains("channelCount", ex.Message);
        ex = Assert.Throws<ModelFileException>(() => ModelProfiles.Parse(pr8.Replace("\"ParallelReactor-8\": \"PR8\"", "\"ParallelReactor-8\": \"PR9\"")));
        Assert.Contains("PR9", ex.Message);
        Assert.Equal(new[] { "PR8" }, ModelProfiles.Parse(pr8).Keys);
    }

    [Fact]
    public void CommModule_MapsToTelemetry()
    {
        var comm = new CommSnap(Temp: true, Pressure: false, Stir: true, Io: false);
        var byKey = T.Get("PR8")!.Comm.ToDictionary(c => c.Key, c => c.OnlineIn(comm));
        Assert.Equal(new Dictionary<string, bool?> { ["temp"] = true, ["press"] = false, ["stir"] = true, ["valve"] = false }, byKey);
        Assert.Null(new CommModule("x", "x", 1).OnlineIn(comm));
        Assert.Null(new CommModule("temp", "x", 1).OnlineIn(null));
    }

    private static int[] Ints(JsonElement a) => a.EnumerateArray().Select(x => x.GetInt32()).ToArray();

    /// <summary>只含 PR8 一条的完整型号表（解析错误测试的底稿）。</summary>
    private static string Embedded8Json() => """
    {
      "deviceModels": { "ParallelReactor-8": "PR8" },
      "profiles": [ {
        "key": "PR8", "name": "HT-PR8", "channelCount": 8, "installed": [1, 2, 3, 4, 5, 6, 7, 8],
        "vessel": { "volumeMl": 50, "maxFillMl": 20, "kind": "50 mL 釜", "note": "" },
        "limits": { "tempMaxC": 200, "pressureMaxMPa": 4.14, "rpmMin": 0, "rpmMax": 1500 },
        "stirMode": "shared", "stirKind": "磁力搅拌", "gasInlets": [], "layout": "row8", "comm": []
      } ]
    }
    """;
}
