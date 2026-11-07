using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using ParallelReactor.Client;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Services;

/// <summary>
/// 每个 RV 的本地程序草稿（HMI 没有 GET program，PC 侧只知道自己下发过什么）+「新建程序」草稿（键 0）。
/// 落盘到配置目录 drafts.json（截图模式不写）。首次打开给 mock 的 4 步示例（升温 80 → 升压 A 300 → 反应 60 min → 结束）。
/// </summary>
public sealed class ProgramDraftStore
{
    private readonly Dictionary<int, ProgramDoc> _drafts = new();
    private static string FilePath => Path.Combine(ControlConfig.Directory, "drafts.json");

    public ProgramDraftStore()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(FilePath));
                foreach (var p in d.RootElement.EnumerateObject())
                    if (int.TryParse(p.Name, out var rv) && p.Value.TryGetProperty("name", out var nm) && p.Value.TryGetProperty("program", out var pg))
                        _drafts[rv] = ProgramDoc.FromJson(pg.GetRawText(), nm.GetString());
            }
        }
        catch { }
    }

    public ProgramDoc Get(int rv)
    {
        if (!_drafts.TryGetValue(rv, out var doc))
        {
            doc = rv == 0 ? Template("新建程序") : Template("加氢测试-0" + rv);
            _drafts[rv] = doc;
        }
        return doc;
    }

    public void Set(int rv, ProgramDoc doc) { _drafts[rv] = doc; Save(); }

    public void Save()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PR_CONTROL_SHOT"))) return;
        try
        {
            Directory.CreateDirectory(ControlConfig.Directory);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var (rv, doc) in _drafts)
                {
                    w.WritePropertyName(rv.ToString());
                    w.WriteStartObject();
                    w.WriteString("name", doc.Name);
                    w.WritePropertyName("program");
                    using (var pd = JsonDocument.Parse(doc.ToJson())) pd.RootElement.WriteTo(w);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            File.WriteAllText(FilePath, System.Text.Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch { }
    }

    /// <summary>mock 示例程序（4 步）。</summary>
    public static ProgramDoc Template(string name)
    {
        var d = new ProgramDoc { Name = name };
        d.Add(StepType.Heat, new { mode = "fixed", temp = 80.0, wait = 10.0 });
        d.Add(StepType.Press, new { gas = "A", pres = 150.0, regulate = "on" });
        d.Add(StepType.React, new { endBy = "time", time = 60.0 });
        d.Add(StepType.End, new { mode = "vent", cool = 30.0 });
        return d;
    }
}
