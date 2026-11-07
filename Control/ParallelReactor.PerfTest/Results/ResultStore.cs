using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ParallelReactor.PerfTest.Engine;
using ParallelReactor.PerfTest.Model;
using ParallelReactor.PerfTest.Report;

namespace ParallelReactor.PerfTest.Results;

/// <summary>
/// 运行目录 &lt;root&gt;/&lt;runId&gt;/：result.json · raw/telemetry.csv（长表，每帧每通道一行，运行期间流式写）· manual-entries.csv · events.csv ·
/// summary.csv · report.html · settings-snapshot.json · hmi/（下载的会话 CSV）。
/// </summary>
public sealed class ResultStore : IDisposable
{
    public const string TelemetryHeader = "ts,seq,rv,state,tempC,tempSpC,heatOutputPct,pressurePsi,pressureRaw,valveOpen,stirOn,rpmSp,busVoltageV,estop,programState,stepIndex,itemId,stepId";
    public const string EventsHeader = "at,kind,itemId,stepId,text";
    public const string ManualHeader = "at,itemId,stepId,iteration,key,label,unit,instrument,channel,index,value,text";

    private readonly StreamWriter _telemetry, _events, _manual;
    private readonly object _lock = new();
    private bool _disposed;

    public ResultStore(string root, string runId)
    {
        Root = root;
        RunId = runId;
        Dir = Path.Combine(root, runId);
        Directory.CreateDirectory(Path.Combine(Dir, "raw"));
        Directory.CreateDirectory(Path.Combine(Dir, "hmi"));
        _telemetry = Open(Path.Combine(Dir, "raw", "telemetry.csv"), TelemetryHeader);
        _events = Open(Path.Combine(Dir, "events.csv"), EventsHeader);
        _manual = Open(Path.Combine(Dir, "manual-entries.csv"), ManualHeader);
    }

    public string Root { get; }
    public string RunId { get; }
    public string Dir { get; }
    public string ResultPath => Path.Combine(Dir, "result.json");
    public string ReportPath => Path.Combine(Dir, "report.html");
    public string SummaryPath => Path.Combine(Dir, "summary.csv");
    public string TelemetryPath => Path.Combine(Dir, "raw", "telemetry.csv");
    public string HmiDir => Path.Combine(Dir, "hmi");

    /// <summary>默认输出根：PR_CONTROL_DATA（默认 ~/Documents/ParallelReactor）/perf。</summary>
    public static string DefaultRoot()
    {
        var env = Environment.GetEnvironmentVariable("PR_CONTROL_DATA");
        var baseDir = string.IsNullOrWhiteSpace(env)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ParallelReactor")
            : env;
        return Path.Combine(baseDir, "perf");
    }

    private static StreamWriter Open(string path, string header)
    {
        bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
        var w = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        if (fresh) w.WriteLine(header);
        return w;
    }

    public void AppendTelemetry(IReadOnlyList<Sample> rows, string? itemId, string? stepId)
    {
        if (rows.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var s in rows)
        {
            sb.Append(s.Ts.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)).Append(',')
              .Append(s.Seq).Append(',').Append(s.Rv).Append(',').Append(s.State).Append(',')
              .Append(N(s.TempC)).Append(',').Append(N(s.TempSpC)).Append(',').Append(N(s.HeatOutputPct)).Append(',')
              .Append(N(s.PressurePsi)).Append(',').Append(s.PressureRaw?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(s.ValveOpen ? 1 : 0).Append(',').Append(s.StirOn ? 1 : 0).Append(',').Append(s.RpmSp).Append(',')
              .Append(N(s.BusVoltageV)).Append(',').Append(s.EstopActive ? 1 : 0).Append(',')
              .Append(s.ProgramState ?? "").Append(',').Append(s.StepIndex?.ToString(CultureInfo.InvariantCulture) ?? "").Append(',')
              .Append(itemId ?? "").Append(',').Append(stepId ?? "").Append('\n');
        }
        lock (_lock) if (!_disposed) _telemetry.Write(sb.ToString());
    }

    public void AppendEvent(RunEventRecord e)
    {
        lock (_lock) if (!_disposed) _events.WriteLine(string.Join(",", Ts(e.At), Q(e.Kind), Q(e.ItemId), Q(e.StepId), Q(e.Text)));
    }

    public void AppendManualEntry(ManualEntryRecord m)
    {
        lock (_lock) if (!_disposed) _manual.WriteLine(string.Join(",", Ts(m.At), Q(m.ItemId), Q(m.StepId), m.Iteration?.ToString() ?? "", Q(m.Key), Q(m.Label), Q(m.Unit), Q(m.Instrument),
            m.Channel?.ToString() ?? "", m.Index.ToString(), N(m.Value), Q(m.Text)));
    }

    public void SaveResult(RunResult r)
    {
        r.OutputDir = Dir;
        r.RawFiles = new List<string> { "raw/telemetry.csv", "events.csv", "manual-entries.csv", "summary.csv", "settings-snapshot.json" };
        foreach (var f in Directory.GetFiles(HmiDir)) r.RawFiles.Add("hmi/" + Path.GetFileName(f));
        lock (_lock) File.WriteAllText(ResultPath, ResultJson.Serialize(r), new UTF8Encoding(false));
    }

    public void SaveSettingsSnapshot(string json)
    {
        lock (_lock) File.WriteAllText(Path.Combine(Dir, "settings-snapshot.json"), json, new UTF8Encoding(false));
    }

    /// <summary>生成 report.html 与 summary.csv。</summary>
    public void WriteReport(RunResult r, TestPlan? plan, SampleRecorder? samples)
    {
        var html = HtmlReportWriter.Write(r, plan, samples);
        var csv = CsvSummaryWriter.Write(r);
        lock (_lock)
        {
            File.WriteAllText(ReportPath, html, new UTF8Encoding(false));
            File.WriteAllText(SummaryPath, csv, new UTF8Encoding(true));
        }
    }

    public Stream OpenHmiFile(string name) => new FileStream(Path.Combine(HmiDir, SafeName(name)), FileMode.Create, FileAccess.Write);

    private static string SafeName(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n; }
    private static string N(double? v) => v.HasValue && !double.IsNaN(v.Value) ? v.Value.ToString("0.###", CultureInfo.InvariantCulture) : "";
    private static string Ts(DateTime t) => t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    public static string Q(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public void Dispose()
    {
        lock (_lock) { if (_disposed) return; _disposed = true; _telemetry.Dispose(); _events.Dispose(); _manual.Dispose(); }
    }
}
