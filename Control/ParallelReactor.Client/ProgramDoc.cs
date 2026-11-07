using System;
using System.Collections.Generic;
using System.Text.Json;
using ParallelReactor.Models;

namespace ParallelReactor.Client;

/// <summary>§6.9 程序 JSON 的客户端模型：<c>{"steps":[{"type":"heat","params":{…}}]}</c>。sel 字段用枚举码（见 /program-schema）。</summary>
public sealed class ProgramDoc
{
    public string Name { get; set; } = "未命名程序";
    public List<ProgramStepDoc> Steps { get; } = new();

    public ProgramDoc Add(StepType type, object? parameters = null)
    {
        Steps.Add(ProgramStepDoc.Of(type, parameters));
        return this;
    }

    /// <summary>PUT /channels/{id}/program 的正文。</summary>
    public string ToJson()
    {
        using var ms = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WritePropertyName("steps");
            w.WriteStartArray();
            foreach (var s in Steps)
            {
                w.WriteStartObject();
                w.WriteString("type", s.Type);
                w.WritePropertyName("params");
                w.WriteStartObject();
                foreach (var (k, v) in s.Params) { w.WritePropertyName(k); v.WriteTo(w); }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    public static ProgramDoc FromJson(string json, string? name = null)
    {
        var doc = new ProgramDoc { Name = name ?? "导入程序" };
        using var d = JsonDocument.Parse(json);
        if (!d.RootElement.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array) throw new FormatException("缺少 steps 数组");
        foreach (var s in steps.EnumerateArray())
        {
            var type = s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : throw new FormatException("步骤缺少 type");
            var step = new ProgramStepDoc(type);
            if (s.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                foreach (var prop in p.EnumerateObject()) step.Params[prop.Name] = prop.Value.Clone();
            doc.Steps.Add(step);
        }
        return doc;
    }
}

/// <summary>一步：type 为小写码，params 为原样 JSON 值。</summary>
public sealed class ProgramStepDoc
{
    public ProgramStepDoc(string type) => Type = type;
    public string Type { get; }
    public StepType? StepType => StepTypeCodes.TryParse(Type, out var t) ? t : null;
    public Dictionary<string, JsonElement> Params { get; } = new(StringComparer.Ordinal);

    public ProgramStepDoc Set(string key, object? value)
    {
        Params[key] = JsonSerializer.SerializeToElement(value, value?.GetType() ?? typeof(object), ClientJson.Reflection);
        return this;
    }

    /// <summary>匿名对象 → params（camelCase）。</summary>
    public static ProgramStepDoc Of(StepType type, object? parameters)
    {
        var s = new ProgramStepDoc(type.ToCode());
        if (parameters != null)
        {
            var el = JsonSerializer.SerializeToElement(parameters, parameters.GetType(), ClientJson.Reflection);
            foreach (var p in el.EnumerateObject()) s.Params[p.Name] = p.Value.Clone();
        }
        return s;
    }
}
