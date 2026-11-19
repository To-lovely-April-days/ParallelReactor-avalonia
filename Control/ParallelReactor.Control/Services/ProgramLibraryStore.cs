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
/// 本机的程序库（融合版 F6 程序页）：配置目录下的 <c>library.json</c>（同 drafts.json / devices.json 的目录，PR_CONTROL_CONFIG）。
/// <code>{ "version": 1, "programs": [ { "id", "name", "model": "PR8", "modified": "2026-11-14T16:20:00", "by": "张工", "draft": false,
///   "steps": [ { "type": "heat", "name": "升温", "params": { "mode": "fixed", "temp": 150, "wait": 15 } } … ] } … ] }</code>
/// 参数键同 HMI 的程序 JSON（§6.9），sel 存协议码；压力存 MPa（下发时换算 psi）。
/// <list type="bullet">
/// <item>第一次打开（没有 library.json）：把旧程序页的草稿（drafts.json，每个 RV 一份）迁移成草稿程序「RV1 草稿 · 名字」，适用型号 = 当时第一台设备的型号；
///   都没有时放设计稿的三个推荐模板（ProgramTemplates）。drafts.json 不删（旧的检查器下拉还读它）。</item>
/// <item>程序步骤 v0.8：旧格式的程序（library.json 里的吹扫 / 等待 / 定时搅拌 / 带方式的结束、drafts.json 的旧草稿）读进来就按 STEPS-SPEC §3 转换
///   （PcProgram.Read / FromDoc → Contracts 的 StepMigration），标 PcProgram.Migrated，编辑器顶上提示「已从旧格式转换，请检查」；保存时按新格式写回。</item>
/// <item>演示 / 截图（PR_CONTROL_DEMO / PR_CONTROL_SHOT）：用样稿的程序库（DemoPrograms），不读也不写文件。</item>
/// </list>
/// </summary>
public sealed class ProgramLibraryStore
{
    private readonly bool _demo;
    private readonly string _dir;

    public ProgramLibraryStore(bool demo, string? firstModel = null, string? dir = null)
    {
        _demo = demo;
        _dir = dir ?? ControlConfig.Directory;
        Programs = demo ? DemoPrograms.Library() : Load(firstModel ?? "PR8");
    }

    public const string FileName = "library.json";
    public string FilePath => Path.Combine(_dir, FileName);

    /// <summary>程序（顺序 = 程序库的顺序：新建 / 复制的在最前）。</summary>
    public List<PcProgram> Programs { get; }

    /// <summary>读的时候遇到的问题（文件坏了：从示例开始、不覆盖坏文件，直到第一次保存）。</summary>
    public string? LoadError { get; private set; }

    /// <summary>这次是从 drafts.json 迁移来的（提示一次）。</summary>
    public int Migrated { get; private set; }

    public PcProgram? Find(string? id) => Programs.FirstOrDefault(p => p.Id == id);

    /// <summary>演示：程序库回到样稿的初始内容（截图前「像新打开」，上一张截图里改过的参数不留下）。非演示不动。</summary>
    public void ResetDemo()
    {
        if (!_demo) return;
        Programs.Clear();
        Programs.AddRange(DemoPrograms.Library());
    }

    /// <summary>不重复的 id（'P' + 时间戳；同一毫秒再加序号）。</summary>
    public string NewId(string prefix = "P")
    {
        string id = prefix + DateTime.Now.ToString("yyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture);
        int k = 1;
        while (Find(id) != null) id = prefix + DateTime.Now.ToString("yyMMddHHmmssfff", System.Globalization.CultureInfo.InvariantCulture) + "-" + k++;
        return id;
    }

    private List<PcProgram> Load(string firstModel)
    {
        var list = new List<PcProgram>();
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath), ControlConfig.TolerantJson);
                if (doc.RootElement.TryGetProperty("programs", out var arr) && arr.ValueKind == JsonValueKind.Array)
                    foreach (var e in arr.EnumerateArray()) list.Add(PcProgram.Read(e));
                return list;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            LoadError = "程序库 library.json 读不了（" + ex.Message + "）· 先用示例程序，保存时会覆盖它";
        }
        // 迁移旧程序页的草稿（drafts.json：{ "1": { "name", "program" } … }）
        try
        {
            var drafts = Path.Combine(_dir, "drafts.json");
            if (LoadError == null && File.Exists(drafts))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(drafts), ControlConfig.TolerantJson);
                var at = File.GetLastWriteTime(drafts);
                foreach (var p in d.RootElement.EnumerateObject().OrderBy(x => int.TryParse(x.Name, out var n) ? n : 99))
                {
                    if (!p.Value.TryGetProperty("program", out var pg)) continue;
                    var name = p.Value.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                    var doc = ProgramDoc.FromJson(pg.GetRawText(), name);
                    var prog = PcProgram.FromDoc(doc, firstModel, "D" + p.Name);
                    prog.Name = (p.Name == "0" ? "新建程序" : "RV" + p.Name + " 草稿") + " · " + doc.Name;
                    prog.Draft = true;
                    prog.Modified = at;
                    prog.By = "本机";
                    list.Add(prog);
                }
                Migrated = list.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or FormatException or UnauthorizedAccessException) { }
        if (list.Count == 0)
        {
            // 第一次打开、没有旧草稿：放设计稿的三个推荐模板（加氢筛选 · 只做气密 · 惰性气氛加热）
            foreach (var t in ProgramTemplates.All(firstModel))
            {
                t.Modified = DateTime.Now;
                t.By = "本机";
                list.Add(t);
            }
        }
        return list;
    }

    /// <summary>写盘（演示 / 截图不写）。返回错误文字；成功为 null。</summary>
    public string? Save()
    {
        if (_demo || ControlConfig.WritesDisabledByEnvironment) return null;
        try
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllText(FilePath, ToJson(Programs), new UTF8Encoding(false));
            LoadError = null;
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "程序库保存失败：" + ex.Message;
        }
    }

    public static string ToJson(IEnumerable<PcProgram> programs)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", 1);
            w.WritePropertyName("programs");
            w.WriteStartArray();
            foreach (var p in programs) p.Write(w);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}

