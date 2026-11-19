using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using ParallelReactor.Client;
using ParallelReactor.Control.Core.Format;
using ParallelReactor.Control.Core.Models;
using ParallelReactor.Models;

namespace ParallelReactor.Control.Core.Programs.Editor;

/// <summary>曲线升温的一段（HMI heat.params.segments[]：段目标温度 ℃、升温速率 ℃/min、保温 min）。</summary>
public sealed record CurveSeg(double Temp, double Rate, double Hold);

/// <summary>
/// 程序的一步（融合版 F6 程序页的编辑模型）：类型 + 用户起的名字 + 参数。参数键同 HMI 的 §6.9（heat.temp、press.pres …），
/// sel 字段存协议码（'inert' / 'A' / 'on' …），数值存电脑端的内部单位（只有压力不同：MPa，界面按设置的压力单位显示，下发时换算 psi）。
/// </summary>
public sealed class PcStep
{
    public PcStep(StepType type, string name)
    {
        Type = type;
        Name = name;
    }

    public StepType Type { get; }
    public string Name { get; set; }
    public Dictionary<string, double> Num { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Sel { get; } = new(StringComparer.Ordinal);
    /// <summary>曲线升温的段（heat.mode = curve 时用）。</summary>
    public List<CurveSeg> Segments { get; } = new();

    public double N(string key, double fallback = double.NaN) => Num.TryGetValue(key, out var v) ? v : fallback;
    public string S(string key, string fallback = "") => Sel.TryGetValue(key, out var v) ? v : fallback;

    public PcStep Clone()
    {
        var c = new PcStep(Type, Name);
        foreach (var (k, v) in Num) c.Num[k] = v;
        foreach (var (k, v) in Sel) c.Sel[k] = v;
        c.Segments.AddRange(Segments);
        return c;
    }

    /// <summary>
    /// 新步骤：数值取 schema 的默认值（压力按设置的单位取整）；选项取 STEPS-SPEC §1 的默认码（schema 里有这一项时；否则第一项）；
    /// schema 没有的键按 §1 的默认值补（<see cref="StepDefaults.Fill"/>）。名字 = 类型名。
    /// </summary>
    public static PcStep New(StepType t, ProgramSchema schema)
    {
        var spec = schema.Of(t);
        var s = new PcStep(t, spec?.Label ?? t.Zh());
        var defs = StepDefaults.Of(t);
        if (spec != null)
            foreach (var f in spec.Fields)
            {
                if (f.IsSelect)
                {
                    string? want = f.DefaultCode ?? defs.FirstOrDefault(d => d.Key == f.Key).Def as string;
                    s.Sel[f.Key] = want != null && f.Options.Any(o => o.Code == want) ? want : f.Options[0].Code;
                }
                else s.Num[f.Key] = f.NewValue();
            }
        if (t == StepType.Heat) s.Segments.Add(new CurveSeg(s.N("temp", 80), 3, 20));
        return StepDefaults.Fill(s);
    }
}

/// <summary>
/// 程序库里的一个程序（融合版 F6）：id、名字、适用型号（型号表的键 PR8 / PR4 / PR16）、修改时刻与人、草稿、步骤。
/// 存在本机（<c>library.json</c>，见 App 的 ProgramLibraryStore）；下发时转换成 HMI 的程序 JSON（<see cref="ToDoc"/>）。
/// </summary>
public sealed class PcProgram
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "未命名程序";
    /// <summary>型号表的键（PR8 …）。</summary>
    public string Model { get; set; } = "PR8";
    public DateTime Modified { get; set; }
    public string By { get; set; } = "本机";
    public bool Draft { get; set; }
    public List<PcStep> Steps { get; } = new();

    /// <summary>
    /// 读进来时从旧格式转换过（STEPS-SPEC §3：吹扫 / 等待 / 定时搅拌 / 带方式的结束 / 按 endBy 的反应）。只在内存里：编辑器顶上提示「已从旧格式转换，请检查」，
    /// 保存后按新格式写盘、下次读进来就不再是转换的了。
    /// </summary>
    public bool Migrated { get; set; }

    /// <summary>转换时旧值超出新范围被收紧的说明（同 HMI 的 PUT 提示；只在内存里，编辑器的转换提示条里列出）。</summary>
    public List<string> MigrationNotes { get; } = new();

