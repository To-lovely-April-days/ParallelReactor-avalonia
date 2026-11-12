using System;
using System.Globalization;
using System.Text.Json;

namespace ParallelReactor.Control.Tests;

/// <summary>
/// 读取 <see cref="CoreMockVectors.Json"/>（gen-core-vectors.js 用样稿的 JS 函数算出的期望值）：只解析一次；数值的特殊写法还原成 double?。
/// </summary>
internal static class MockVectorData
{
    private static readonly Lazy<JsonDocument> Doc = new(() => JsonDocument.Parse(CoreMockVectors.Json));

    public static JsonElement Root => Doc.Value.RootElement;

    public static JsonElement Get(string name) => Root.GetProperty(name);

    /// <summary>"null" / "undefined" → null；"NaN" / "Infinity" / "-Infinity" / "-0" → 对应的 double；其余是 JS 的最短往返写法，按不变区域解析。</summary>
    public static double? Num(string s) => s switch
    {
        "null" or "undefined" => null,
        "NaN" => double.NaN,
        "Infinity" => double.PositiveInfinity,
        "-Infinity" => double.NegativeInfinity,
        "-0" => BitConverter.Int64BitsToDouble(long.MinValue),
        _ => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture),
    };
}
