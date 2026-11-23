using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Demo;
using ParallelReactor.Control.Core.Programs.Editor;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 本机的模板库（设计稿 research/fusion-port/tpl-design 方向 A）：内置模板在代码里（<see cref="ProgramTemplates.BuiltIn"/>，按型号生成、不存盘），
/// 「我的模板」存在配置目录下的 <c>templates.json</c>（同 library.json 的目录，PR_CONTROL_CONFIG）：
/// <code>{ "version": 1, "templates": [ { "note": "…", "from": "催化加氢筛选 · 120 ℃", "program": { 同 library.json 的一个程序 } } … ] }</code>
/// 演示 / 截图（PR_CONTROL_DEMO / PR_CONTROL_SHOT）：放两个示例的「我的模板」，不读也不写文件。
/// </summary>
public sealed class ProgramTemplateStore
{
    private readonly bool _demo;
    private readonly string _dir;

    public ProgramTemplateStore(bool demo, string? dir = null)
    {
        _demo = demo;
        _dir = dir ?? ControlConfig.Directory;
        Mine = demo ? DemoMine() : Load();
    }

    public const string FileName = "templates.json";
    public string FilePath => Path.Combine(_dir, FileName);

    /// <summary>我的模板（新存的在最前）。</summary>
    public List<PcTemplate> Mine { get; }

    /// <summary>读的时候遇到的问题（文件坏了：当作没有，不覆盖坏文件，直到第一次保存）。</summary>
    public string? LoadError { get; private set; }

    /// <summary>某个型号能用的全部模板：内置（按这个型号生成）在前，我的模板（同型号）在后。</summary>
    public IReadOnlyList<PcTemplate> For(string model) => ProgramTemplates.BuiltIn(model).Concat(Mine.Where(t => t.Model == model)).ToList();

    public PcTemplate? FindMine(string? id) => Mine.FirstOrDefault(t => t.Id == id);

    /// <summary>不重复的 id（'U' + 时间戳）。</summary>
    public string NewId()
    {
        string stamp() => "U" + DateTime.Now.ToString("yyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        string id = stamp();
        int k = 1;
        while (FindMine(id) != null) id = stamp() + "-" + k++;
        return id;
    }

    /// <summary>存一个新模板（放最前）并写盘。返回错误文字；成功为 null。</summary>
    public string? Add(PcTemplate t)
    {
        Mine.Insert(0, t);
        return Save();
    }

    /// <summary>改名并写盘。</summary>
    public string? Rename(PcTemplate t, string name)
    {
        t.Name = name.Trim();
        return Save();
    }

    /// <summary>删除并写盘。</summary>
    public string? Remove(PcTemplate t)
    {
        Mine.Remove(t);
        return Save();
    }

    /// <summary>演示：回到示例内容（截图前「像新打开」）。非演示不动。</summary>
    public void ResetDemo()
    {
        if (!_demo) return;
        Mine.Clear();
        Mine.AddRange(DemoMine());
    }

    private static List<PcTemplate> DemoMine()
    {
        var lib = DemoPrograms.Library();
        var list = new List<PcTemplate>();
        if (lib.FirstOrDefault(p => p.Id == "H1") is { } h1)
            list.Add(PcTemplate.FromProgram(h1, "U1", "催化加氢 · 120 ℃ 标准流程", "Pd/C 体系常用的温度和压力", new DateTime(2026, 11, 12, 10, 5, 0), "张工"));
        if (lib.FirstOrDefault(p => p.Id == "A") is { } a)
            list.Add(PcTemplate.FromProgram(a, "U2", "平行合成 · 150 ℃", "", new DateTime(2026, 11, 10, 16, 40, 0), "李工"));
        return list;
    }

    private List<PcTemplate> Load()
    {
        var list = new List<PcTemplate>();
        try
        {
            if (!File.Exists(FilePath)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath), ControlConfig.TolerantJson);
            if (doc.RootElement.TryGetProperty("templates", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var e in arr.EnumerateArray())
                {
                    if (!e.TryGetProperty("program", out var pg) || pg.ValueKind != JsonValueKind.Object) continue;
                    var p = PcProgram.Read(pg);
                    p.Draft = false;
                    list.Add(new PcTemplate(p, false, Str(e, "note"), Str(e, "from")));
                }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or FormatException or InvalidOperationException or KeyNotFoundException)
        {
            list.Clear();
            LoadError = "模板库 templates.json 读不了（" + ex.Message + "）· 先当作没有自己的模板，下次存模板时会覆盖它";
        }
        return list;
    }

    private static string Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    /// <summary>写盘（演示 / 截图不写）。返回错误文字；成功为 null。</summary>
    public string? Save()
    {
        if (_demo || ControlConfig.WritesDisabledByEnvironment) return null;
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, ToJson(Mine), new UTF8Encoding(false));
            LoadError = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "模板库保存失败：" + ex.Message;
        }
    }

    public static string ToJson(IEnumerable<PcTemplate> templates)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WritePropertyName("templates");
            w.WriteStartArray();
            foreach (var t in templates)
            {
                w.WriteStartObject();
                w.WriteString("note", t.Note);
                w.WriteString("from", t.From);
                w.WritePropertyName("program");
                t.Program.Write(w);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