    public PcProgram Clone(string? id = null, string? name = null)
    {
        var p = new PcProgram { Id = id ?? Id, Name = name ?? Name, Model = Model, Modified = Modified, By = By, Draft = Draft, Migrated = Migrated };
        p.MigrationNotes.AddRange(MigrationNotes);
        p.Steps.AddRange(Steps.Select(s => s.Clone()));
        return p;
    }

    /// <summary>修改时刻的写法（样稿 LIB.mod）：今天「今天 10:12」，否则「11-14 16:20」。</summary>
    public static string ModText(DateTime t, DateTime now) =>
        t.Date == now.Date ? "今天 " + t.ToString("HH:mm", CultureInfo.InvariantCulture) : t.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);

    // ———————————————————— HMI 程序 JSON ————————————————————

    /// <summary>
    /// 转成 HMI 的程序（PUT /channels/{id}/program 的正文，§6.9 / STEPS-SPEC §6）：类型码、sel 码、数值（压力 MPa → psi，保留 1 位小数；压降 MPa/h → psi/h，2 位）、
    /// 曲线段（升温方式 = 曲线时）。只发 v0.8 的键（<see cref="StepDefaults.Known"/>）。步骤名不在协议里（HMI 的 name 是只读的），本机的名字只在 PC 上显示。
    /// </summary>
    public ProgramDoc ToDoc(string? name = null)
    {
        var doc = new ProgramDoc { Name = name ?? Name };
        foreach (var s in Steps) doc.Steps.Add(StepDoc(s));
        return doc;
    }

    public static ProgramStepDoc StepDoc(PcStep s)
    {
        var d = new ProgramStepDoc(s.Type.ToCode());
        foreach (var (k, v) in s.Sel) if (StepDefaults.Known(s.Type, k)) d.Params[k] = JsonSerializer.SerializeToElement(v);
        foreach (var (k, v) in s.Num) if (StepDefaults.Known(s.Type, k)) d.Params[k] = JsonSerializer.SerializeToElement(StepDefaults.ToWire(s.Type, k, v));
        if (s.Type == StepType.Heat && s.S("mode") == "curve" && s.Segments.Count > 0)
            d.Params["segments"] = JsonSerializer.SerializeToElement(s.Segments.Select(g => new Dictionary<string, double> { ["temp"] = g.Temp, ["rate"] = g.Rate, ["hold"] = g.Hold }).ToArray());
        return d;
    }

    /// <summary>
    /// HMI 程序 JSON → 本机（旧程序页的草稿 drafts.json 迁移、导入 JSON）：旧格式按 STEPS-SPEC §3 转换（经 Contracts 的 StepMigration，
    /// 转换过就标 <see cref="Migrated"/>），压力 psi → MPa（按设置的压力单位取整），缺的参数按 §1 的默认值补；不认识的步骤类型跳过。
    /// </summary>
    public static PcProgram FromDoc(ProgramDoc doc, string model, string id)
    {
        var p = new PcProgram { Id = id, Name = doc.Name, Model = model };
        var mig = new List<MigStep>(doc.Steps.Count);
        foreach (var d in doc.Steps)
        {
            if (d.StepType is null) continue;
            var ps = new Dictionary<string, object>(StringComparer.Ordinal);
            var segs = new List<(double, double, double)>();
            foreach (var (k, e) in d.Params)
            {
                if (e.ValueKind == JsonValueKind.String) ps[k] = e.GetString() ?? "";
                else if (e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var v)) ps[k] = v;
                else if (k == "segments" && e.ValueKind == JsonValueKind.Array)
                    foreach (var g in e.EnumerateArray()) segs.Add((G(g, "temp"), G(g, "rate"), G(g, "hold")));
            }
            mig.Add(new MigStep(d.Type, ps, segs));
        }
        p.Steps.AddRange(StepDefaults.FromWire(mig, out bool migrated, out var notes));
        p.Migrated = migrated;
        p.MigrationNotes.AddRange(notes);
        return p;
    }

    private static double G(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    /// <summary>
    /// 转成显示 / 演示用的程序（Core 的 ProgramDef：步骤名 = 本机的名字；预估分钟 = <see cref="ProgramPlanner"/> 的估计，演示数据按它推进）。
    /// </summary>
    public ProgramDef ToDef(ModelProfile? model, string? id = null, string? name = null)
    {
        var steps = ProgramDocs.ToSteps(ToDoc(name), model);
        var plan = ProgramPlanner.Plan(Steps);
        var list = new List<ProgramStep>(steps.Count);
        for (int i = 0; i < steps.Count && i < Steps.Count; i++)
        {
            double est = plan.Bounds[i].M1 - plan.Bounds[i].M0;
            list.Add(steps[i] with { Name = Steps[i].Name, EstMin = Math.Max(steps[i].EstMin, Math.Round(est, 1)) });
        }
        return new ProgramDef(id ?? Id, name ?? Name, "", list);
    }

    // ———————————————————— 本机存储（library.json 的一项） ————————————————————

    public void Write(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("id", Id);
        w.WriteString("name", Name);
        w.WriteString("model", Model);
        w.WriteString("modified", Modified.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
        w.WriteString("by", By);
        w.WriteBoolean("draft", Draft);
        w.WritePropertyName("steps");
        WriteSteps(w, Steps);
        w.WriteEndObject();
    }

    public static void WriteSteps(Utf8JsonWriter w, IEnumerable<PcStep> steps)
    {
        w.WriteStartArray();
        foreach (var s in steps)
        {
            w.WriteStartObject();
            w.WriteString("type", s.Type.ToCode());
            w.WriteString("name", s.Name);
            w.WritePropertyName("params");
            w.WriteStartObject();
            foreach (var (k, v) in s.Sel) w.WriteString(k, v);
            foreach (var (k, v) in s.Num) w.WriteNumber(k, v);
            if (s.Segments.Count > 0)
            {
                w.WritePropertyName("segments");
                w.WriteStartArray();
                foreach (var g in s.Segments)
                {
                    w.WriteStartObject();
                    w.WriteNumber("temp", g.Temp);
                    w.WriteNumber("rate", g.Rate);
                    w.WriteNumber("hold", g.Hold);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    public static PcProgram Read(JsonElement e)
    {
        var p = new PcProgram
        {
            Id = Str(e, "id") ?? Guid.NewGuid().ToString("N")[..8],
            Name = Str(e, "name") ?? "未命名程序",
            Model = Str(e, "model") ?? "PR8",
            By = Str(e, "by") ?? "本机",
            Draft = e.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True,
            Modified = DateTime.TryParse(Str(e, "modified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var m) ? m : DateTime.Now,
        };
        if (e.TryGetProperty("steps", out var st))
        {
            // 旧版程序库（吹扫 / 等待 / 定时搅拌 / 带方式的结束）读进来就转换（STEPS-SPEC §3），保存时按新格式写回
            p.Steps.AddRange(StepDefaults.Migrate(ReadSteps(st), out bool migrated, out var notes));
            p.Migrated = migrated;
            p.MigrationNotes.AddRange(notes);
        }
        return p;
    }

    /// <summary>读 library.json / experiments.json 的步骤原样（旧格式的步骤也读出来，不转换；转换见 <see cref="StepDefaults.Migrate"/>）。</summary>
    public static List<PcStep> ReadSteps(JsonElement arr)
    {
        var list = new List<PcStep>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var x in arr.EnumerateArray())
        {
            if (!StepTypeCodes.TryParse(Str(x, "type"), out var t)) continue;
            var s = new PcStep(t, Str(x, "name") ?? t.Zh());
            if (x.TryGetProperty("params", out var ps) && ps.ValueKind == JsonValueKind.Object)
                foreach (var pr in ps.EnumerateObject())
                {
                    if (pr.Value.ValueKind == JsonValueKind.String) s.Sel[pr.Name] = pr.Value.GetString() ?? "";
                    else if (pr.Value.ValueKind == JsonValueKind.Number) s.Num[pr.Name] = pr.Value.GetDouble();
                    else if (pr.Name == "segments" && pr.Value.ValueKind == JsonValueKind.Array)
                        foreach (var g in pr.Value.EnumerateArray()) s.Segments.Add(new CurveSeg(G(g, "temp"), G(g, "rate"), G(g, "hold")));
                }
            list.Add(s);
        }
        return list;
    }

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
