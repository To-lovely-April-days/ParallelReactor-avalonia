using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using ParallelReactor.Models;

namespace ParallelReactor.Api;

/// <summary>
/// 下发快照 <see cref="StepSnapshot"/> → §6.9 程序 JSON（<see cref="ProgramStepDto"/>），是 Api/Hmi/ProgramJson.Parse 的反方向：
/// sel 字段的界面 label 经 <see cref="ProgramSchemaCodes"/> 换成英文枚举码（已经是枚举码的、表里没有的原样输出）；数值输出为 JSON 数字；
/// heat 步骤总是带 mode（缺省 fixed），mode = curve 时追加 segments[{temp, rate, hold}]。纯逻辑（不依赖 StepDef.Registry），测试工程直接链接。
/// GET /channels/{id}/program/run 用它导出运行中程序的冻结步骤（SnapshotBuilder 按步骤列表缓存，每次运行只转换一次）。
/// </summary>
public static class ProgramStepFormat
{
    public static ProgramStepDto[] Format(IReadOnlyList<StepSnapshot> steps)
    {
        var list = new ProgramStepDto[steps.Count];
        for (int i = 0; i < steps.Count; i++) list[i] = Format(steps[i]);
        return list;
    }

    public static ProgramStepDto Format(StepSnapshot s)
    {
        string type = s.Type.ToString().ToLowerInvariant();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            bool heat = s.Type == StepType.Heat;
            if (heat) w.WriteString("mode", s.IsCurve ? "curve" : "fixed");
            foreach (var (key, value) in s.Params)
            {
                if (heat && key == "mode") continue;
                WriteValue(w, type, key, value);
            }
            if (s.IsCurve)
            {
                w.WriteStartArray("segments");
                foreach (var (t, r, h) in s.Segments)
                {
                    w.WriteStartObject();
                    w.WriteNumber("temp", t);
                    w.WriteNumber("rate", r);
                    w.WriteNumber("hold", h);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(ms.ToArray());
        return new ProgramStepDto(type, doc.RootElement.Clone(), s.Name, s.Detail);
    }

    private static void WriteValue(Utf8JsonWriter w, string type, string key, object? value)
    {
        switch (value)
        {
            case null: w.WriteNull(key); break;
            case string str:
                // 界面 label → 枚举码；已是枚举码或不在映射表里的字符串原样输出
                w.WriteString(key, ProgramSchemaCodes.CodeOf(type, key, str) ?? str);
                break;
            case bool b: w.WriteBoolean(key, b); break;
            case double d: WriteNumber(w, key, d); break;
            case float f: WriteNumber(w, key, f); break;
            case decimal m: w.WriteNumber(key, m); break;
            case IConvertible c when value is int or long or short or byte or uint or ulong or ushort or sbyte:
                w.WriteNumber(key, c.ToInt64(CultureInfo.InvariantCulture)); break;
            default: w.WriteString(key, Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    /// <summary>非有限数（理论上不会出现：ProgramJson / 键盘都已拒绝）输出 null，不让整份响应因一个值序列化失败。</summary>
    private static void WriteNumber(Utf8JsonWriter w, string key, double d)
    {
        if (double.IsFinite(d)) w.WriteNumber(key, d);
        else w.WriteNull(key);
    }
}
