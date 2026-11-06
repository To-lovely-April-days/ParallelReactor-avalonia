using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ParallelReactor.Services;

/// <summary>
/// CSV 基础格式化（纯逻辑，单元测试直接链接本文件）：
/// 字段含 逗号 / 双引号 / 回车 / 换行 时整体加引号、内部双引号加倍（RFC 4180）；
/// 以 = + - @ 开头的非数字文本前加 '（防 Excel 把标记 / 详情当公式）；
/// 数值一律按 InvariantCulture 输出（小数点为「.」）；null / NaN / ∞ 输出空单元格（不臆造数据）。
/// </summary>
public static class Csv
{
    /// <summary>时间戳格式：ISO 8601 本地时间 + 时区偏移 + 毫秒，如 2026-11-06T14:03:21.123+08:00。</summary>
    public const string TimeFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz";

    public static string Escape(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        // 防 Excel 公式注入：以 = + - @ 开头的文本（标记、详情等）前加 '；纯数字（如 -1.5）不动
        if (s[0] is '=' or '+' or '-' or '@'
            && !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            s = "'" + s;
        bool quote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0;
        if (!quote) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public static string Num(double? v, string format = "0.###")
        => v is { } d && !double.IsNaN(d) && !double.IsInfinity(d) ? d.ToString(format, CultureInfo.InvariantCulture) : "";

    public static string Int(long? v) => v is { } n ? n.ToString(CultureInfo.InvariantCulture) : "";

    public static string Bool(bool? v) => v is { } b ? (b ? "1" : "0") : "";

    public static string Time(DateTimeOffset? t) => t is { } x ? x.ToString(TimeFormat, CultureInfo.InvariantCulture) : "";

    /// <summary>把已格式化（尚未转义）的字段拼成一行（不含换行符）。</summary>
    public static string Line(IEnumerable<string?> fields)
    {
        var sb = new StringBuilder();
        bool first = true;
        foreach (var f in fields)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append(Escape(f));
        }
        return sb.ToString();
    }
}

/// <summary>运行记录里单个通道（RV1..8）一行的数据。无法获得的值保持 null → 空单元格。</summary>
public sealed class TelemetryChannel
{
    public double? Pv;          // AI-8 PV ℃（温控离线时为 null：旧值不可信）
    public int? PvRaw;          // PV 寄存器原码（有符号，= PV × 10^dPt）
    public double? SpWritten;   // 软件最近一次成功写入仪表的 SP ℃（仪表 SV 不回读）
    public double? TargetSp;    // 界面目标温度 TSp ℃
    public double? OutputPct;   // 输出 %
    public int? At;             // 工作模式 At 原值（0=APID 1=自整定 2=ON/OFF 3=手动 4=停止）
    public string? Approach;    // 防超调逼近阶段（Off/Settle/Creep/Hold）
    public double? ApproachSp;  // 逼近当前给定值 ℃（逼近未运行时 null）
    public double? CurveSp;     // 曲线运行时的 SP(t) ℃（未运行曲线时 null）
    public int? PressRaw;       // 8AI 原码（0..16000）
    public double? PressPsi;    // 压力（内部存储单位 psi，表压）
    public string? State;       // 通道状态（ReactorState 名）
    public bool OverTemp;       // 超温切断已触发（未恢复）
    public bool? Valve;         // SV 进气阀（软件已确认下发的状态，非线圈回读）
}

/// <summary>运行记录里单个通讯模块的统计。</summary>
public sealed class TelemetryModule
{
    public bool Configured;
    public bool Online;
    public long Polls;
    public long Fails;
    public DateTimeOffset? LastOkAt;

    public static TelemetryModule From(bool configured, bool online, CommStats s) => new()
    {
        Configured = configured, Online = online, Polls = s.Polls, Fails = s.Fails, LastOkAt = s.LastOkAt,
    };
}

/// <summary>一行运行记录（每个实时 Tick 一行）。</summary>
public sealed class TelemetryRow
{
    public const int ChannelCount = 8;

    public DateTimeOffset Time;
    public string? Marker;
    public string? Session;
    public TelemetryChannel[] Ch = NewChannels();

    // —— 全局 ——
    public int? Srun;            // AI-8 全局运行状态（0x0845，全局寄存器，非每通道）
    public int? Ctn;             // AI-8 启用回路数
    public bool StirOn;          // 搅拌开（命令态）
    public int? StirRpmSet;      // 设定转速 rpm（命令态，无转速反馈）
    public int? StirRampMs;      // 加减速时间 ms/1000rpm（加速与减速共用一个值，命令态）
    public int? StirAlarm;       // DM2C 报警码（0=无）
    public double? BusVoltage;   // DM2C 母线电压 V（未知时 null）
    public bool[] Gas = new bool[3];   // 惰性 / A / B 气源阀（命令态）
    public bool Vent;            // 排空阀（命令态）
    public bool Estopped;

    public TelemetryModule Temp = new(), Press = new(), Io = new(), Stir = new();

    public bool Sim;             // PR_SIM 模拟模式（读数非真实设备）

