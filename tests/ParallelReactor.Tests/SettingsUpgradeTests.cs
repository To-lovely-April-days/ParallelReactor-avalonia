using System;
using System.IO;
using System.Text.Json;
using ParallelReactor.Models;
using ParallelReactor.Services;
using Xunit;

namespace ParallelReactor.Tests;

/// <summary>
/// 升级保护：现场设备上已经整定好的 PID（settings.json 的 BandPids）升级到 v0.6 后必须原样保留；
/// 旧存档先备份；读不出来的存档先另存再按「没有存档」处理（<see cref="SettingsFileGuard"/>）。
/// </summary>
public class SettingsUpgradeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pr-upgrade-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public SettingsUpgradeTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    /// <summary>与 SettingsStore.PersistedState 同形（测试工程不链接 SettingsStore：它依赖串口配置）。</summary>
    private sealed class Persisted
    {
        public AppSettings? Settings { get; set; }
        public double[]? BandBounds { get; set; }
        public double[][][]? BandPids { get; set; }
        public BandTuneMeta?[][]? BandTune { get; set; }
    }

    /// <summary>v0.5 及以前写出的存档（字段同 2dad29d 的 AppSettings 子集，没有 BandTune / 容积 / 传动比），PID 是现场整定值。</summary>
    private const string OldFile = """
    {
      "Settings": { "TMaxSp": 200, "PMaxSp": 500, "RpmMax": 1000, "VolMax": 50, "OverPressure": 510, "OverTemp": 250,
                    "StirRampMs": 1000, "PressFullScaleMPa": 4.137, "LeakRate": 2, "LastLeakTestUtc": "2026-11-01T08:00:00Z" },
      "BandBounds": [ 100, 200 ],
      "BandPids": [
        [ [ 11.3, 152, 27 ], [ 13.8, 191, 33 ], [ 20, 240, 40 ] ],
        [ [ 11.9, 160, 28 ], [ 14.2, 197, 34 ], [ 20, 240, 40 ] ],
        [ [ 12.4, 171, 29 ], [ 15.1, 204, 37 ], [ 20, 240, 40 ] ],
        [ [ 10.8, 149, 26 ], [ 13.7, 211, 35 ], [ 20, 240, 40 ] ],
        [ [ 12.1, 158, 27 ], [ 15.1, 218, 33 ], [ 20, 240, 40 ] ],
        [ [ 11.5, 162, 30 ], [ 13.0, 185, 31 ], [ 20, 240, 40 ] ],
        [ [ 12.0, 150, 25 ], [ 14.0, 190, 30 ], [ 20, 240, 40 ] ],
        [ [ 12.7, 175, 31 ], [ 15.8, 199, 36 ], [ 20, 240, 40 ] ]
      ]
    }
    """;

    [Fact]
    public void OldFile_KeepsEveryTunedPid_AndIsBackedUpOnce()
    {
        File.WriteAllText(FilePath, OldFile);
        var st = SettingsFileGuard.Read(FilePath, t => JsonSerializer.Deserialize<Persisted>(t));
        Assert.NotNull(st);
        // 现场整定的 PID 一个数都不变
        var want = JsonSerializer.Deserialize<Persisted>(OldFile)!.BandPids!;
        Assert.Equal(JsonSerializer.Serialize(want), JsonSerializer.Serialize(st!.BandPids));
        Assert.Equal(15.1, st.BandPids![4][1][0]);
        Assert.Equal(new[] { 100.0, 200.0 }, st.BandBounds);
        // 没有整定来历：为空（界面显示「无整定记录」，不当成未整定）
        Assert.Null(st.BandTune);
        // 用户原来的设置照旧（转速上限仍是存档里的 1000，不被新默认值 450 覆盖）；新增项取默认
        Assert.Equal(1000, st.Settings!.RpmMax);
        Assert.Equal(50, st.Settings.VolMax);
        Assert.Equal(10, st.Settings.VesselVolume);
        Assert.Equal(StirGear.DefaultRatio, st.Settings.StirGearRatio, 6);
        // 原样备份一次
        string bak = FilePath + ".bak-" + SettingsFileGuard.PreV06Tag;
        Assert.Equal(OldFile, File.ReadAllText(bak));
        File.WriteAllText(bak, "已有的备份");
        SettingsFileGuard.Read(FilePath, t => JsonSerializer.Deserialize<Persisted>(t));
        Assert.Equal("已有的备份", File.ReadAllText(bak));   // 不覆盖已有备份
    }

    [Fact]
    public void NewFile_WithBandTune_IsNotBackedUp()
    {
        File.WriteAllText(FilePath, """{ "BandPids": [], "BandTune": [] }""");
        Assert.NotNull(SettingsFileGuard.Read(FilePath, t => JsonSerializer.Deserialize<Persisted>(t)));
        Assert.False(File.Exists(FilePath + ".bak-" + SettingsFileGuard.PreV06Tag));
    }

    [Fact]
    public void UnreadableFile_IsSavedAside_BeforeReturningNull()
    {
        File.WriteAllText(FilePath, "{ 坏掉的文件 ");
        var st = SettingsFileGuard.Read(FilePath, t => JsonSerializer.Deserialize<Persisted>(t), () => new DateTime(2026, 11, 18, 9, 30, 0));
        Assert.Null(st);
        Assert.Equal("{ 坏掉的文件 ", File.ReadAllText(FilePath + ".bak-unreadable-20261118-093000"));
    }

    [Fact]
    public void MissingFile_ReturnsNull_WithoutBackup()
    {
        Assert.Null(SettingsFileGuard.Read(FilePath, t => JsonSerializer.Deserialize<Persisted>(t)));
        Assert.Empty(Directory.GetFiles(_dir));
    }
}