/// <summary>
/// 本机的实验方案（融合版 F6 条件矩阵）：数据目录下的 <c>experiments.json</c>（PR_CONTROL_DATA；没设时 文稿/ParallelReactor —— 同旧程序页的导出目录）。
/// 设备上没有样品 / 催化剂名，所以存在 PC 上：
/// <code>{ "version": 1,
///   "plans": { "&lt;设备 Id&gt;/&lt;模板 id&gt;": { 条件矩阵，见 Core 的 MatrixJson } },
///   "runs": [ { "at": "2026-11-15T14:31:00", "device": "&lt;设备 Id&gt;", "alias": "1号机", "session": "催化剂 × 温度筛选 · 批次 8", "plan": { 同上 },
///               "channels": [1, 2, 3], "sent": [1, 2] } ] }</code>
/// plans = 「保存方案」与每次下发时的最新方案（再打开同一台设备的同一个模板时接着用）；runs = 每次「下发并运行」的记录（记录页对照样品名用，最新在前，最多 200 条）。
/// 演示 / 截图不读也不写（只在内存里）。
/// </summary>
public sealed class ExperimentStore
{
    private readonly bool _memoryOnly;
    private readonly Dictionary<string, string> _plans = new(StringComparer.Ordinal);
    private readonly List<string> _runs = new();

    public ExperimentStore(bool memoryOnly, string? dir = null)
    {
        _memoryOnly = memoryOnly || ControlConfig.WritesDisabledByEnvironment;
        Directory = dir ?? DefaultDirectory;
        if (!_memoryOnly) Load();
    }

    public const string FileName = "experiments.json";
    public const int MaxRuns = 200;

    /// <summary>数据目录（<see cref="DataDirs.Root"/>：PR_CONTROL_DATA &gt; 设置页 &gt; 文稿/ParallelReactor）。</summary>
    public static string DefaultDirectory => DataDirs.Root;

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, FileName);
    public int RunCount => _runs.Count;

    public static string PlanKey(Guid device, string template) => device.ToString("D") + "/" + template;

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(FilePath), ControlConfig.TolerantJson);
            if (doc.RootElement.TryGetProperty("plans", out var plans) && plans.ValueKind == JsonValueKind.Object)
                foreach (var p in plans.EnumerateObject()) _plans[p.Name] = p.Value.GetRawText();
            if (doc.RootElement.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
                foreach (var r in runs.EnumerateArray()) _runs.Add(r.GetRawText());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }

    /// <summary>读一个方案（没有为 null）。</summary>
    public MatrixPlan? Plan(Guid device, PcProgram template, int k, IReadOnlyList<MxChannel> chs)
    {
        if (!_plans.TryGetValue(PlanKey(device, template.Id), out var json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return MatrixJson.Read(doc.RootElement, template, k, chs);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>存方案（保存方案 / 下发时）。返回错误文字；成功为 null。</summary>
    public string? SavePlan(Guid device, MatrixPlan m, DateTime now)
    {
        _plans[PlanKey(device, m.TemplateId)] = Json(w => MatrixJson.Write(w, m, now));
        return Flush();
    }

    /// <summary>记一次下发（样品名、参数、哪几路下发成功）。</summary>
    public string? AddRun(Guid device, string alias, MatrixPlan m, IReadOnlyList<int> channels, IReadOnlyList<int> sent, DateTime now)
    {
        _plans[PlanKey(device, m.TemplateId)] = Json(w => MatrixJson.Write(w, m, now));
        _runs.Insert(0, Json(w =>
        {
            w.WriteStartObject();
            w.WriteString("at", now.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteString("device", device.ToString("D"));
            w.WriteString("alias", alias);
            w.WriteString("session", m.Name);
            w.WritePropertyName("plan");
            MatrixJson.Write(w, m, now);
            w.WritePropertyName("channels");
            w.WriteStartArray();
            foreach (var n in channels) w.WriteNumberValue(n);
            w.WriteEndArray();
            w.WritePropertyName("sent");
            w.WriteStartArray();
            foreach (var n in sent) w.WriteNumberValue(n);
            w.WriteEndArray();
            w.WriteEndObject();
        }));
        while (_runs.Count > MaxRuns) _runs.RemoveAt(_runs.Count - 1);
        return Flush();
    }

    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) write(w);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string? Flush()
    {
        if (_memoryOnly) return null;
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartObject();
                w.WriteNumber("version", 1);
                w.WritePropertyName("plans");
                w.WriteStartObject();
                foreach (var (k, v) in _plans) { w.WritePropertyName(k); using var d = JsonDocument.Parse(v); d.RootElement.WriteTo(w); }
                w.WriteEndObject();
                w.WritePropertyName("runs");
                w.WriteStartArray();
                foreach (var r in _runs) { using var d = JsonDocument.Parse(r); d.RootElement.WriteTo(w); }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            File.WriteAllBytes(FilePath, ms.ToArray());
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "实验方案保存失败：" + ex.Message; }
    }
}