    private static TelemetryChannel[] NewChannels()
    {
        var a = new TelemetryChannel[ChannelCount];
        for (int i = 0; i < a.Length; i++) a[i] = new TelemetryChannel();
        return a;
    }
}

/// <summary>
/// 运行记录（telemetry-YYYYMMDD.csv）的表头与行格式。列顺序固定：只能在末尾追加新列，
/// 不得改名或调整顺序（DataLogger 检测到同名文件表头不一致时会另起新文件）。
/// </summary>
public static class TelemetryCsv
{
    private static readonly string[] ModuleNames = { "温控", "压力", "阀门", "搅拌" };

    private static string? _header;

    /// <summary>表头行（不含换行）。</summary>
    public static string Header => _header ??= Csv.Line(HeaderFields());

    public static IEnumerable<string> HeaderFields()
    {
        yield return "时间";
        yield return "标记";
        yield return "会话";
        for (int i = 1; i <= TelemetryRow.ChannelCount; i++)
        {
            string p = $"RV{i}_";
            yield return p + "PV(℃)";
            yield return p + "PV原码";
            yield return p + "SP已下发(℃)";
            yield return p + "目标TSp(℃)";
            yield return p + "输出(%)";
            yield return p + "At";
            yield return p + "逼近阶段";
            yield return p + "逼近SP(℃)";
            yield return p + "曲线SP(℃)";
            yield return p + "压力原码";
            yield return p + "压力(psi)";
            yield return p + "压力(MPa)";
            yield return p + "状态";
            yield return p + "超温切断";
            yield return p + "进气阀";
        }
        yield return "AI8_Srun";
        yield return "AI8_Ctn";
        yield return "搅拌开";
        yield return "搅拌设定(rpm)";
        yield return "搅拌加减速(ms/1000rpm)";
        yield return "搅拌报警码";
        yield return "母线电压(V)";
        yield return "阀_惰性气体";
        yield return "阀_气体A";
        yield return "阀_气体B";
        yield return "阀_排空";
        yield return "急停";
        foreach (var m in ModuleNames)
        {
            yield return m + "_已配置";
            yield return m + "_在线";
            yield return m + "_事务数";
            yield return m + "_失败数";
            yield return m + "_最近响应";
        }
        yield return "模拟";   // 1 = PR_SIM 模拟模式（读数非真实设备）
    }

    /// <summary>psi → MPa（与 Models.Units 一致：1 MPa = 145.037738 psi）。</summary>
    public const double MpaPerPsi = 1 / 145.037738;

    /// <summary>格式化一行（不含换行）。</summary>
    public static string Format(TelemetryRow r)
    {
        var f = new List<string>(160)
        {
            Csv.Time(r.Time),
            r.Marker ?? "",
            r.Session ?? "",
        };
        for (int i = 0; i < TelemetryRow.ChannelCount; i++)
        {
            var c = i < r.Ch.Length ? r.Ch[i] : new TelemetryChannel();
            f.Add(Csv.Num(c.Pv, "0.0##"));
            f.Add(Csv.Int(c.PvRaw));
            f.Add(Csv.Num(c.SpWritten, "0.0##"));
            f.Add(Csv.Num(c.TargetSp, "0.0##"));
            f.Add(Csv.Num(c.OutputPct, "0.##"));
            f.Add(Csv.Int(c.At));
            f.Add(c.Approach ?? "");
            f.Add(Csv.Num(c.ApproachSp, "0.0##"));
            f.Add(Csv.Num(c.CurveSp, "0.0##"));
            f.Add(Csv.Int(c.PressRaw));
            f.Add(Csv.Num(c.PressPsi, "0.0##"));
            f.Add(Csv.Num(c.PressPsi * MpaPerPsi, "0.00000"));
            f.Add(c.State ?? "");
            f.Add(Csv.Bool(c.OverTemp));
            f.Add(Csv.Bool(c.Valve));
        }
        f.Add(Csv.Int(r.Srun));
        f.Add(Csv.Int(r.Ctn));
        f.Add(Csv.Bool(r.StirOn));
        f.Add(Csv.Int(r.StirRpmSet));
        f.Add(Csv.Int(r.StirRampMs));
        f.Add(Csv.Int(r.StirAlarm));
        f.Add(Csv.Num(r.BusVoltage, "0.0#"));
        for (int g = 0; g < 3; g++) f.Add(Csv.Bool(g < r.Gas.Length && r.Gas[g]));
        f.Add(Csv.Bool(r.Vent));
        f.Add(Csv.Bool(r.Estopped));
        foreach (var m in new[] { r.Temp, r.Press, r.Io, r.Stir })
        {
            f.Add(Csv.Bool(m.Configured));
            f.Add(Csv.Bool(m.Online));
            f.Add(Csv.Int(m.Polls));
            f.Add(Csv.Int(m.Fails));
            f.Add(Csv.Time(m.LastOkAt));
        }
        f.Add(Csv.Bool(r.Sim));
        return Csv.Line(f);
    }

    /// <summary>事件日志（events-YYYYMMDD.csv）表头。</summary>
    public static readonly string EventHeader = Csv.Line(new[] { "时间", "级别", "来源", "标题", "详情" });

    public static string FormatEvent(DateTimeOffset t, string level, string source, string title, string detail)
        => Csv.Line(new[] { Csv.Time(t), level, source, title, detail });
}
