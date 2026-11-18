using System;
using System.IO;
using ParallelReactor.Control.Core.Models;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// pc-v3 样稿的型号表（Fixtures/models-pcv3.json = 2026-11-18 之前内置的 models.json 原样，样稿 research/pc-v3/js/models.js 的移植）。
/// 2026-11-18 用户确认真实规格后内置表改了（全部型号一台电机齿轮传动、转速上限 450、气路 惰性气体 / 气体 A / 气体 B、HT-PR8 10 mL 釜），
/// 但对照 pc-v3 样稿算出来的向量（MockVectors/、DemoDump 的场景、曲线与文字宽度）仍是旧表的结果：这些对照测试用这份表，
/// 算法照旧逐字对照；内置表的真实规格由 ModelProfilesTests.Embedded_ConfirmedFacts 等测试单独检查。
/// 两个测试工程共用（App.Tests 链接这个文件和 json，见那边的 csproj）。
/// </summary>
public static class PcV3Models
{
    public const string FileName = "models-pcv3.json";

    private static readonly Lazy<ModelProfiles> Lazy = new(() =>
        ModelProfiles.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", FileName)), FileName));

    /// <summary>pc-v3 样稿的型号表（只解析这份文件，不叠加内置表）。</summary>
    public static ModelProfiles Table => Lazy.Value;

    /// <summary>按键取 pc-v3 的型号（PR4 / PR8 / PR8-4 / PR16）；没有时抛出。</summary>
    public static ModelProfile Get(string key) => Table.Get(key) ?? throw new ArgumentException("pc-v3 型号表里没有 " + key, nameof(key));
}
